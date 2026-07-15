# Phase 1C Production Launch Boundary Design

Date: 2026-07-15

Status: Approved for implementation after fresh repository, upstream, and
independent architecture review

Base commit: `9ac8e813`

Related documents:

- [`2026-07-15-phase-1b-apphost-hardening-design.md`](2026-07-15-phase-1b-apphost-hardening-design.md)
- [`2026-07-13-twenty-runtime-modes-design.md`](2026-07-13-twenty-runtime-modes-design.md)
- [`../../architecture/phase-1a-windows-apphost-supervision-report.md`](../../architecture/phase-1a-windows-apphost-supervision-report.md)
- [`../../architecture/phase-0b-logical-queue-ledger-report.md`](../../architecture/phase-0b-logical-queue-ledger-report.md)

## Decision

Adopt a launch-boundary-first Phase 1C. The hardened .NET 10 AppHost will stop
constructing server and worker launches around a fixture executable and will
instead consume an explicit, trusted role-launch plan. A small Node control
shim and controlled Node probes will prove the existing authenticated named-pipe
protocol across the .NET/Node boundary before the full Twenty NestJS processes
are imported.

Phase 1C is split into two gates:

1. **Phase 1C-A — production-shaped launch boundary.** Extract the launch-plan
   contract, add the Node control shim and probes, define secret-safe runtime
   environment construction, and fail closed when an experimental runtime mode
   is not ready.
2. **Phase 1C-B — real Twenty compatibility boot.** Package Windows-native Node
   and built Twenty artifacts, then boot the real server and worker under
   AppHost against app-owned PostgreSQL and a test-managed Redis compatibility
   endpoint. This gate creates the behavior oracle for later Redis replacement;
   it does not ship Redis as the final local-desktop architecture.

This sequence is preferred over additional queue work first because Phase 0 and
Phase 0B already characterized the pg-boss semantic mismatch, while the real
Node packaging and lifecycle boundary has only been exercised with a .NET
fixture. It is preferred over broad Redis removal because upstream Twenty still
uses Redis independently for BullMQ, cache, sessions, coordination, realtime,
AI stream state, and administration.

## Honest current-state guardrail

The fork currently defaults `RUNTIME_BACKEND` to `postgres-desktop`, but the
message-queue factory creates a direct `PgBossDriver` without enabling the
logical ledger. Direct pg-boss was rejected in Phase 0, and the logical ledger
was adopted only for further hardening in Phase 0B. Cache and session factories
also still require `REDIS_URL` unconditionally.

Phase 1C must not allow that configuration to appear production-ready. Until
all required PostgreSQL runtime ports pass real server and worker boot tests:

- a real Twenty compatibility boot explicitly selects `RUNTIME_BACKEND=redis`;
- an attempted real `postgres-desktop` launch fails preflight with a stable,
  non-secret reason code before either Node role starts; and
- focused queue contract tests may continue to select `postgres-desktop`
  directly without passing through the production AppHost launch provider.

The product intent remains Local Desktop as the eventual default. Failing
closed during construction protects that intent from becoming a misleading or
unsafe partial implementation.

## Goals

- Make server and worker launch construction independent of the Phase 1A/1B
  fixture executable.
- Preserve AppHost as the sole lifecycle owner for PostgreSQL, server, worker,
  and future sidecars in Option A foreground mode.
- Preserve the existing named-pipe framing and payload identities while adding
  a versioned challenge-first handshake that lets Node echo AppHost's exact
  Windows process identity without approximation. Prove authentication,
  generation fencing, drain, shutdown, identity-bound HTTP readiness, and
  bounded-output behavior with Node children.
- Keep application paths, working directories, arguments, ordinary environment,
  secret environment, build identity, readiness strategy, and artifact trust
  explicit and testable.
- Ensure secret values never enter command arguments, diagnostic fields,
  stdout/stderr, or persisted launch plans.
- Establish an installed-like compatibility boot oracle before replacing more
  Redis responsibilities.
