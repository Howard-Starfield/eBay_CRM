import { createConnection } from 'node:net';
import type { Duplex } from 'node:stream';

import {
  MAX_FRAME_BYTES,
  MAX_GENERATION,
  MAX_TEXT_FIELD_CHARS,
  ProtocolError,
  assertDirection,
  decodeFrame,
  encodeFrame,
  type ControlEnvelope,
  type RuntimeRole,
} from '../protocol/control-protocol.js';

const MAX_FRAMES_PER_SESSION = 1_024;
const MAX_TIMER_MILLISECONDS = 2_147_483_647;
const CONTROL_KEYS = [
  'HOWARDLAB_APPHOST_CONTROL_PIPE',
  'HOWARDLAB_APPHOST_CONTROL_NONCE',
  'HOWARDLAB_APPHOST_CONTROL_ROLE',
  'HOWARDLAB_APPHOST_CONTROL_GENERATION',
  'HOWARDLAB_APPHOST_CONTROL_OPERATION',
  'HOWARDLAB_APPHOST_CONTROL_BUILD',
] as const;

export type ControlEnvironment = {
  pipeName: string;
  role: Exclude<RuntimeRole, 'database'>;
  generation: number;
  startupOperationId: string;
  capabilityNonce: string;
  buildIdentity: string;
};

type ControlIdentity = Omit<ControlEnvironment, 'pipeName'> & {
  processId: number;
};

type RunCallbacks = {
  drain: () => Promise<void>;
  shutdown: () => Promise<void>;
  shutdownReplayWindowMs?: number;
};

export function readControlEnvironment(
  environment: NodeJS.ProcessEnv = process.env,
): ControlEnvironment {
  const values = new Map<string, string>();
  let missing = false;
  try {
    for (const key of CONTROL_KEYS) {
      const value = environment[key];
      if (value === undefined) missing = true;
      else values.set(key, value);
    }
  } finally {
    for (const key of CONTROL_KEYS) delete environment[key];
  }

  if (missing) throw new Error('invalid-control-environment');

  const pipeName = values.get(CONTROL_KEYS[0])!;
  const capabilityNonce = values.get(CONTROL_KEYS[1])!;
  const roleText = values.get(CONTROL_KEYS[2])!;
  const generationText = values.get(CONTROL_KEYS[3])!;
  const startupOperationId = values.get(CONTROL_KEYS[4])!;
  const buildIdentity = values.get(CONTROL_KEYS[5])!;
  const generation = Number(generationText);

  if (
    !/^[A-Za-z0-9._-]{1,200}$/.test(pipeName) ||
    !isText(capabilityNonce) ||
    !isText(buildIdentity) ||
    (roleText !== 'Server' && roleText !== 'Worker') ||
    !/^(0|[1-9][0-9]{0,15})$/.test(generationText) ||
    !Number.isSafeInteger(generation) ||
    generation > MAX_GENERATION ||
    !isUuid(startupOperationId)
  ) {
    throw new Error('invalid-control-environment');
  }

  return Object.freeze({
    pipeName,
    role: roleText === 'Server' ? 'server' : 'worker',
    generation,
    startupOperationId,
    capabilityNonce,
    buildIdentity,
  });
}

export function connectNamedPipe(pipeName: string): Duplex {
  return createConnection(toWindowsPipePath(pipeName));
}

export function toWindowsPipePath(pipeName: string): string {
  if (!/^[A-Za-z0-9._-]{1,200}$/.test(pipeName)) {
    throw new Error('invalid-control-environment');
  }
  return `\\\\.\\pipe\\${pipeName}`;
}

export class AppHostControlClient implements AsyncDisposable {
  readonly #stream: Duplex;
  readonly #identity: Readonly<ControlIdentity>;
  readonly #reader: FrameReader;
  readonly #operationTimeoutMs: number;
  #state:
    | 'created'
    | 'authenticating'
    | 'authenticated'
    | 'running'
    | 'stopped'
    | 'faulted'
    | 'closed' = 'created';
  #frameCount = 0;

  constructor(
    stream: Duplex,
    identity: ControlIdentity,
    operationTimeoutMs: number,
  ) {
    if (
      !Number.isInteger(identity.processId) ||
      identity.processId <= 0 ||
      identity.processId > 2_147_483_647 ||
      (identity.role !== 'server' && identity.role !== 'worker') ||
      !Number.isSafeInteger(identity.generation) ||
      identity.generation < 0 ||
      identity.generation > MAX_GENERATION ||
      !isUuid(identity.startupOperationId) ||
      !isText(identity.capabilityNonce) ||
      !isText(identity.buildIdentity) ||
      !Number.isSafeInteger(operationTimeoutMs) ||
      operationTimeoutMs <= 0 ||
      operationTimeoutMs > MAX_TIMER_MILLISECONDS
    ) {
      throw new Error('invalid-control-client-options');
    }
    this.#stream = stream;
    this.#identity = Object.freeze({ ...identity });
    this.#operationTimeoutMs = operationTimeoutMs;
    this.#reader = new FrameReader(stream);
  }

