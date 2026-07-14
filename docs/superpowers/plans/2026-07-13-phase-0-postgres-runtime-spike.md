# Phase 0 PostgreSQL Runtime Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve an exact, updateable Twenty 2.21.0 baseline and prove or reject `pg-boss` as the PostgreSQL-backed replacement for Twenty's BullMQ queue contract, while keeping Redis/BullMQ compatibility mode intact.

**Architecture:** Phase 0 introduces a typed runtime-backend selector and a neutral asynchronous message-queue port. BullMQ remains the `redis` compatibility adapter; a new `pg-boss` adapter stores jobs in the same PostgreSQL server under an isolated `desktop_runtime` schema. A shared black-box contract suite runs against both adapters and produces the adoption verdict. This phase does not claim that the full server is Redis-free: sessions, cache/locks, realtime subscriptions, admin queue views, and AI streaming remain explicitly inventoried follow-on migrations.

**Tech Stack:** Twenty `2.21.0` at upstream commit `1b168ac1f7d466adf3be83b2676039e120d0db1c`, Node `24.16.0`, Yarn `4.13.0`, Nx `22.7.5`, TypeScript strict mode, NestJS, Jest `29.7`, PostgreSQL `18` in CI with PostgreSQL `16` as the Windows bundle target, Redis/BullMQ `5.78.0` compatibility adapter, `pg-boss` `12.26.0` spike adapter.

## Global Constraints

- The Windows product default is `postgres-desktop`; `redis` remains user-selectable and CI-supported.
- The canonical CRM and runtime database remains PostgreSQL. Do not replace Twenty's PostgreSQL schemas, TypeORM entities, metadata model, or migrations.
- The `pg-boss` objects live only in PostgreSQL schema `desktop_runtime`; do not rely on mutable `search_path`.
- The queue delivery contract is at-least-once. External side effects require receipts/outbox handling in later domain phases even when a queue library advertises exactly-once delivery.
- Required queue semantics: immediate and delayed delivery, priority, exact retry count, per-queue concurrency, cron and interval schedules, schedule upsert/removal, waiting-only deduplication, stalled-job reclaim, worker-restart recovery, bounded shutdown drain, abort signal where supported, retention, health/metrics, inspection, retry, and deletion.
- Passing the queue spike is not evidence that Twenty can boot without Redis. Phase 0 CI's PostgreSQL-only job exercises only the neutral queue contract.
- Preserve all upstream behavior in `redis` mode and reject new direct Redis/BullMQ imports outside compatibility adapters or the frozen baseline allowlist.
- Never commit credentials, generated PostgreSQL data, Redis data, `.superpowers/`, `node_modules/`, test result JSON, or local model files.
- Follow repository conventions: named exports, no `any`, string-literal unions instead of new enums, strict TypeScript, colocated focused tests, Yarn/Nx commands.
- On this Windows host, prepend `C:\Users\sdokd\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin` to `PATH`, then invoke Yarn as `node .yarn/releases/yarn-4.13.0.cjs` because Node/Yarn are not initially on the interactive shell `PATH`.

---

### Task 1: Import and Prove the Upstream Twenty Baseline

**Files:**
- Modify: `.gitignore`
- Create: `.twenty-upstream.json`
- Create: `scripts/verify-upstream-pin.mjs`
- Create: `docs/upstream-maintenance.md`
- Import unchanged: all currently untracked upstream Twenty source files

**Interfaces:**
- Consumes: local untracked Twenty snapshot and Git object `1b168ac1f7d466adf3be83b2676039e120d0db1c` from `https://github.com/twentyhq/twenty.git`.
- Produces: `node scripts/verify-upstream-pin.mjs --upstream-root $env:TEMP\twenty-1b168ac1`; exit `0` means every upstream path exists locally with the same SHA-256 before fork changes.

- [ ] **Step 1: Record the pre-import state and configure the bundled Node runtime**

Run:

```powershell
git status --short
git remote get-url origin
git rev-parse --abbrev-ref HEAD
$env:PATH = 'C:\Users\sdokd\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin;' + $env:PATH
node --version
```

Expected: the starting branch is reported; origin is `https://github.com/Howard-Starfield/eBay_CRM.git`; the Twenty source is untracked; the two approved design documents are tracked; Node reports `v24.x`. Do not edit any upstream file, including `.gitignore`, before Step 5 passes.

- [ ] **Step 2: Write the failing verifier test fixture**

Create temporary fixture directories and run the not-yet-created verifier:

```powershell
$fixture = Join-Path $env:TEMP 'ebay-crm-upstream-pin-test'
Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $fixture 'upstream') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $fixture 'local') | Out-Null
Set-Content -LiteralPath (Join-Path $fixture 'upstream\same.txt') -Value 'same' -NoNewline
Set-Content -LiteralPath (Join-Path $fixture 'local\same.txt') -Value 'different' -NoNewline
node scripts/verify-upstream-pin.mjs --upstream-root (Join-Path $fixture 'upstream') --local-root (Join-Path $fixture 'local')
```

