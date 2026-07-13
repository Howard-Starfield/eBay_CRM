import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { findViolations } from './check-runtime-backend-boundaries.mjs';

const withFixture = async (files, callback) => {
  const root = await mkdtemp(path.join(tmpdir(), 'runtime-boundaries-'));

  try {
    await Promise.all(
      Object.entries(files).map(async ([relativePath, source]) => {
        const filePath = path.join(root, relativePath);

        await mkdir(path.dirname(filePath), { recursive: true });
        await writeFile(filePath, source, 'utf8');
      }),
    );

    await callback(root);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
};

test('rejects a new direct bullmq import outside adapters', async () => {
  await withFixture(
    {
      'src/new-feature.ts':
        "import { Queue } from 'bullmq';\n\nexport const queue = new Queue('new-feature');\n",
    },
    async (root) => {
      const policy = {
        baselineDirectImports: [],
        adapterPathPrefixes: [],
      };

      assert.deepEqual(await findViolations({ root, policy }), [
        'src/new-feature.ts -> bullmq',
      ]);
    },
  );
});

test('allows a frozen upstream direct import', async () => {
  await withFixture(
    {
      'src/existing.ts':
        "const redisModule = require('redis');\n\nexport const createClient = redisModule.createClient;\n",
    },
    async (root) => {
      const policy = {
        baselineDirectImports: ['src/existing.ts'],
        adapterPathPrefixes: [],
      };

      assert.deepEqual(await findViolations({ root, policy }), []);
    },
  );
});

test('allows direct imports in an adapter directory', async () => {
  await withFixture(
    {
      'src/adapters/redis-adapter.ts':
        "import 'connect-redis';\n\nexport const adapterName = 'redis';\n",
    },
    async (root) => {
      const policy = {
        baselineDirectImports: [],
        adapterPathPrefixes: ['src/adapters/'],
      };

      assert.deepEqual(await findViolations({ root, policy }), []);
    },
  );
});

test('detects every supported import form and sorts violations', async () => {
  await withFixture(
    {
      'src/z-dynamic.ts': "export const loadRedis = () => import('ioredis');\n",
      'src/a-static.ts': "import 'graphql-redis-subscriptions';\n",
      'src/m-from.ts':
        "export { redisInsStore } from 'cache-manager-redis-yet';\n",
      'src/n-require.cjs': "module.exports = require('connect-redis');\n",
    },
    async (root) => {
      const policy = {
        baselineDirectImports: [],
        adapterPathPrefixes: [],
      };

      assert.deepEqual(await findViolations({ root, policy }), [
        'src/a-static.ts -> graphql-redis-subscriptions',
        'src/m-from.ts -> cache-manager-redis-yet',
        'src/n-require.cjs -> connect-redis',
        'src/z-dynamic.ts -> ioredis',
      ]);
    },
  );
});

test('ignores unsupported extensions, skipped directories, and package subpaths', async () => {
  await withFixture(
    {
      'coverage/report.ts': "import Redis from 'ioredis';\n",
      'dist/generated.js': "require('redis');\n",
      'node_modules/example/index.js': "import { Queue } from 'bullmq';\n",
      'src/document.txt': "import { Queue } from 'bullmq';\n",
      'src/job-state.ts':
        "import type { JobState } from 'bullmq/dist/esm/types';\n\nexport type State = JobState;\n",
    },
    async (root) => {
      const policy = {
        baselineDirectImports: [],
        adapterPathPrefixes: [],
      };

      assert.deepEqual(await findViolations({ root, policy }), []);
    },
  );
});

test('limits scanning to policy source path prefixes', async () => {
  await withFixture(
    {
      'scripts/fixture.ts': "import { Queue } from 'bullmq';\n",
      'src/new-feature.ts': "import { Queue } from 'bullmq';\n",
    },
    async (root) => {
      const policy = {
        baselineDirectImports: [],
        adapterPathPrefixes: [],
        sourcePathPrefixes: ['src/'],
      };

      assert.deepEqual(await findViolations({ root, policy }), [
        'src/new-feature.ts -> bullmq',
      ]);
    },
  );
});

test('ignores restricted package text in comments and arbitrary strings', async () => {
  await withFixture(
    {
      'src/examples.ts': `
// import { Queue } from 'bullmq';
/*
 * require('redis');
 * import('ioredis');
 */
const examples = [
  "import 'connect-redis';",
  "export { redisInsStore } from 'cache-manager-redis-yet';",
  "require('graphql-redis-subscriptions');",
];
const loader = { require: (specifier) => specifier };
loader.require('bullmq');
export { examples, loader };
`,
    },
    async (root) => {
      const policy = {
        baselineDirectImports: [],
        adapterPathPrefixes: [],
      };

      assert.deepEqual(await findViolations({ root, policy }), []);
    },
  );
});

test('detects restricted imports when comments or options separate syntax tokens', async () => {
  await withFixture(
    {
      'src/a-side-effect.ts': "import/* comment */'redis';\n",
      'src/b-require.cjs':
        "module.exports = require(/* comment */ 'bullmq');\n",
      'src/c-dynamic.ts':
        "export const loadRedis = () => import(/* comment */ 'ioredis', { with: { type: 'json' } });\n",
      'src/d-from.ts':
        "import { createClient } /* comment */ from /* comment */ 'connect-redis';\nexport { createClient };\n",
      'src/e-export.ts':
        "export { redisInsStore } from /* comment */ 'cache-manager-redis-yet';\n",
    },
    async (root) => {
      const policy = {
        baselineDirectImports: [],
        adapterPathPrefixes: [],
      };

      assert.deepEqual(await findViolations({ root, policy }), [
        'src/a-side-effect.ts -> redis',
        'src/b-require.cjs -> bullmq',
        'src/c-dynamic.ts -> ioredis',
        'src/d-from.ts -> connect-redis',
        'src/e-export.ts -> cache-manager-redis-yet',
      ]);
    },
  );
});
