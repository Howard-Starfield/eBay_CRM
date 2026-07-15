const PROTOCOL_VERSION = 2;
export const MAX_FRAME_BYTES = 65_536;
export const MAX_TEXT_FIELD_CHARS = 1_024;
export const MAX_GENERATION = 9_007_199_254_740_991;
export const MAX_ACTIVE_WORK_REMAINING = 2_147_483_647;

const messageTypes = [
  'identityChallenge',
  'hello',
  'drain',
  'drainAccepted',
  'noNewWorkAcquisition',
  'activeWorkRemaining',
  'drained',
  'shutdown',
  'shutdownAccepted',
  'stopped',
  'health',
] as const;
const roles = ['database', 'server', 'worker'] as const;
const directionMatrix = new Set<string>([
  'server:appHostToChild:identityChallenge',
  'server:appHostToChild:shutdown',
  'server:childToAppHost:hello',
  'server:childToAppHost:health',
  'server:childToAppHost:shutdownAccepted',
  'server:childToAppHost:stopped',
  'worker:appHostToChild:identityChallenge',
  'worker:appHostToChild:drain',
  'worker:appHostToChild:shutdown',
  'worker:childToAppHost:hello',
  'worker:childToAppHost:health',
  'worker:childToAppHost:drainAccepted',
  'worker:childToAppHost:noNewWorkAcquisition',
  'worker:childToAppHost:activeWorkRemaining',
  'worker:childToAppHost:drained',
  'worker:childToAppHost:shutdownAccepted',
  'worker:childToAppHost:stopped',
]);

export type ControlMessageType = (typeof messageTypes)[number];
export type RuntimeRole = (typeof roles)[number];
export type ControlDirection = 'appHostToChild' | 'childToAppHost';

export type ControlEnvelope = {
  version: 2;
  operationId: string;
  role: RuntimeRole;
  generation: number;
  type: ControlMessageType;
  payload: Record<string, unknown>;
};

export class ProtocolError extends Error {
  constructor(public readonly code: string) {
    super(`Control protocol error: ${code}.`);
    this.name = 'ProtocolError';
  }
}

export function decodeFrame(frame: Uint8Array): ControlEnvelope {
  if (frame.byteLength < 4) {
    throw new ProtocolError('truncated-prefix');
  }

  const view = new DataView(frame.buffer, frame.byteOffset, frame.byteLength);
  const length = view.getUint32(0, true);
  if (length === 0) {
    throw new ProtocolError('zero-length');
  }
  if (length > MAX_FRAME_BYTES) {
    throw new ProtocolError('frame-too-large');
  }
  if (frame.byteLength !== length + 4) {
    throw new ProtocolError('truncated-payload');
  }

  let json: string;
  try {
    json = new TextDecoder('utf-8', { fatal: true }).decode(frame.subarray(4));
  } catch {
    throw new ProtocolError('invalid-utf8');
  }

  let value: unknown;
  try {
    rejectDuplicateJsonProperties(json);
    value = JSON.parse(json) as unknown;
  } catch (error) {
    if (error instanceof ProtocolError) {
      throw error;
    }
    throw new ProtocolError('invalid-json');
  }
  return parseEnvelope(value);
}

function rejectDuplicateJsonProperties(json: string): void {
  let index = 0;

  const skipWhitespace = (): void => {
    while (index < json.length && /\s/.test(json[index]!)) {
      index += 1;
    }
  };

  const parseString = (): string => {
    const start = index;
    if (json[index] !== '"') {
      throw new ProtocolError('invalid-json');
    }
    index += 1;
    while (index < json.length) {
      const character = json[index]!;
      index += 1;
      if (character === '\\') {
        index += 1;
        continue;
      }
      if (character === '"') {
        return JSON.parse(json.slice(start, index)) as string;
      }
    }
    throw new ProtocolError('invalid-json');
  };

  const parseValue = (): void => {
    skipWhitespace();
    if (json[index] === '{') {
      parseObject();
      return;
    }
    if (json[index] === '[') {
      parseArray();
      return;
    }
    if (json[index] === '"') {
      parseString();
      return;
    }
    const start = index;
    while (index < json.length && !/[\s,\]}]/.test(json[index]!)) {
      index += 1;
    }
    if (index === start) {
      throw new ProtocolError('invalid-json');
    }
  };

  const parseObject = (): void => {
    index += 1;
    const keys = new Set<string>();
    skipWhitespace();
    if (json[index] === '}') {
      index += 1;
      return;
    }
    while (index < json.length) {
      skipWhitespace();
      const key = parseString();
      if (keys.has(key)) {
        throw new ProtocolError('duplicate-json-property');
      }
      keys.add(key);
      skipWhitespace();
      if (json[index] !== ':') {
        throw new ProtocolError('invalid-json');
      }
      index += 1;
      parseValue();
      skipWhitespace();
      if (json[index] === '}') {
        index += 1;
        return;
      }
      if (json[index] !== ',') {
        throw new ProtocolError('invalid-json');
      }
      index += 1;
    }
    throw new ProtocolError('invalid-json');
  };

  const parseArray = (): void => {
    index += 1;
    skipWhitespace();
    if (json[index] === ']') {
      index += 1;
      return;
    }
    while (index < json.length) {
      parseValue();
      skipWhitespace();
      if (json[index] === ']') {
        index += 1;
        return;
      }
      if (json[index] !== ',') {
        throw new ProtocolError('invalid-json');
      }
      index += 1;
    }
    throw new ProtocolError('invalid-json');
  };

  parseValue();
  skipWhitespace();
  if (index !== json.length) {
    throw new ProtocolError('invalid-json');
  }
}

