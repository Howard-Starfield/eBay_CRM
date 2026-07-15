import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { Duplex } from 'node:stream';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import type {
  AppHostControlClient,
  ControlEnvironment,
} from '../src/control/apphost-control-client.js';
import type {
  IdentityHealthServer,
  IdentityHealthServerOptions,
} from '../src/control/identity-health-server.js';
import {
  completeProbeProcess,
  parseHealthPort,
  runProbe,
  type ProbeDependencies,
} from '../src/probes/probe-orchestrator.js';

const startupOperationId = '046e33a9-8d67-4439-a69f-13debb7f5241';

test('reports one allowlisted failure line before setting exit code and keeps success silent', () => {
  const events: string[] = [];
  const sink = {
    writeStderr: (line: string) => events.push(`stderr:${line}`),
    setExitCode: (code: 0 | 1) => events.push(`exit:${code}`),
  };

  completeProbeProcess(
    { exitCode: 1, errorCode: 'control-authenticate-failed' },
    sink,
  );
  completeProbeProcess({ exitCode: 0, errorCode: 'none' }, sink);

  assert.deepEqual(events, [
    'stderr:probe-error:control-authenticate-failed\n',
    'exit:1',
    'exit:0',
  ]);
});

test('sets the failure exit code without exposing a broken stderr sink', () => {
  const exitCodes: number[] = [];

  assert.doesNotThrow(() =>
    completeProbeProcess(
      { exitCode: 1, errorCode: 'cleanup-failed' },
      {
        writeStderr: () => {
          throw new Error('secret stderr detail');
        },
        setExitCode: (code) => exitCodes.push(code),
      },
    ),
  );
  assert.deepEqual(exitCodes, [1]);
});

for (const entrypoint of ['server-probe.ts', 'worker-probe.ts']) {
  test(`${entrypoint} reports a sanitized top-level control-environment failure`, () => {
    const secretCanary = `entrypoint-secret-${entrypoint}`;
    const environment: NodeJS.ProcessEnv = {
      ...process.env,
      HOWARDLAB_APPHOST_CONTROL_PIPE: `pipe-${secretCanary}`,
      HOWARDLAB_APPHOST_CONTROL_NONCE: `nonce-${secretCanary}`,
      HOWARDLAB_APPHOST_CONTROL_ROLE: entrypoint.startsWith('server')
        ? 'Server'
        : 'Worker',
      HOWARDLAB_APPHOST_CONTROL_GENERATION: '7',
      HOWARDLAB_APPHOST_CONTROL_OPERATION: startupOperationId,
    };
    delete environment.HOWARDLAB_APPHOST_CONTROL_BUILD;

    const child = spawnSync(
      process.execPath,
      [
        '--import',
        'tsx',
        fileURLToPath(new URL(`../src/probes/${entrypoint}`, import.meta.url)),
        '43123',
      ],
      {
        cwd: process.cwd(),
        env: environment,
        encoding: 'utf8',
        timeout: 10_000,
        windowsHide: true,
        maxBuffer: 64 * 1024,
      },
    );

    assert.equal(child.error, undefined);
    assert.equal(child.signal, null);
    assert.equal(child.status, 1);
    assert.equal(child.stdout, '');
    assert.equal(child.stderr, 'probe-error:invalid-control-environment\n');
    assert.doesNotMatch(
      `${child.stdout}${child.stderr}`,
      new RegExp(secretCanary),
    );
  });
}

test('accepts exactly one canonical non-privileged health port', () => {
  assert.equal(parseHealthPort(['1024']), 1024);
  assert.equal(parseHealthPort(['65535']), 65_535);

  for (const args of [
    [],
    ['1023'],
    ['65536'],
    ['01024'],
    ['+1024'],
    ['1024.0'],
    [' 1024'],
    ['1024', '1025'],
  ]) {
    assert.throws(() => parseHealthPort(args), /^Error: invalid-health-port$/);
  }
});

