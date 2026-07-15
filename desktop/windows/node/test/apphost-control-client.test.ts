import assert from 'node:assert/strict';
import { Duplex } from 'node:stream';
import test from 'node:test';

import {
  AppHostControlClient,
  readControlEnvironment,
  toWindowsPipePath,
} from '../src/control/apphost-control-client.js';
import {
  decodeFrame,
  encodeFrame,
  type ControlEnvelope,
} from '../src/protocol/control-protocol.js';

const operationId = '046e33a9-8d67-4439-a69f-13debb7f5241';
const drainId = 'c7daa0b1-abfe-49c5-98de-f4030a838a91';
const shutdownId = '29e70eb2-df9d-442f-9f27-10194202a803';
const identity = {
  role: 'worker' as const,
  generation: 7,
  startupOperationId: operationId,
  capabilityNonce: 'nonce-01',
  buildIdentity: 'node-probe/1',
  processId: 4242,
};

test('waits for one challenge then sends an exact challenge-bound HelloV2', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');

  await writeEnvelope(host, challenge());
  const hello = await readEnvelope(host);
  await authentication;

  assert.deepEqual(hello, {
    version: 2,
    operationId,
    role: 'worker',
    generation: 7,
    type: 'hello',
    payload: {
      processId: 4242,
      processCreationTimeUtcTicks: '638880000000000123',
      capabilityNonce: 'nonce-01',
      buildIdentity: 'node-probe/1',
      loopbackEndpoint: 'http://127.0.0.1:43123/health',
      challengeId: 'challenge-01',
    },
  });
  await client.close();
  host.destroy();
});

test('replays the exact worker drain and shutdown sequences without repeating callbacks', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const auth = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, challenge());
  await readEnvelope(host);
  await auth;

  let drains = 0;
  let shutdowns = 0;
  const running = client.run({
    drain: async () => {
      drains += 1;
    },
    shutdown: async () => {
      shutdowns += 1;
    },
    shutdownReplayWindowMs: 40,
  });

  await writeEnvelope(host, empty('drain', drainId));
  const firstDrain = await readMany(host, 4);
  await writeEnvelope(host, empty('drain', drainId));
  const replayedDrain = await readMany(host, 4);
  assert.deepEqual(replayedDrain, firstDrain);
  assert.deepEqual(
    firstDrain.map((frame) => [frame.type, frame.payload]),
    [
      ['drainAccepted', {}],
      ['noNewWorkAcquisition', {}],
      ['activeWorkRemaining', { count: 0 }],
      ['drained', {}],
    ],
  );
  assert.equal(drains, 1);

  await writeEnvelope(host, empty('shutdown', shutdownId));
  const firstShutdown = await readMany(host, 2);
  await writeEnvelope(host, empty('shutdown', shutdownId));
  const replayedShutdown = await readMany(host, 2);
  assert.deepEqual(replayedShutdown, firstShutdown);
  assert.deepEqual(
    firstShutdown.map((frame) => frame.type),
    ['shutdownAccepted', 'stopped'],
  );
  await running;
  assert.equal(shutdowns, 1);
  host.destroy();
});

test('faults deterministically on a stale drain operation and cannot be reused', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const auth = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, challenge());
  await readEnvelope(host);
  await auth;

  const running = client.run({
    drain: async () => {},
    shutdown: async () => {},
  });
  await writeEnvelope(host, empty('drain', drainId));
  await readMany(host, 4);
  await writeEnvelope(
    host,
    empty('drain', 'b6296c08-76e2-4f18-90cc-13d86d1a2214'),
  );
  await assert.rejects(running, /stale-operation/);
  await assert.rejects(
    client.authenticate('http://127.0.0.1:43123/health'),
    /control-client-faulted/,
  );
  host.destroy();
});

