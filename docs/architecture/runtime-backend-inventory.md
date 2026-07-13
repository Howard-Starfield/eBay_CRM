# Runtime Backend Inventory

This inventory freezes the runtime-backend coupling surface as observed on
2026-07-13. Counts cover `packages/twenty-server/src` unless a row names a
different path. They describe migration surfaces, not an assertion that the
server can already boot without Redis.

The status values have these meanings:

- `Phase 0 queue spike`: owned by the queue compatibility spike.
- `Later runtime phase`: must be migrated before a Redis-free full boot.
- `Compatibility-only`: retained to preserve the existing `redis` runtime.

## Frozen direct backend imports

The guard baseline contains 12 unique files. A file can import more than one
restricted package, so the package-specific file counts overlap.

| Restricted package | Files | Status |
| --- | ---: | --- |
| `bullmq` | 5 | `Compatibility-only` |
| `ioredis` | 5 | `Compatibility-only` |
| `redis` | 2 | `Compatibility-only` |
| `connect-redis` | 1 | `Compatibility-only` |
| `cache-manager-redis-yet` | 2 | `Compatibility-only` |
| `graphql-redis-subscriptions` | 1 | `Compatibility-only` |

The canonical normalized file list is
`packages/twenty-server/runtime-backend-boundaries.json`. New direct imports
are rejected outside the adapter-owned prefixes in that policy.

## Queue and processors

| Surface | Exact count | Evidence | Status |
| --- | ---: | --- | --- |
| Processor files decorated with `@Processor(...)` | 87 | `packages/twenty-server/src/**/*.ts` | `Phase 0 queue spike` |
| Existing BullMQ message-queue driver | 1 | `engine/core-modules/message-queue/drivers/bullmq.driver.ts` | `Compatibility-only` |

All 87 processors already enter through Twenty's message-queue decorators and
driver contract. Phase 0 changes the driver selection and contract coverage;
it does not rewrite each processor.

## Sessions

| Surface | Exact count | Evidence | Status |
| --- | ---: | --- | --- |
| Redis-backed session factories | 1 | `engine/core-modules/session-storage/session-storage.module-factory.ts` | `Later runtime phase` |

The factory owns one `connect-redis` store backed by one `redis` client. The
future PostgreSQL session adapter must preserve expiry, cookie-secret, restart,
and cleanup behavior. The Redis store remains available in compatibility mode.

## Cache API

| Surface | Exact count | Evidence | Status |
| --- | ---: | --- | --- |
| Public asynchronous `CacheStorageService` operations | 24 | `engine/core-modules/cache-storage/services/cache-storage.service.ts` | `Later runtime phase` |
| Cache namespaces | 13 | `engine/core-modules/cache-storage/types/cache-storage-namespace.enum.ts` | `Later runtime phase` |
| Redis cache module factory | 1 | `engine/core-modules/cache-storage/cache-storage.module-factory.ts` | `Compatibility-only` |

The 24-operation API mixes disposable values with sets, scans, counters,
hashes, pattern deletion, and locks. Later phases must split those semantics
into focused cache, coordination, and lease ports instead of implementing a
general PostgreSQL Redis emulator.

## Lease locks

| Surface | Exact count | Evidence | Status |
| --- | ---: | --- | --- |
| Redis lock primitives | 2 | `CacheStorageService.acquireLock` and `releaseLock` | `Later runtime phase` |
| Production lock consumer sites | 20 across 12 files | Direct primitives, `CacheLockService.withLock`, and `@WithLock` outside the two cache-lock implementation files | `Later runtime phase` |

The current primitive stores only a value of `lock` with a TTL and releases by
key. Long-running PostgreSQL replacements require owner identity, expiry,
heartbeat, and fencing; preserving only mutual exclusion is insufficient.

## Import staging sets

