# Phase 0B Logical Queue Ledger Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove or reject a PostgreSQL logical job ledger that uses pg-boss only for physical delivery while preserving independent handler-failure and stalled-worker recovery semantics.

**Architecture:** Add an opt-in logical overlay to the existing pg-boss driver without changing the reviewed direct-adapter rejection mode. Three owned PostgreSQL tables hold queue policy, canonical logical jobs, and attempt receipts; public pg-boss transaction adapters atomically compose physical send/complete operations with logical transitions.

**Tech Stack:** TypeScript, NestJS-compatible driver classes, PostgreSQL 16/18 SQL, node-postgres 8.12, pg-boss 12.26, Jest 29, uuid 11.

## Global Constraints

- Base commit is `0c7180bb0605e1c01fe81d2a8ec7b891bb208232`; preserve the Phase 0 `REJECT_PG_BOSS` evidence and diagnostic CI.
- The overlay is opt-in and must not become the default runtime during this spike.
- Use only public pg-boss APIs and the documented `Db.executeSql` transaction adapter.
- Never read or mutate private pg-boss tables from overlay code.
- Canonical status, counters, IDs, and receipts live in `desktop_runtime` ledger tables.
- The handler receives a stable logical UUID; physical pg-boss UUIDs are internal.
- Handler failures and stall recoveries use independent persisted counters.
- Every canonical start and settlement is fenced by logical ID, generation, and execution token.
- Run only the six approved PostgreSQL acceptance cases twice, BullMQ compatibility once, and focused static checks.
- Stop and record `REJECT_LOGICAL_LEDGER_OVERLAY` immediately when an approved rejection condition is proven.
- Do not weaken, skip, invert, or rename an acceptance case to obtain adoption.
- Run direct Jest commands from `packages/twenty-server`; run Nx, boundary, and Git commands from the repository root.

---

### Task 1: Add Ledger Schema, Queue Policy, and Deterministic Identity

**Files:**
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger.types.ts`
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger.ts`
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger.spec.ts`

**Interfaces:**
- Produces: `PgBossLogicalLedger`, `LogicalPgBossEnvelope`, `LogicalQueuePolicy`, `LogicalJobStart`, and `LogicalTransport`.
- Produces: `initialize()`, `registerQueuePolicy()`, `createJob()`, and `physicalJobId()` for later tasks.
- Consumes: a `pg.Pool`, schema literal `desktop_runtime`, and public pg-boss callbacks supplied by the driver.

Use these exact public signatures:

```ts
initialize(): Promise<void>;
registerQueuePolicy(
  queueName: MessageQueue,
  options: MessageQueueWorkerOptions,
): Promise<LogicalQueuePolicy>;
createJob<T extends MessageQueueJobData>(args: {
  queueName: MessageQueue;
  jobName: string;
  data: T;
  options?: QueueJobOptions;
}): Promise<string | null>;
physicalJobId(logicalJobId: string, generation: number): string;
```

`createJob()` returns the new logical UUID, or `null` when the partial unique
index rejects a duplicate waiting job. The driver continues to expose
`Promise<void>` and ignores the returned UUID.

- [ ] **Step 1: Write failing schema and policy tests**

Add tests that use a mocked `PoolClient` to require all three idempotent tables and the explicit default stall policy:

```ts
it('creates the queue policy, logical job, and attempt tables', async () => {
  await ledger.initialize();

  expect(executedSql).toEqual(
    expect.arrayContaining([
      expect.stringContaining('CREATE TABLE IF NOT EXISTS desktop_runtime.queue_policy'),
      expect.stringContaining('CREATE TABLE IF NOT EXISTS desktop_runtime.queue_job'),
      expect.stringContaining('CREATE TABLE IF NOT EXISTS desktop_runtime.queue_job_attempt'),
      expect.stringContaining('queue_job_waiting_dedup_key_idx'),
    ]),
  );
});

it('materializes one stalled recovery when worker options omit it', async () => {
  await ledger.registerQueuePolicy(queueName, {});

  expect(lastQuery).toMatchObject({
    values: [queueName, 1, null],
  });
});
```

- [ ] **Step 2: Run the tests and observe RED**

Run:

```powershell
node ../../node_modules/jest/bin/jest.js --config jest.config.mjs --runInBand --runTestsByPath src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger.spec.ts
```

Expected: FAIL because the ledger module does not exist.

- [ ] **Step 3: Define exact types and schema initialization**

Define:

```ts
export type LogicalPgBossEnvelope = {
  version: 2;
  logicalJobId: string;
  generation: number;
};

