import type { Duplex } from 'node:stream';

import {
  AppHostControlClient,
  connectNamedPipe,
  readControlEnvironment,
  type ControlEnvironment,
} from '../control/apphost-control-client.js';
import {
  IdentityHealthServer,
  type IdentityHealthServerOptions,
} from '../control/identity-health-server.js';

const OPERATION_TIMEOUT_MS = 10_000;
const SHUTDOWN_REPLAY_WINDOW_MS = 250;

type ProbeRole = ControlEnvironment['role'];

type ControlIdentity = Omit<ControlEnvironment, 'pipeName'> & {
  processId: number;
};

type ControlRunCallbacks = {
  drain: () => Promise<void>;
  shutdown: () => Promise<void>;
  shutdownReplayWindowMs: number;
};

interface ProbeControlClient {
  authenticate(endpoint: string): Promise<void>;
  run(callbacks: ControlRunCallbacks): Promise<void>;
  close(): Promise<void>;
}

interface ProbeHealthServer {
  readonly endpoint: string;
  listen(): Promise<void>;
  markReady(activeWorkRemaining: number): void;
  close(): Promise<void>;
}

export interface ProbeDependencies {
  createHealthServer(options: IdentityHealthServerOptions): ProbeHealthServer;
  connectPipe(pipeName: string): Duplex;
  createControlClient(
    stream: Duplex,
    identity: ControlIdentity,
    operationTimeoutMs: number,
  ): ProbeControlClient;
}

export type ProbeErrorCode =
  | 'none'
  | 'invalid-control-environment'
  | 'invalid-health-port'
  | 'role-mismatch'
  | 'health-create-failed'
  | 'health-listen-failed'
  | 'control-connect-failed'
  | 'control-create-failed'
  | 'control-authenticate-failed'
  | 'role-initialization-failed'
  | 'health-ready-failed'
  | 'control-run-failed'
  | 'cleanup-failed';

export type ProbeResult = Readonly<{
  exitCode: 0 | 1;
  errorCode: ProbeErrorCode;
}>;

export interface RunProbeOptions {
  expectedRole: ProbeRole;
  args: readonly string[];
  environment: NodeJS.ProcessEnv;
  processId: number;
  initialize: () => Promise<void>;
  dependencies?: ProbeDependencies;
}

export interface ProbeProcessSink {
  writeStderr(line: string): void;
  setExitCode(code: 0 | 1): void;
}

type ProbeStage = Exclude<
  ProbeErrorCode,
  | 'none'
  | 'invalid-control-environment'
  | 'invalid-health-port'
  | 'role-mismatch'
  | 'cleanup-failed'
>;

const realDependencies: ProbeDependencies = {
  createHealthServer: (options) => new IdentityHealthServer(options),
  connectPipe: connectNamedPipe,
  createControlClient: (stream, identity, operationTimeoutMs) =>
    new AppHostControlClient(stream, identity, operationTimeoutMs),
};

type FailureCode = Exclude<ProbeErrorCode, 'none'>;

const failureLines = Object.freeze({
  'invalid-control-environment': 'probe-error:invalid-control-environment\n',
  'invalid-health-port': 'probe-error:invalid-health-port\n',
  'role-mismatch': 'probe-error:role-mismatch\n',
  'health-create-failed': 'probe-error:health-create-failed\n',
  'health-listen-failed': 'probe-error:health-listen-failed\n',
  'control-connect-failed': 'probe-error:control-connect-failed\n',
  'control-create-failed': 'probe-error:control-create-failed\n',
  'control-authenticate-failed': 'probe-error:control-authenticate-failed\n',
  'role-initialization-failed': 'probe-error:role-initialization-failed\n',
  'health-ready-failed': 'probe-error:health-ready-failed\n',
  'control-run-failed': 'probe-error:control-run-failed\n',
  'cleanup-failed': 'probe-error:cleanup-failed\n',
}) satisfies Readonly<Record<FailureCode, string>>;

