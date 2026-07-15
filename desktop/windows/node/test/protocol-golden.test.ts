import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

import {
  MAX_FRAME_BYTES,
  MAX_ACTIVE_WORK_REMAINING,
  MAX_GENERATION,
  ProtocolError,
  assertDirection,
  decodeFrame,
  encodeFrame,
  parseEnvelope,
} from '../src/protocol/control-protocol.js';

type GoldenVector = {
  name: string;
  origin: 'csharp' | 'node';
  direction: 'appHostToChild' | 'childToAppHost';
  envelope: unknown;
  frameBase64: string;
};

type InvalidVector = {
  name: string;
  envelope: unknown;
  expectedError: string;
};

type GoldenArtifact = {
  protocolVersion: number;
  maxFrameBytes: number;
  validFrames: GoldenVector[];
  validSequences: Array<{
    name: string;
    frames: GoldenVector[];
  }>;
  invalidEnvelopes: InvalidVector[];
  expectedIdentity: ExpectedIdentity;
  invalidTranscripts: InvalidTranscript[];
};

type ExpectedIdentity = {
  role: 'worker';
  generation: number;
  startupOperationId: string;
  processId: number;
  processCreationTimeUtcTicks: string;
  capabilityNonce: string;
  buildIdentity: string;
  challengeId: string;
};

type TranscriptEvent =
  | {
      kind: 'frame';
      direction: 'appHostToChild' | 'childToAppHost';
      vector: string;
      mutations?: Array<{ path: string; value: unknown }>;
    }
  | { kind: 'rawLength'; length: number }
  | { kind: 'timeout' | 'cancellation'; phase: 'awaitChallenge' };

type InvalidTranscript = {
  name: string;
  expectedError: string;
  events: TranscriptEvent[];
};

const goldenPath = new URL(
  '../../protocol/control-protocol-v2.golden.json',
  import.meta.url,
);
const artifact = JSON.parse(
  await readFile(goldenPath, 'utf8'),
) as GoldenArtifact;

test('decodes every C# golden frame and re-encodes it byte-for-byte', () => {
  assert.equal(artifact.protocolVersion, 2);
  assert.equal(artifact.maxFrameBytes, MAX_FRAME_BYTES);

  for (const vector of artifact.validFrames) {
    const expectedFrame = Buffer.from(vector.frameBase64, 'base64');
    const decoded = decodeFrame(expectedFrame);
    assert.deepEqual(decoded, parseEnvelope(vector.envelope), vector.name);
    assert.doesNotThrow(
      () => assertDirection(decoded, vector.direction),
      vector.name,
    );
    assert.deepEqual(encodeFrame(decoded), expectedFrame, vector.name);
  }

  for (const sequence of artifact.validSequences) {
    for (const vector of sequence.frames) {
      const expectedFrame = Buffer.from(vector.frameBase64, 'base64');
      const envelope = decodeFrame(expectedFrame);
      assert.deepEqual(envelope, parseEnvelope(vector.envelope), vector.name);
      assert.doesNotThrow(
        () => assertDirection(envelope, vector.direction),
        `${sequence.name}:${envelope.type}`,
      );
      assert.deepEqual(encodeFrame(envelope), expectedFrame, vector.name);
    }
  }
});

test('rejects every typed invalid golden envelope with its stable code', () => {
  for (const vector of artifact.invalidEnvelopes) {
    assert.throws(
      () => parseEnvelope(vector.envelope),
      (error: unknown) =>
        error instanceof ProtocolError && error.code === vector.expectedError,
      vector.name,
    );
  }
});

test('rejects oversized length before decoding or allocating its declared payload', () => {
  const prefixOnly = Buffer.alloc(4);
  prefixOnly.writeUInt32LE(MAX_FRAME_BYTES + 1);

  assert.throws(
    () => decodeFrame(prefixOnly),
    (error: unknown) =>
      error instanceof ProtocolError && error.code === 'frame-too-large',
  );
});