Expected: FAIL because `scripts/verify-upstream-pin.mjs` does not exist.

- [ ] **Step 3: Implement the deterministic tree verifier**

Create `scripts/verify-upstream-pin.mjs` with this public behavior:

```javascript
import { createHash } from 'node:crypto';
import { readdir, readFile } from 'node:fs/promises';
import { resolve, relative } from 'node:path';

const args = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  args.set(process.argv[index], process.argv[index + 1]);
}

const upstreamRoot = resolve(args.get('--upstream-root') ?? '');
const localRoot = resolve(args.get('--local-root') ?? process.cwd());
if (!args.has('--upstream-root')) {
  throw new Error('Usage: node scripts/verify-upstream-pin.mjs --upstream-root C:\\path\\to\\upstream [--local-root C:\\path\\to\\local]');
}

const walk = async (root, directory = root) => {
  const entries = await readdir(directory, { withFileTypes: true });
  const paths = [];
  for (const entry of entries.sort((left, right) => left.name.localeCompare(right.name))) {
    if (entry.name === '.git') continue;
    const absolutePath = resolve(directory, entry.name);
    if (entry.isDirectory()) paths.push(...await walk(root, absolutePath));
    else if (entry.isFile()) paths.push(relative(root, absolutePath).replaceAll('\\', '/'));
  }
  return paths;
};

const sha256 = async (path) => createHash('sha256').update(await readFile(path)).digest('hex');
const failures = [];
for (const path of await walk(upstreamRoot)) {
  try {
    const [upstreamHash, localHash] = await Promise.all([
      sha256(resolve(upstreamRoot, path)),
      sha256(resolve(localRoot, path)),
    ]);
    if (upstreamHash !== localHash) failures.push(`modified: ${path}`);
  } catch {
    failures.push(`missing: ${path}`);
  }
}

if (failures.length > 0) {
  process.stderr.write(`${failures.join('\n')}\n`);
  process.exitCode = 1;
} else {
  process.stdout.write('Upstream tree matches local snapshot.\n');
}
```

Do not make the verifier reject extra fork-owned files; it proves that the imported upstream tree is present and unchanged at the moment of import.

- [ ] **Step 4: Prove the verifier detects drift and accepts an exact copy**

Run the fixture command from Step 2.

Expected: FAIL with `modified: same.txt`.

Then run:

```powershell
Copy-Item -LiteralPath (Join-Path $fixture 'upstream\same.txt') -Destination (Join-Path $fixture 'local\same.txt') -Force
node scripts/verify-upstream-pin.mjs --upstream-root (Join-Path $fixture 'upstream') --local-root (Join-Path $fixture 'local')
```

Expected: PASS with `Upstream tree matches local snapshot.`

- [ ] **Step 5: Fetch the exact upstream object and verify the complete source tree before editing it**

Run:

```powershell
if (-not (git remote | Select-String -SimpleMatch 'upstream')) { git remote add upstream https://github.com/twentyhq/twenty.git }
git fetch upstream 1b168ac1f7d466adf3be83b2676039e120d0db1c --depth=1
$upstreamTree = Join-Path $env:TEMP 'twenty-1b168ac1'
Remove-Item -LiteralPath $upstreamTree -Recurse -Force -ErrorAction SilentlyContinue
git worktree add --detach $upstreamTree 1b168ac1f7d466adf3be83b2676039e120d0db1c
node scripts/verify-upstream-pin.mjs --upstream-root $upstreamTree --local-root .
```

Expected: PASS. If it fails, stop; restore the listed local paths from the pinned worktree and rerun before any source modification.

- [ ] **Step 6: Protect local worker files, then write provenance and update instructions**

Only after the complete-tree verification passes, append this exact ignore entry to `.gitignore`:

```gitignore
# Local agent orchestration state
.superpowers/
```

Create `.twenty-upstream.json`:

```json
{
  "repository": "https://github.com/twentyhq/twenty.git",
  "commit": "1b168ac1f7d466adf3be83b2676039e120d0db1c",
  "version": "2.21.0",
  "importedAt": "2026-07-13",
  "verificationCommand": "node scripts/verify-upstream-pin.mjs --upstream-root %TEMP%\\twenty-upstream --local-root ."
}
```

Create `docs/upstream-maintenance.md` with the exact update sequence: fetch the candidate stable release into a detached worktree, read release notes, run the verifier before fork patches, merge rather than copy individual files, run both runtime contract jobs, run server unit/integration tests, review the fork-boundary diff, and update `.twenty-upstream.json` only after all checks pass. State that stable tagged releases are preferred over tracking `main`.