test('runs the server probe in the production order and cleans up in order', async () => {
  const fixture = createFixture('server');

  const result = await runProbe({
    expectedRole: 'server',
    args: ['43123'],
    environment: fixture.environment,
    processId: 4242,
    initialize: async () => {
      fixture.events.push('initialize:server');
    },
    dependencies: fixture.dependencies,
  });

  assert.deepEqual(result, { exitCode: 0, errorCode: 'none' });
  assert.deepEqual(fixture.events, [
    'health:create:43123',
    'health:listen',
    'pipe:connect:HowardLab.EbayCrm.AppHost.token',
    'control:create:server:4242:10000',
    'control:authenticate:http://127.0.0.1:43123/health',
    'initialize:server',
    'health:ready:0',
    'control:run:250',
    'control:close',
    'health:close',
  ]);
  assertControlEnvironmentScrubbed(fixture.environment);
});

test('runs worker drain and shutdown callbacks without closing control early', async () => {
  const fixture = createFixture('worker', {
    runCallbacks: true,
  });

  const result = await runProbe({
    expectedRole: 'worker',
    args: ['43124'],
    environment: fixture.environment,
    processId: 4243,
    initialize: async () => {
      fixture.events.push('initialize:worker');
    },
    dependencies: fixture.dependencies,
  });

  assert.deepEqual(result, { exitCode: 0, errorCode: 'none' });
  assert.deepEqual(fixture.events, [
    'health:create:43124',
    'health:listen',
    'pipe:connect:HowardLab.EbayCrm.AppHost.token',
    'control:create:worker:4243:10000',
    'control:authenticate:http://127.0.0.1:43124/health',
    'initialize:worker',
    'health:ready:0',
    'control:run:250',
    'worker:drain',
    'worker:shutdown',
    'control:run:return',
    'control:close',
    'health:close',
  ]);
});

test('scrubs control secrets and rejects a mismatched entrypoint role before listening', async () => {
  const fixture = createFixture('worker');

  const result = await runProbe({
    expectedRole: 'server',
    args: ['43123'],
    environment: fixture.environment,
    processId: 4242,
    initialize: async () => {},
    dependencies: fixture.dependencies,
  });

  assert.deepEqual(result, { exitCode: 1, errorCode: 'role-mismatch' });
  assert.deepEqual(fixture.events, []);
  assertControlEnvironmentScrubbed(fixture.environment);
});

test('scrubs control secrets and rejects invalid CLI arguments before listening', async () => {
  const fixture = createFixture('server');

  const result = await runProbe({
    expectedRole: 'server',
    args: ['043123'],
    environment: fixture.environment,
    processId: 4242,
    initialize: async () => {},
    dependencies: fixture.dependencies,
  });

  assert.deepEqual(result, { exitCode: 1, errorCode: 'invalid-health-port' });
  assert.deepEqual(fixture.events, []);
  assertControlEnvironmentScrubbed(fixture.environment);
});

test('returns one stable code and scrubs all present keys for an invalid control environment', async () => {
  const fixture = createFixture('server');
  delete fixture.environment.HOWARDLAB_APPHOST_CONTROL_BUILD;

  const result = await runProbe({
    expectedRole: 'server',
    args: ['43123'],
    environment: fixture.environment,
    processId: 4242,
    initialize: async () => {},
    dependencies: fixture.dependencies,
  });

  assert.deepEqual(result, {
    exitCode: 1,
    errorCode: 'invalid-control-environment',
  });
  assert.deepEqual(fixture.events, []);
  assertControlEnvironmentScrubbed(fixture.environment);
});

