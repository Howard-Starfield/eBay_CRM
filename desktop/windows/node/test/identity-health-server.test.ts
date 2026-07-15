import assert from 'node:assert/strict';
import { request } from 'node:http';
import { connect, createServer as createNetServer } from 'node:net';
import test from 'node:test';

import {
  IdentityHealthServer,
  IdentityHealthServerError,
  type IdentityHealthServerOptions,
} from '../src/control/identity-health-server.js';

const identity = {
  protocolVersion: 2 as const,
  buildIdentity: 'node-probe-build',
  generation: 7,
  generationNonce: 'generation-nonce',
};

function options(
  overrides: Partial<IdentityHealthServerOptions> = {},
): IdentityHealthServerOptions {
  return {
    port: 0,
    allowEphemeralPort: true,
    ...identity,
    ...overrides,
  };
}

async function start(
  overrides: Partial<IdentityHealthServerOptions> = {},
): Promise<IdentityHealthServer> {
  const server = new IdentityHealthServer(options(overrides));
  await server.listen();
  return server;
}

function headers(
  overrides: Record<string, string> = {},
): Record<string, string> {
  return {
    'X-AppHost-Protocol': '2',
    'X-AppHost-Build': identity.buildIdentity,
    'X-AppHost-Generation': String(identity.generation),
    'X-AppHost-Nonce': identity.generationNonce,
    ...overrides,
  };
}

async function fetchHealth(
  server: IdentityHealthServer,
  overrides: {
    method?: string;
    path?: string;
    headers?: Record<string, string>;
  } = {},
): Promise<{ status: number; body: string; connection?: string }> {
  const url = new URL(server.endpoint);
  return await new Promise((resolve, reject) => {
    const req = request(
      {
        hostname: url.hostname,
        port: Number(url.port),
        method: overrides.method ?? 'GET',
        path: overrides.path ?? '/health',
        headers: overrides.headers ?? headers(),
      },
      (response) => {
        const chunks: Buffer[] = [];
        response.on('data', (chunk: Buffer) => chunks.push(chunk));
        response.on('end', () => {
          resolve({
            status: response.statusCode ?? 0,
            body: Buffer.concat(chunks).toString('utf8'),
            connection: response.headers.connection,
          });
        });
      },
    );
    req.once('error', reject);
    req.end();
  });
}

test('validates identity and port boundaries before listening', () => {
  assert.throws(
    () =>
      new IdentityHealthServer(null as unknown as IdentityHealthServerOptions),
    (error: unknown) =>
      error instanceof IdentityHealthServerError &&
      error.code === 'invalid-options',
  );
  for (const invalid of [
    { protocolVersion: 1 },
    { generation: -1 },
    { generation: Number.MAX_SAFE_INTEGER + 1 },
    { buildIdentity: ' ' },
    { buildIdentity: 'x'.repeat(1_025) },
    { generationNonce: '' },
    { generationNonce: 'x'.repeat(1_025) },
    { port: 0, allowEphemeralPort: false },
    { port: 1 },
    { port: 65_536 },
  ] satisfies Array<Record<string, unknown>>) {
    assert.throws(
      () =>
        new IdentityHealthServer(
          options(invalid as Partial<IdentityHealthServerOptions>),
        ),
    );
  }

  assert.doesNotThrow(
    () =>
      new IdentityHealthServer(
        options({
          buildIdentity: 'x'.repeat(1_024),
          generationNonce: 'x'.repeat(1_024),
          generation: Number.MAX_SAFE_INTEGER,
        }),
      ),
  );
});

test('serves exact identity headers and transitions not-ready to ready', async () => {
  await using server = await start();
  assert.match(server.endpoint, /^http:\/\/127\.0\.0\.1:\d+\/health$/);

  const notReady = await fetchHealth(server);
  assert.deepEqual(notReady, {
    status: 200,
    body: JSON.stringify({
      ...identity,
      status: 'not-ready',
      activeWorkRemaining: 0,
    }),
    connection: 'close',
  });

  server.markReady(3);
  const ready = await fetchHealth(server);
  assert.deepEqual(JSON.parse(ready.body), {
    ...identity,
    status: 'ready',
    activeWorkRemaining: 3,
  });
});

test('validates active work boundaries without mutating the last health payload', async () => {
  await using server = await start();
  server.markReady(0);
  server.markReady(2_147_483_647);

  for (const invalid of [-1, 2_147_483_648, 0.5, '1']) {
    assert.throws(
      () => server.markReady(invalid as number),
      (error: unknown) =>
        error instanceof IdentityHealthServerError &&
        error.code === 'invalid-active-work',
    );
  }

  assert.deepEqual(JSON.parse((await fetchHealth(server)).body), {
    ...identity,
    status: 'ready',
    activeWorkRemaining: 2_147_483_647,
  });
});

test('rejects readiness changes outside a confirmed listening lifetime without mutation', async () => {
  const server = new IdentityHealthServer(options());
  assert.throws(
    () => server.markReady(9),
    (error: unknown) =>
      error instanceof IdentityHealthServerError &&
      error.code === 'not-listening',
  );

  const listening = server.listen();
  assert.throws(
    () => server.markReady(9),
    (error: unknown) =>
      error instanceof IdentityHealthServerError &&
      error.code === 'not-listening',
  );
  await listening;
  assert.equal(
    JSON.parse((await fetchHealth(server)).body).status,
    'not-ready',
  );

  const closing = server.close();
  assert.throws(
    () => server.markReady(9),
    (error: unknown) =>
      error instanceof IdentityHealthServerError &&
      error.code === 'server-closed',
  );
  await closing;
  assert.throws(
    () => server.markReady(9),
    (error: unknown) =>
      error instanceof IdentityHealthServerError &&
      error.code === 'server-closed',
  );
});

