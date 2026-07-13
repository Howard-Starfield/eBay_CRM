# Phase 0 PostgreSQL Runtime Spike Report

Date: 2026-07-13

Decision: **REJECT_PG_BOSS**

## Scope and provenance

The fork baseline is Twenty `2.21.0` at upstream commit
`1b168ac1f7d466adf3be83b2676039e120d0db1c`. The clean baseline import is
`6f3c158cf8f80b494cbd7fbcd5ddfb9978357383`; the final pg-boss spike code
under test is `1ac022ef66fee9cdd7cba564a508b94731aaea63`.

The upstream tree verifier passed before the baseline import. It is expected
to report intentional drift after the fork changes. The distinction is
audited with:

```powershell
$baselineImportCommit = git log --format='%H' --grep='^chore: import Twenty 2.21.0 baseline$' -1
git diff $baselineImportCommit --name-only
```

The Phase 0 diff contained 41 changed paths before this report and CI evidence
were added. This is intentional fork work, not a claim that the changed tree
still byte-matches upstream.

**This verdict covers the message queue only. It does not certify a Redis-free Twenty server.**

## Mechanical decision rule

`ADOPT_PG_BOSS` is permitted only when every mandatory pg-boss case passes in
three consecutive runs, the BullMQ regression passes, TypeScript and unit
tests pass, and the PostgreSQL-only CI job has no Redis service or listener.

Otherwise the decision is `REJECT_PG_BOSS`, and the next implementation must
be a native PostgreSQL queue adapter behind the existing `MessageQueueDriver`
port. The evidence below triggers that rejection rule: three required combined
retry/recovery semantics pass BullMQ and fail pg-boss on every run.

## Mandatory semantic evidence

The three rejected cases are permanent shared acceptance tests. They are not
deleted, weakened, or skipped for pg-boss. The PostgreSQL CI job runs the full
contract and accepts its non-zero exit only when the JSON report contains the
exact complete assertion map: 22 specifically named passing cases, these three
specifically named failures, and the specifically named opposite-adapter
sentinel pending. Exact test/suite totals, suite statuses, no runtime suite
error, and no interruption are also required.

| Mandatory semantic | BullMQ 5.78.0 | pg-boss 12.26.0 |
| --- | --- | --- |
| Immediate delivery and preserved payload | Pass | Pass |
| Delayed delivery | Pass | Pass |
| Lower-numeric priority ordering | Pass | Pass |
| Exact configured handler retry count | Pass | Pass |
| Stalled allowance does not add handler retries | **Pass: one call** | **Fail: three calls** |
| `retryLimit: 0` does not remove crash recovery | **Pass: recovered** | **Fail: no re-entry within 45 s** |
| Default worker allows one stalled recovery | **Pass: recovered at about 30 s** | **Fail: no re-entry within 45 s** |
| Per-queue concurrency | Pass | Pass |
| Waiting-only deduplication | Pass | Pass |
| Cron upsert and removal | Pass | Pass |
| Interval upsert, limit, and removal | Pass | Pass |
| Worker-restart reclaim | Pass | Pass |
| Bounded shutdown and abort signal | Pass | Pass |
| Completed/failed retention | Pass | Pass |
| Health and queue metrics | Pass | Pass |
| Retry and deletion controls | Pass | Pass |
| Stop-before-claim persistence | Pass | Pass |
| Lease-expiry recovery | Pass | Pass |
| PostgreSQL receipt protects durable effect | Pass | Pass |
| External receipt protects side effect | Pass | Pass |

Additional pg-boss spike regressions pass for atomic cross-instance
waiting-only deduplication, producer/worker recovery-policy propagation,
healthy long handlers, second-level cron cadence, and retrying a limited cron
occurrence. Those successes do not override the failed mandatory cases.

## Reproduction and timings

Local service versions:

- Node.js `24.14.0` in the available bundled Windows runtime; CI is pinned to
  the planned Node.js `24.16.0`.
- PostgreSQL `16.4` (Visual C++ 1940, 64-bit) locally; CI uses `postgres:18`.
- Redis `7.2.8` locally and in compatibility CI.
- BullMQ `5.78.0` and pg-boss `12.26.0` from the immutable Yarn lock.

At adapter commit `1ac022ef66fee9cdd7cba564a508b94731aaea63`, the focused
three-case investigation produced:

| Run | Result | Jest time |
| --- | --- | ---: |
| BullMQ combined A/B/C | 3 passed, 0 failed | 33.320 s |
| pg-boss A: ordinary failure with stall allowance | Failed as required evidence | 9.114 s |
| pg-boss B: crash with `retryLimit: 0` | Failed as required evidence | 50.352 s |
| pg-boss C: default stalled recovery | Failed as required evidence | 50.463 s |

After promoting A/B/C into the permanent shared contract, the full BullMQ run
passed **20/20** (Jest 41.178 s; wall 46.411 s). The three full pg-boss
rejection runs each executed all tests and were checked by
`scripts/verify-pg-boss-rejection.mjs`:

| Full pg-boss run | Passing | Exact expected failures | Skipped opposite-adapter sentinel | Jest / wall time |
| --- | ---: | ---: | ---: | ---: |
| 1 | 22 | 3 | 1 | 173.568 / 178.675 s |
| 2 | 22 | 3 | 1 | 175.580 / 180.274 s |
| 3 | 22 | 3 | 1 | 174.586 / 179.047 s |

## Failed history and fixes retained

The spike did not move directly from implementation to rejection:

1. The initial pg-boss adapter could not load its ESM chain through the
   committed Jest target. The unsupported VM flag was removed.
2. Waiting-only deduplication raced across processes. A transaction-scoped
   advisory lock and one pinned PostgreSQL client made lookup/send atomic.
3. Failed retention inherited the completed-job age, count caps were absent,
   and contract queue metadata leaked. Separate age/count cleanup and complete
   harness teardown fixed these defects.
4. Scheduling/recovery review found producer policy overwrite, hard handler
   deadlines in place of heartbeats, recurrence-limit/retry coupling,
   inadequate second-level cron behavior, and an unbounded scheduler stop.
   Persisted policy, renewable heartbeats, owned cron/interval tables, and
   bounded shutdown fixed those defects.
5. Final combined tests exposed the unfixable adapter-boundary mismatch:
   pg-boss persists one retry counter/budget for ordinary callback failures and
   heartbeat-expiry or shutdown recovery, while BullMQ and the Twenty port need
   independent budgets.

Public-API side state, replacement jobs, direct SQL supervision, and a pg-boss
fork were investigated. They either change job identity/settlement semantics,
introduce crash gaps, couple the adapter to undocumented internals, or become a
second queue coordinator. The recommended path is a native PostgreSQL adapter
with separate handler-attempt and stall counters.

## CI evidence and Redis boundary

Workflow `.github/workflows/ci-runtime-backends.yaml` contains two independent
jobs:

- `queue-contract-redis` starts PostgreSQL and Redis, sets
  `RUNTIME_BACKEND=redis`, and requires the BullMQ contract to pass.
- `queue-contract-postgres-desktop` starts PostgreSQL only, sets
  `RUNTIME_BACKEND=postgres-desktop`, has no `REDIS_URL`, fails if port 6379 is
  listening, and reproduces the exact pg-boss rejection three times. A green
  job means the rejection evidence was reproduced; it does **not** certify
  pg-boss compatibility.

The upstream full-server workflow remains Redis-backed and now explicitly sets
`RUNTIME_BACKEND=redis` at workflow scope because Phase 0 proves only the
neutral message-queue boundary.

The runtime-boundary policy freezes **12** baseline direct-import files and
allows **5** narrow adapter path prefixes. The 12 files break down as six admin
health/queue files (including three tests), two cache/coordination files, and
one each for BullMQ compatibility, the shared Redis client, session storage,
and AI cancellation.

Deferred Redis-backed surfaces remain:

| Surface | Phase 0 status |
| --- | --- |
| Sessions | Deferred; compatibility CI still uses Redis |
| Disposable cache, counters, sets, hashes, streams, locks | Deferred and must be split by responsibility |
| Realtime subscriptions/pub-sub | Deferred; requires durable outbox plus PostgreSQL notification wake-up |
| AI streaming/cancellation/heartbeat | Deferred |
| Admin queue and Redis health views | Deferred |
| Windows process supervision, backup/restore, mode switching | Later runtime phases |

## Recommendation

Preserve the neutral queue port and BullMQ compatibility adapter. Build a
native PostgreSQL adapter under `desktop_runtime` with stable job identity,
separate `handler_attempt_count/limit` and `stall_count/limit`, transactional
settlement, `FOR UPDATE SKIP LOCKED` claiming, bounded recovery, and the same
shared acceptance contract. Do not ship the pg-boss spike as the Local Desktop
queue backend.