- Preserve all Phase 1B safety and acceptance evidence.

## Non-goals

- Claiming a Redis-free or standalone Twenty boot in Phase 1C-A.
- Enabling the pg-boss logical ledger as a production default.
- Completing logical cron, retry/delete administration, ledger repair, or
  retention cleanup.
- Replacing sessions, cache, leases, pub/sub, realtime, AI stream state, or
  rate limiting.
- Adding the eBay dashboard, eBay OAuth, message synchronization, local LLM,
  tray UI, installer, updater, or backup workflow.
- Installing a permanent Windows service.
- Importing the full NestJS server from the controlled Node-probe tests.
- Reusing Linux/Alpine `node_modules` in the Windows payload.

## Architecture

```text
.NET 10 AppHost
├── app-owned PostgreSQL 16.14
├── trusted role-launch plan provider
│   ├── fixture provider (existing acceptance compatibility)
│   ├── Node probe provider (Phase 1C-A)
│   └── packaged Twenty provider (Phase 1C-B)
├── authenticated named-pipe control channel
│   └── Node control shim
├── server role
└── worker role
```

The lifecycle coordinator and `LaunchSpecification` remain authoritative. The
provider is a composition boundary, not a second supervisor or a general
plugin system.

### Role launch plan

Introduce an internal `IRoleLaunchPlanProvider` that returns one immutable plan
for a requested `ProcessGeneration`. A plan contains:

- role and complete generation identity;
- executable path and ordered arguments;
- working directory;
- ordinary environment;
- secret environment as `SecretValue` instances;
- expected build identity;
- readiness strategy and optional loopback health port; and
- bootstrap artifact leases held through process creation.

Construction is deliberately two-stage. The provider first returns a trusted
static plan containing the expected build identity and no control values. The
executor uses that identity and the requested generation to create the
authenticated control channel. It then merges the channel's reserved ordinary
and secret environment into the plan. The merge fails if the provider declared
any reserved control key; provider values can never override pipe, nonce, role,
generation, operation, or build identity. The executor remains responsible for
Job Object assignment, retained process identity, acceptance, timeouts,
reconciliation, and disposal.

The existing fixture provider preserves Phase 1B behavior. The production
executor must not branch on fixture filenames or fixture modes after the
contract is installed.

### Node control shim

Add a dependency-light TypeScript module under a desktop-specific package. It
implements the versioned Phase 1C control protocol:

1. read AppHost-issued control values from the secret environment;
2. connect to the Windows named pipe;
3. receive one bounded `IdentityChallenge` only after AppHost has verified the
   pipe client's PID, creation time, and Job membership through Windows APIs;
4. bind the identity-bound loopback HTTP listener before sending Hello;
5. echo the challenged process ID, exact creation-time ticks, and challenge ID
   in the version-2 Hello payload together with role, generation, operation,
   build identity, capability nonce, and loopback endpoint;
6. initialize the role while the bound health endpoint reports a bounded
   not-ready response, then report the current identity-bound Health payload
   only after the role readiness callback succeeds;
7. serve the identity-bound loopback HTTP health response with
   the current build identity, protocol version, generation, and nonce using
   the current `HealthPayload` shape; role association remains owned by the
   retained AppHost role resource;
8. enter draining exactly once;
9. invoke a bounded application drain callback;
10. acknowledge shutdown and terminate normally; and
11. reject malformed, stale, oversized, or out-of-order frames while applying
   the exact duplicate-operation replay rules defined below.

The module must not log environment values, control frames, customer data, or
exception text. It returns typed reason codes to the controlled probe and lets
AppHost own external diagnostics.

### Versioned identity challenge