- [ ] **Step 7: Import and commit the clean baseline separately**

Run:

```powershell
git add .gitignore .twenty-upstream.json scripts/verify-upstream-pin.mjs docs/upstream-maintenance.md
git add --all -- ':!.superpowers'
git status --short
git commit -m "chore: import Twenty 2.21.0 baseline"
git worktree remove $upstreamTree
```

Expected: the commit contains the previously untracked Twenty source plus the provenance files, never `.superpowers/`. Record the commit SHA in the Phase 0 report created in Task 7.

- [ ] **Step 8: Install the immutable workspace dependency baseline**

Run:

```powershell
node .yarn/releases/yarn-4.13.0.cjs install --immutable
```

Expected: PASS without modifying `yarn.lock`. If the immutable install fails because the imported snapshot and lockfile disagree, stop and report the exact Yarn error; do not regenerate the lockfile during baseline setup.

---

### Task 2: Freeze Runtime Couplings and Enforce Fork Boundaries

**Files:**
- Create: `docs/architecture/runtime-backend-inventory.md`
- Create: `docs/architecture/fork-boundaries.md`
- Create: `packages/twenty-server/runtime-backend-boundaries.json`
- Create: `scripts/check-runtime-backend-boundaries.mjs`
- Create: `scripts/check-runtime-backend-boundaries.test.mjs`
- Modify: `package.json`

**Interfaces:**
- Consumes: current direct imports of `bullmq`, `ioredis`, `redis`, `connect-redis`, `cache-manager-redis-yet`, and `graphql-redis-subscriptions`.
- Produces: `node scripts/check-runtime-backend-boundaries.mjs`; new occurrences fail unless their normalized path is in the frozen baseline or an adapter-owned path.

- [ ] **Step 1: Capture the exact baseline import set**

Run:

```powershell
rg -l "from ['\"](bullmq|ioredis|redis|connect-redis|cache-manager-redis-yet|graphql-redis-subscriptions)['\"]|require\(['\"](bullmq|ioredis|redis|connect-redis|cache-manager-redis-yet|graphql-redis-subscriptions)['\"]\)" packages/twenty-server/src | Sort-Object
```

Store the normalized `/`-separated paths in `packages/twenty-server/runtime-backend-boundaries.json` under `baselineDirectImports`. Set `adapterPathPrefixes` to:

```json
[
  "packages/twenty-server/src/engine/core-modules/message-queue/drivers/",
  "packages/twenty-server/src/engine/core-modules/redis-client/",
  "packages/twenty-server/src/engine/core-modules/cache-storage/drivers/",
  "packages/twenty-server/src/engine/core-modules/session-storage/drivers/",
  "packages/twenty-server/src/engine/subscriptions/drivers/"
]
```

- [ ] **Step 2: Write failing boundary tests**

Create `scripts/check-runtime-backend-boundaries.test.mjs` using `node:test`. Export `findViolations({ root, policy })` from the checker and test all three cases:

```javascript
test('rejects a new direct bullmq import outside adapters', async () => {
  assert.deepEqual(await findViolations({ root, policy }), ['src/new-feature.ts -> bullmq']);
});

test('allows a frozen upstream direct import', async () => {
  policy.baselineDirectImports = ['src/existing.ts'];
  assert.deepEqual(await findViolations({ root, policy }), []);
});

test('allows direct imports in an adapter directory', async () => {
  policy.adapterPathPrefixes = ['src/adapters/'];
  assert.deepEqual(await findViolations({ root, policy }), []);
});
```

Each test creates and removes its own `mkdtemp` fixture and writes the complete fixture TypeScript source.

- [ ] **Step 3: Run tests to verify failure**

Run:

```powershell
node --test scripts/check-runtime-backend-boundaries.test.mjs
```

Expected: FAIL because the checker module does not exist.

- [ ] **Step 4: Implement the boundary scanner**

Create `scripts/check-runtime-backend-boundaries.mjs` with:

```javascript
export const restrictedPackages = new Set([
  'bullmq', 'ioredis', 'redis', 'connect-redis',
  'cache-manager-redis-yet', 'graphql-redis-subscriptions',
]);
```

Walk only `.ts`, `.tsx`, `.js`, `.mjs`, and `.cjs` files, skip `.git`, `.yarn`, `node_modules`, `dist`, and coverage directories, parse static `from`, side-effect `import`, dynamic `import()`, and `require()` package specifiers, normalize paths with `/`, and return sorted strings such as `src/new-feature.ts -> bullmq`. The CLI loads `packages/twenty-server/runtime-backend-boundaries.json`, prints violations to stderr, and exits `1` if any exist.

- [ ] **Step 5: Verify the unit tests and the real tree**