export type LogicalQueuePolicy = {
  stallRecoveryLimit: number;
  heartbeatSeconds?: number;
};

export type LogicalTransport = {
  send: (args: {
    queueName: MessageQueue;
    envelope: LogicalPgBossEnvelope;
    physicalJobId: string;
    stallRecoveryLimit: number;
    priority: number;
    availableAt: Date;
    db: PgBossDatabase;
  }) => Promise<void>;
  complete: (args: {
    queueName: MessageQueue;
    physicalJobId: string;
    db: PgBossDatabase;
  }) => Promise<void>;
};
```

Use one `PoolClient` transaction and execute the complete SQL from the approved spec. Add checks for non-negative counters/limits, the six allowed job statuses, the six allowed attempt outcomes, the attempt foreign key, unique `(job_id, execution_token)`, and the partial waiting deduplication index.

- [ ] **Step 4: Write failing deterministic identity and policy-upsert tests**

```ts
it('derives a stable unique physical UUID for each generation', () => {
  expect(ledger.physicalJobId(logicalId, 0)).toBe(
    ledger.physicalJobId(logicalId, 0),
  );
  expect(ledger.physicalJobId(logicalId, 1)).not.toBe(
    ledger.physicalJobId(logicalId, 0),
  );
});

it('persists explicit worker recovery policy before work starts', async () => {
  await ledger.registerQueuePolicy(queueName, {
    maxStalledCount: 2,
    lockDuration: 10_000,
  });

  expect(lastQuery.values).toEqual([queueName, 2, 10]);
});
```

- [ ] **Step 5: Implement identity and policy upsert**

Use `v4()` for each logical job and UUIDv5 with a fixed source constant for physical IDs:

```ts
const PHYSICAL_JOB_NAMESPACE = '47c50f4e-5f71-5f5d-a93c-8040c7f65296';

physicalJobId(logicalJobId: string, generation: number): string {
  return v5(`${logicalJobId}:${generation}`, PHYSICAL_JOB_NAMESPACE);
}
```

`registerQueuePolicy()` stores `maxStalledCount ?? 1` and converts a supplied
`lockDuration` to `Math.max(10, Math.ceil(lockDuration / 1_000))` heartbeat
seconds. Reject negative/non-integer limits before SQL.

- [ ] **Step 6: Implement transactional logical job creation**

`createJob()` must:

1. begin a transaction;
2. read `queue_policy`, inserting the explicit default when absent;
3. create a random logical UUID and deterministic generation-zero physical UUID;
4. insert the canonical row with handler retry limit from `QueueJobOptions.retryLimit ?? 0`;
5. call `transport.send()` with the same `Db` wrapper and only the stall limit as physical `retryLimit`;
6. commit, or roll back both rows and envelope on failure.

For `options.id`, rely on the partial unique index and treat PostgreSQL code
`23505` for the waiting deduplication index as a successful no-op. Do not query
then insert.

- [ ] **Step 7: Run focused tests and commit**

Expected: all ledger schema, policy, identity, transaction, and deduplication tests PASS.

```powershell
git add packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger*
git commit -m "feat: add PostgreSQL logical queue ledger"
```

---

### Task 2: Implement Fenced Starts and Atomic Settlement

**Files:**
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger.types.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger.spec.ts`

**Interfaces:**
- Consumes: Task 1 ledger and transport.
- Produces: `startAttempt()`, `settleSuccess()`, `settleFailure()`, `completeFencedEnvelope()`, `getStats()`, `findJobs()`, and `reconcileDeadLetter()`.
- Produces discriminated start result `{ kind: 'execute'; job; executionToken } | { kind: 'fenced' } | { kind: 'stall-exhausted' }`.

Use these exact lifecycle signatures:

```ts
startAttempt(args: LogicalStartArgs): Promise<LogicalJobStart>;
settleSuccess(args: LogicalSettlementArgs): Promise<'settled' | 'fenced'>;
settleFailure(
  args: LogicalSettlementArgs,
  error: unknown,
): Promise<'retried' | 'failed' | 'fenced'>;
completeFencedEnvelope(args: LogicalPhysicalArgs): Promise<void>;
getStats(queueName: MessageQueue): Promise<MessageQueueStats>;
findJobs(
  queueName: MessageQueue,
  states: MessageQueueJobState[],
): Promise<MessageQueueJobRecord[]>;
reconcileDeadLetter(args: LogicalDeadLetterArgs): Promise<'failed' | 'fenced'>;
```

- [ ] **Step 1: Write failing stale-generation and start-accounting tests**

```ts
it('fences a physical generation that is no longer current', async () => {
  const result = await ledger.startAttempt({
    queueName,
    physicalJobId: stalePhysicalId,
    envelope: { version: 2, logicalJobId, generation: 0 },
    transportRetryCount: 0,
    workerInstanceId,
  });

  expect(result).toEqual({ kind: 'fenced' });
  expect(attemptInsertCount).toBe(0);
});

it('accounts each transport retry delta once and creates a fenced attempt', async () => {
  const result = await ledger.startAttempt({
    queueName,
    physicalJobId,
    envelope,
    transportRetryCount: 1,
    workerInstanceId,
  });

  expect(result).toMatchObject({ kind: 'execute', logicalJobId });
  expect(updatedJob).toMatchObject({ stallCount: 1, startedCount: 1 });
  expect(insertedAttempt.executionToken).toBe(result.executionToken);
});
```

- [ ] **Step 2: Run RED, then implement `startAttempt()`**

Use `SELECT id, generation, current_physical_job_id, status, stall_count, stall_recovery_limit, transport_retry_count FROM desktop_runtime.queue_job WHERE id = $1 FOR UPDATE`. Compare logical ID, generation, physical ID, and executable state before any handler start. Reconcile only the positive difference between incoming metadata retry count and stored `transport_retry_count`. Create a new execution UUID, update canonical state to `active`, and insert the attempt in the same transaction.

When the generation is stale, call `completeFencedEnvelope()` without invoking domain code. When the reconciled stall count exceeds the logical allowance, mark the job `failed` with `failure_kind = 'stall_exhausted'`, write a terminal attempt outcome, and complete the envelope transactionally.

- [ ] **Step 3: Write failing success and handler-failure transaction tests**

```ts
it('settles logical success and physical completion in one transaction', async () => {
  await ledger.settleSuccess(startedAttempt);

  expect(canonicalStatus).toBe('completed');
  expect(attemptOutcome).toBe('completed');
  expect(transport.complete).toHaveBeenCalledWith(
    expect.objectContaining({ db: expect.any(Object) }),
  );
});

it('creates the next generation without consuming stall allowance', async () => {
  await ledger.settleFailure(startedAttempt, new Error('handler failed'));

  expect(canonicalJob).toMatchObject({
    status: 'retry_wait',
    generation: 1,
    handlerFailureCount: 1,
    stallCount: 0,
  });
  expect(transport.send).toHaveBeenCalledWith(
    expect.objectContaining({ stallRecoveryLimit: 2 }),
  );
});
```

- [ ] **Step 4: Implement fenced success and failure settlement**

Every update includes the exact fencing predicate from the spec. Treat a zero-row update as a fenced result, not success. Sanitize errors to `{ name, message }`; do not store stack traces or arbitrary thrown objects.

For handler failure, increment the failure count first. Create the next generation when `handler_failure_count <= handler_retry_limit`; otherwise mark `handler_exhausted`. Send the replacement before completing the current physical envelope, using the same transaction adapter, then commit them together.

- [ ] **Step 5: Write rollback and duplicate-terminal-receipt regressions**

Use a transport fake whose `complete()` performs a sentinel insert through the supplied `Db` and then throws. Assert the transaction rolls back the logical transition, sentinel insert, and attempt finish. Retry settlement with a healthy transport and assert one terminal receipt.

```ts
expect(logicalJob.status).toBe('active');
expect(sentinelCount).toBe(0);

await healthyLedger.settleSuccess(startedAttempt);
expect(terminalAttemptCount).toBe(1);
```

- [ ] **Step 6: Implement inspection and dead-letter reconciliation**