test('reads exact AppHost environment values once and removes control secrets', () => {
  const env: NodeJS.ProcessEnv = {
    PATH: 'preserved',
    HOWARDLAB_APPHOST_CONTROL_PIPE: 'HowardLab.EbayCrm.AppHost.token',
    HOWARDLAB_APPHOST_CONTROL_NONCE: 'nonce-01',
    HOWARDLAB_APPHOST_CONTROL_ROLE: 'Worker',
    HOWARDLAB_APPHOST_CONTROL_GENERATION: '7',
    HOWARDLAB_APPHOST_CONTROL_OPERATION: operationId,
    HOWARDLAB_APPHOST_CONTROL_BUILD: 'node-probe/1',
  };

  assert.deepEqual(readControlEnvironment(env), {
    pipeName: 'HowardLab.EbayCrm.AppHost.token',
    role: 'worker',
    generation: 7,
    startupOperationId: operationId,
    capabilityNonce: 'nonce-01',
    buildIdentity: 'node-probe/1',
  });
  assert.equal(env.PATH, 'preserved');
  for (const name of [
    'HOWARDLAB_APPHOST_CONTROL_PIPE',
    'HOWARDLAB_APPHOST_CONTROL_NONCE',
    'HOWARDLAB_APPHOST_CONTROL_ROLE',
    'HOWARDLAB_APPHOST_CONTROL_GENERATION',
    'HOWARDLAB_APPHOST_CONTROL_OPERATION',
    'HOWARDLAB_APPHOST_CONTROL_BUILD',
  ]) {
    assert.equal(env[name], undefined, name);
  }
});

test('faults authentication on a challenge for a different process', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  const mismatched = challenge();
  mismatched.payload.processId = 4243;
  await writeEnvelope(host, mismatched);

  await assert.rejects(authentication, /process-id-mismatch/);
  await assert.rejects(
    client.authenticate('http://127.0.0.1:43123/health'),
    /control-client-faulted/,
  );
  host.destroy();
});

test('faults on a second identity challenge after authentication', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, challenge());
  await readEnvelope(host);
  await authentication;

  const running = client.run({
    drain: async () => {},
    shutdown: async () => {},
  });
  await writeEnvelope(host, challenge());
  await assert.rejects(running, /duplicate-identity-challenge/);
  await assert.rejects(
    client.authenticate('http://127.0.0.1:43123/health'),
    /control-client-faulted/,
  );
  host.destroy();
});

test('rejects a drain replay after shutdown instead of reopening drain state', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, challenge());
  await readEnvelope(host);
  await authentication;

  const running = client.run({
    drain: async () => {},
    shutdown: async () => {},
    shutdownReplayWindowMs: 100,
  });
  await writeEnvelope(host, empty('drain', drainId));
  await readMany(host, 4);
  await writeEnvelope(host, empty('shutdown', shutdownId));
  await readMany(host, 2);
  await writeEnvelope(host, empty('drain', drainId));

  await assert.rejects(running, /out-of-order-control-message/);
  host.destroy();
});

test('close settles a pending run and keeps the client closed', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, challenge());
  await readEnvelope(host);
  await authentication;

  const running = client.run({
    drain: async () => {},
    shutdown: async () => {},
  });
  await client.close();
  await assert.rejects(running, /control-stream-closed/);
  await assert.rejects(
    client.authenticate('http://127.0.0.1:43123/health'),
    /control-client-invalid-state/,
  );
  host.destroy();
});