  async authenticate(loopbackEndpoint: string): Promise<void> {
    if (this.#state === 'faulted') throw new Error('control-client-faulted');
    if (this.#state !== 'created')
      throw new Error('control-client-invalid-state');
    if (!isLoopbackHealthEndpoint(loopbackEndpoint)) {
      throw new Error('invalid-loopback-endpoint');
    }
    this.#state = 'authenticating';

    try {
      const challenge = await this.#read(this.#operationTimeoutMs);
      assertDirection(challenge, 'appHostToChild');
      if (
        challenge.type !== 'identityChallenge' ||
        challenge.operationId !== this.#identity.startupOperationId ||
        challenge.role !== this.#identity.role ||
        challenge.generation !== this.#identity.generation
      ) {
        throw new Error('invalid-identity-challenge');
      }
      const payload = challenge.payload;
      if (payload.processId !== this.#identity.processId) {
        throw new Error('process-id-mismatch');
      }

      await this.#write({
        version: 2,
        operationId: this.#identity.startupOperationId,
        role: this.#identity.role,
        generation: this.#identity.generation,
        type: 'hello',
        payload: {
          processId: this.#identity.processId,
          processCreationTimeUtcTicks: payload.processCreationTimeUtcTicks,
          capabilityNonce: this.#identity.capabilityNonce,
          buildIdentity: this.#identity.buildIdentity,
          loopbackEndpoint,
          challengeId: payload.challengeId,
        },
      });
      this.#state = 'authenticated';
    } catch (error) {
      this.#fault();
      throw error;
    }
  }

  async run(callbacks: RunCallbacks): Promise<void> {
    if (this.#state === 'faulted') throw new Error('control-client-faulted');
    if (this.#state !== 'authenticated')
      throw new Error('control-client-invalid-state');
    const replayWindow = callbacks.shutdownReplayWindowMs ?? 100;
    if (
      !Number.isSafeInteger(replayWindow) ||
      replayWindow <= 0 ||
      replayWindow > MAX_TIMER_MILLISECONDS
    ) {
      throw new Error('invalid-shutdown-replay-window');
    }
    this.#state = 'running';
    let drainOperation: string | undefined;
    let drainReplies: readonly ControlEnvelope[] | undefined;
    let shutdownOperation: string | undefined;
    let shutdownReplies: readonly ControlEnvelope[] | undefined;
    let shutdownReplayDeadline: number | undefined;

    try {
      while (true) {
        let command: ControlEnvelope;
        try {
          if (
            shutdownReplayDeadline !== undefined &&
            shutdownReplayDeadline <= performance.now()
          ) {
            this.#state = 'stopped';
            return;
          }
          command = await this.#read(
            shutdownReplayDeadline === undefined
              ? undefined
              : Math.max(1, shutdownReplayDeadline - performance.now()),
          );
        } catch (error) {
          if (shutdownReplies !== undefined && isTimeout(error)) {
            this.#state = 'stopped';
            return;
          }
          throw error;
        }
        assertDirection(command, 'appHostToChild');
        this.#assertIdentity(command);
        if (command.type === 'identityChallenge') {
          throw new Error('duplicate-identity-challenge');
        }

        if (shutdownReplies !== undefined) {
          if (
            command.type !== 'shutdown' ||
            command.operationId !== shutdownOperation
          ) {
            throw new Error('out-of-order-control-message');
          }
          try {
            await this.#writeAllBeforeDeadline(
              shutdownReplies,
              shutdownReplayDeadline!,
            );
          } catch (error) {
            if (
              error instanceof Error &&
              error.message === 'shutdown-replay-expired'
            ) {
              this.#state = 'stopped';
              return;
            }
            throw error;
          }
          continue;
        }

        if (command.type === 'drain') {
          if (
            drainOperation !== undefined &&
            command.operationId !== drainOperation
          ) {
            throw new Error('stale-operation');
          }
          if (drainReplies === undefined) {
            drainOperation = command.operationId;
            const admissionReplies = [
              this.#empty('drainAccepted', command.operationId),
              this.#empty('noNewWorkAcquisition', command.operationId),
            ] as const;
            await this.#writeAll(admissionReplies);
            await invokeCallback(
              callbacks.drain,
              this.#operationTimeoutMs,
              'drain-callback-timeout',
              'drain-callback-failed',
            );
            drainReplies = [
              ...admissionReplies,
              this.#envelope('activeWorkRemaining', command.operationId, {
                count: 0,
              }),
              this.#empty('drained', command.operationId),
            ];
            await this.#writeAll(drainReplies.slice(2));
            continue;
          }
          await this.#writeAll(drainReplies);
          continue;
        }

        if (command.type === 'shutdown') {
          if (this.#identity.role === 'worker' && drainReplies === undefined) {
            throw new Error('shutdown-before-drain');
          }
          if (
            shutdownOperation !== undefined &&
            command.operationId !== shutdownOperation
          ) {
            throw new Error('stale-operation');
          }
          if (shutdownReplies === undefined) {
            shutdownOperation = command.operationId;
            const accepted = this.#empty(
              'shutdownAccepted',
              command.operationId,
            );
            await this.#write(accepted);
            await invokeCallback(
              callbacks.shutdown,
              this.#operationTimeoutMs,
              'shutdown-callback-timeout',
              'shutdown-callback-failed',
            );
            const stopped = this.#empty('stopped', command.operationId);
            await this.#write(stopped);
            shutdownReplies = [accepted, stopped];
            shutdownReplayDeadline = performance.now() + replayWindow;
            continue;
          }
          await this.#writeAll(shutdownReplies);
          continue;
        }