JavaScript cannot reliably derive the exact Windows `FILETIME` creation ticks
that AppHost currently validates. Phase 1C therefore increments the control
protocol version and adds one AppHost-to-child `IdentityChallenge` message
before Hello. The challenge payload contains the expected process ID, exact
creation-time UTC ticks as a canonical decimal string, and a cryptographically
random per-endpoint challenge ID. The tick string has bounded length and allows
ASCII digits only, with no sign, whitespace, decimal separator, exponent, or
noncanonical leading zeros. .NET parses it with invariant `long` rules; Node
keeps it as a string/`bigint` and never converts it to a JSON number. The
challenge contains no pipe name, capability nonce, database secret, or
application secret.

Before sending the challenge, `WindowsControlChannel` uses the existing native
pipe-client verifier to prove that the connected client is the retained process
and belongs to the expected Job Object. The child must echo the challenged PID,
ticks, and challenge ID in Hello; the existing nonce, role, generation,
operation, build, endpoint, and process checks still apply. Version 2 adds only
the challenge ID to the existing Hello data fields and represents its echoed
creation ticks with the same canonical string. Comparison is exact after
bounded invariant parsing. A mismatched, duplicate, stale, or missing challenge
fails closed.

Version 2 has no downgrade path. Exactly one challenge is sent after native
PID/creation/Job verification. Hello before challenge, a v1 Hello, duplicate or
conflicting challenge/Hello, wrong PID/ticks/challenge, cross-generation replay,
timeout, cancellation, and invalid direction all fault the endpoint. Challenge
frames count toward the existing frame and size budgets. Authentication state
and `AuthenticationPublishHook` are published only after a valid HelloV2.

The .NET fixture is updated to the same versioned sequence so fresh runs of
every Phase 1B lifecycle partition re-establish the protocol evidence. The
historical Phase 1B report remains explicitly v1 evidence. No approximation
based on `process.uptime`, WMI, PowerShell, or wall-clock subtraction is
permitted.

### Controlled Node probes

Create small server and worker probe entrypoints that import the Node control
shim but not NestJS or the Twenty application graph. Both probes bind one
AppHost-reserved loopback port and expose the same identity-bound HTTP health
contract already verified by the executor. The worker endpoint reports worker
registration state; it is not a public application endpoint.

Probes support test-only deterministic behaviors selected by trusted test
construction, not arbitrary profile files or production environment variables:

- delayed hello;
- delayed readiness;
- delayed drain;
- refusal to drain;
- controlled crash before or after hello;
- bounded stdout/stderr flood; and
- stale or malformed protocol messages.

These probes establish cross-language evidence without conflating protocol
bugs with full Twenty dependency initialization.

### Runtime environment construction

The launch provider creates a minimal allowlisted environment rather than
inheriting the parent block wholesale. It supplies stable, production-shaped
values including:

- `PG_DATABASE_URL` through secret environment;
- `RUNTIME_BACKEND` as an ordinary value;
- `NODE_PORT` for the server role only;
- AppHost control credentials through secret environment;
- session and application secrets through secret environment; and
- fixed profile-owned file/storage paths outside the immutable payload.

Secrets are never accepted through command-line options. The immutable Windows
payload contains no `.env` file. The supervised Node bootstrap rejects a `.env`
file in the payload or working directory and clears dotenv-related preload
options. All production configuration is the allowlisted AppHost environment.
Diagnostics record key categories and reason codes, never the environment block
or values.

### Trust boundary

Phase 1C-A extends trust from one fixture executable to the Node probe payload.
The trusted manifest contains normalized relative paths, lengths, and SHA-256
digests generated during publish. Validation rejects:

- missing or extra required artifacts;
- payload paths outside the immutable application root;
- reparse points in any validated path component;
- hash, length, or manifest-version mismatch; and
- writable profile paths used as executable or module inputs.

The immutable application root is installed with an owner/DACL that denies
ordinary mutation, contains no reparse points, and is separate from the user's
profile. Validation hashes the complete declared dependency closure but holds
open leases only for the signed/versioned manifest, `node.exe`, and
bootstrap-critical entrypoints through process creation. This avoids the
impractical promise of opening every `node_modules` file while preventing a
bootstrap swap. Acceptance rechecks the manifest after shutdown and proves the
payload root remained non-writable and unchanged. Phase 1C-B extends this
mechanism to compiled server/worker entrypoints, production dependency closure,
and frontend assets. It does not hash mutable PostgreSQL data, logs, uploads,
knowledge files, or user configuration.

