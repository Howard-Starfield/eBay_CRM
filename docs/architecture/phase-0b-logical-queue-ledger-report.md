# Phase 0B Logical Queue Ledger Evidence Report

Date: 2026-07-13

Base commit: `0c7180bb0605e1c01fe81d2a8ec7b891bb208232`

Final implementation evidence commit: `362f443a`

## Mechanical verdict

ADOPT_LOGICAL_LEDGER_OVERLAY_FOR_HARDENING

All six approved semantic acceptance cases passed the required two-run adoption
matrix at `0164ebc6`, passed one covering run after each later correction at
`48e87db7` and `f038261e`, and passed twice at final correction `362f443a`. The
BullMQ compatibility contract passed once after the final correction, the
overlay uses only public pg-boss APIs, and none of the design's immediate
rejection conditions occurred. This is an adoption decision for a hardening
phase, not a production-readiness claim.

The reviewed Phase 0 report and its direct-adapter rejection diagnostic remain
unchanged. Their file hashes at the final Task 4 gate were:

- `docs/architecture/phase-0-runtime-spike-report.md`:
  `160b21d3a0447d72d1d71a554ab8d3a958a34e99`
- `scripts/verify-pg-boss-rejection.mjs`:
  `2698e2091aba5675f79e1ae501a7116ba7da71d8`

## Architecture proved

The existing `MessageQueueDriver` remains the application boundary. PostgreSQL
tables in `desktop_runtime` own the stable logical job identity, status,
independent handler-failure and stall-recovery counters, queue policy, fencing
generation and execution token, and attempt receipts. pg-boss owns only physical
delivery and crash redelivery. Each physical generation has a deterministic
UUIDv5, while the business handler sees the stable logical UUID.

Logical start and settlement lock and fence the canonical row. Logical failure,
replacement-envelope send, and current-envelope completion compose through the
same PostgreSQL transaction adapter. A deterministic dead-letter queue closes a
current stall-exhausted generation, including exhaustion before logical start;
that path creates one deterministic synthetic terminal receipt. Durable
`worker_ready_at` policy state blocks producers until both the main and
dead-letter workers register successfully. The overlay is opt-in; the default direct
pg-boss path and the BullMQ implementation remain unchanged.

## Explicit non-goals

- This phase does not remove Redis-backed sessions, caches, locks, realtime, AI
  streaming, or any other Redis surface.
- It does not ship the overlay as production-ready software.
- It does not complete backup/restore, schema-migration, installer, Windows
  sleep/resume, repair tooling, or crash-boundary soak work.
- It does not expose or reproduce physical pg-boss IDs or statuses as canonical
  application identifiers.
- It does not implement unused BullMQ features outside the neutral queue port.
- It does not replace or revise the reviewed Phase 0 direct-adapter result.

## Six-case acceptance evidence

The required two-run adoption matrix ran at implementation commit `0164ebc6`
before the later cron-guard and metadata-typing corrections. Jest reported
suite-level rather than per-test durations. Accordingly, each row records the
exact duration of the encompassing approved six-case run; no per-case duration
is inferred. Both matrix commands executed only these six selected cases; 20
discovered cases were skipped in each run.

| Acceptance case                                                                           | Matrix run 1 at `0164ebc6`         | Matrix run 2 at `0164ebc6`         |
| ----------------------------------------------------------------------------------------- | ---------------------------------- | ---------------------------------- |
| Keeps stalled-recovery allowance independent from ordinary handler failures               | PASS; Jest 12.029 s, wall 12.989 s | PASS; Jest 11.864 s, wall 12.848 s |
| Keeps crash recovery available when handler `retryLimit` is zero                          | PASS; Jest 12.029 s, wall 12.989 s | PASS; Jest 11.864 s, wall 12.848 s |
| Uses one stalled recovery by default without adding handler retries                       | PASS; Jest 12.029 s, wall 12.989 s | PASS; Jest 11.864 s, wall 12.848 s |
| Rolls back logical retry, replacement envelope, and current completion together           | PASS; Jest 12.029 s, wall 12.989 s | PASS; Jest 11.864 s, wall 12.848 s |
| Fences a stale generation before handler invocation and settlement                        | PASS; Jest 12.029 s, wall 12.989 s | PASS; Jest 11.864 s, wall 12.848 s |
| Recovers settlement interruption without losing the job or duplicating a terminal receipt | PASS; Jest 12.029 s, wall 12.989 s | PASS; Jest 11.864 s, wall 12.848 s |

Matrix totals were 6 passed, 0 failed, and 20 skipped in each run. The early
overlay-only cron guard at `48e87db7` was then covered by one exact six-case run:
6 passed, 0 failed, 20 skipped; Jest 12.158 seconds and wall 13.195 seconds. The
public-metadata type correction at final implementation commit `f038261e` was
covered by one further exact six-case run: 6 passed, 0 failed, 20 skipped; Jest
13.985 seconds and wall 15.303 seconds. The report therefore does not represent
the two-run matrix as having executed at final head.