`getStats()` and `findJobs()` query only canonical ledger rows and map the six
logical states into the neutral five-state API. `reconcileDeadLetter()` locks
the logical row, verifies the physical ID and generation, closes the running
attempt as `stalled`, and marks the logical job `failed` with
`failure_kind = 'stall_exhausted'`. Add a unit regression proving a dead letter
for an old physical generation returns `fenced` without changing the current
job.

- [ ] **Step 7: Run focused tests and commit**

```powershell
git add packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger*
git commit -m "feat: add fenced logical queue settlement"
```

---

### Task 3: Wire the Experimental Overlay and Prove the Six Gates

**Files:**
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss.driver.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss.driver.spec.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss.driver.contract-spec.ts`
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss-logical-overlay.driver.contract-spec.ts`

**Interfaces:**
- Consumes: Task 2 ledger lifecycle.
- Produces: `PgBossDriverOptions.logicalLedgerEnabled?: boolean`, default `false`.
- Produces: `RUNTIME_CONTRACT_DRIVER=pg-boss-overlay` experimental contract selection.
- Preserves: direct `pg-boss` rejection mode and BullMQ behavior.

- [ ] **Step 1: Write failing driver wiring tests**

Require that overlay mode:

- initializes the ledger after pg-boss starts;
- upserts queue policy before `boss.work()`;
- creates logical jobs from `add()`;
- passes logical IDs to handlers;
- catches handler exceptions and calls logical failure settlement;
- leaves existing direct mode calls unchanged when the flag is absent.

```ts
const overlayDriver = new PgBossDriver(
  { ...driverOptions, logicalLedgerEnabled: true },
  metricsService,
  configService,
);

await overlayDriver.add(queueName, 'reply', {}, { retryLimit: 0 });
expect(logicalLedger.createJob).toHaveBeenCalled();
expect(boss.send).not.toHaveBeenCalled();
```

- [ ] **Step 2: Implement opt-in driver wiring**

Keep envelope version 1 and every existing branch for direct mode. Overlay mode uses version 2 envelopes and public transport callbacks:

```ts
send: async ({ queueName, envelope, physicalJobId, stallRecoveryLimit, priority, availableAt, db }) => {
  await this.boss.send(queueName, envelope, {
    id: physicalJobId,
    retryLimit: stallRecoveryLimit,
    priority: -priority,
    startAfter: availableAt,
    deleteAfterSeconds: QUEUE_RETENTION.failedMaxAge,
    db,
  });
},
complete: async ({ queueName, physicalJobId, db }) => {
  await this.boss.complete(queueName, physicalJobId, undefined, { db });
},
```

In `work()`, use `includeMetadata: true`. Start through the ledger, invoke the handler only for `kind: 'execute'`, pass the logical ID, and route success/failure to fenced settlement. Materialize queue policy before registering the physical worker.

Create a deterministic dead-letter queue name for each logical queue and set it
on every overlay envelope. Register one pg-boss worker for that dead-letter
queue; it calls `reconcileDeadLetter()` from the envelope metadata. Direct mode
must not create or consume these overlay dead-letter queues.

In overlay mode, `getStats()` and `findJobs()` delegate to the ledger. Keep
`retryJob()` and `deleteJob()` behind an explicit unsupported-operation error
for the spike and list their logical implementations as adoption-hardening
work; never fall through to a physical pg-boss ID operation.

- [ ] **Step 3: Add the three existing semantic cases to overlay selection**

Reuse the exact shared test names and assertions:

- `keeps stalled-recovery allowance independent from ordinary handler failures`
- `keeps crash recovery available when handler retryLimit is zero`
- `uses one stalled recovery by default without adding handler retries`

Do not duplicate or modify their assertions. The overlay harness sets `logicalLedgerEnabled: true` and uses a unique application name and queue prefix.

- [ ] **Step 4: Add the three overlay-specific service-backed gates**

Add exactly these tests:

1. `rolls back logical retry, replacement envelope, and current completion together`
2. `fences a stale generation before handler invocation and settlement`
3. `recovers settlement interruption without losing the job or duplicating a terminal receipt`

Use real PostgreSQL and pg-boss. Inject failure through public transport callbacks or a transaction-scoped sentinel operation; do not add production-only test branches. Each test must clean logical rows, attempts, queue policy, physical jobs, and queue metadata.

- [ ] **Step 5: Run the six-case overlay gate twice**