Run:

```powershell
node --test scripts/check-runtime-backend-boundaries.test.mjs
node scripts/check-runtime-backend-boundaries.mjs
```

Expected: both PASS; the real-tree command prints `Runtime backend boundaries passed.`

- [ ] **Step 6: Document the migration inventory and ownership rules**

In `docs/architecture/runtime-backend-inventory.md`, include exact sections and counts for queue/processors, sessions, cache API, lease locks, import staging sets, GraphQL subscriptions, AI stream chunks/heartbeats/cancel, admin health, and integration utilities. Mark each row `Phase 0 queue spike`, `Later runtime phase`, or `Compatibility-only`.

In `docs/architecture/fork-boundaries.md`, define these ownership rules:

- domain code imports neutral ports only;
- Redis/BullMQ and PostgreSQL implementations stay under adapter paths;
- `redis` behavior is preserved;
- `postgres-desktop` is the product default only after all required runtime ports pass full boot tests;
- generated Twenty metadata schemas are untouched;
- desktop packaging code is a new package in Phase 1 and must not repurpose `packages/twenty-companion`.

Add root package script:

```json
"check:runtime-boundaries": "node scripts/check-runtime-backend-boundaries.mjs"
```

- [ ] **Step 7: Commit the guardrail**

```powershell
git add docs/architecture packages/twenty-server/runtime-backend-boundaries.json scripts/check-runtime-backend-boundaries.mjs scripts/check-runtime-backend-boundaries.test.mjs package.json
git commit -m "chore: freeze runtime backend boundaries"
```

---

### Task 3: Define the Typed Runtime Selector and Asynchronous Queue Port

**Files:**
- Create: `packages/twenty-server/src/engine/core-modules/runtime-backend/runtime-backend.constants.ts`
- Create: `packages/twenty-server/src/engine/core-modules/runtime-backend/runtime-backend.spec.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/twenty-config/config-variables.ts`
- Modify: `packages/twenty-server/.env.example`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/interfaces/message-queue-driver.interface.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/services/message-queue.service.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/message-queue.explorer.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/bullmq.driver.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/sync.driver.ts`
- Test: existing affected unit tests plus new runtime-backend spec

**Interfaces:**
- Produces: `RUNTIME_BACKENDS`, `RuntimeBackend`, and `isRuntimeBackend(value): value is RuntimeBackend`.
- Changes: `MessageQueueDriver.work(...): Promise<void>` and `MessageQueueService.work(...): Promise<void>`.

- [ ] **Step 1: Write failing selector tests**

Create tests asserting:

```typescript
expect(RUNTIME_BACKENDS).toEqual({
  POSTGRES_DESKTOP: 'postgres-desktop',
  REDIS: 'redis',
});
expect(isRuntimeBackend('postgres-desktop')).toBe(true);
expect(isRuntimeBackend('redis')).toBe(true);
expect(isRuntimeBackend('sqlite')).toBe(false);
expect(new ConfigVariables().RUNTIME_BACKEND).toBe('postgres-desktop');
```

- [ ] **Step 2: Verify selector tests fail**

Run:

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern=runtime-backend.spec.ts --runInBand
```

Expected: FAIL because the constants and config field do not exist.

- [ ] **Step 3: Implement the selector without a new enum**

Create:

```typescript
export const RUNTIME_BACKENDS = {
  POSTGRES_DESKTOP: 'postgres-desktop',
  REDIS: 'redis',
} as const;

export type RuntimeBackend =
  (typeof RUNTIME_BACKENDS)[keyof typeof RUNTIME_BACKENDS];

export const isRuntimeBackend = (value: unknown): value is RuntimeBackend =>
  typeof value === 'string' &&
  Object.values(RUNTIME_BACKENDS).includes(value as RuntimeBackend);
```

Add an env-only, admin-hidden config variable validated with `@IsIn(Object.values(RUNTIME_BACKENDS))`:

```typescript
RUNTIME_BACKEND: RuntimeBackend = RUNTIME_BACKENDS.POSTGRES_DESKTOP;
```

Add to `packages/twenty-server/.env.example`:

```dotenv
# postgres-desktop (default product mode) or redis
RUNTIME_BACKEND=postgres-desktop
```

- [ ] **Step 4: Make queue worker registration awaitable**

Change the port to:

```typescript
work<T extends MessageQueueJobData>(
  queueName: MessageQueue,
  handler: ({ data, id }: { data: T; id: string }) => Promise<void> | void,
  options?: MessageQueueWorkerOptions,
): Promise<void>;
```

Return the driver promise from `MessageQueueService.work`. Make `MessageQueueExplorer.onModuleInit` and `explore` asynchronous, collect each `handleProcessorGroupCollection` promise, and `await Promise.all(...)`. Make the BullMQ and Sync `work` methods `async` and return after worker registration; do not change handler behavior.

