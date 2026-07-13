# Twenty Runtime Modes and Windows Desktop Foundation

Date: 2026-07-13

Status: Approved design direction; awaiting review of this written specification

## 1. Purpose

This specification defines how the eBayCRM fork of Twenty will run on Windows 11 without requiring the user to install or configure Docker, PostgreSQL, Redis, or a web server manually.

The product will retain Twenty's PostgreSQL CRM data model, metadata engine, custom objects, server, worker, and user interface. It will support two selectable runtime modes:

1. **Local Desktop mode**, the default and recommended mode. The application owns a local PostgreSQL process and uses PostgreSQL-backed runtime services instead of Redis.
2. **Twenty Compatibility mode**, an advanced mode that retains Twenty's standard Redis and BullMQ behavior for users who want the closest possible compatibility with upstream Twenty or an externally managed deployment.

This specification covers runtime selection, process supervision, Redis replacement boundaries, database isolation, mode switching, recovery, backup implications, and testing. It does not define the eBay dashboard, eBay synchronization, OAuth, semantic retrieval, or customer-reply policies. Those will be separate specifications after the runtime foundation is reviewed.

## 2. Goals

- Make Local Desktop mode the default first-run experience.
- Require one Windows installer and one application icon.
- Require no Docker and no manual server configuration in Local Desktop mode.
- Preserve Twenty's existing `core` and `workspace_*` PostgreSQL schemas and metadata behavior.
- Preserve separate Twenty server and worker processes for crash isolation and easier upstream merges.
- Replace Redis by responsibility rather than building a generic Redis emulator in PostgreSQL.
- Keep the existing Redis/BullMQ implementation available as a selectable compatibility backend.
- Make runtime backend selection explicit, testable, and guarded against unsafe live switching.
- Keep upstream changes localized behind provider interfaces and new runtime-specific modules.
- Provide durable background jobs and safe recovery after an application, worker, or computer restart.

## 3. Non-goals

- Replacing PostgreSQL with SQLite.
- Supporting multiple simultaneous server instances in Local Desktop mode.
- Supporting many concurrent users in Local Desktop mode.
- Matching Redis throughput at hosted SaaS scale.
- Removing Redis source code or BullMQ support from the fork.
- Migrating live in-flight jobs between Redis and PostgreSQL backends.
- Delivering eBay integration or local LLM management in this runtime milestone.
- Treating PostgreSQL `LISTEN/NOTIFY` as a durable message queue.

## 4. Product decomposition

The complete eBayCRM product is too large for one implementation plan. Work will be split into these specifications, in order:

1. **Runtime modes and Windows desktop foundation** — this document.
2. **eBay seller dashboard and local CRM objects** — navigation, layouts, buyers, conversations, messages, orders, listings, reply drafts, approvals, and sync cursors.
3. **eBay OAuth and synchronization** — authorization, backfill, polling, idempotent imports, deletion/revocation handling, and outbound delivery receipts.
4. **AI retrieval and reply safety** — provider routing, llama.cpp supervision, tools, evidence, approvals, embeddings policy, and outbound-risk classes.

Each later specification depends on the runtime interfaces defined here but must not bypass them.

## 5. User-visible runtime modes

### 5.1 Local Desktop mode — default

The first-run wizard preselects **Local Desktop (Recommended)**.

The launcher owns these child processes:

```text
eBayCRM.exe
├── PostgreSQL
├── Twenty server
├── Twenty worker
└── optional future sidecars, such as llama.cpp
```

Properties:

- PostgreSQL listens only on `127.0.0.1` on an app-selected private port.
- The Twenty server listens only on localhost.
- The launcher generates database credentials and application secrets.
- The launcher initializes and migrates the database automatically.
- The runtime backend is `postgres-desktop`.
- PostgreSQL stores durable jobs and coordination records in a dedicated runtime schema.
- Disposable caches remain bounded and process-local when cross-process consistency is not required.
- The Twenty server and worker remain separate processes.
- Closing the application performs a bounded, graceful shutdown. Background work does not continue after a full exit.

### 5.2 Twenty Compatibility mode — advanced

The first-run wizard exposes **Twenty Compatibility (Advanced)** under advanced setup.