### Readiness and drain

Control authentication proves process identity, not application readiness.

- The Node probe server reports ready after its loopback listener and readiness
  callback succeed.
- The Node probe worker reports ready after its simulated processor registration
  succeeds.
- Phase 1C-B server readiness must verify the expected PostgreSQL connection,
  Redis compatibility endpoint, migrations, and HTTP health.
- Phase 1C-B worker readiness must verify application-context initialization and
  required processor registration; merely starting the Node process is
  insufficient. The end-to-end BullMQ canary runs only after both roles are
  ready and performs deterministic cleanup.

After valid HelloV2, the executor polls the bound loopback endpoint until the
existing role-readiness deadline. Requests never overlap, use a bounded
cancellation-aware delay, and stop immediately when the role process exits or
the authenticated control channel is lost. The only retryable result is an HTTP
success response containing a well-formed, identity-matching `HealthPayload`
whose status is exactly `not-ready`. Status `ready` completes readiness.
Nonce, build, generation, or protocol mismatch; malformed payload; unexpected
HTTP status; any other health status; process exit; or control loss fails
immediately. When the deadline expires while the role remains authenticated and
identity-matching but not ready, the executor records stable reason
`role-readiness-timeout` and enters the existing indeterminate-operation
reconciliation path rather than starting a second generation. Focused tests
cover readiness within the deadline and not-ready beyond it.

Drain preserves the reviewed exchange exactly. For one shutdown operation the
child sends `DrainAccepted`, stops admission, sends
`NoNewWorkAcquisition`, sends exactly one bounded `ActiveWorkRemaining`, and
finally sends `Drained`. A duplicate request for the same operation replays the
complete cached four-frame response sequence without invoking the drain
callback twice; a different or stale operation fails closed. AppHost sends
`Shutdown` only after `Drained`, and the child answers `ShutdownAccepted`
followed by `Stopped` as it terminates. AppHost retains the existing late-stop
reconciliation and kill-on-close containment when any deadline is exceeded.

## Implementation workstreams

### Workstream A — role-launch contract

- Add the provider and immutable plan contract.
- Move fixture-specific argument, health-port, and artifact-lease construction
  into a fixture provider.
- Inject the provider into `LifecycleCommandExecutor`.
- Preserve all existing fixture tests without weakening trust or lifecycle
  assertions.

### Workstream B — Node protocol boundary

- Add protocol golden vectors shared between C# and TypeScript tests.
- Implement the dependency-light Node control client.
- Add server and worker probes plus focused unit tests.
- Publish the probes as Windows-native test artifacts and supervise them with
  the real AppHost.

### Workstream C — environment, trust, and readiness gate

- Add the allowlisted environment builder and secret registry integration.
- Add the versioned trusted Node payload manifest and artifact lease.
- Add a stable `postgres-desktop-runtime-incomplete` preflight outcome for real
  launches while Redis-backed ports remain.
- Ensure Compatibility mode is explicit and cannot be inferred from a missing
  or malformed backend value.

### Workstream D — Phase 1C-B compatibility boot

- Build Windows-native production artifacts using the repository-pinned Node
  and package-manager versions.
- Add a dedicated supervised Twenty migration bootstrap with the same minimal
  allowlisted environment as the server. It does not call the permissive Docker
  upgrade shell and does not treat process exit as proof of completion.
- Record AppHost-control, Twenty metadata/application, and future
  `desktop_runtime` schema versions independently. After the migration process
  exits, query the expected Twenty migration catalogs and compare them with a
  release-pinned catalog/version manifest and schema snapshot.
- Reconcile migration timeout or AppHost interruption from database state:
  rerun only migrations whose catalogs prove they are unapplied, accept a
  committed expected version, reject partial or drifted catalog state, and
  reject a database newer than the installed payload.