- [ ] **Step 5: Run focused tests and typecheck**

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='runtime-backend.spec.ts|message-queue' --runInBand
node .yarn/releases/yarn-4.13.0.cjs nx typecheck twenty-server
```

Expected: PASS with no implicit floating queue-worker registration promise.

- [ ] **Step 6: Commit the neutral contract change**

```powershell
git add packages/twenty-server/src/engine/core-modules/runtime-backend packages/twenty-server/src/engine/core-modules/twenty-config/config-variables.ts packages/twenty-server/.env.example packages/twenty-server/src/engine/core-modules/message-queue
git commit -m "refactor: define asynchronous queue runtime port"
```

---

### Task 4: Build the Shared Queue Contract Harness Against BullMQ

**Files:**
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/testing/message-queue-driver-test-harness.ts`
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/testing/message-queue-driver.contract.ts`
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/bullmq.driver.contract-spec.ts`
- Create: `packages/twenty-server/jest-runtime-contract.config.mjs`
- Modify: `packages/twenty-server/project.json`

**Interfaces:**
- Produces: `MessageQueueDriverTestHarness` with `driver`, `start()`, `stop()`, `clear()`, `waitFor(predicate, timeoutMs)`, `restartWorker()`, and neutral inspection operations.
- Produces: `defineMessageQueueDriverContract(name, createHarness)` reused unchanged by both adapters.

- [ ] **Step 1: Define failing black-box cases**

Write contract cases with deterministic unique queue/job IDs and bounded waits for:

1. immediate job delivery and job-name/data preservation;
2. delay not early and eventual delivery;
3. lower numeric priority processes before higher numeric priority when both are waiting, matching Twenty's existing priority table;
4. `retryLimit: 2` yields exactly three attempts;
5. configured concurrency never exceeds the limit;
6. `id` suppresses a matching ready-but-not-active job, including BullMQ's prioritized state, and permits another after the first is active;
7. cron pattern schedule upsert and removal;
8. interval schedule upsert, `limit`, and removal;
9. a killed/restarted worker reclaims a non-acknowledged job;
10. bounded shutdown aborts a long handler and returns within the configured timeout;
11. completed and failed jobs remain inspectable for the configured retention window;
12. health/stats report queue depth and active/failed counts;
13. failed jobs can be retried and waiting jobs can be deleted;
14. a worker stopped before claim leaves the job available;
15. a worker terminated after claim/mid-handler causes lease-based recovery without job loss;
16. a handler killed after a PostgreSQL write but before acknowledgement demonstrates at-least-once re-entry while a unique receipt prevents duplicate durable state;
17. a simulated external side effect recorded through an idempotency receipt is not repeated after termination-before-acknowledgement.

The suite must not import BullMQ or pg-boss types. Each test calls only the neutral port/harness.

- [ ] **Step 2: Add the dedicated Jest target and observe a red contract**

`jest-runtime-contract.config.mjs` extends the server Jest config, matches only `*.driver.contract-spec.ts`, sets `maxWorkers: 1`, and uses a `60_000` ms timeout. Add Nx target:

```json
"test-runtime-contract": {
  "executor": "nx:run-commands",
  "options": {
    "cwd": "packages/twenty-server",
    "command": "node --experimental-vm-modules ../../node_modules/jest/bin/jest.js --config jest-runtime-contract.config.mjs --runInBand"
  }
}
```

Run:

```powershell
$env:RUNTIME_CONTRACT_DRIVER='bullmq'
node .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:test-runtime-contract
```

Expected: FAIL because the harness and any newly required neutral inspection methods are not implemented.

- [ ] **Step 3: Extend the neutral port only as required by the tests**

Add focused types:

```typescript
export type MessageQueueJobState =
  | 'created' | 'active' | 'completed' | 'failed' | 'retry';

export type MessageQueueStats = {
  queueName: MessageQueue;
  created: number;
  active: number;
  completed: number;
  failed: number;
  retry: number;
  healthy: boolean;
};
```

Add neutral driver methods `getStats(queueName)`, `findJobs(queueName, states)`, `retryJob(queueName, jobId)`, and `deleteJob(queueName, jobId)`. Put shared types in focused files under `drivers/interfaces/`. Adapt BullMQ using its queue getters; do not expose BullMQ job objects.

- [ ] **Step 4: Make the BullMQ adapter pass the shared suite**

Use the existing Redis connection and preserve its current semantics, including `attempts = 1 + retryLimit`, UUID-suffixed physical IDs, queue-specific concurrency, and bounded drain behavior. Correct the existing prioritized-job deduplication gap so the neutral ready-but-not-active contract is consistent for default-priority jobs. Add no production behavior beyond the neutral inspection surface.

