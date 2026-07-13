# Fork Boundaries

These rules keep the PostgreSQL desktop runtime separable from upstream Twenty
and preserve a tested compatibility path.

## Domain ownership

Domain code imports neutral runtime ports only. It must not import `bullmq`,
`ioredis`, `redis`, `connect-redis`, `cache-manager-redis-yet`, or
`graphql-redis-subscriptions` directly. The automated boundary check freezes
the current exceptions and rejects new ones.

Redis/BullMQ and PostgreSQL implementations stay under adapter-owned paths.
Queue, cache, session, subscription, and Redis-client details belong beneath
the driver or adapter directories listed in
`packages/twenty-server/runtime-backend-boundaries.json`. A PostgreSQL
implementation is a sibling adapter behind the same neutral port, not a branch
inside domain services.

## Runtime compatibility

The `redis` runtime behavior is preserved. Existing installations retain their
BullMQ queue, Redis sessions, cache/coordination semantics, pub/sub behavior,
health reporting, and upgrade path while PostgreSQL adapters are developed.
Changes to neutral ports require shared contract tests against both backends.

`postgres-desktop` becomes the product default only after every required
runtime port passes full server-and-worker boot tests. Queue-spike success alone
does not authorize changing the default; sessions, cache/coordination, leases,
subscriptions, AI streaming where enabled, health, and shutdown must also pass.

## Upstream schema boundary

Generated Twenty metadata schemas are untouched. Desktop runtime tables live
only in the explicitly configured `desktop_runtime` PostgreSQL schema. Fork
work must not rewrite generated metadata, place infrastructure tables in
`core`, `public`, or `workspace_*`, or repurpose Twenty CRM entities as runtime
coordination records.

## Desktop package ownership

Desktop packaging and process-supervision code is a new package in Phase 1. It
must not repurpose `packages/twenty-companion`, whose upstream purpose and
upgrade history remain independent of the desktop distribution.

## Automated enforcement

Run the guard locally with:

```powershell
node scripts/check-runtime-backend-boundaries.mjs
```

or through the root package script:

```powershell
yarn check:runtime-boundaries
```

The scanner examines source files under `packages/twenty-server/src`, accepts
only the frozen baseline and adapter-owned prefixes, prints every normalized
`path -> package` violation, and exits nonzero when a new direct backend import
crosses the boundary. Moving a coupling into the baseline requires an explicit
architecture review; the baseline is not an automatic suppression list.