Final correction `362f443a` then ran the preserved matrix twice: run 1 reported
6 passed, 0 failed, 22 skipped (Jest 12.980 seconds; wall 14.318 seconds), and
run 2 reported 6 passed, 0 failed, 22 skipped (Jest 13.955 seconds; wall 15.173
seconds). Two additional service regressions for durable readiness and
pre-start physical exhaustion passed together (Jest 6.936 seconds; wall 8.239
seconds).

## BullMQ compatibility

The final BullMQ compatibility run passed 20 tests and failed 0; 3 selector
placeholders were skipped. Jest duration was 43.474 seconds and wall duration
was 49.187 seconds. The ignored `twenty-shared` build artifact was
prepared before this run; no extra suite was used for that preparation.

## Public pg-boss surface and private-table confirmation

The overlay composes through public `PgBoss` methods `start`, `stop`, `send`,
`complete`, `updateQueue`, `supervise`, `work`, `offWork`, `fetch`, and `fail`;
public `work()` metadata (`includeMetadata`, `retryCount`, and dead-letter
`sourceId`); the public `deadLetter` send option; and the exported public
`Db.executeSql` transaction adapter. Final cleanup inspection used public
`getQueues()`.

Application SQL reads and writes only the owned
`desktop_runtime.queue_policy`, `desktop_runtime.queue_job`, and
`desktop_runtime.queue_job_attempt` tables. No private pg-boss table was read,
updated, inserted into, deleted from, or required by an acceptance case. No
pg-boss internal API was used.

## Failures observed and fixes applied

The following is the exhaustive failure/fix chronology recorded by the durable
Task 1-3 reports and the Task 4 gate:

1. Task 1 began with the ledger module absent, then exposed unimplemented
   identity, validation, creation, rollback, deduplication, and retry-limit
   behavior. The schema test also found nullable attempt outcomes. The ledger,
   validation, transactional creation, deduplication conversion, and non-null
   outcome constraint fixed those failures.
2. Task 1's direct full server typecheck exited 1 with thousands of unrelated
   missing generated/internal-package resolution and cascade diagnostics. A
   filtered repeat also found two Task 1 test uses of `Array.at()` incompatible
   with the project target. Replacing both with compatible index access removed
   all filtered `pg-boss-logical-ledger` diagnostics; the unrelated global
   workspace failures remained, so Task 1 did not claim a passing full
   typecheck.
3. Task 2 RED runs found all lifecycle methods absent and then found that a
   zero-row attempt update incorrectly reported settlement. The lifecycle and
   row-count fencing checks fixed those failures.
4. Important-review tests then found zero-row canonical starts accepted in both
   normal and stall-exhausted branches, success settlement lacked a canonical
   lock, and stale physical IDs could mutate receipts. Canonical row-count
   checks plus lock/order and physical-ID fencing fixed all four cases.
5. Task 3 initially had five driver failures covering startup ordering, logical
   creation routing, worker-policy materialization, logical handler identity,
   and handler-failure settlement. Wiring the opt-in overlay fixed them.
6. A settlement-classification test found a rejected success settlement was
   caught and reclassified as handler failure. Narrowing the handler catch
   boundary made transport settlement interruption propagate for recovery.
7. The first service teardown timed out because fake timers leaked into the
   pg-boss shutdown path. Scoping the service gate to real timers fixed the
   harness; the rollback gate then passed in 1.350 seconds and the stale-fence
   plus settlement-recovery gates passed together in 1.884 seconds.
8. Earlier forcibly killed diagnostic processes left three logical jobs, three
   policies, and five attempts. They were transactionally removed before the
   clean final service audit.
9. Review found logical `addCron()` could create a version-1 envelope for a
   version-2-only overlay worker. A RED test proved the path was reachable; an
   early overlay-only unsupported guard fixed it without changing direct mode.
10. The first Task 4 full server typecheck exited 2 in 27.079 seconds. Unbuilt
    workspace packages caused unrelated module-resolution failures, and five
    filtered Task-file diagnostics remained: a missing `MessageQueue` type in
    the contract spec, unsupported `Array.at()` in the driver spec, and three
    pg-boss job-metadata type errors for `retryCount`/`sourceId`. Correction
    commit `f038261e` imported the existing public `MessageQueue` and
    `JobWithMetadata` types and replaced `.at(-1)` with target-compatible
    indexing. The Task 3 correction report's immediate repeat still exited 2
    globally with 15 unrelated unbuilt-workspace diagnostics, but reported zero
    diagnostics in the three corrected Task files. A later independent Task 4
    verification observed global exit 2 with 13 unrelated workspace diagnostics
    and again exactly zero Task-file diagnostics. Neither global run was reported
    as passing; the differing unrelated counts reflect the observed workspace
    state at each run.