- [ ] **Step 5: Run the BullMQ contract twice**

```powershell
$env:RUNTIME_CONTRACT_DRIVER='bullmq'
node .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:test-runtime-contract
node .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:test-runtime-contract
```

Expected: 17 contract cases PASS on both consecutive runs without leaked queues/workers.

- [ ] **Step 6: Commit the executable contract**

```powershell
git add packages/twenty-server/src/engine/core-modules/message-queue packages/twenty-server/jest-runtime-contract.config.mjs packages/twenty-server/project.json
git commit -m "test: codify message queue runtime contract"
```

---

### Task 5: Implement the `pg-boss` PostgreSQL Queue Adapter

**Files:**
- Modify: `packages/twenty-server/package.json`
- Modify: `yarn.lock`
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss.driver.ts`
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss.driver.spec.ts`
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss.driver.contract-spec.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/interfaces/message-queue-module-options.interface.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/message-queue-core.module.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/message-queue.module-factory.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/core-engine.module.ts`

**Interfaces:**
- Produces: `PgBossDriverOptions { connectionString: string; schema: 'desktop_runtime'; applicationName: string }`.
- Produces: `PgBossDriver implements MessageQueueDriver, OnModuleInit, OnModuleDestroy`.
- Envelope: `{ version: 1; jobName: string; logicalId?: string; data: MessageQueueJobData }`.

- [ ] **Step 1: Pin and install the spike dependency**

Run:

```powershell
node .yarn/releases/yarn-4.13.0.cjs workspace twenty-server add --exact pg-boss@12.26.0
```

Expected: `packages/twenty-server/package.json` contains exact `12.26.0` and Yarn updates the lockfile. Do not use a caret range.

- [ ] **Step 2: Write failing adapter unit tests**

Mock the `pg-boss` constructor and assert:

- schema is exactly `desktop_runtime`;
- `start()` is called once and registered queues are created;
- immediate mapping uses `priority` and exact `retryLimit`;
- delay milliseconds map to `startAfter` without early execution;
- logical IDs are stored in the versioned envelope;
- `work` maps `localConcurrency`, unwraps the envelope, and forwards `job.signal`;
- `stop` uses graceful bounded timeout for bounded queues;
- adapter lifecycle is idempotent.

Run:

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern=pg-boss.driver.spec.ts --runInBand
```

Expected: FAIL because the adapter does not exist.

- [ ] **Step 3: Implement lifecycle, immediate jobs, workers, and inspection**

Construct one PgBoss instance per driver. `register` records queue names before startup. `onModuleInit` starts the boss and calls `createQueue` for all registered queues. `add` creates the queue on demand, queries only queued jobs whose envelope contains the same `logicalId`, skips only when such a job exists, and sends the versioned envelope. Convert delay ms to an absolute `Date` so sub-second intent is not silently rounded. `work` calls `boss.work(queueName, { localConcurrency }, async ([job]) => ...)` and forwards `{ id, name: envelope.jobName, data: envelope.data, abortSignal: job.signal }`.

Implement inspection using `findJobs`, `getQueueStats`, `retry`, and `deleteJob`; translate pg-boss states into the neutral states in one private mapping function. Do not leak pg-boss types outside this file.

- [ ] **Step 4: Add module selection without constructing a Redis queue client in PostgreSQL mode**

Extend the module option union with:

```typescript
export type PgBossDriverFactoryOptions = {
  type: 'pg-boss';
  options: PgBossDriverOptions;
  metricsService: MetricsService;
  twentyConfigService: TwentyConfigService;
};
```

Update the module factory to branch on `RUNTIME_BACKEND`. For `postgres-desktop`, return pg-boss options from `PG_DATABASE_URL`; for `redis`, call `redisClientService.getQueueClient()`. Because `RedisClientModule` remains required by other Phase 0 subsystems, explicitly document that this removes only the queue adapter's use of Redis; it does not yet remove RedisClientModule from `CoreEngineModule`.