test('accepts exact text and frame boundaries and rejects the next byte', () => {
  const base = structuredClone(artifact.validFrames[1]!.envelope) as {
    payload: { buildIdentity: string };
  };
  base.payload.buildIdentity = 'x'.repeat(1_024);
  assert.doesNotThrow(() => parseEnvelope(base));
  base.payload.buildIdentity += 'x';
  assert.throws(
    () => parseEnvelope(base),
    (error: unknown) =>
      error instanceof ProtocolError && error.code === 'invalid-payload',
  );

  const envelope = artifact.validFrames[0]!.envelope;
  const json = Buffer.from(JSON.stringify(envelope), 'utf8');
  const exactMaximum = Buffer.alloc(MAX_FRAME_BYTES + 4, 0x20);
  exactMaximum.writeUInt32LE(MAX_FRAME_BYTES, 0);
  json.copy(exactMaximum, 4);
  assert.doesNotThrow(() => decodeFrame(exactMaximum));
});

test('rejects duplicate nested identity properties before JSON parsing', () => {
  const json = JSON.stringify(artifact.validFrames[0]!.envelope).replace(
    '"challengeId":"challenge-01"',
    '"challengeId":"challenge-01","challengeId":"conflicting-challenge"',
  );
  const payload = Buffer.from(json, 'utf8');
  const frame = Buffer.allocUnsafe(payload.byteLength + 4);
  frame.writeUInt32LE(payload.byteLength);
  payload.copy(frame, 4);

  assert.throws(
    () => decodeFrame(frame),
    (error: unknown) =>
      error instanceof ProtocolError &&
      error.code === 'duplicate-json-property',
  );
});

test('rejects server worker-only responses and all database control traffic', () => {
  const serverDrainAccepted = parseEnvelope({
    version: 2,
    operationId: 'c7daa0b1-abfe-49c5-98de-f4030a838a91',
    role: 'server',
    generation: 7,
    type: 'drainAccepted',
    payload: {},
  });
  const databaseChallenge = parseEnvelope({
    version: 2,
    operationId: '046e33a9-8d67-4439-a69f-13debb7f5241',
    role: 'database',
    generation: 7,
    type: 'identityChallenge',
    payload: {
      processId: 4242,
      processCreationTimeUtcTicks: '638880000000000123',
      challengeId: 'challenge-01',
    },
  });

  for (const [envelope, direction] of [
    [serverDrainAccepted, 'childToAppHost'],
    [databaseChallenge, 'appHostToChild'],
  ] as const) {
    assert.throws(
      () => assertDirection(envelope, direction),
      (error: unknown) =>
        error instanceof ProtocolError && error.code === 'invalid-direction',
    );
  }
});

test('accepts shared numeric maximums and rejects the next integers', () => {
  const generation = {
    version: 2,
    operationId: '29e70eb2-df9d-442f-9f27-10194202a803',
    role: 'server',
    generation: MAX_GENERATION,
    type: 'shutdown',
    payload: {},
  };
  const activeWork = {
    version: 2,
    operationId: 'c7daa0b1-abfe-49c5-98de-f4030a838a91',
    role: 'worker',
    generation: 7,
    type: 'activeWorkRemaining',
    payload: { count: MAX_ACTIVE_WORK_REMAINING },
  };
  assert.doesNotThrow(() => parseEnvelope(generation));
  assert.doesNotThrow(() => parseEnvelope(activeWork));

  assert.throws(
    () => parseEnvelope({ ...generation, generation: MAX_GENERATION + 1 }),
    (error: unknown) =>
      error instanceof ProtocolError && error.code === 'invalid-envelope',
  );
  assert.throws(
    () =>
      parseEnvelope({
        ...activeWork,
        payload: { count: MAX_ACTIVE_WORK_REMAINING + 1 },
      }),
    (error: unknown) =>
      error instanceof ProtocolError && error.code === 'invalid-payload',
  );
});

test('executes every shared invalid transcript against the Node protocol', () => {
  const vectors = new Map<string, unknown>();
  for (const vector of artifact.validFrames) {
    vectors.set(vector.name, vector.envelope);
  }
  for (const sequence of artifact.validSequences) {
    for (const vector of sequence.frames) {
      vectors.set(vector.name, vector.envelope);
    }
  }

  for (const transcript of artifact.invalidTranscripts) {
    const runner = new TranscriptRunner(artifact.expectedIdentity, vectors);
    let actualError: string | undefined;
    for (const event of transcript.events) {
      try {
        runner.accept(event);
      } catch (error) {
        actualError =
          error instanceof ProtocolError ? error.code : String(error);
        break;
      }
    }
    assert.equal(actualError, transcript.expectedError, transcript.name);
  }
});