```powershell
$env:RUNTIME_CONTRACT_DRIVER='pg-boss-overlay'
$env:PG_DATABASE_URL='postgresql://postgres:postgres@127.0.0.1:55432/runtime_contract'
$pattern='stalled-recovery allowance|handler retryLimit is zero|one stalled recovery by default|rolls back logical retry|fences a stale generation|recovers settlement interruption'
1..2 | ForEach-Object {
  Push-Location packages/twenty-server
  node ../../node_modules/jest/bin/jest.js --config jest-runtime-contract.config.mjs --runInBand --testNamePattern=$pattern
  $testExit = $LASTEXITCODE
  Pop-Location
  if ($testExit -ne 0) { exit $testExit }
}
```

Expected: six cases PASS twice, with no unrelated cases executed.

- [ ] **Step 6: Run BullMQ compatibility once**

```powershell
$env:RUNTIME_CONTRACT_DRIVER='bullmq'
$env:REDIS_URL='redis://127.0.0.1:6380/15'
$env:PG_DATABASE_URL='postgresql://postgres:postgres@127.0.0.1:55432/runtime_contract'
node .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:test-runtime-contract
```

Expected: all 20 BullMQ cases PASS.

- [ ] **Step 7: Commit the experimental overlay**

```powershell
git add packages/twenty-server/src/engine/core-modules/message-queue/drivers
git commit -m "feat: prove pg-boss logical queue overlay"
```

---

### Task 4: Record the Evidence-Based Phase 0B Verdict

**Files:**
- Create: `docs/architecture/phase-0b-logical-queue-ledger-report.md`
- Modify: `docs/superpowers/specs/2026-07-13-phase-0b-logical-queue-ledger-design.md`

**Interfaces:**
- Produces exactly one verdict token: `ADOPT_LOGICAL_LEDGER_OVERLAY_FOR_HARDENING` or `REJECT_LOGICAL_LEDGER_OVERLAY`.
- Preserves the Phase 0 report and `REJECT_PG_BOSS` diagnostic unchanged.

- [ ] **Step 1: Run focused non-service verification**

```powershell
node ../../node_modules/jest/bin/jest.js --config jest.config.mjs --runInBand --runTestsByPath src/engine/core-modules/message-queue/drivers/pg-boss-logical-ledger.spec.ts src/engine/core-modules/message-queue/drivers/pg-boss.driver.spec.ts
npx tsgo -p packages/twenty-server/tsconfig.json --noEmit
node scripts/check-runtime-backend-boundaries.mjs
git diff --check
```

Expected: focused tests, typecheck, boundary check, and diff check PASS. Use the direct server typecheck because the upstream Windows Nx wrapper still passes single-quoted globs to `rimraf`.

- [ ] **Step 2: Verify cleanup**

Query `desktop_runtime.queue_job`, `queue_job_attempt`, `queue_policy`, pg-boss queue metadata, and active PostgreSQL sessions. No `runtime-contract-%` artifacts or test sessions may remain.

- [ ] **Step 3: Write the report from actual results**

The report must include:

- base and final commit IDs;
- the scoped architecture and explicit non-goals;
- one row per six acceptance cases, with duration and result for both final runs;
- BullMQ count and duration;
- every observed failure and fix;
- public pg-boss APIs used;
- confirmation that private pg-boss tables were not accessed;
- cleanup evidence;
- remaining hardening work;
- exactly one mechanical verdict token.

If any stop condition occurred, write `REJECT_LOGICAL_LEDGER_OVERLAY` and stop implementation. Do not hide a failure behind a known-issue exception.

- [ ] **Step 4: Link the report from the spec and commit**

```powershell
git add docs/architecture/phase-0b-logical-queue-ledger-report.md docs/superpowers/specs/2026-07-13-phase-0b-logical-queue-ledger-design.md
git commit -m "docs: record Phase 0B queue overlay verdict"
git status --short
```

Expected: tracked worktree is clean.

---

## Plan Self-Review

- Every approved acceptance gate is assigned to Task 2 or Task 3.
- The cross-process worker-policy gap is handled by persisted `queue_policy` and launcher readiness.
- Direct pg-boss rejection evidence remains selectable and unchanged.
- No task removes or substitutes another Redis surface.
- No placeholder, deferred implementation instruction, or unnamed error-handling step remains inside the spike scope.
- Types and names are consistent across ledger, driver wiring, contract selection, and report tasks.