export function parseHealthPort(args: readonly string[]): number {
  if (args.length !== 1 || !/^[1-9][0-9]{3,4}$/.test(args[0] ?? '')) {
    throw new Error('invalid-health-port');
  }
  const port = Number(args[0]);
  if (port < 1024 || port > 65_535 || String(port) !== args[0]) {
    throw new Error('invalid-health-port');
  }
  return port;
}

export function completeProbeProcess(
  result: ProbeResult,
  sink: ProbeProcessSink = {
    writeStderr: (line) => {
      process.stderr.write(line);
    },
    setExitCode: (code) => {
      process.exitCode = code;
    },
  },
): void {
  if (result.errorCode === 'none') {
    sink.setExitCode(result.exitCode);
    return;
  }

  const line =
    failureLines[result.errorCode as FailureCode] ??
    failureLines['cleanup-failed'];
  try {
    sink.writeStderr(line);
  } catch {
    // A diagnostics sink must never turn a sanitized failure into a raw error.
  } finally {
    sink.setExitCode(result.exitCode);
  }
}

export async function runProbe(options: RunProbeOptions): Promise<ProbeResult> {
  let environment: ControlEnvironment;
  try {
    environment = readControlEnvironment(options.environment);
  } catch {
    return failure('invalid-control-environment');
  }

  let port: number;
  try {
    port = parseHealthPort(options.args);
  } catch {
    return failure('invalid-health-port');
  }

  if (environment.role !== options.expectedRole) {
    return failure('role-mismatch');
  }

  const dependencies = options.dependencies ?? realDependencies;
  let health: ProbeHealthServer | undefined;
  let stream: Duplex | undefined;
  let control: ProbeControlClient | undefined;
  let stage: ProbeStage = 'health-create-failed';
  let primaryFailure: ProbeStage | undefined;

  try {
    health = dependencies.createHealthServer({
      port,
      protocolVersion: 2,
      buildIdentity: environment.buildIdentity,
      generation: environment.generation,
      generationNonce: environment.capabilityNonce,
    });
    stage = 'health-listen-failed';
    await health.listen();

    stage = 'control-connect-failed';
    stream = dependencies.connectPipe(environment.pipeName);

    stage = 'control-create-failed';
    control = dependencies.createControlClient(
      stream,
      {
        role: environment.role,
        generation: environment.generation,
        startupOperationId: environment.startupOperationId,
        capabilityNonce: environment.capabilityNonce,
        buildIdentity: environment.buildIdentity,
        processId: options.processId,
      },
      OPERATION_TIMEOUT_MS,
    );

    stage = 'control-authenticate-failed';
    await control.authenticate(health.endpoint);

    stage = 'role-initialization-failed';
    await options.initialize();

    stage = 'health-ready-failed';
    health.markReady(0);

    stage = 'control-run-failed';
    await control.run({
      drain: async () => {},
      shutdown: async () => {},
      shutdownReplayWindowMs: SHUTDOWN_REPLAY_WINDOW_MS,
    });
  } catch {
    primaryFailure = stage;
  }

  const cleanupFailed = await cleanup(control, stream, health);
  if (primaryFailure !== undefined) return failure(primaryFailure);
  if (cleanupFailed) return failure('cleanup-failed');
  return Object.freeze({ exitCode: 0, errorCode: 'none' });
}

async function cleanup(
  control: ProbeControlClient | undefined,
  stream: Duplex | undefined,
  health: ProbeHealthServer | undefined,
): Promise<boolean> {
  let failed = false;
  if (control !== undefined) {
    try {
      await control.close();
    } catch {
      failed = true;
    }
  }
  if (stream !== undefined && !stream.destroyed) {
    try {
      stream.destroy();
    } catch {
      failed = true;
    }
  }

  if (health !== undefined) {
    try {
      await health.close();
    } catch {
      failed = true;
    }
  }
  return failed;
}

function failure(errorCode: FailureCode): ProbeResult {
  return Object.freeze({ exitCode: 1, errorCode });
}