Properties:

- The runtime backend is `redis`.
- Twenty uses its existing BullMQ, Redis cache, Redis session store, Redis pub/sub, and current worker topology.
- The user supplies or selects PostgreSQL and Redis endpoints, or uses a separately supported deployment package.
- This mode is intended for upstream parity, development, hosted deployments, and advanced users.
- The Windows installer will not silently bundle an unlicensed Redis-compatible implementation.

### 5.3 Persisted choice

The launcher stores the active runtime choice in a local launcher configuration file under `%LOCALAPPDATA%\eBayCRM\config`. The application also records the runtime mode and runtime schema version in PostgreSQL for diagnostics, but PostgreSQL does not override the launcher's explicit choice.

Environment construction is owned by the launcher:

```text
RUNTIME_BACKEND=postgres-desktop
```

or:

```text
RUNTIME_BACKEND=redis
```

Individual Twenty modules must not infer the backend from the presence of `REDIS_URL`.

## 6. Architecture

### 6.1 Preserve process separation

Local Desktop mode keeps the HTTP server and queue worker in separate Node processes. The launcher supervises both.

This choice preserves:

- Worker crash isolation.
- Independent worker restart.
- Twenty's current `QueueWorkerModule` structure.
- Existing graceful-drain behavior.
- A smaller upstream merge surface than importing all job modules into `AppModule`.

The cost is that process-local cache and event delivery cannot be used for state shared between the server and worker. Shared coordination must use the runtime database or PostgreSQL notifications.

### 6.2 Runtime service boundaries

The current `CacheStorageService` combines disposable caching, coordination, locking, sets, counters, hashes, and stream state. The fork will split these contracts:

```text
MessageQueueDriver  Durable, delayed, retried, scheduled jobs
EphemeralCache      Recomputable bounded TTL values
CoordinationStore   Atomic counters, sets, hashes, and work buffers
LeaseService        Ownership, expiry, heartbeat, and fencing
RealtimeBus         Cross-process wake-ups and live delivery
SessionStore        Web session persistence
AiStreamStore       Stream chunks, cancellation, heartbeat, and catch-up
```

Redis mode adapts these interfaces to existing Redis/BullMQ services. Local Desktop mode implements each interface with the narrowest suitable PostgreSQL or in-process mechanism.

No general-purpose `PostgresCacheStorageService` will emulate arbitrary Redis commands.

### 6.3 PostgreSQL schema isolation

Local Desktop infrastructure uses an explicitly configured schema:

```text
desktop_runtime
```

It must not place runtime tables in `core`, `public`, or any `workspace_*` schema.

The schema owns:

- Queue-library tables.
- Runtime schema version.
- Durable realtime event/outbox rows.
- Leases and fencing tokens.
- Coordination records that require cross-process atomicity.
- AI stream chunks and control state when AI streaming is enabled later.
- Optional persistent sessions.
- Cleanup cursors and retention bookkeeping.

Queue-library schema configuration must be explicit and must not depend on PostgreSQL `search_path`.

## 7. Message queue design

### 7.1 Preserve Twenty's queue contract

The PostgreSQL driver implements Twenty's existing `MessageQueueDriver` contract rather than changing every producer and processor.

The conformance contract includes:

- Immediate jobs.
- Delayed jobs.
- Priority ordering.
- Exact configured retry count.
- Independent per-queue concurrency.
- Recurring schedules using both interval and cron patterns.
- Recurring schedule upsert and removal.
- Waiting-only deduplication semantics.
- Stalled-job reclamation.
- Worker restart recovery.
- Bounded shutdown drain.
- Abort signaling where the underlying worker supports it.
- Completed and failed retention.
- Queue inspection, retry, deletion, health, and metrics.

### 7.2 pg-boss semantic spike

`pg-boss` is the first implementation candidate because it exposes PostgreSQL-backed priorities, retries, delays, cron, queue policies, and `SKIP LOCKED` job claiming.

The first implementation milestone is a semantic compatibility spike, not immediate adoption. The spike is accepted only if the shared conformance suite demonstrates correct behavior for Twenty's required queue contract.