export function encodeFrame(value: unknown): Buffer {
  const envelope = parseEnvelope(value);
  const payload = Buffer.from(JSON.stringify(envelope), 'utf8');
  if (payload.byteLength > MAX_FRAME_BYTES) {
    throw new ProtocolError('frame-too-large');
  }

  const frame = Buffer.allocUnsafe(payload.byteLength + 4);
  frame.writeUInt32LE(payload.byteLength, 0);
  payload.copy(frame, 4);
  return frame;
}

export function parseEnvelope(value: unknown): ControlEnvelope {
  const envelope = requireObject(value, 'invalid-envelope');
  requireExactKeys(
    envelope,
    ['version', 'operationId', 'role', 'generation', 'type', 'payload'],
    'invalid-envelope',
  );

  if (envelope.version !== PROTOCOL_VERSION) {
    throw new ProtocolError('unknown-version');
  }
  if (!isUuid(envelope.operationId)) {
    throw new ProtocolError('invalid-envelope');
  }
  if (!roles.includes(envelope.role as RuntimeRole)) {
    throw new ProtocolError('unknown-role');
  }
  if (
    !Number.isSafeInteger(envelope.generation) ||
    (envelope.generation as number) < 0 ||
    (envelope.generation as number) > MAX_GENERATION
  ) {
    throw new ProtocolError('invalid-envelope');
  }
  if (!messageTypes.includes(envelope.type as ControlMessageType)) {
    throw new ProtocolError('unknown-message-type');
  }

  const payload = requireObject(envelope.payload, 'invalid-payload');
  validatePayload(envelope.type as ControlMessageType, payload, envelope);
  return envelope as ControlEnvelope;
}

export function assertDirection(
  envelope: ControlEnvelope,
  direction: ControlDirection,
): void {
  if (!directionMatrix.has(`${envelope.role}:${direction}:${envelope.type}`)) {
    throw new ProtocolError('invalid-direction');
  }
}

export function parseCreationTicks(value: unknown): bigint {
  if (typeof value !== 'string' || !/^[1-9][0-9]{0,18}$/.test(value)) {
    throw new ProtocolError('invalid-creation-ticks');
  }
  const ticks = BigInt(value);
  if (ticks > 9_223_372_036_854_775_807n || ticks.toString(10) !== value) {
    throw new ProtocolError('invalid-creation-ticks');
  }
  return ticks;
}

function validatePayload(
  type: ControlMessageType,
  payload: Record<string, unknown>,
  envelope: Record<string, unknown>,
): void {
  switch (type) {
    case 'identityChallenge':
      requireExactKeys(
        payload,
        ['processId', 'processCreationTimeUtcTicks', 'challengeId'],
        'invalid-payload',
      );
      requirePositiveProcessId(payload.processId);
      parseCreationTicks(payload.processCreationTimeUtcTicks);
      requireText(payload.challengeId);
      return;
    case 'hello':
      requireExactKeys(
        payload,
        [
          'processId',
          'processCreationTimeUtcTicks',
          'capabilityNonce',
          'buildIdentity',
          'loopbackEndpoint',
          'challengeId',
        ],
        'invalid-payload',
      );
      requirePositiveProcessId(payload.processId);
      parseCreationTicks(payload.processCreationTimeUtcTicks);
      requireText(payload.capabilityNonce);
      requireText(payload.buildIdentity);
      requireText(payload.challengeId);
      if (payload.loopbackEndpoint !== null) {
        requireText(payload.loopbackEndpoint);
      }
      return;
    case 'health':
      requireExactKeys(
        payload,
        [
          'protocolVersion',
          'buildIdentity',
          'generation',
          'generationNonce',
          'status',
          'activeWorkRemaining',
        ],
        'invalid-payload',
      );
      if (
        payload.protocolVersion !== PROTOCOL_VERSION ||
        payload.generation !== envelope.generation
      ) {
        throw new ProtocolError('invalid-payload');
      }
      requireText(payload.buildIdentity);
      requireText(payload.generationNonce);
      requireText(payload.status);
      requireBoundedCount(payload.activeWorkRemaining);
      return;
    case 'activeWorkRemaining':
      requireExactKeys(payload, ['count'], 'invalid-payload');
      requireBoundedCount(payload.count);
      return;
    default:
      requireExactKeys(payload, [], 'invalid-payload');
  }
}

function requireObject(value: unknown, code: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new ProtocolError(code);
  }
  return value as Record<string, unknown>;
}

function requireExactKeys(
  value: Record<string, unknown>,
  expected: readonly string[],
  code: string,
): void {
  const actual = Object.keys(value);
  if (
    actual.length !== expected.length ||
    expected.some((key) => !Object.hasOwn(value, key))
  ) {
    throw new ProtocolError(code);
  }
}

function requireText(value: unknown): asserts value is string {
  if (
    typeof value !== 'string' ||
    value.trim().length === 0 ||
    value.length > MAX_TEXT_FIELD_CHARS
  ) {
    throw new ProtocolError('invalid-payload');
  }
}

function requirePositiveProcessId(value: unknown): void {
  if (
    !Number.isInteger(value) ||
    (value as number) <= 0 ||
    (value as number) > 2_147_483_647
  ) {
    throw new ProtocolError('invalid-payload');
  }
}

function requireBoundedCount(value: unknown): void {
  if (
    !Number.isSafeInteger(value) ||
    (value as number) < 0 ||
    (value as number) > MAX_ACTIVE_WORK_REMAINING
  ) {
    throw new ProtocolError('invalid-payload');
  }
}

function isUuid(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
      value,
    ) &&
    value !== '00000000-0000-0000-0000-000000000000'
  );
}