- Test fresh, repeat/idempotent, interrupted-before-commit,
  interrupted-after-commit, partial-commit, schema-drift, and newer-schema
  cases. Unexpected objects in `desktop_runtime`, `core`, `public`, or
  workspace schemas fail acceptance.
- Keep immutable frontend/server assets separate from profile-owned storage.
- Boot the real server and worker with a test-managed Redis compatibility
  endpoint and run one workspace CRUD plus one BullMQ worker canary.
- Record evidence without claiming Redis-free readiness.

Workstream D begins only after A–C pass their acceptance gates.

## Error handling

Every launch failure returns a stable reason code and leaves no owned process:

- `role-launch-plan-invalid`
- `role-payload-trust-failed`
- `role-control-handshake-failed`
- `role-readiness-failed`
- `role-drain-timeout`
- `postgres-desktop-runtime-incomplete`
- `compatibility-runtime-dependency-unavailable`

Raw child exceptions, command lines, environment blocks, and secret-bearing
connection strings are never persisted. AppHost continues bounded
reconciliation after indeterminate start or stop outcomes.

## Testing and acceptance

Phase 1C-A requires:

1. every authoritative non-overlapping Phase 1B test partition remains present
   and passes with zero failures and zero unexpected skips; the verifier must
   also prove no previously recorded test disappeared;
2. C# and TypeScript protocol golden vectors agree byte-for-byte;
3. controlled Node server and worker start, authenticate, become ready, drain,
   and stop under the published AppHost;
4. delayed start/stop, crash, duplicate generation, malformed frame, and output
   flood cases remain bounded and leave no descendant;
5. artifact hash, length, path, and reparse failures fail closed;
6. registered secret canaries are absent from arguments, stdout/stderr,
   diagnostics, and persisted artifacts;
7. a real `postgres-desktop` launch is rejected before Node process creation;
   and
8. the evidence report states that full Twenty and Redis-free boot remain
   unproven.

Phase 1C-B additionally requires:

1. fresh and repeat startup from an installed-like immutable payload without
   system Node, Yarn, or PostgreSQL;
2. verified AppHost and Twenty migrations across fresh, repeat, interrupted,
   partial-commit, drift, and newer-schema cases;
3. frontend load and one workspace CRUD round trip;
4. one post-ready real BullMQ canary completed by the supervised worker and
   deterministically removed;
5. explicit Redis-unavailable preflight, PostgreSQL failure with real Node
   roles, and late real-role start/stop reconciliation evidence;
6. independent server and worker restart without PostgreSQL restart and repeat
   startup without schema drift;
7. bounded shutdown with no surviving AppHost-owned, PostgreSQL, Node, or
   test-managed Redis process and no retained test database;
8. immutable, non-writable install-root verification before and after the run,
   plus secret scans; and
9. no pg-boss tables or unexpected `desktop_runtime`, `core`, `public`, or
   workspace-schema objects while Compatibility mode is selected.

## Delegation and review

Implementation is task-sequential because the workstreams touch shared launch
contracts. Each bounded task receives an implementation subagent, followed by
an independent specification review and an independent code-quality review.
The primary agent owns integration, conflict resolution, commands, release
evidence, and final decision tokens.

The first delegated coding goal is Workstream A only. It must be behavior
preserving and may not add Node probes, change runtime defaults, or claim a real
Twenty boot. Workstreams B and C may proceed in parallel only after Workstream
A's contract is reviewed and committed.

## Outcome tokens

Phase 1C-A records exactly one:

- `ADOPT_PRODUCTION_LAUNCH_BOUNDARY`
- `REVISE_PRODUCTION_LAUNCH_BOUNDARY`
- `REJECT_PRODUCTION_LAUNCH_BOUNDARY`

Only the first token permits Phase 1C-B. Neither Phase 1C token permits enabling
`postgres-desktop` as the user-facing default; that requires the later complete
Redis-free boot gate defined by the runtime-modes design.
