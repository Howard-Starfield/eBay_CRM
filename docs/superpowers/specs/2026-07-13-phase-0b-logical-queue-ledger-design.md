# Phase 0B Logical Queue Ledger Design

Date: 2026-07-13

Status: Approved for implementation planning

Base commit: `0c7180bb0605e1c01fe81d2a8ec7b891bb208232`

Evidence report:
[`phase-0b-logical-queue-ledger-report.md`](../../architecture/phase-0b-logical-queue-ledger-report.md)

## Decision being tested

Phase 0 proved `REJECT_PG_BOSS` for a direct BullMQ-compatible adapter because
pg-boss has one retry budget for both ordinary handler failures and lost-worker
recovery. Phase 0B does not change that result. It tests a different design:

> PostgreSQL owns the logical job, status, counters, identity, and receipts.
> pg-boss owns only physical delivery and crash redelivery.

The Phase 0B outcome is one of:

- `ADOPT_LOGICAL_LEDGER_OVERLAY_FOR_HARDENING` when all six acceptance gates
  pass without private pg-boss access.
- `REJECT_LOGICAL_LEDGER_OVERLAY` when any stop condition is reached. The
  logical ledger is then reused above a native PostgreSQL transport.

Neither outcome certifies that the complete Twenty server is Redis-free.

## Goals

- Preserve one stable logical job ID across handler retries and crash recovery.
- Keep handler-failure and stall-recovery budgets independent.
- Make logical settlement, replacement-envelope creation, and current-envelope
  completion one PostgreSQL transaction.
- Fence stale physical generations and stale workers before handler invocation
  and settlement.
- Keep BullMQ as the hosted and compatibility implementation.
- Produce a fast go/no-go result with six focused semantic tests.

## Non-goals

- Removing sessions, caches, locks, realtime, AI streaming, or other Redis
  surfaces.
- Shipping the overlay as production-ready software.
- Completing backup/restore, installer, Windows sleep/resume, or long soak work.
- Reproducing pg-boss physical IDs or physical statuses in user-facing APIs.
- Implementing unused BullMQ features outside Twenty's neutral queue port.
- Replacing the reviewed Phase 0 report or its `REJECT_PG_BOSS` token.

## Runtime boundary

The existing `MessageQueueDriver` remains the application boundary.

```text
Twenty MessageQueueDriver
        |
        v
LogicalQueueDriver
  - canonical PostgreSQL ledger
  - stable logical IDs
  - counters, status, attempts, receipts
        |
        v
pg-boss
  - physical envelopes only
  - heartbeat-based crash redelivery
```

The handler receives the logical ID. `findJobs`, `getStats`, `retryJob`, and
`deleteJob` must ultimately operate on logical jobs. The fast experiment may
defer complete admin-control behavior, but it must prove that physical envelope
IDs are never exposed as canonical IDs and document the exact follow-on work.

## Canonical data model

Phase 0B owns three tables in schema `desktop_runtime`.

### `queue_policy`

| Column                 | Type                   | Meaning                                                             |
| ---------------------- | ---------------------- | ------------------------------------------------------------------- |
| `queue_name`           | `text primary key`     | Neutral queue name                                                  |
| `stall_recovery_limit` | `integer not null`     | Materialized worker crash-recovery policy                           |
| `heartbeat_seconds`    | `integer`              | Explicit pg-boss heartbeat policy                                   |
| `worker_ready_at`      | `timestamptz not null` | Durable readiness sentinel; `-infinity` until both workers register |
| `updated_at`           | `timestamptz not null` | Last policy update                                                  |

The default stall recovery limit is explicitly stored as one. `work()` upserts
the queue policy and atomically resets `worker_ready_at` to `-infinity` before
registration. It marks the policy ready only after both the main worker and its
dead-letter reconciler register successfully. `add()` rejects production while
the policy is absent or not ready; once ready, it reads the persisted policy in
its creation transaction and assigns that stall limit to both the logical job
and its physical envelope. This durable gate is required because the producer
and worker are separate processes and cannot share an in-memory options map.

Local mode therefore uses the materialized default of one only after successful
worker registration. A future hosted overlay would require a stronger
deployment-wide policy registration protocol; hosted mode remains BullMQ in
this design.

### `queue_job`