        throw new Error('out-of-order-control-message');
      }
    } catch (error) {
      if (!this.#isClosed()) this.#fault();
      throw error;
    }
  }

  async close(): Promise<void> {
    if (this.#state === 'closed') return;
    this.#state = 'closed';
    this.#reader.close();
    this.#stream.destroy();
  }

  async [Symbol.asyncDispose](): Promise<void> {
    await this.close();
  }

  #assertIdentity(envelope: ControlEnvelope): void {
    if (
      envelope.role !== this.#identity.role ||
      envelope.generation !== this.#identity.generation
    ) {
      throw new Error('control-identity-mismatch');
    }
  }

  #empty(type: ControlEnvelope['type'], operationId: string): ControlEnvelope {
    return this.#envelope(type, operationId, {});
  }

  #envelope(
    type: ControlEnvelope['type'],
    operationId: string,
    payload: Record<string, unknown>,
  ): ControlEnvelope {
    return {
      version: 2,
      operationId,
      role: this.#identity.role,
      generation: this.#identity.generation,
      type,
      payload,
    };
  }

  async #writeAll(envelopes: readonly ControlEnvelope[]): Promise<void> {
    for (const envelope of envelopes) await this.#write(envelope);
  }

  async #writeAllBeforeDeadline(
    envelopes: readonly ControlEnvelope[],
    deadline: number,
  ): Promise<void> {
    for (const envelope of envelopes) {
      const remaining = deadline - performance.now();
      if (remaining <= 0) throw new Error('shutdown-replay-expired');
      const operationBoundWins = this.#operationTimeoutMs < remaining;
      await this.#write(
        envelope,
        Math.max(1, Math.min(this.#operationTimeoutMs, remaining)),
        operationBoundWins
          ? 'control-write-timeout'
          : 'shutdown-replay-expired',
      );
    }
  }

  async #write(
    envelope: ControlEnvelope,
    timeoutMs = this.#operationTimeoutMs,
    timeoutCode = 'control-write-timeout',
  ): Promise<void> {
    assertDirection(envelope, 'childToAppHost');
    this.#countFrame();
    const frame = encodeFrame(envelope);
    await withTimeout(
      new Promise<void>((resolve, reject) => {
        this.#stream.write(frame, (error) =>
          error ? reject(new Error('control-write-failed')) : resolve(),
        );
      }),
      timeoutMs,
      timeoutCode,
    );
  }

  async #read(timeoutMs: number | undefined): Promise<ControlEnvelope> {
    this.#countFrame();
    return this.#reader.read(timeoutMs);
  }

  #countFrame(): void {
    if (this.#frameCount >= MAX_FRAMES_PER_SESSION) {
      throw new ProtocolError('frame-limit-exceeded');
    }
    this.#frameCount += 1;
  }

  #fault(): void {
    if (this.#state === 'closed') return;
    this.#state = 'faulted';
    this.#reader.close();
    this.#stream.destroy();
  }

  #isClosed(): boolean {
    return this.#state === 'closed';
  }
}

type FrameWaiter = {
  resolve: (value: ControlEnvelope) => void;
  reject: (error: Error) => void;
  timer?: NodeJS.Timeout;
};

class FrameReader {
  readonly #stream: Duplex;
  #buffer = Buffer.alloc(0);
  #frames: ControlEnvelope[] = [];
  #waiters: FrameWaiter[] = [];
  #error: Error | undefined;
  #decodedFrameCount = 0;