test('rejects ambiguous pipe paths, role casing, and noncanonical generations', () => {
  for (const overrides of [
    { HOWARDLAB_APPHOST_CONTROL_PIPE: String.raw`\\.\pipe\already-prefixed` },
    { HOWARDLAB_APPHOST_CONTROL_ROLE: 'worker' },
    { HOWARDLAB_APPHOST_CONTROL_GENERATION: '07' },
  ]) {
    const env: NodeJS.ProcessEnv = {
      HOWARDLAB_APPHOST_CONTROL_PIPE: 'HowardLab.EbayCrm.AppHost.token',
      HOWARDLAB_APPHOST_CONTROL_NONCE: 'nonce-01',
      HOWARDLAB_APPHOST_CONTROL_ROLE: 'Worker',
      HOWARDLAB_APPHOST_CONTROL_GENERATION: '7',
      HOWARDLAB_APPHOST_CONTROL_OPERATION: operationId,
      HOWARDLAB_APPHOST_CONTROL_BUILD: 'node-probe/1',
      ...overrides,
    };
    assert.throws(
      () => readControlEnvironment(env),
      /invalid-control-environment/,
    );
  }
});

test('converts a logical AppHost pipe name to the exact Windows IPC path', () => {
  assert.equal(
    toWindowsPipePath('HowardLab.EbayCrm.AppHost.token'),
    String.raw`\\.\pipe\HowardLab.EbayCrm.AppHost.token`,
  );
});

test('reports a stable error and scrubs every control key when one is missing', () => {
  const env: NodeJS.ProcessEnv = {
    PATH: 'preserved',
    HOWARDLAB_APPHOST_CONTROL_PIPE: 'HowardLab.EbayCrm.AppHost.token',
    HOWARDLAB_APPHOST_CONTROL_NONCE: 'nonce-01',
    HOWARDLAB_APPHOST_CONTROL_ROLE: 'Worker',
    HOWARDLAB_APPHOST_CONTROL_GENERATION: '7',
    HOWARDLAB_APPHOST_CONTROL_OPERATION: operationId,
  };

  assert.throws(
    () => readControlEnvironment(env),
    /^Error: invalid-control-environment$/,
  );
  assert.equal(env.PATH, 'preserved');
  for (const name of [
    'HOWARDLAB_APPHOST_CONTROL_PIPE',
    'HOWARDLAB_APPHOST_CONTROL_NONCE',
    'HOWARDLAB_APPHOST_CONTROL_ROLE',
    'HOWARDLAB_APPHOST_CONTROL_GENERATION',
    'HOWARDLAB_APPHOST_CONTROL_OPERATION',
    'HOWARDLAB_APPHOST_CONTROL_BUILD',
  ]) {
    assert.equal(env[name], undefined, name);
  }
});

test('parses multiple complete frames delivered in one transport chunk', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(
    child,
    { ...identity, role: 'server' },
    1_000,
  );
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeBytes(
    host,
    Buffer.concat([
      encodeFrame({ ...challenge(), role: 'server' }),
      encodeFrame({ ...empty('shutdown', shutdownId), role: 'server' }),
    ]),
  );
  await readEnvelope(host);
  await authentication;

  const running = client.run({
    drain: async () => {},
    shutdown: async () => {},
    shutdownReplayWindowMs: 20,
  });
  assert.deepEqual(
    (await readMany(host, 2)).map((frame) => frame.type),
    ['shutdownAccepted', 'stopped'],
  );
  await running;
  host.destroy();
});

test('rejects an oversized prefix before waiting for its declared payload', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  const prefix = Buffer.alloc(4);
  prefix.writeUInt32LE(65_537);
  await writeBytes(host, prefix);

  await assert.rejects(authentication, /frame-too-large/);
  host.destroy();
});

test('faults a complete-frame flood before retaining an unbounded decoded queue', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, challenge());
  await readEnvelope(host);
  await authentication;

  const frame = encodeFrame(empty('shutdown', shutdownId));
  await writeBytes(
    host,
    Buffer.concat(Array.from({ length: 1_024 }, () => frame)),
  );
  const running = client.run({
    drain: async () => {},
    shutdown: async () => {},
  });

  await assert.rejects(running, /frame-limit-exceeded/);
  host.destroy();
});

test('worker rejects shutdown until its drain sequence has completed', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, challenge());
  await readEnvelope(host);
  await authentication;

  const running = client.run({
    drain: async () => {},
    shutdown: async () => {},
  });
  await writeEnvelope(host, empty('shutdown', shutdownId));
  await assert.rejects(running, /shutdown-before-drain/);
  host.destroy();
});