class TranscriptRunner {
  private challengeSeen = false;
  private authenticated = false;
  private state: 'awaitDrain' | 'awaitDrainAccepted' = 'awaitDrain';

  constructor(
    private readonly expected: ExpectedIdentity,
    private readonly vectors: ReadonlyMap<string, unknown>,
  ) {}

  accept(event: TranscriptEvent): void {
    if (event.kind === 'rawLength') {
      const frame = Buffer.alloc(4);
      frame.writeUInt32LE(event.length);
      decodeFrame(frame);
      return;
    }
    if (event.kind === 'timeout') {
      throw new ProtocolError('timeout');
    }
    if (event.kind === 'cancellation') {
      throw new ProtocolError('cancelled');
    }
    if (event.kind !== 'frame') {
      throw new ProtocolError('invalid-transcript-event');
    }

    const source = this.vectors.get(event.vector);
    assert.notEqual(source, undefined, `Unknown golden vector ${event.vector}`);
    const value = structuredClone(source);
    for (const mutation of event.mutations ?? []) {
      applyJsonPointer(value, mutation.path, mutation.value);
    }
    const envelope = parseEnvelope(value);
    assertDirection(envelope, event.direction);

    if (!this.challengeSeen) {
      if (envelope.type !== 'identityChallenge') {
        throw new ProtocolError(
          envelope.type === 'hello'
            ? 'unexpected-hello'
            : 'unexpected-challenge',
        );
      }
      this.challengeSeen = true;
      return;
    }
    if (envelope.type === 'identityChallenge') {
      throw new ProtocolError('unexpected-challenge');
    }
    if (!this.authenticated) {
      if (envelope.type !== 'hello') {
        throw new ProtocolError('hello-required');
      }
      this.validateHello(envelope);
      this.authenticated = true;
      return;
    }
    if (envelope.type === 'hello') {
      throw new ProtocolError('unexpected-hello');
    }
    if (envelope.generation !== this.expected.generation) {
      throw new ProtocolError('generation-mismatch');
    }
    if (envelope.role !== this.expected.role) {
      throw new ProtocolError('role-mismatch');
    }
    if (this.state === 'awaitDrain' && envelope.type === 'drain') {
      this.state = 'awaitDrainAccepted';
      return;
    }
    throw new ProtocolError('out-of-order');
  }

  private validateHello(envelope: ReturnType<typeof parseEnvelope>): void {
    if (envelope.operationId !== this.expected.startupOperationId) {
      throw new ProtocolError('operation-id-mismatch');
    }
    if (envelope.role !== this.expected.role) {
      throw new ProtocolError('role-mismatch');
    }
    if (envelope.generation !== this.expected.generation) {
      throw new ProtocolError('generation-mismatch');
    }
    const payload = envelope.payload as Record<string, unknown>;
    if (payload.processId !== this.expected.processId) {
      throw new ProtocolError('process-id-mismatch');
    }
    if (
      payload.processCreationTimeUtcTicks !==
      this.expected.processCreationTimeUtcTicks
    ) {
      throw new ProtocolError('process-creation-time-mismatch');
    }
    if (payload.challengeId !== this.expected.challengeId) {
      throw new ProtocolError('challenge-mismatch');
    }
    if (payload.capabilityNonce !== this.expected.capabilityNonce) {
      throw new ProtocolError('capability-nonce-mismatch');
    }
    if (payload.buildIdentity !== this.expected.buildIdentity) {
      throw new ProtocolError('build-identity-mismatch');
    }
  }
}

function applyJsonPointer(target: unknown, path: string, value: unknown): void {
  const segments = path
    .split('/')
    .slice(1)
    .map((segment) => segment.replaceAll('~1', '/').replaceAll('~0', '~'));
  let current = target as Record<string, unknown>;
  for (const segment of segments.slice(0, -1)) {
    current = current[segment] as Record<string, unknown>;
  }
  const leaf = segments.at(-1);
  assert.notEqual(leaf, undefined, `Invalid JSON pointer ${path}`);
  current[leaf!] = value;
}