  constructor(stream: Duplex) {
    this.#stream = stream;
    stream.on('data', this.#onData);
    stream.once('error', this.#onError);
    stream.once('end', this.#onEnd);
    stream.once('close', this.#onEnd);
  }

  read(timeoutMs?: number): Promise<ControlEnvelope> {
    if (this.#error !== undefined) return Promise.reject(this.#error);
    const frame = this.#frames.shift();
    if (frame !== undefined) return Promise.resolve(frame);

    return new Promise<ControlEnvelope>((resolve, reject) => {
      const waiter: FrameWaiter = { resolve, reject };
      if (timeoutMs !== undefined) {
        waiter.timer = setTimeout(() => {
          const index = this.#waiters.indexOf(waiter);
          if (index >= 0) this.#waiters.splice(index, 1);
          reject(new Error('control-read-timeout'));
        }, timeoutMs);
      }
      this.#waiters.push(waiter);
    });
  }

  close(): void {
    this.#fail(new Error('control-stream-closed'));
  }

  #onData = (chunk: Buffer | Uint8Array): void => {
    if (this.#error !== undefined) return;
    try {
      let incoming = Buffer.isBuffer(chunk)
        ? chunk
        : Buffer.from(chunk.buffer, chunk.byteOffset, chunk.byteLength);
      while (incoming.byteLength > 0) {
        const targetLength = this.#targetFrameLength();
        const needed = targetLength - this.#buffer.byteLength;
        const taken = Math.min(needed, incoming.byteLength);
        this.#buffer = Buffer.concat([
          this.#buffer,
          incoming.subarray(0, taken),
        ]);
        incoming = incoming.subarray(taken);

        const completedTargetLength = this.#targetFrameLength();
        if (
          this.#buffer.byteLength === completedTargetLength &&
          completedTargetLength > 4
        ) {
          if (this.#decodedFrameCount >= MAX_FRAMES_PER_SESSION) {
            throw new ProtocolError('frame-limit-exceeded');
          }
          this.#decodedFrameCount += 1;
          const envelope = decodeFrame(this.#buffer);
          this.#buffer = Buffer.alloc(0);
          const waiter = this.#waiters.shift();
          if (waiter === undefined) this.#frames.push(envelope);
          else {
            if (waiter.timer !== undefined) clearTimeout(waiter.timer);
            waiter.resolve(envelope);
          }
        }
      }
    } catch (error) {
      this.#fail(
        error instanceof ProtocolError
          ? error
          : new Error('control-protocol-failed'),
      );
    }
  };

  #targetFrameLength(): number {
    if (this.#buffer.byteLength < 4) return 4;
    const length = this.#buffer.readUInt32LE(0);
    if (length === 0) throw new ProtocolError('zero-length');
    if (length > MAX_FRAME_BYTES) throw new ProtocolError('frame-too-large');
    return length + 4;
  }

  #onError = (): void => this.#fail(new Error('control-transport-failed'));
  #onEnd = (): void => this.#fail(new Error('control-disconnected'));

  #fail(error: Error): void {
    if (this.#error !== undefined) return;
    this.#error = error;
    this.#frames = [];
    for (const waiter of this.#waiters.splice(0)) {
      if (waiter.timer !== undefined) clearTimeout(waiter.timer);
      waiter.reject(error);
    }
    this.#stream.off('data', this.#onData);
  }
}

async function withTimeout<T>(
  promise: Promise<T>,
  timeoutMs: number,
  code: string,
): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      promise,
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(code)), timeoutMs);
      }),
    ]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}

async function invokeCallback(
  callback: () => Promise<void>,
  timeoutMs: number,
  timeoutCode: string,
  failureCode: string,
): Promise<void> {
  try {
    await withTimeout(Promise.resolve().then(callback), timeoutMs, timeoutCode);
  } catch (error) {
    if (error instanceof Error && error.message === timeoutCode) throw error;
    throw new Error(failureCode);
  }
}

function isTimeout(error: unknown): boolean {
  return error instanceof Error && error.message === 'control-read-timeout';
}

function isText(value: string): boolean {
  return value.trim().length > 0 && value.length <= MAX_TEXT_FIELD_CHARS;
}

function isUuid(value: string): boolean {
  return (
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
      value,
    ) && value !== '00000000-0000-0000-0000-000000000000'
  );
}

function isLoopbackHealthEndpoint(value: string): boolean {
  try {
    const url = new URL(value);
    return (
      url.protocol === 'http:' &&
      url.hostname === '127.0.0.1' &&
      url.pathname === '/health' &&
      url.search === '' &&
      url.hash === '' &&
      url.port !== ''
    );
  } catch {
    return false;
  }
}