| Column                    | Type                   | Meaning                                                                                   |
| ------------------------- | ---------------------- | ----------------------------------------------------------------------------------------- |
| `id`                      | `uuid primary key`     | Stable logical job ID                                                                     |
| `queue_name`              | `text not null`        | Neutral queue name                                                                        |
| `job_name`                | `text not null`        | Registered Twenty job name                                                                |
| `payload`                 | `jsonb not null`       | Versioned handler input                                                                   |
| `payload_version`         | `integer not null`     | Envelope/schema version                                                                   |
| `status`                  | `text not null`        | `queued`, `active`, `retry_wait`, `completed`, `failed`, or `cancelled`                   |
| `generation`              | `integer not null`     | Current physical generation, starting at zero                                             |
| `handler_failure_count`   | `integer not null`     | Completed handler failures                                                                |
| `handler_retry_limit`     | `integer not null`     | Additional handler retries allowed                                                        |
| `stall_count`             | `integer not null`     | Accounted lost-worker recoveries                                                          |
| `stall_recovery_limit`    | `integer not null`     | Lost-worker recoveries allowed                                                            |
| `started_count`           | `integer not null`     | Business-handler starts                                                                   |
| `transport_retry_count`   | `integer not null`     | Highest pg-boss retry count accounted for the current generation                          |
| `priority`                | `integer not null`     | Neutral priority                                                                          |
| `available_at`            | `timestamptz not null` | Earliest logical execution time                                                           |
| `dedup_key`               | `text`                 | Optional logical waiting-job deduplication key                                            |
| `current_physical_job_id` | `uuid not null`        | Current pg-boss envelope ID                                                               |
| `current_execution_token` | `uuid`                 | Fencing token for the active handler start                                                |
| `failure_kind`            | `text`                 | `handler_exhausted`, `stall_exhausted`, `cancelled`, `hard_timeout`, or `invalid_payload` |
| `last_error`              | `jsonb`                | Sanitized last failure metadata                                                           |
| `created_at`              | `timestamptz not null` | Creation time                                                                             |
| `updated_at`              | `timestamptz not null` | Last canonical transition                                                                 |
| `completed_at`            | `timestamptz`          | Successful terminal time                                                                  |
| `failed_at`               | `timestamptz`          | Failed terminal time                                                                      |

Checks enforce non-negative counters, non-negative limits, and generation zero
or greater. A partial unique index protects waiting-job deduplication:

```sql
CREATE UNIQUE INDEX queue_job_waiting_dedup_key_idx
ON desktop_runtime.queue_job (queue_name, dedup_key)
WHERE dedup_key IS NOT NULL
  AND status IN ('queued', 'retry_wait');
```

This permits a replacement while the previous logical job is active, matching
the Phase 0 contract.

### `queue_job_attempt`

| Column                  | Type                   | Meaning                                                                       |
| ----------------------- | ---------------------- | ----------------------------------------------------------------------------- |
| `id`                    | `uuid primary key`     | Attempt receipt ID                                                            |
| `job_id`                | `uuid not null`        | Logical job ID                                                                |
| `generation`            | `integer not null`     | Physical generation                                                           |
| `physical_job_id`       | `uuid not null`        | pg-boss envelope ID                                                           |
| `worker_instance_id`    | `uuid not null`        | Worker process identity                                                       |
| `execution_token`       | `uuid not null`        | Fencing token for this start                                                  |
| `transport_retry_count` | `integer not null`     | pg-boss metadata at start                                                     |
| `started_at`            | `timestamptz not null` | Handler-start time                                                            |
| `finished_at`           | `timestamptz`          | Attempt settlement time                                                       |
| `outcome`               | `text`                 | `running`, `completed`, `handler_failed`, `stalled`, `fenced`, or `cancelled` |
| `error`                 | `jsonb`                | Sanitized attempt error                                                       |

`(job_id, execution_token)` is unique. History is append-oriented; the current
job row is not used as the attempt log.

## Identity and envelope format

Each logical job has one stable UUID. Every physical generation receives a
deterministic UUIDv5 derived from a fixed application namespace, the logical
job ID, and the generation number.

```json
{
  "version": 2,
  "logicalJobId": "uuid",
  "generation": 0
}
```

The physical payload contains no canonical status or counters. The worker must
load the logical row before invoking domain code.

## Start and fencing transition

On physical delivery, the worker reads pg-boss metadata with
`includeMetadata: true`, then starts a short PostgreSQL transaction:

1. Lock the logical job row.
2. Verify the physical ID and generation match the current logical generation.
3. Verify the logical status is executable.
4. Reconcile any increase in pg-boss `retryCount` into `stall_count` exactly
   once using `transport_retry_count`.
5. Reject execution when the stall allowance is exhausted.
6. Create a new execution token and attempt receipt.
7. Increment `started_count`, set status `active`, and commit.

A stale generation or stale physical ID is completed without invoking the
business handler. A stale worker cannot settle because all terminal transitions
include this fencing predicate:

```sql
WHERE id = $logical_job_id
  AND generation = $generation
  AND current_execution_token = $execution_token
  AND status = 'active'
```

## Handler success

After the business handler succeeds, one short transaction:

1. Locks and fences the logical job.
2. Marks the attempt `completed`.
3. Marks the logical job `completed`.
4. Completes the current pg-boss envelope using the same transaction adapter.
5. Commits.

If fencing fails, the worker does not mutate canonical state. External effects
must rely on their own operation receipt or reconciliation.

## Handler failure and logical retry

The wrapper catches ordinary handler exceptions instead of allowing pg-boss to
classify them. One transaction:

1. Locks and fences the logical job.
2. Marks the attempt `handler_failed` and stores sanitized error metadata.
3. Increments `handler_failure_count`.
4. When another handler start is allowed, increments `generation`, resets the
   current-generation transport count, derives the next physical UUID, sets
   `retry_wait`, and sends the replacement envelope through the same pg-boss
   database adapter.
5. Otherwise marks the logical job `failed` with
   `failure_kind = 'handler_exhausted'`.
6. Completes the current physical envelope through the same adapter.
7. Commits.

The next envelope receives `retryLimit = stall_recovery_limit - stall_count`.
An ordinary retry therefore cannot reset or override the remaining stall
allowance.

## Worker crash and stall recovery

pg-boss is configured with explicit heartbeat behavior. `lockDuration` is not
mapped to `expireInSeconds`; pg-boss expiration is a separate hard execution
deadline. For the spike, hard timeout is deliberately larger than every test.

When the worker disappears, pg-boss redelivers the same physical generation.
The next start reconciles the transport retry delta into the logical
`stall_count`. The same logical job and generation are preserved.

When pg-boss exhausts the physical retry limit without another fetch, a
dedicated dead-letter reconciliation path must mark the logical job failed with
`failure_kind = 'stall_exhausted'`. If this cannot be implemented with public
APIs and deterministic receipts, the experiment stops with rejection.

Exhaustion may occur before logical `startAttempt` commits. When the dead-letter
envelope matches the canonical physical ID and generation, the canonical job is
`queued` or `retry_wait`, and its execution token is null, reconciliation must
atomically mark it failed and insert exactly one deterministic synthetic
terminal `stalled` receipt. Repeated delivery is idempotent. The existing
active-attempt reconciliation remains fenced by the current execution token.

## External side effects

The ledger provides at-least-once execution with fenced PostgreSQL settlement.
It cannot guarantee exactly-once eBay, email, or other remote mutations. Such
jobs require a stable operation ID and one of:

- provider-supported idempotency keys;
- a local operation ledger plus read-after-write reconciliation; or
- a human approval boundary before consequential actions.

The Phase 0B tests use receipts and do not claim exactly-once remote execution.

## Fast experiment scope

Only the following six acceptance cases are mandatory:

1. Ordinary handler failure with a stall allowance does not gain handler
   retries.
2. Worker crash recovery remains independent of the handler retry limit.
3. Default worker crash recovery is materialized explicitly and succeeds.
4. Logical transition, replacement send, and physical completion all commit or
   roll back together.
5. A stale generation cannot invoke or settle the business handler.
6. A crash during settlement produces neither a lost logical job nor a
   duplicated terminal receipt.

The PostgreSQL overlay suite runs twice at the final gate. The BullMQ
compatibility contract runs once. Focused TypeScript, formatting, runtime
boundary, and diff checks run once. Full frontend, full server, and long soak
suites are outside this fast experiment.

Final whole-branch review adds two non-substituting service regressions: one
proves that production is blocked until both logical workers are durably ready;
the other proves deterministic terminal reconciliation when physical
exhaustion precedes logical start. These supplement rather than alter the six
mandatory semantic cases.

## Immediate rejection conditions

Stop and produce `REJECT_LOGICAL_LEDGER_OVERLAY` when any of these is true:

- A private pg-boss table or internal API is required.
- Public transaction adapters cannot atomically compose logical state,
  replacement send, and current completion.
- A stale generation can invoke business logic or settle canonical state.
- A crash can lose the logical retry.
- Terminal receipts can be duplicated.
- Dead-letter reconciliation cannot deterministically close a stall-exhausted
  logical job.

Do not weaken, skip, or invert a failing acceptance case to obtain adoption.

## Follow-on work after adoption

Adoption means the design is viable, not production-ready. A later hardening
phase must complete logical inspection/retry/delete behavior, recurring
schedules, cancellation, retention, repair commands, Windows sleep/resume,
backup restoration safety, schema migrations, and crash-boundary soak tests.

Only after queue hardening may the project move to the next Redis surface.