test('acknowledges drain admission before awaiting callback and reports only final zero', async () => {
  const [child, host] = duplexPair();
  const client = new AppHostControlClient(child, identity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, challenge());
  await readEnvelope(host);
  await authentication;
  const drain = deferred<void>();
  const running = client.run({
    drain: () => drain.promise,
    shutdown: async () => {},
  });

  await writeEnvelope(host, empty('drain', drainId));
  assert.deepEqual(
    (await readMany(host, 2)).map((frame) => frame.type),
    ['drainAccepted', 'noNewWorkAcquisition'],
  );
  const third = readEnvelope(host);
  assert.equal(await pendingFor(third, 20), true);
  drain.resolve(undefined);
  assert.deepEqual(
    [(await third).type, (await readEnvelope(host)).type],
    ['activeWorkRemaining', 'drained'],
  );

  await writeEnvelope(host, empty('shutdown', shutdownId));
  await readMany(host, 2);
  await running;
  host.destroy();
});

test('acknowledges server shutdown before awaiting callback and then reports stopped', async () => {
  const [child, host] = duplexPair();
  const serverIdentity = { ...identity, role: 'server' as const };
  const client = new AppHostControlClient(child, serverIdentity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, { ...challenge(), role: 'server' });
  await readEnvelope(host);
  await authentication;
  const shutdown = deferred<void>();
  const running = client.run({
    drain: async () => {},
    shutdown: () => shutdown.promise,
  });

  await writeEnvelope(host, {
    ...empty('shutdown', shutdownId),
    role: 'server',
  });
  assert.equal((await readEnvelope(host)).type, 'shutdownAccepted');
  const stopped = readEnvelope(host);
  assert.equal(await pendingFor(stopped, 20), true);
  shutdown.resolve(undefined);
  assert.equal((await stopped).type, 'stopped');
  await running;
  host.destroy();
});

test('does not extend the absolute shutdown replay deadline for duplicates', async () => {
  const [child, host] = duplexPair();
  const serverIdentity = { ...identity, role: 'server' as const };
  const client = new AppHostControlClient(child, serverIdentity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, { ...challenge(), role: 'server' });
  await readEnvelope(host);
  await authentication;
  const running = client.run({
    drain: async () => {},
    shutdown: async () => {},
    shutdownReplayWindowMs: 80,
  });
  await writeEnvelope(host, {
    ...empty('shutdown', shutdownId),
    role: 'server',
  });
  await readMany(host, 2);
  const started = Date.now();
  await new Promise((resolve) => setTimeout(resolve, 55));
  await writeEnvelope(host, {
    ...empty('shutdown', shutdownId),
    role: 'server',
  });
  await readMany(host, 2);
  await running;
  assert.ok(Date.now() - started < 115, 'duplicate extended replay deadline');
  host.destroy();
});

test('bounds backpressured duplicate replay writes by the same absolute deadline', async () => {
  const [child, host] = memoryDuplexPair();
  const serverIdentity = { ...identity, role: 'server' as const };
  const client = new AppHostControlClient(child, serverIdentity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, { ...challenge(), role: 'server' });
  await readEnvelope(host);
  await authentication;
  const running = client.run({
    drain: async () => {},
    shutdown: async () => {},
    shutdownReplayWindowMs: 60,
  });
  await writeEnvelope(host, {
    ...empty('shutdown', shutdownId),
    role: 'server',
  });
  await readMany(host, 2);
  child.delayWritesAfter(3, 150);
  const started = performance.now();
  await writeEnvelope(host, {
    ...empty('shutdown', shutdownId),
    role: 'server',
  });
  assert.equal((await readEnvelope(host)).type, 'shutdownAccepted');
  await running;
  assert.ok(
    performance.now() - started < 110,
    'backpressured replay escaped its absolute deadline',
  );
  host.destroy();
});