- [ ] **Step 5: Run unit tests and the PostgreSQL contract**

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern=pg-boss.driver.spec.ts --runInBand
$env:RUNTIME_CONTRACT_DRIVER='pg-boss'
$env:PG_DATABASE_URL='postgres://postgres:postgres@127.0.0.1:5432/default'
node .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:test-runtime-contract
```

Expected: unit tests PASS. The contract may still be red only for cron/interval and fault-injection cases assigned to Task 6; all immediate/delay/retry/concurrency/dedup/inspection cases must pass before proceeding.

- [ ] **Step 6: Commit the core adapter**

```powershell
git add packages/twenty-server/package.json yarn.lock packages/twenty-server/src/engine/core-modules/message-queue packages/twenty-server/src/engine/core-modules/core-engine.module.ts
git commit -m "feat: add PostgreSQL message queue adapter"
```

---

### Task 6: Close Scheduling, Recovery, and Shutdown Semantic Gaps

**Files:**
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-interval-scheduler.ts`
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-interval-scheduler.spec.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss.driver.ts`
- Modify: shared contract/harness files from Task 4 only when a library-neutral clarification is required

**Interfaces:**
- Produces: deterministic schedule key from existing `getJobKey(queueName, jobName, jobId)`.
- Produces: interval schedule controller with `upsert`, `remove`, `start`, and `stop` using persisted PostgreSQL state, not process-only timers.

- [ ] **Step 1: Prove schedule and crash gaps with the shared contract**

Run the pg-boss contract with only scheduling/recovery/shutdown cases enabled through Jest `--testNamePattern`.

Expected: any unsupported behavior is a visible failing test; do not weaken or skip a required case to make the adapter pass.

- [ ] **Step 2: Implement cron schedule upsert/removal**

Map `repeat.pattern` to `boss.schedule(queueName, pattern, envelope, { key: getJobKey(...) })`. A second call with the same key must update, not duplicate, the schedule. Map removal to `boss.unschedule(queueName, key)`. Map `repeat.limit` to a remaining-run value persisted with the schedule and stop enqueueing at zero.

- [ ] **Step 3: Implement persisted interval scheduling**

Do not emulate `repeat.every` with an in-memory `setInterval`. Create a migration-owned table in schema `desktop_runtime` through idempotent SQL executed by the adapter:

```sql
CREATE TABLE IF NOT EXISTS desktop_runtime.interval_schedule (
  schedule_key text PRIMARY KEY,
  queue_name text NOT NULL,
  job_name text NOT NULL,
  payload jsonb,
  every_ms integer NOT NULL CHECK (every_ms > 0),
  remaining_runs integer,
  next_run_at timestamptz NOT NULL,
  updated_at timestamptz NOT NULL DEFAULT now()
);
```

Claim due schedules with a transaction and `FOR UPDATE SKIP LOCKED`, advance `next_run_at` from its previous value to avoid drift, decrement `remaining_runs`, enqueue the versioned job envelope, and delete exhausted schedules. Use one cancellable polling loop with a 250 ms maximum wake-up during contract tests and configurable 1,000 ms production default. `removeCron` deletes the same deterministic schedule key from both pg-boss cron scheduling and this table.

- [ ] **Step 4: Implement stalled recovery and bounded drain**

Map `lockDuration` to pg-boss queue expiration/heartbeat options with `Math.ceil(milliseconds / 1000)` and document the one-second precision. Verify a worker terminated mid-handler makes the job available for the configured retry/recovery path. On shutdown, stop accepting new work, wait up to `AI_STREAM_SHUTDOWN_DRAIN_MS` for bounded queues, abort the handler through `AbortSignal`, then stop pg-boss gracefully. Never forcibly terminate PostgreSQL.

- [ ] **Step 5: Run the complete contract repeatedly**

```powershell
$env:RUNTIME_CONTRACT_DRIVER='pg-boss'
1..3 | ForEach-Object { node .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:test-runtime-contract; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
$env:RUNTIME_CONTRACT_DRIVER='bullmq'
node .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:test-runtime-contract
```

Expected: all 13 shared cases PASS on three PostgreSQL runs and one BullMQ regression run. Any flaky run is a failure.

- [ ] **Step 6: Commit completed semantics**

```powershell
git add packages/twenty-server/src/engine/core-modules/message-queue
git commit -m "feat: complete PostgreSQL queue semantics"
```

---

### Task 7: Add Dual-Backend CI and Record the Evidence-Based Verdict

**Files:**
- Create: `.github/workflows/ci-runtime-backends.yaml`
- Modify: `.github/workflows/ci-server.yaml`
- Create: `docs/architecture/phase-0-runtime-spike-report.md`
- Modify: `docs/superpowers/specs/2026-07-13-twenty-runtime-modes-design.md` only to link the report, not to rewrite approved requirements

**Interfaces:**
- Produces: two independent CI jobs, `queue-contract-redis` and `queue-contract-postgres-desktop`.
- Produces: Phase 0 decision `ADOPT_PG_BOSS` or `REJECT_PG_BOSS` from mandatory automated evidence.

- [ ] **Step 1: Add separate CI jobs rather than a service matrix**

Both jobs check out the pinned fork, use Node `24.16.0`, run the repository Yarn install action, run `yarn check:runtime-boundaries`, and run the same Nx contract target.

The compatibility job starts PostgreSQL `18` and Redis, sets `RUNTIME_BACKEND=redis`, and sets `RUNTIME_CONTRACT_DRIVER=bullmq`.

The desktop job starts PostgreSQL `18` only, sets no `REDIS_URL`, sets `RUNTIME_BACKEND=postgres-desktop`, and sets `RUNTIME_CONTRACT_DRIVER=pg-boss`. Add a pre-test PowerShell/Bash-equivalent socket check that fails if the job runner has a listener on port `6379`; the test must not silently reach a host Redis instance.

Keep the services and test commands in `.github/workflows/ci-server.yaml` unchanged because the full server still has non-queue Redis dependencies, but add `RUNTIME_BACKEND=redis` explicitly so upstream regression CI stays on BullMQ while the product default is `postgres-desktop`.

- [ ] **Step 2: Run all local non-service verification**

```powershell
node scripts/verify-upstream-pin.mjs --upstream-root $env:TEMP\twenty-1b168ac1 --local-root .
node --test scripts/check-runtime-backend-boundaries.test.mjs
node .yarn/releases/yarn-4.13.0.cjs check:runtime-boundaries
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='runtime-backend|message-queue|pg-boss' --runInBand
node .yarn/releases/yarn-4.13.0.cjs nx typecheck twenty-server
```

Expected: the unit/type/boundary checks PASS. The upstream verifier is expected to report intentional fork changes after the import commit; set `$baselineImportCommit = git log --format='%H' --grep='^chore: import Twenty 2.21.0 baseline$' -1` and run `git diff $baselineImportCommit --name-only` to distinguish them. The report must state this distinction explicitly rather than claiming the post-change tree still matches upstream.

- [ ] **Step 3: Run both service-backed contracts and capture exact output**

Run the BullMQ contract once and pg-boss contract three times as in Task 6. Save Jest JSON outside the repository or under an ignored temporary directory. Record test counts, duration, PostgreSQL version, Redis version, Node version, and commit SHA.

- [ ] **Step 4: Write the spike report with a mechanical decision rule**

`docs/architecture/phase-0-runtime-spike-report.md` must contain:

- pinned upstream and baseline-import commit;
- inventory counts and deferred Redis surfaces;
- one row per mandatory semantic case for each adapter;
- observed failures and fixes without deleting failed-history notes;
- three-run flake result for pg-boss;
- CI workflow/job names;
- adoption rule: `ADOPT_PG_BOSS` only if all mandatory pg-boss cases pass three consecutive runs, BullMQ regression passes, TypeScript and unit tests pass, and the PostgreSQL-only job uses no Redis service;
- rejection rule: otherwise `REJECT_PG_BOSS` and plan a native PostgreSQL queue adapter behind the same port;
- explicit statement: “This verdict covers the message queue only. It does not certify a Redis-free Twenty server.”

Do not write the decision until the commands have actually run. Use the exact uppercase decision token.

- [ ] **Step 5: Independent review gate**

Dispatch a fresh reviewer with the approved runtime spec, this plan, the complete Phase 0 diff, and test output. Require two passes:

1. spec compliance: every Phase 0 acceptance criterion and every queue semantic is supported by evidence;
2. code quality: no adapter leakage, no hidden Redis dependency in the PostgreSQL contract job, safe shutdown, deterministic cleanup, and maintainable upstream boundaries.

Resolve every high/medium finding and rerun affected tests before proceeding.

- [ ] **Step 6: Final verification and commit**

```powershell
git status --short
node .yarn/releases/yarn-4.13.0.cjs check:runtime-boundaries
node .yarn/releases/yarn-4.13.0.cjs nx typecheck twenty-server
git diff --check
git add .github/workflows/ci-runtime-backends.yaml .github/workflows/ci-server.yaml docs/architecture/phase-0-runtime-spike-report.md docs/superpowers/specs/2026-07-13-twenty-runtime-modes-design.md
git commit -m "ci: verify PostgreSQL and Redis queue runtimes"
git status --short
```

Expected: verification commands PASS; the final status contains only intentionally ignored/local files; Phase 0 report includes one evidence-backed decision token.

---

## Phase 0 Exit Criteria

- The complete upstream Twenty baseline is committed separately and tied to exact commit `1b168ac1f7d466adf3be83b2676039e120d0db1c` / version `2.21.0`.
- Fork-boundary automation rejects new direct Redis/BullMQ coupling.
- The asynchronous neutral queue contract is shared by BullMQ and pg-boss.
- BullMQ compatibility semantics remain green.
- pg-boss receives an `ADOPT_PG_BOSS` verdict only after all mandatory contract cases pass three consecutive runs with PostgreSQL and no Redis service.
- Failure produces a documented `REJECT_PG_BOSS` verdict without weakening the neutral contract.
- The report clearly scopes the result to queues and carries sessions, caches/locks, realtime, AI streaming, admin health, Windows process supervision, backups, and eBay work into later phases.