11. Two initial read-only cleanup-audit probes used incompatible ESM import
    forms for CommonJS `pg` and the installed `pg-boss` export shape. A CommonJS
    probe using the same public `getQueues()` API corrected the audit tooling;
    no product or database state was changed by either failed probe.
12. Final whole-branch review found two correctness gaps: physical exhaustion
    before logical start could leave a matching canonical job queued, and a
    materialized queue policy did not prove that both required workers were
    registered. Six focused RED tests captured dead-letter reconciliation,
    durable readiness, producer gating, and driver ordering. Correction commit
    `362f443a` atomically closes the pre-start case with one deterministic
    synthetic `stalled` receipt and adds a non-null readiness sentinel that is
    reset before registration and marked ready only after the main and
    dead-letter workers register. The implementation uses only public pg-boss
    `offWork`, `fetch`, and `fail` APIs. Both new real-service regressions and
    the preserved six-case matrix then passed as recorded above.

No observed failure met an immediate semantic rejection condition after the
corresponding fix. The remaining unrelated workspace diagnostics prevent a
green full server typecheck claim but do not contradict the two-run service
evidence.

## Focused final verification

Only focused checks and the explicitly approved service contracts were run. No
frontend or full test suite was run.

| Check                                                                     | Result                                                                                       | Duration                                |
| ------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- | --------------------------------------- |
| Ledger and pg-boss driver focused Jest suites after final correction      | PASS: 2 suites, 58 tests, 0 failures                                                         | Jest 1.493 s                            |
| Durable-readiness and pre-start-exhaustion real-service regressions       | PASS: 2 tests, 0 failures                                                                    | Jest 6.936 s; wall 8.239 s              |
| Preserved six-case PostgreSQL overlay matrix, final run 1                 | PASS: 6 selected, 0 failures, 22 skipped                                                     | Jest 12.980 s; wall 14.318 s            |
| Preserved six-case PostgreSQL overlay matrix, final run 2                 | PASS: 6 selected, 0 failures, 22 skipped                                                     | Jest 13.955 s; wall 15.173 s            |
| BullMQ compatibility contract                                             | PASS: 20 tests, 0 failures, 3 selector placeholders skipped                                  | Jest 43.474 s; wall 49.187 s            |
| Direct server `tsgo --noEmit` after final correction                      | Global FAIL: exit 2 with 15 unrelated workspace diagnostics; exactly 0 Task-file diagnostics | 9.7 s                                   |
| Runtime backend boundary, formatting, and `git diff --check` final checks | PASS                                                                                         | Individual final durations not recorded |

The global typecheck failure is intentionally reported as a failure. It was not
waived into a passing result; only the filtered Task-file diagnostic result is
green. No full-suite claim is made. The final correction was covered by two
fresh exact runs of the approved six-case overlay matrix, the two added
real-service regressions, and one fresh BullMQ compatibility run.

## Cleanup evidence

The final audit connected to the runtime-contract PostgreSQL database and used
public pg-boss queue inspection plus read-only queries of owned logical tables
and `pg_stat_activity`.

- `desktop_runtime.queue_job` rows with `runtime-contract-%` queue names: 0.
- `desktop_runtime.queue_job_attempt` rows belonging to those jobs: 0.
- `desktop_runtime.queue_policy` rows with those queue names: 0.
- Public pg-boss queue metadata names beginning `runtime-contract-`: 0.
- Redis keys created by the compatibility contract: 0.
- Active runtime-contract or Task 4 audit PostgreSQL sessions after the audit
  pg-boss instance stopped: 0.

No test artifact or test session required cleanup at the Task 4 final audit.

## Required adoption hardening

1. Define logical recurring-occurrence identity and a transactionally fenced
   scheduler-to-ledger handoff before enabling logical cron; keep the current
   early rejection until then.
2. Implement canonical logical `retryJob()` and `deleteJob()` semantics instead
   of falling through to physical job operations.
3. Define logical job and attempt retention, cleanup ownership, and repair
   behavior independently from physical pg-boss retention.
4. Define and persist an authoritative outcome for superseded running attempt
   receipts.
5. Serialize or promise-cache dead-letter worker registration so concurrent
   `work()` calls cannot register duplicate reconcilers.
6. Make service cleanup failure-aggregating across logical queues, dead-letter
   queues, jobs, attempts, policies, sentinels, pools, and worker connections.
7. Build all required workspace packages and obtain a globally green direct
   server typecheck; the overlay Task files themselves now have zero filtered
   diagnostics.
8. Add cancellation, migrations, backup/restore safety, Windows sleep/resume,
   repair commands, and crash-boundary soak coverage before production use.
9. Preserve the primary transaction error if rollback itself fails, while
   retaining the rollback failure as diagnostic context; apply one policy to
   every logical-ledger transaction helper.
10. Add and validate a database `CHECK` constraint for canonical
    `failure_kind` values through an explicit migration rather than relying on
    TypeScript-only narrowing.