test('uses the shorter operation timeout for a blocked duplicate replay write', async () => {
  const [child, host] = memoryDuplexPair();
  const serverIdentity = { ...identity, role: 'server' as const };
  const client = new AppHostControlClient(child, serverIdentity, 30);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(host, { ...challenge(), role: 'server' });
  await readEnvelope(host);
  await authentication;
  const running = client.run({
    drain: async () => {},
    shutdown: async () => {},
    shutdownReplayWindowMs: 1_000,
  });
  await writeEnvelope(host, {
    ...empty('shutdown', shutdownId),
    role: 'server',
  });
  await readMany(host, 2);
  child.delayWritesAfter(3, 150);
  await writeEnvelope(host, {
    ...empty('shutdown', shutdownId),
    role: 'server',
  });
  assert.equal((await readEnvelope(host)).type, 'shutdownAccepted');
  await assert.rejects(running, /^Error: control-write-timeout$/);
  host.destroy();
});

test('bounds operation and replay timeouts to the Node timer maximum', async () => {
  const [child, host] = duplexPair();
  assert.doesNotThrow(
    () => new AppHostControlClient(child, identity, 2_147_483_647),
  );
  assert.throws(
    () => new AppHostControlClient(child, identity, 2_147_483_648),
    /invalid-control-client-options/,
  );
  host.destroy();

  const [authenticatedChild, authenticatedHost] = duplexPair();
  const client = new AppHostControlClient(authenticatedChild, identity, 1_000);
  const authentication = client.authenticate('http://127.0.0.1:43123/health');
  await writeEnvelope(authenticatedHost, challenge());
  await readEnvelope(authenticatedHost);
  await authentication;
  await assert.rejects(
    client.run({
      drain: async () => {},
      shutdown: async () => {},
      shutdownReplayWindowMs: 2_147_483_648,
    }),
    /invalid-shutdown-replay-window/,
  );
  await client.close();
  authenticatedHost.destroy();
});

test('rejects database and unknown roles at the runtime constructor boundary', () => {
  for (const role of ['database', 'unexpected']) {
    const [child, host] = duplexPair();
    assert.throws(
      () =>
        new AppHostControlClient(
          child,
          { ...identity, role } as unknown as typeof identity,
          1_000,
        ),
      /^Error: invalid-control-client-options$/,
    );
    child.destroy();
    host.destroy();
  }
});

test('sanitizes callback and transport failures', async () => {
  {
    const [child, host] = duplexPair();
    const client = new AppHostControlClient(child, identity, 1_000);
    const authentication = client.authenticate('http://127.0.0.1:43123/health');
    await writeEnvelope(host, challenge());
    await readEnvelope(host);
    await authentication;
    const running = client.run({
      drain: async () => {
        throw new Error('secret callback detail');
      },
      shutdown: async () => {},
    });
    await writeEnvelope(host, empty('drain', drainId));
    await readMany(host, 2);
    await assert.rejects(running, /^Error: drain-callback-failed$/);
    host.destroy();
  }

  {
    const [child, host] = duplexPair();
    const client = new AppHostControlClient(child, identity, 1_000);
    const authentication = client.authenticate('http://127.0.0.1:43123/health');
    child.emit('error', new Error('secret transport detail'));
    await assert.rejects(authentication, /^Error: control-transport-failed$/);
  }
});

function challenge(): ControlEnvelope {
  return {
    version: 2,
    operationId,
    role: 'worker',
    generation: 7,
    type: 'identityChallenge',
    payload: {
      processId: 4242,
      processCreationTimeUtcTicks: '638880000000000123',
      challengeId: 'challenge-01',
    },
  };
}

function empty(type: 'drain' | 'shutdown', id: string): ControlEnvelope {
  return {
    version: 2,
    operationId: id,
    role: 'worker',
    generation: 7,
    type,
    payload: {},
  };
}