If pg-boss cannot reproduce waiting-only deduplication, dynamic scheduler behavior, per-queue concurrency, stalled-job recovery, or bounded shutdown without fragile patches, the fallback is a focused native PostgreSQL driver behind the same interface. Graphile Worker remains an evaluated alternative but will not be selected merely by mapping each Twenty queue name to a Graphile serialized queue.

### 7.3 Delivery semantics

All job handlers are treated as at-least-once under crash conditions, regardless of a queue library's exactly-once claim.

External side effects must use durable receipts or an outbox. An eBay reply, refund, cancellation, or notification must never rely only on the queue job status to prevent duplication.

Persisted job payloads include a payload version. Application upgrades must either decode older payload versions or drain incompatible jobs before completing the upgrade.

## 8. Cache and coordination design

### 8.1 Ephemeral cache

Derived values that can be safely recomputed use a bounded in-process LRU/TTL cache in each process. Cache invalidation must not be required for correctness.

Examples include read-through metadata projections when a stale value has a bounded and harmless lifetime. Each namespace must be classified before migration.

### 8.2 Coordination store

Redis sets, hashes, counters, scans, and conditional updates used for workflows, throttling, imports, metrics, or active-stream registries are coordination algorithms, not cache entries.

These call sites receive feature-specific PostgreSQL operations using:

- Unique keys and atomic `INSERT ... ON CONFLICT` statements.
- Row locking where state already has a durable row.
- Explicit set/member tables where membership matters.
- Atomic counters with documented reset or expiry behavior.
- Cleanup jobs for expired coordination records.

The migration must preserve atomicity; existing non-Redis fallback implementations are not automatically accepted.

## 9. Locks and leases

### 9.1 Short database critical sections

Use transaction-level PostgreSQL advisory locks only when all protected work occurs in one short database transaction using one pinned connection or TypeORM `QueryRunner`.

### 9.2 Long-running ownership

Work that includes network calls, file operations, AI generation, or heartbeat-based ownership uses lease rows rather than advisory locks.

Each lease includes:

```text
resource_key
owner_instance_id
fencing_token
acquired_at
heartbeat_at
expires_at
```

Lease renewal and release validate the owner token. Mutations performed by a lease holder validate the current fencing token where practical.

This prevents an expired older worker from releasing or overwriting a newer worker's ownership.

## 10. Realtime subscriptions

PostgreSQL `LISTEN/NOTIFY` is a wake-up mechanism, not the durable event payload.

The publish flow is:

1. Insert an event or outbox row in `desktop_runtime`.
2. Commit the transaction.
3. Notify listeners with a small event ID or newest sequence.
4. Each listener reads all unseen committed rows.
5. The listener advances its live cursor.

Listener startup and reconnection follow this order:

1. Establish and commit `LISTEN`.
2. Read durable state and catch up.
3. Process subsequent notifications.

The design must handle missed, duplicate, and folded notifications. Event cleanup runs only after the configured catch-up window.

## 11. Sessions

Local Desktop mode uses a PostgreSQL session store by default so application restart does not unexpectedly sign the user out. Session rows live in `desktop_runtime` and have bounded expiry and cleanup.

Compatibility mode keeps Twenty's existing Redis session store.

Session secrets remain in Windows-protected local configuration and are not regenerated during ordinary upgrades.

## 12. AI streaming compatibility boundary

AI streaming is not required for the first Redis-free boot milestone, but the runtime interfaces reserve a safe design for it.

The future PostgreSQL implementation contains:

```text
ai_stream_lease
ai_stream_chunk
ai_stream_control
```

Stream chunks are committed in bounded batches rather than one row per token. A notification carries only the newest committed sequence. Clients resume after their last received sequence. Cancellation is a durable control flag plus a notification for low latency. Dead-stream detection reads the lease heartbeat.

Redis mode continues using the existing Redis lists, pub/sub, and heartbeat keys until the PostgreSQL implementation passes reconnect, cancellation, crash, and shutdown tests.

## 13. Mode switching

Runtime modes cannot be switched while any application process is running.

The settings workflow is:

1. Request a backup.
2. Stop new job production.
3. Wait for all active jobs to complete.
4. Require that the current queue has no pending, delayed, or failed jobs needing action.
5. Stop server and worker processes.
6. Validate the destination backend.
7. Persist the new runtime selection.
8. Restart and run destination health checks.

There is no automatic transfer of queue jobs, sessions, cache state, leases, or live AI streams between backends in version 1. CRM records remain in PostgreSQL and are preserved.

If the queue cannot be drained, mode switching is blocked with a diagnostic showing the remaining job categories. The user may return to the old mode to resolve them.

## 14. Windows launcher and process supervision

The launcher owns a single-instance lock and a process-state machine:

```text
Stopped
Starting PostgreSQL
Migrating
Starting server
Starting worker
Healthy
Degraded
Stopping
Recovery required
```

Startup behavior:

1. Resolve and validate app-owned paths.
2. Load or generate protected secrets.
3. Acquire the single-instance supervisor lock.
4. Start PostgreSQL and wait for readiness.
5. Run Twenty and runtime-schema migrations.
6. Start the server and verify its health endpoint.
7. Start the worker and verify queue-worker health.
8. Open the desktop window.

Shutdown behavior:

1. Stop accepting new background work.
2. Request bounded worker drain.
3. Stop the worker.
4. Stop the server.
5. Stop PostgreSQL cleanly.
6. Release the supervisor lock.

Unexpected child-process exits trigger bounded automatic restarts and visible diagnostics. PostgreSQL is never terminated with an immediate shutdown during ordinary operation.

Windows sleep and resume are treated as a recovery event: database health, notification listeners, leases, scheduled work, and eBay sync cursors are revalidated before normal work resumes.

## 15. Backup and restore implications

Local Desktop backups include:

- Twenty `core` and all `workspace_*` schemas.
- `desktop_runtime` durable jobs and coordination state.
- Local attachments and imported files.
- Application version, runtime backend, runtime schema version, and database version.
- Encrypted configuration required for restoration, or an explicit instruction to reconnect external accounts.

Before backup, the launcher quiesces new job production and waits for a consistent state. Restored active leases are considered expired until reclaimed. Pending jobs remain available for processing.

Compatibility mode cannot include Redis queue state in a PostgreSQL dump. A compatibility-mode backup therefore requires queue drain and records that no Redis job state was captured.

## 16. Failure handling

- A worker crash leaves durable jobs reclaimable after their lease expires.
- A server crash does not terminate the worker immediately, but the supervisor restarts the server and re-establishes subscriptions.
- A PostgreSQL outage pauses queue claiming and event delivery; operations fail closed rather than silently dropping work.
- A notification-listener disconnect triggers reconnect and durable catch-up.
- A stale lease holder cannot overwrite a newer holder when fencing validation is available.
- Repeated external operations are prevented through domain-level idempotency and effect receipts.
- Failed migrations leave the old application version and database backup available for recovery.
- Logs are bounded, rotated, and grouped by launcher, PostgreSQL, server, and worker.

## 17. Testing strategy

### 17.1 Dual-backend contract tests

Run one behavior suite against Redis/BullMQ and PostgreSQL desktop implementations. Tests cover queue semantics, cache contracts, coordination atomicity, lease ownership, sessions, realtime catch-up, health, and shutdown.

### 17.2 Queue fault injection

Terminate the worker:

- Before claim.
- After claim.
- Mid-handler.
- After a database write.
- After an external side effect but before acknowledgement.
- During graceful shutdown.

Verify recovery, retry count, deduplication, receipts, and absence of lost jobs.

### 17.3 CRM regression

Both runtime modes must pass Twenty's relevant tests for:

- Initial database setup and upgrades.
- Workspace creation.
- Standard and custom-object CRUD.
- Custom fields and relationships.
- Metadata regeneration.
- Permissions.
- Import and export.
- Server restart with queued work.

Schema snapshots confirm that the runtime work does not alter Twenty's existing CRM schema definitions outside approved runtime metadata.

### 17.4 Realtime and recovery

Test listener downtime, missed notifications, duplicates, reconnect catch-up, Windows sleep/resume, PostgreSQL restart, server-only restart, worker-only restart, and full application restart.