test('reports a stable non-secret listen failure', async () => {
  const occupied = createNetServer();
  await new Promise<void>((resolve, reject) => {
    occupied.once('error', reject);
    occupied.listen(0, '127.0.0.1', resolve);
  });
  const address = occupied.address();
  if (address === null || typeof address === 'string') {
    throw new Error('Expected an IPv4 test listener.');
  }
  const server = new IdentityHealthServer(
    options({ port: address.port, allowEphemeralPort: false }),
  );
  try {
    await assert.rejects(
      server.listen(),
      (error: unknown) =>
        error instanceof IdentityHealthServerError &&
        error.code === 'listen-failed' &&
        error.message === 'Identity health server error: listen-failed.' &&
        !error.message.includes(identity.buildIdentity) &&
        !error.message.includes(identity.generationNonce),
    );
  } finally {
    await server.close();
    await new Promise<void>((resolve, reject) =>
      occupied.close((error) => (error ? reject(error) : resolve())),
    );
  }
});

test('returns one fixed non-disclosing 404 for wrong method, path, or identity header', async () => {
  await using server = await start();
  for (const invalid of [
    { method: 'POST' },
    { path: '/other' },
    { headers: headers({ 'X-AppHost-Nonce': 'wrong' }) },
    { headers: {} },
  ]) {
    assert.deepEqual(await fetchHealth(server, invalid), {
      status: 404,
      body: 'not found',
      connection: 'close',
    });
  }
});

test('rejects oversized raw headers with the fixed non-disclosing response', async () => {
  await using server = await start();
  const response = await rawRequest(
    server,
    `GET /health HTTP/1.1\r\nHost: localhost\r\nX-Fill: ${'x'.repeat(9_000)}\r\n\r\n`,
  );
  assert.match(response, /^HTTP\/1\.1 404 Not Found\r\n/);
  assert.match(response, /\r\nConnection: close\r\n\r\nnot found$/);
  assert.doesNotMatch(response, /node-probe-build|generation-nonce/);
});

test('closes a client that does not finish headers within one second', async () => {
  await using server = await start();
  const socket = connectTo(server);
  socket.write('GET /health HTTP/1.1\r\nHost: localhost\r\n');
  const started = Date.now();
  await onceClosed(socket);
  const elapsed = Date.now() - started;
  assert.ok(elapsed >= 750, `closed too soon after ${elapsed}ms`);
  assert.ok(elapsed < 2_500, `closed too late after ${elapsed}ms`);
});

test('rejects a ninth concurrent socket while eight clients occupy the cap', async () => {
  await using server = await start();
  const occupants = Array.from({ length: 8 }, () => connectTo(server));
  await Promise.all(occupants.map(onceConnected));
  for (const socket of occupants) {
    socket.write('GET /health HTTP/1.1\r\nHost: localhost\r\n');
  }

  const excess = connectTo(server);
  await onceConnected(excess);
  await Promise.race([
    onceClosed(excess),
    new Promise((_, reject) =>
      setTimeout(() => reject(new Error('excess socket remained open')), 500),
    ),
  ]);

  for (const socket of occupants) socket.destroy();
});

test('listen and close are idempotent under concurrent calls', async () => {
  const server = new IdentityHealthServer(options());
  const firstListen = server.listen();
  const secondListen = server.listen();
  assert.strictEqual(firstListen, secondListen);
  await firstListen;

  const firstClose = server.close();
  const secondClose = server.close();
  assert.strictEqual(firstClose, secondClose);
  await Promise.all([firstClose, secondClose]);
  await server[Symbol.asyncDispose]();
  await assert.rejects(server.listen());
});

test('close during listen or an in-flight request settles without leaking sockets', async () => {
  const duringListen = new IdentityHealthServer(options());
  const listening = duringListen.listen();
  const closing = duringListen.close();
  await Promise.allSettled([listening, closing]);
  await duringListen[Symbol.asyncDispose]();

  const inFlight = await start();
  const socket = connectTo(inFlight);
  await onceConnected(socket);
  socket.write('GET /health HTTP/1.1\r\nHost: localhost\r\n');
  await Promise.all([inFlight.close(), onceClosed(socket)]);
  await inFlight[Symbol.asyncDispose]();
});

function connectTo(server: IdentityHealthServer) {
  const url = new URL(server.endpoint);
  return connect({ host: '127.0.0.1', port: Number(url.port) });
}

async function rawRequest(
  server: IdentityHealthServer,
  content: string,
): Promise<string> {
  const socket = connectTo(server);
  await onceConnected(socket);
  const chunks: Buffer[] = [];
  socket.on('data', (chunk: Buffer) => chunks.push(chunk));
  socket.end(content);
  await onceClosed(socket);
  return Buffer.concat(chunks).toString('utf8');
}

function onceConnected(socket: ReturnType<typeof connect>): Promise<void> {
  if (!socket.connecting) return Promise.resolve();
  return new Promise((resolve, reject) => {
    socket.once('connect', resolve);
    socket.once('error', reject);
  });
}

function onceClosed(socket: ReturnType<typeof connect>): Promise<void> {
  if (socket.closed) return Promise.resolve();
  return new Promise((resolve) => socket.once('close', () => resolve()));
}