| Key family | Calls / files | Current operations | Status |
| --- | ---: | --- | --- |
| `messages-to-import:{workspaceId}:{messageChannelId}` | 4 calls across 3 files | `setAdd`, `setPop`, retry re-add | `Later runtime phase` |
| `calendar-events-to-import:{workspaceId}:{calendarChannelId}` | 2 calls across 2 files | `setAdd`, `setPop` | `Later runtime phase` |

Together these are 2 key families and 6 calls across 5 production files. They
are coordination work buffers whose atomic pop and retry behavior must be
preserved by a feature-specific PostgreSQL design.

## GraphQL subscriptions

| Surface | Exact count | Evidence | Status |
| --- | ---: | --- | --- |
| Subscription channel kinds | 4 | `engine/subscriptions/enums/subscription-channel.enum.ts` | `Later runtime phase` |
| Redis pub/sub service methods | 6 | Three subscribe/publish pairs in `engine/subscriptions/subscription.service.ts` | `Later runtime phase` |
| Redis pub/sub client construction | 1 | `engine/core-modules/redis-client/redis-client.service.ts` | `Compatibility-only` |

The four channel kinds are logic-function logs, event streams, agent chat, and
workspace events. The PostgreSQL design needs durable catch-up around
`LISTEN/NOTIFY`; notifications alone cannot replace the event payload.

## AI stream chunks, heartbeats, and cancel

| State family | Exact count | Current owner | Status |
| --- | ---: | --- | --- |
| Stream chunk list family | 1 | `agent-chat-event-publisher.service.ts` | `Later runtime phase` |
| Claim/running heartbeat key family | 1 | `agent-chat-stream-heartbeat.service.ts` | `Later runtime phase` |
| Cancel pub/sub channel family | 1 | `agent-chat-cancel-subscriber.service.ts`, resolver publishers, and `get-cancel-channel.util.ts` | `Later runtime phase` |

These are 3 separate state families. Chunk sequencing/catch-up, heartbeat-based
dead-stream detection, and durable low-latency cancellation each need their own
contract before Redis can be removed from AI streaming.

## Admin health and queue views

| Surface | Exact count | Evidence | Status |
| --- | ---: | --- | --- |
| Backend-aware production services or indicators | 4 | Admin queue service, admin health service, Redis health indicator, worker health indicator | `Later runtime phase` |
| Direct backend-import admin test files in the frozen baseline | 3 | `admin-panel/__tests__` | `Compatibility-only` |

The production count includes Redis health, worker/queue health, queue metrics,
inspection, retry, and deletion surfaces. Backend-neutral admin contracts must
report the selected runtime rather than manufacturing BullMQ queues directly.

## Integration utilities

| Surface | Exact count | Evidence | Status |
| --- | ---: | --- | --- |
| BullMQ-specific queue drain utilities | 1 file / 2 restricted imports | `packages/twenty-server/test/integration/utils/wait-for-all-jobs-to-finish.util.ts` imports `bullmq` and `ioredis` | `Phase 0 queue spike` |

The Phase 0 contract and integration harness must drain and close the selected
queue adapter without reaching directly into BullMQ or Redis.

## Reproducing the counts

Run these commands from the repository root:

```powershell
rg -l "from ['\"](bullmq|ioredis|redis|connect-redis|cache-manager-redis-yet|graphql-redis-subscriptions)['\"]|require\(['\"](bullmq|ioredis|redis|connect-redis|cache-manager-redis-yet|graphql-redis-subscriptions)['\"]\)" packages/twenty-server/src | Sort-Object
(rg -l '@Processor\(' packages/twenty-server/src --glob '*.ts' | Measure-Object).Count
(rg '^  async [A-Za-z]' packages/twenty-server/src/engine/core-modules/cache-storage/services/cache-storage.service.ts | Measure-Object).Count
rg -n '\.(acquireLock|releaseLock|withLock)\(|@WithLock\(' packages/twenty-server/src --glob '*.ts' --glob '!**/__tests__/**'
rg -n '\.(setAdd|setPop)\(' packages/twenty-server/src/modules/calendar packages/twenty-server/src/modules/messaging --glob '*.ts'
```