### 17.5 Upstream guardrails

CI rejects new direct Redis imports outside approved Redis adapters. CI boots and exercises both `redis` and `postgres-desktop` backends. The fork tracks stable Twenty releases rather than every commit on `main`.

## 18. Delivery stages and estimates

Planning ranges assume one senior engineer familiar with NestJS, TypeORM, PostgreSQL, and job queues.

1. Runtime inventory, instrumentation, and shared contracts: **1–2 weeks**.
2. pg-boss compatibility spike and queue contract suite: **2–4 weeks**.
3. Basic Redis-free desktop backend for CRM CRUD, metadata, sessions, and core jobs: **4–6 additional weeks**.
4. Coordination, workflows, imports, realtime subscriptions, and administration parity: **4–8 additional weeks**.
5. AI streaming parity and failure testing: **2–4 additional weeks**.
6. Windows supervision, upgrades, backup, diagnostics, and soak testing: **3–6 weeks**, partly parallel.

A deliberately scoped desktop MVP is expected in roughly **6–10 engineer-weeks** after the compatibility spike. Broad production parity is expected to require approximately **4–8 person-months**.

Tracking upstream closely should budget approximately **0.25–0.5 senior engineer equivalent**, with larger bursts when Twenty changes queueing, workflows, subscriptions, caching, or AI streaming.

## 19. Acceptance criteria

Local Desktop mode is ready for the next product specification when:

- A clean Windows machine installs and launches without Docker or Redis.
- PostgreSQL, server, and worker are app-owned and health-checked.
- First-run setup defaults to Local Desktop mode.
- Advanced setup can select Twenty Compatibility mode.
- Twenty can create a workspace, create custom objects and fields, and perform normal CRUD in both modes.
- Durable jobs survive application and worker restart in Local Desktop mode.
- The queue conformance suite passes for all features enabled in the first desktop release.
- No existing Twenty CRM schema is replaced or rewritten.
- Runtime tables exist only in the explicitly configured `desktop_runtime` schema.
- Mode switching is blocked until the source queue is drained.
- Backup and restore have passed a full restoration test.
- The installer and launcher expose actionable diagnostics for startup or recovery failure.
- CI runs both runtime backends and prevents new unapproved direct Redis dependencies.

## 20. Sources and evidence

- Current Twenty queue contract: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/interfaces/message-queue-driver.interface.ts`
- Current BullMQ implementation: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/bullmq.driver.ts`
- Current hard-coded queue factory: `packages/twenty-server/src/engine/core-modules/message-queue/message-queue.module-factory.ts`
- Current worker process: `packages/twenty-server/src/queue-worker/queue-worker.module.ts`
- Current cache contract and Redis-only operations: `packages/twenty-server/src/engine/core-modules/cache-storage/services/cache-storage.service.ts`
- Current Redis subscription service: `packages/twenty-server/src/engine/subscriptions/subscription.service.ts`
- Current AI stream state: `packages/twenty-server/src/engine/metadata-modules/ai/ai-chat/services/agent-chat-event-publisher.service.ts`
- pg-boss: https://github.com/timgit/pg-boss
- Graphile Worker: https://worker.graphile.org/docs
- PostgreSQL advisory locks: https://www.postgresql.org/docs/current/explicit-locking.html
- PostgreSQL `LISTEN`: https://www.postgresql.org/docs/current/sql-listen.html
- PostgreSQL `NOTIFY`: https://www.postgresql.org/docs/current/sql-notify.html

## 21. Approved decisions

- Local Desktop mode is the default.
- The user may choose a compatibility runtime mode.
- Local Desktop mode keeps PostgreSQL and does not migrate Twenty to SQLite.
- Local Desktop mode does not require Docker or Redis.
- The Twenty server and worker remain separate supervised processes.
- Redis support remains in the source tree as the compatibility backend.
- Redis is replaced by focused runtime interfaces, not a PostgreSQL Redis emulator.
- pg-boss receives the first queue compatibility spike.
- PostgreSQL runtime data is isolated in `desktop_runtime`.
- AI streaming migration happens after the queue, sessions, coordination, and realtime foundations.