function duplexPair(): [Duplex, Duplex] {
  return memoryDuplexPair();
}

function memoryDuplexPair(): [MemoryDuplex, MemoryDuplex] {
  const left = new MemoryDuplex();
  const right = new MemoryDuplex();
  left.peer = right;
  right.peer = left;
  return [left, right];
}

class MemoryDuplex extends Duplex {
  peer!: MemoryDuplex;
  #writeCount = 0;
  #delayAfter = Number.POSITIVE_INFINITY;
  #delayMs = 0;

  delayWritesAfter(completedWrites: number, delayMs: number): void {
    this.#delayAfter = completedWrites;
    this.#delayMs = delayMs;
  }

  override _read(): void {}

  override _write(
    chunk: Buffer | Uint8Array,
    _encoding: BufferEncoding,
    callback: (error?: Error | null) => void,
  ): void {
    if (this.destroyed || this.peer.destroyed) {
      callback(new Error('memory-duplex-closed'));
      return;
    }
    this.#writeCount += 1;
    this.peer.push(Buffer.from(chunk));
    if (this.#writeCount > this.#delayAfter) {
      setTimeout(callback, this.#delayMs);
    } else {
      callback();
    }
  }

  override _destroy(
    error: Error | null,
    callback: (error?: Error | null) => void,
  ): void {
    callback(error);
  }
}

async function writeEnvelope(
  stream: Duplex,
  envelope: ControlEnvelope,
): Promise<void> {
  await writeBytes(stream, encodeFrame(envelope));
}

async function writeBytes(stream: Duplex, bytes: Uint8Array): Promise<void> {
  await new Promise<void>((resolve, reject) =>
    stream.write(bytes, (error) => (error ? reject(error) : resolve())),
  );
}

async function readEnvelope(stream: Duplex): Promise<ControlEnvelope> {
  let reader = testReaders.get(stream);
  if (reader === undefined) {
    reader = new TestFrameReader(stream);
    testReaders.set(stream, reader);
  }
  return reader.read();
}

async function readMany(
  stream: Duplex,
  count: number,
): Promise<ControlEnvelope[]> {
  const frames: ControlEnvelope[] = [];
  while (frames.length < count) frames.push(await readEnvelope(stream));
  return frames;
}

const testReaders = new WeakMap<Duplex, TestFrameReader>();

class TestFrameReader {
  #buffer = Buffer.alloc(0);
  #frames: ControlEnvelope[] = [];
  #waiters: Array<(frame: ControlEnvelope) => void> = [];

  constructor(stream: Duplex) {
    stream.on('data', (chunk: Buffer | Uint8Array) => {
      this.#buffer = Buffer.concat([this.#buffer, Buffer.from(chunk)]);
      while (this.#buffer.byteLength >= 4) {
        const length = this.#buffer.readUInt32LE(0);
        if (this.#buffer.byteLength < length + 4) return;
        const frame = decodeFrame(this.#buffer.subarray(0, length + 4));
        this.#buffer = this.#buffer.subarray(length + 4);
        const waiter = this.#waiters.shift();
        if (waiter === undefined) this.#frames.push(frame);
        else waiter(frame);
      }
    });
  }

  read(): Promise<ControlEnvelope> {
    const frame = this.#frames.shift();
    if (frame !== undefined) return Promise.resolve(frame);
    return new Promise((resolve) => this.#waiters.push(resolve));
  }
}

function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T | PromiseLike<T>) => void;
} {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((innerResolve) => {
    resolve = innerResolve;
  });
  return { promise, resolve };
}

async function pendingFor<T>(
  promise: Promise<T>,
  milliseconds: number,
): Promise<boolean> {
  return Promise.race([
    promise.then(() => false),
    new Promise<true>((resolve) =>
      setTimeout(() => resolve(true), milliseconds),
    ),
  ]);
}
