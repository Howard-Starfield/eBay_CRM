import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const script = fileURLToPath(new URL('./copy-build-assets.mjs', import.meta.url));

async function fixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'copy-build-assets-'));
  const server = path.join(root, 'packages', 'twenty-server');
  const sdk = path.join(root, 'packages', 'twenty-client-sdk');
  await mkdir(path.join(server, 'dist'), { recursive: true });
  await mkdir(path.join(sdk, 'dist', 'nested'), { recursive: true });
  await writeFile(path.join(sdk, 'package.json'), '{"name":"twenty-client-sdk"}\n');
  await writeFile(path.join(sdk, 'dist', 'index.js'), 'export const sdk = true;\n');
  await writeFile(path.join(sdk, 'dist', 'nested', 'asset.txt'), 'asset\n');
  return { root, server, sdk };
}

function run(server, environment = {}) {
  return spawnSync(process.execPath, [script], {
    cwd: server,
    encoding: 'utf8',
    env: { ...process.env, ...environment },
    shell: false,
    windowsHide: true,
  });
}

test('copies the literal client SDK package and dist as ordinary byte-identical files', async () => {
  const value = await fixture();
  try {
    const result = run(value.server, { PATH: '' });
    assert.equal(result.status, 0, result.stderr);
    const destination = path.join(
      value.server,
      'dist',
      'assets',
      'twenty-client-sdk',
    );
    assert.deepEqual(
      await readFile(path.join(destination, 'package.json')),
      await readFile(path.join(value.sdk, 'package.json')),
    );
    assert.deepEqual(
      await readFile(path.join(destination, 'dist', 'index.js')),
      await readFile(path.join(value.sdk, 'dist', 'index.js')),
    );
    assert.deepEqual(
      await readFile(path.join(destination, 'dist', 'nested', 'asset.txt')),
      await readFile(path.join(value.sdk, 'dist', 'nested', 'asset.txt')),
    );
  } finally {
    await rm(value.root, { recursive: true, force: true });
  }
});

test('rejects missing source, pre-existing destination, and source reparse points', async () => {
  const missing = await fixture();
  try {
    await rm(path.join(missing.sdk, 'dist'), { recursive: true, force: true });
    assert.notEqual(run(missing.server).status, 0);
  } finally {
    await rm(missing.root, { recursive: true, force: true });
  }

  const extra = await fixture();
  try {
    const destination = path.join(
      extra.server,
      'dist',
      'assets',
      'twenty-client-sdk',
    );
    await mkdir(destination, { recursive: true });
    await writeFile(path.join(destination, 'extra.txt'), 'extra');
    assert.notEqual(run(extra.server).status, 0);
  } finally {
    await rm(extra.root, { recursive: true, force: true });
  }

  const linked = await fixture();
  try {
    const realDist = path.join(linked.root, 'real-dist');
    await mkdir(realDist);
    await writeFile(path.join(realDist, 'index.js'), 'linked');
    await rm(path.join(linked.sdk, 'dist'), { recursive: true, force: true });
    await symlink(realDist, path.join(linked.sdk, 'dist'), 'junction');
    assert.notEqual(run(linked.server).status, 0);
  } finally {
    await rm(linked.root, { recursive: true, force: true });
  }
});

test('rejects a destination ancestor symlink or junction before writing outside the server', async (t) => {
  const value = await fixture();
  try {
    const external = path.join(value.root, 'external-assets');
    const destinationAncestor = path.join(value.server, 'dist', 'assets');
    await mkdir(external);
    try {
      await symlink(
        external,
        destinationAncestor,
        process.platform === 'win32' ? 'junction' : 'dir',
      );
    } catch (error) {
      if (['EACCES', 'EPERM', 'ENOTSUP'].includes(error?.code)) {
        t.skip(`cannot create destination reparse fixture: ${error.code}`);
        return;
      }
      throw error;
    }

    const result = run(value.server);

    assert.notEqual(result.status, 0);
    await assert.rejects(
      readFile(path.join(external, 'twenty-client-sdk', 'package.json')),
      { code: 'ENOENT' },
    );
  } finally {
    await rm(value.root, { recursive: true, force: true });
  }
});

test('implementation is Node filesystem only and never delegates to a shell or copy tool', async () => {
  const source = await readFile(script, 'utf8');
  assert.doesNotMatch(source, /node:child_process|\b(?:exec|execFile|spawn|fork)\s*\(/u);
  assert.doesNotMatch(source, /['"](?:cp|mkdir|xcopy|robocopy)(?:\.exe)?['"]/iu);
});

test('rejects excessive source traversal depth with a stable budget reason', async () => {
  const value = await fixture();
  try {
    let current = path.join(value.sdk, 'dist');
    for (let index = 0; index < 70; index += 1) {
      current = path.join(current, 'd');
      await mkdir(current);
    }
    await writeFile(path.join(current, 'deep.txt'), 'bounded\n');

    const result = run(value.server);

    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /copy-build-assets-traversal-budget/u);
  } finally {
    await rm(value.root, { recursive: true, force: true });
  }
});