for (const failure of [
  {
    name: 'health creation',
    at: 'health:create:43123',
    code: 'health-create-failed',
    expected: ['health:create:43123'],
  },
  {
    name: 'control client creation',
    at: 'control:create:server:4242:10000',
    code: 'control-create-failed',
    expected: [
      'health:create:43123',
      'health:listen',
      'pipe:connect:HowardLab.EbayCrm.AppHost.token',
      'control:create:server:4242:10000',
      'health:close',
    ],
    streamDestroyed: true,
  },
  {
    name: 'listen',
    at: 'health:listen',
    code: 'health-listen-failed',
    expected: ['health:create:43123', 'health:listen', 'health:close'],
  },
  {
    name: 'ready transition',
    at: 'health:ready:0',
    code: 'health-ready-failed',
    expected: [
      'health:create:43123',
      'health:listen',
      'pipe:connect:HowardLab.EbayCrm.AppHost.token',
      'control:create:server:4242:10000',
      'control:authenticate:http://127.0.0.1:43123/health',
      'initialize:server',
      'health:ready:0',
      'control:close',
      'health:close',
    ],
  },
  {
    name: 'connect',
    at: 'pipe:connect:HowardLab.EbayCrm.AppHost.token',
    code: 'control-connect-failed',
    expected: [
      'health:create:43123',
      'health:listen',
      'pipe:connect:HowardLab.EbayCrm.AppHost.token',
      'health:close',
    ],
  },
  {
    name: 'authenticate',
    at: 'control:authenticate:http://127.0.0.1:43123/health',
    code: 'control-authenticate-failed',
    expected: [
      'health:create:43123',
      'health:listen',
      'pipe:connect:HowardLab.EbayCrm.AppHost.token',
      'control:create:server:4242:10000',
      'control:authenticate:http://127.0.0.1:43123/health',
      'control:close',
      'health:close',
    ],
  },
  {
    name: 'initialize',
    at: 'initialize:server',
    code: 'role-initialization-failed',
    expected: [
      'health:create:43123',
      'health:listen',
      'pipe:connect:HowardLab.EbayCrm.AppHost.token',
      'control:create:server:4242:10000',
      'control:authenticate:http://127.0.0.1:43123/health',
      'initialize:server',
      'control:close',
      'health:close',
    ],
  },
  {
    name: 'run',
    at: 'control:run:250',
    code: 'control-run-failed',
    expected: [
      'health:create:43123',
      'health:listen',
      'pipe:connect:HowardLab.EbayCrm.AppHost.token',
      'control:create:server:4242:10000',
      'control:authenticate:http://127.0.0.1:43123/health',
      'initialize:server',
      'health:ready:0',
      'control:run:250',
      'control:close',
      'health:close',
    ],
  },
] as const) {
  test(`maps a ${failure.name} failure to a stable code and cleans up`, async () => {
    const fixture = createFixture('server', { failAt: failure.at });

    const result = await runProbe({
      expectedRole: 'server',
      args: ['43123'],
      environment: fixture.environment,
      processId: 4242,
      initialize: async () => {
        fixture.record('initialize:server');
      },
      dependencies: fixture.dependencies,
    });

    assert.deepEqual(result, { exitCode: 1, errorCode: failure.code });
    assert.deepEqual(fixture.events, failure.expected);
    if ('streamDestroyed' in failure) {
      assert.equal(fixture.stream?.destroyed, failure.streamDestroyed);
    }
    assert.doesNotMatch(JSON.stringify(result), /secret|nonce|pipe/i);
  });
}

test('attempts both cleanup steps and reports only a stable cleanup code', async () => {
  const fixture = createFixture('server', {
    failAt: ['control:close', 'health:close'],
  });

  const result = await runProbe({
    expectedRole: 'server',
    args: ['43123'],
    environment: fixture.environment,
    processId: 4242,
    initialize: async () => {
      fixture.events.push('initialize:server');
    },
    dependencies: fixture.dependencies,
  });

  assert.deepEqual(result, { exitCode: 1, errorCode: 'cleanup-failed' });
  assert.deepEqual(fixture.events.slice(-2), ['control:close', 'health:close']);
  assert.equal(fixture.stream?.destroyed, true);
  assert.doesNotMatch(JSON.stringify(result), /secret detail/);
});

function createFixture(
  role: ControlEnvironment['role'],
  options: {
    failAt?: string | readonly string[];
    runCallbacks?: boolean;
  } = {},
) {
  const events: string[] = [];
  const failures = new Set(
    typeof options.failAt === 'string'
      ? [options.failAt]
      : (options.failAt ?? []),
  );
  const environment: NodeJS.ProcessEnv = {
    PATH: 'preserved',
    HOWARDLAB_APPHOST_CONTROL_PIPE: 'HowardLab.EbayCrm.AppHost.token',
    HOWARDLAB_APPHOST_CONTROL_NONCE: 'secret-generation-nonce',
    HOWARDLAB_APPHOST_CONTROL_ROLE: role === 'server' ? 'Server' : 'Worker',
    HOWARDLAB_APPHOST_CONTROL_GENERATION: '7',
    HOWARDLAB_APPHOST_CONTROL_OPERATION: startupOperationId,
    HOWARDLAB_APPHOST_CONTROL_BUILD: 'node-probe/1',
  };
  const record = (event: string): void => {
    events.push(event);
    if (failures.has(event)) throw new Error(`secret detail from ${event}`);
  };
  let health!: FakeHealth;
  let control!: FakeControl;
  let stream: Duplex | undefined;

  class FakeHealth {
    readonly endpoint: string;

    constructor(options: IdentityHealthServerOptions) {
      record(`health:create:${options.port}`);
      this.endpoint = `http://127.0.0.1:${options.port}/health`;
      health = this;
    }

    async listen(): Promise<void> {
      record('health:listen');
    }

    markReady(count: number): void {
      record(`health:ready:${count}`);
    }

    async close(): Promise<void> {
      record('health:close');
    }
  }

  class FakeControl {
    constructor(
      _stream: Duplex,
      identity: { role: ControlEnvironment['role']; processId: number },
      operationTimeoutMs: number,
    ) {
      record(
        `control:create:${identity.role}:${identity.processId}:${operationTimeoutMs}`,
      );
      control = this;
    }

    async authenticate(endpoint: string): Promise<void> {
      record(`control:authenticate:${endpoint}`);
    }

    async run(callbacks: {
      drain: () => Promise<void>;
      shutdown: () => Promise<void>;
      shutdownReplayWindowMs?: number;
    }): Promise<void> {
      record(`control:run:${callbacks.shutdownReplayWindowMs}`);
      if (options.runCallbacks) {
        await callbacks.drain();
        events.push('worker:drain');
        await callbacks.shutdown();
        events.push('worker:shutdown');
        events.push('control:run:return');
      }
    }

    async close(): Promise<void> {
      record('control:close');
    }
  }

  const dependencies: ProbeDependencies = {
    createHealthServer: (identity) =>
      new FakeHealth(identity) as unknown as IdentityHealthServer,
    connectPipe: (pipeName) => {
      record(`pipe:connect:${pipeName}`);
      stream = new Duplex({
        read() {},
        write(_chunk, _encoding, callback) {
          callback();
        },
      });
      return stream;
    },
    createControlClient: (stream, identity, operationTimeoutMs) =>
      new FakeControl(
        stream,
        identity,
        operationTimeoutMs,
      ) as unknown as AppHostControlClient,
  };

  return {
    events,
    environment,
    dependencies,
    record,
    get health() {
      return health;
    },
    get control() {
      return control;
    },
    get stream() {
      return stream;
    },
  };
}

function assertControlEnvironmentScrubbed(
  environment: NodeJS.ProcessEnv,
): void {
  assert.equal(environment.PATH, 'preserved');
  for (const key of Object.keys(environment)) {
    assert.doesNotMatch(key, /^HOWARDLAB_APPHOST_CONTROL_/);
  }
}
