# Phase 1C-B Compatibility Boot Design Amendment

Date: 2026-07-16

Status: Approved by independent specification and architecture review

Base commit: `b58cd17818ca1af2adce080ac37bca14d0940841`

Amends, without replacing:

- `docs/superpowers/specs/2026-07-15-phase-1c-production-launch-boundary-design.md`
- `docs/architecture/phase-1c-production-launch-boundary-report.md`

## Purpose

The approved Phase 1C design intentionally deferred production dependency
closure, immutable frontend handling, real Twenty initialization hooks, and the
choice of an acceptance-only Redis-compatible endpoint to Phase 1C-B. Fresh
inspection of the pinned fork and current upstream shows that those details now
need to be fixed before implementation. This amendment only closes those gaps.
It does not weaken or reopen the accepted Phase 1C-A launch, control, trust,
readiness, drain, process-containment, or explicit runtime-mode boundaries.

## Fresh-lens evidence and drift

- Local `main` and the feature worktree start at
  `b58cd17818ca1af2adce080ac37bca14d0940841`.
- `origin/main` advertises
  `c5633340c4c239dfbcfc3efcc9a2cc4c57869791`; it is behind local `main` and
  is not a valid replacement base.
- The locally recorded upstream reference is
  `5f8baa9761d658dd5de57059b10cbaab5510c936`.
- Official upstream `main` advertised
  `1b9152d4c5eed4213be364f93532940bb80c37c6` during validation. That commit is
  a one-file AI model-catalog refresh whose parent is `5f8baa97`; the inspected
  server, worker, and production command entrypoints are therefore unchanged.
- The fork's semver engine is only `^24.5.0`; that is not an installed-runtime
  identity. Phase 1C-B therefore pins the official Node `24.18.0` Windows x64
  ZIP, `node-v24.18.0-win-x64.zip`, whose official release-list SHA-256 is
  `0AE68406B42D7725661DA979B1403EC9926DA205C6770827F33AAC9D8F26E821`.
  The extracted official `win-x64/node.exe` SHA-256 is
  `9A4EB5F1C29C6A2E93852EAD46B999E284A6A5CA8BAB4D4E241D587D025A52DE`
  and it must carry a valid Authenticode chain for subject
  `CN=OpenJS Foundation, O=OpenJS Foundation, L=San Francisco, S=California,
  C=US`. The repository package manager remains `yarn@4.13.0`.
- The production server entrypoint is `packages/twenty-server/dist/main.js`.
  The worker entrypoint is
  `packages/twenty-server/dist/queue-worker/queue-worker.js`. The current
  migration entrypoint is
  `packages/twenty-server/dist/command/command.js run-instance-commands`.
- The Vite frontend builds to `packages/twenty-front/build` and the production
  image copies it to `packages/twenty-server/dist/front`.
- The current server calls `generateFrontConfig()` before listening. That
  function invokes dotenv and rewrites `dist/front/index.html`. A successful
  boot would therefore mutate the trusted payload and invalidate the required
  before/after install-root proof.
- The Phase 1C-A manifest is intentionally bounded for a nine-file probe. It
  cannot safely represent the much larger production `node_modules`, compiled
  server, workspace-package, and frontend closure without a new bounded,
  streaming format.

No upstream commit is fetched, merged, or copied by this amendment. Phase 1C-B
is built from the pinned fork identity above; upstream drift is recorded only
as validation evidence.

## Amendment 1: immutable desktop frontend mode

The installed-like payload contains a prebuilt same-origin frontend. Desktop
production boot sets an explicit ordinary configuration value selecting the
immutable frontend path. In that path:

1. `dist/front/index.html` is built with an empty runtime override so the
   existing frontend same-origin fallback resolves the AppHost-reserved server
   origin.
2. `generateFrontConfig()` performs a read-only validation that the expected
   empty configuration marker exists and returns without writing any file.
3. A single desktop configuration bootstrap runs before any production module
   graph is imported. When the explicit immutable-desktop value is present it
   prevents dotenv loading in `generate-front-config.ts`,
   `database/typeorm/core/core.datasource.ts`, and
   `database/typeorm/raw/raw.datasource.ts`, rather than loading and later
   overwriting values. Every server, worker, setup, migration, and acceptance
   role uses this bootstrap. The provider rejects `.env` and `.env.*` files in
   the payload root and in every effective role working directory before any
   Node process starts. Tests enumerate every production-reachable dotenv
   import and fail when a new unguarded site is introduced.
4. `NODE_OPTIONS` and other Node preload/injection variables are rejected or
   cleared by the allowlisted environment builder. They are never inherited.
5. The server may serve `dist/front`, but neither server nor worker may write
   beneath the immutable payload. Uploads, local storage, logs, caches, and all
   mutable application data are redirected to profile-owned paths outside it.

The release manifest also records a frontend-configuration schema and digest.
For the inspected fork the complete `generateFrontConfig` field set is only
`REACT_APP_SERVER_BASE_URL`; immutable desktop mode requires that key to be
absent from the prebuilt empty override and requires the marker to contain no
unknown or unresolved substitution. A future field added to the generator or
frontend configuration reader changes the schema/digest and fails packaging
until explicitly classified. Importing the exported server, worker, setup, or
migration bootstrap with immutable mode enabled must be side-effect-free:
tests do so from a physically read-only payload with `.env` files absent and
prove zero file writes and zero dotenv calls before invoking a bootstrap.

Non-desktop upstream behavior remains unchanged. The immutable mode is an
explicit desktop launch contract, not an inference from missing environment.

## Amendment 2: installed-like production closure

The generated Windows payload mirrors the production image's logical runtime
closure while remaining Windows-native. Toolchain identity is exact, not a
semver range: the builder downloads or accepts only the official Node
`24.18.0` Windows x64 ZIP named and hashed above, validates its archive hash,
validates the extracted `node.exe` Authenticode subject and chain, and records
the extracted file hash. That exact extracted `node.exe` runs the build and is
the exact file copied into and manifested in the installed payload. A different
system Node, a matching version with a different file hash, or a failed/offline
signature check fails the build before Yarn runs.

On 2026-07-20, the user approved replacing the initial full monorepo install
after two cold closure failures at production build command 2, while a warmed
exact rerun passed. The initial development-capable focus is exactly
`workspaces focus twenty twenty-server twenty-front twenty-emails`, run through
the checked-in Yarn `4.13.0` executable and pinned Node. It does not use
`--production`; Nx, Lingui, TypeScript, and Vite build dependencies remain
available. Lockfile immutability remains enforced by the existing exact
`YARN_ENABLE_IMMUTABLE_INSTALLS=true` child environment.

The focused immutable development install retains the lockfile's Git-sourced Electron
`@electron/node-gyp` locator. Its sole build-time Git is the official Git for
Windows `MinGit-2.55.0.2-64-bit.zip` release asset for
`2.55.0.windows.2`, length `38,839,825`, SHA-256
`E3EA2944CEA4B3FABCD69C7C1669EF69B1B66C05AC7806D81224D0ABAD2DEC31`.
The builder downloads only that exact release URL or accepts the same exact
offline archive, preflights every ZIP entry without following links, extracts
an ordinary exact tree, and validates `cmd/git.exe` length, SHA-256,
Authenticode signer/chain, and exact version output. It copies that tree only
to the generated build root at `.phase1cb-toolchain/mingit`; command `PATH` is
exactly the pinned Node directory, its `cmd` directory, and Windows System32.
Before Yarn runs, the builder requires the exact `yarn.lock` key, resolution,
and dependent-package reference for
`https://github.com/electron/node-gyp.git` at commit
`06b29aafb7708acef8b3669835c8a7857ebc92d2`. It then supervises the staged
`git.exe` through an explicit canary: initialize a generated bare repository,
perform a depth-one fetch of that exact URL and commit with prompts and ambient
Git configuration disabled, and verify the exact commit in `FETCH_HEAD`. The
bounded Trace2 record proves the canary used MinGit `2.55.0.windows.2` with that
locator and commit; it is not represented as trace emitted by Yarn. Yarn's Git
use remains separately constrained by the exact child `PATH` and immutable
lock. The focused install must not fetch or materialize the retired Electron
locator cache path
`@electron-node-gyp-https-d0f303c37e-e8c97bb534.zip`; its absence is positive
proof that the unrelated Companion/Electron graph stayed excluded. The lock
identity and MinGit canary remain required. The resolution ledger binds these
identities, the required cache absence, and generated evidence roots. MinGit is
build-only: it is never sourced from the developer checkout or ambient `PATH`,
and no MinGit file enters the payload or runtime manifest.

The closure contains:

- the exact release-pinned `node.exe` used for the build;
- root runtime package metadata needed for module resolution;
- the production-focused `node_modules` closure for `twenty-server`,
  `twenty-emails`, `twenty-shared`, and `twenty-client-sdk`;
- each referenced workspace package's physical `package.json` and compiled
  `dist` tree;
- the compiled Twenty server, worker, migration, and desktop bootstrap
  entrypoints plus required server assets; and
- the complete Vite frontend copied beneath the server `dist/front` tree.

The build uses the checked-in Yarn `4.13.0` executable and immutable lockfile.
It creates a clean generated build root containing only copied, manifest-listed
source/build inputs, performs the approved development-capable focus there for
`twenty`, `twenty-server`, `twenty-front`, and `twenty-emails`, builds Lingui
catalogs, workspace packages, the server/worker/command bundles, and the
frontend, and then performs the unchanged final `--production` focus/prune
transition as the production image. Only after that transition may the final
payload be materialized. Build logs and dependency-resolution records must prove
that no module, executable, cache, or generated output was resolved from the
developer source checkout or its `node_modules`. Runtime commands invoke the
payload's `node.exe` directly; runtime never invokes Yarn, npm, npx, Corepack,
or a system Node installation.

Yarn workspace links and any package-manager reparse points are not copied into
the final payload. A staging step resolves only links whose canonical targets
remain inside the generated build root, copies their contents as ordinary
files/directories, and then rejects every remaining reparse point. The final
payload contains no symlink, junction, mount point, or other reparse-point
module input.

The final validator additionally enumerates native `.node` dependencies,
requires PE x64 machine type, verifies each can be loaded by the pinned payload
Node from an installed-like path, and rejects dependency paths resolving
outside the payload. The acceptance build must run on Windows from a clean
staging root; a Linux-built closure or a closure copied from the source
checkout is not equivalent evidence.

## Amendment 3: production payload manifest version 2

Phase 1C-B adds a separate version-2 production manifest. The Phase 1C-A probe
manifest and validator remain unchanged.

The production manifest is generated atomically as UTF-8 without BOM and
contains:

- manifest schema version;
- source commit, build identity, Node version, Yarn version, target RID, and
  expected protocol/generation identity;
- normalized server, worker, setup, instance-command, acceptance-orchestrator,
  and frontend entrypoints;
- the release-pinned migration/catalog manifest digest; and
- one ordinal, unique record for every payload file other than the manifest,
  with normalized relative path, byte length, and uppercase SHA-256.

The manifest has one canonical byte serialization. A release digest covers
every semantic header and every sorted file record—including all normalized
entrypoints, tool/RID/protocol/generation identities, frontend-configuration
digest, migration/catalog digest, bounds, paths, lengths, and hashes—and
excludes only its own digest slot. The value stored in that slot must equal the
compiled value. That full canonical digest is compiled into
the trusted AppHost release catalog; a manifest cannot authenticate itself and
a file-record-only Merkle root is insufficient. The AppHost verifies the
anchored canonical digest before loading or launching any payload Node,
JavaScript, native addon, migration, or acceptance code. Tests mutate each
selection-critical header and each record class independently and require
rejection before process creation, including redirects to another already
declared payload file. The payload build and AppHost build are therefore one
release unit even though their protocol/schema versions remain independently
checked.

The validator reads records with explicit byte, path-length, file-count, and
aggregate-size bounds. It compares the manifest stream with an independently
sorted filesystem enumeration so missing and undeclared files both fail. It
rejects duplicate/case-colliding paths, path escape, alternate data streams,
device aliases, reparse points in every component, manifest/version/build/tool
mismatch, unexpected writable executable inputs, length mismatch, and hash
mismatch. It holds read-only no-delete-sharing handles for the manifest,
`node.exe`, and the selected bootstrap-critical entrypoints through process
creation, then revalidates the complete closure after shutdown.

Packaging and installed-like setup apply and verify a release-pinned Windows
ACL: the invoking standard user, inherited interactive groups, and application
roles receive read/execute only; only the trusted installer boundary may write.
The validator scans the final installed tree without following links, verifies
canonical paths and ACLs immediately before every launch, and rejects any
untrusted write/delete/owner permission or externally mutable alias. Critical
handles plus the no-delete-sharing policy close replacement of the files that
select code; the before/after full-root revalidation detects all other payload
change and invalidates acceptance.

Production-closure acceptance includes a cold boot from the final tree in a
clean Windows test account or VM with no source checkout, Yarn, global modules,
or system Node on `PATH`. `PATH`, `NODE_PATH`, current directory, temporary
paths, and DLL search inputs are explicitly constructed and constrained. A
successful developer-worktree boot is useful diagnostics but is not closure
evidence.

Mutable profile data, PostgreSQL data, logs, uploads, and acceptance results are
outside the manifest and outside the immutable root.

## Amendment 4: real Twenty bootstrap and control ownership

The existing server and worker bootstrap functions are refactored only enough
to return their initialized Nest application objects while preserving their
normal direct production entrypoint behavior. Desktop-specific wrapper
entrypoints consume those exported functions and the already reviewed Node
control shim.

For each real role the wrapper:

1. validates and consumes AppHost control environment;
2. binds the identity health endpoint before Hello;
3. initializes the real Nest server or `QueueWorkerModule` application
   context;
4. proves the role-specific readiness conditions below;
5. exposes ready only through the existing identity-bound health contract;
6. implements the reviewed four-frame drain exchange; and
7. executes the concrete role drain adapter described below and reports
   `Drained` only after that adapter proves zero admitted/in-flight work, then
   remains under AppHost control until shutdown is accepted.

The wrapper is not a second supervisor. It cannot launch PostgreSQL, the other
Node role, the compatibility endpoint, or migrations. AppHost remains the sole
lifecycle and Job owner.

Before Hello, every wrapper presents a compatibility tuple containing AppHost
release catalog, payload build/root digest, control protocol, role-bootstrap
API, Node, database-manifest, BullMQ, and compatibility-candidate versions.
AppHost compares it with a release-pinned compatibility matrix and rejects any
unknown combination. Independent version values without an accepted tuple are
not a compatibility contract.

Server readiness requires all of the following from the initialized
application and independent AppHost verification:

- connection to the AppHost-owned PostgreSQL database identity;
- connection to the expected compatibility endpoint;
- exact accepted migration/catalog identity;
- the real loopback HTTP listener answering a bounded application health
  request; and
- the expected payload build, protocol, role, and generation identity.

Worker readiness requires:

- successful `QueueWorkerModule` application-context initialization;
- the release-pinned required processor registry present;
- a live queue-driver connection to the expected compatibility endpoint; and
- the expected payload build, protocol, role, and generation identity.

Process start, a listening port, control authentication, or a Redis `PING`
alone is insufficient.

The server drain adapter atomically closes HTTP admission before acknowledging
drain: the listener is stopped from accepting new sockets, keep-alive requests
receive the fixed shutdown response, and the adapter counts already admitted
requests until the count reaches zero. Only then does it call and await the
real Nest `app.close()` path. The worker drain adapter first pauses every
worker in the release-pinned BullMQ worker registry locally so no new job can be
acquired, then reads the registry's real active-handler counters, awaits those
counters reaching zero, and finally closes every Worker, Queue, and the Nest
application context. Both adapters share the AppHost deadline; expiration is a
drain failure and AppHost containment owns termination. Focused tests must
prove a late HTTP request is refused after admission closes, a queued job is
not acquired after worker pause, an already active handler is allowed to
finish, and `Drained` cannot precede the observed zero counts.

Role readiness is an internal prerequisite, not the final Phase 1C-B success
signal. AppHost withholds the accepted application-ready result until the
frontend/API persistence test, the workspace/CRM CRUD sequence, and the exact
BullMQ compatibility canary have all completed after verified migrations.

## Amendment 5: supervised migration and catalog truth

AppHost runs two ordered, dedicated one-shot Twenty database bootstraps before
either real role. Each is launched from a normalized manifest entrypoint in the
trusted payload under the AppHost Job with the same minimal server environment,
receives secrets only through secret environment values, holds an AppHost
bootstrap lease, and is bounded by an AppHost-owned deadline. Neither invokes
`entrypoint.sh`, `upgrade.sh`, Docker, Yarn, a shell, nor a command assembled
from profile or document text.

The first one-shot calls a refactored library form of the current
`database/scripts/setup-db.ts` behavior. With FDW explicitly disabled it
creates `public` and `core`, installs only `uuid-ossp` and `unaccent`, and
creates `public.unaccent_immutable` with its release-pinned definition. AppHost
then reconnects and verifies those schemas, extension names/versions, function
owner/language/volatility/arguments/result type/body digest, and absence of
unapproved extensions before the second one-shot can start. The second
one-shot calls the typed `run-instance-commands` service directly with the
semantic equivalents of the production flags `--force --include-slow`; it does
not parse or spawn the production command string. AppHost again reconnects and
performs the complete catalog/object verification below. Exit zero without
the corresponding verified state is failure at either boundary.

Before changing any non-disposable accepted starting state, AppHost stops its
owned PostgreSQL, makes a complete staged application-owned backup of the
canonically verified data directory in a distinct canonically verified profile
backup root, records its source identity and hash inventory, atomically renames
the complete staging directory into its final backup name, and restarts
PostgreSQL. AppHost then opens a dedicated PostgreSQL session and obtains a
release-fixed session advisory lock that is held across both one-shots and all
intermediate/final verification. Failure to obtain or retain the lock prevents
all role starts. Restore is permitted
only while PostgreSQL is stopped, only to the exact owned data root after the
same canonical/reparse checks, and is followed by full state verification.
Disposable acceptance databases may be destroyed instead of restored; real or
unowned profiles never participate in this phase's destructive tests.

Migration success is database state, never process exit. After every exit,
timeout, cancellation, or AppHost-recovery path, AppHost reconnects directly to
its owned PostgreSQL and compares a static, versioned release manifest with:

- `desktop_runtime.apphost_control` for the independent AppHost-control schema
  version;
- `core._typeorm_migrations` for the frozen legacy TypeORM catalog;
- `core.upgradeMigration` for instance/workspace upgrade-command attempts;
- the expected active Twenty instance-command sequence and workspace version
  state; and
- an independent future desktop-runtime product schema version, which remains
  zero in this phase and therefore authorizes no pg-boss/product runtime
  tables.

Catalog comparison accepts only an exact starting state or exact target state
for the installed payload. Interrupted-before-commit may retry only when every
catalog and schema object still equals the exact starting state.
Interrupted-after-commit is accepted only when every catalog and schema object
equals the exact target state. Partial prefixes, failed/unknown attempts,
missing expected entries, extra entries, reordered or renamed entries, schema
shape drift, and database-newer-than-payload all fail closed and require repair;
they are never normalized by guessing or arbitrary SQL.

The release manifest also pins the complete allowed non-system namespace and
extension universe at each checkpoint. It includes `desktop_runtime`, `core`,
`public`, the PostgreSQL-owned system namespaces, and exactly the workspace
schemas derived by the repository's canonical `getWorkspaceSchemaName`
function from accepted `core.workspace.id` rows. Any other non-system schema or
extension fails closed. There must be a bijection: every accepted workspace
row has exactly one canonical workspace schema and every workspace schema maps
to exactly one accepted workspace row. The manifest pins the object inventory
and semantic shape inside every allowed product schema.

The two Twenty catalogs are compared semantically rather than by unstable row
bytes. For `core._typeorm_migrations`, the manifest pins ordered migration
identity/name and catalog shape while permitting only the documented generated
numeric identifier representation. For `core.upgradeMigration`, it pins the
ordered command name, command hash/version, scope/workspace association,
terminal status, attempt cardinality, and schema shape while treating generated
row UUIDs and execution timestamps as nondeterministic values subject to type,
non-null, ordering, and bounded execution-window constraints. Unknown command
names/hashes, unknown or nonterminal statuses, extra attempts, duplicate scope,
impossible timestamps, reordered commands, or column/constraint drift fail.
Queries are compiled, static catalog queries with bound parameters; the
verifier never executes SQL supplied by a model, document, profile, or
manifest. Verification runs after setup, after instance commands, and after
every timeout, cancellation, recovery, or restart decision, so a partial setup
state cannot be mistaken for an authorized migration retry.

## Amendment 6: acceptance-only compatibility endpoint

Phase 1C-B keeps the selected backend explicitly `RedisCompatibility`. It does
not select `postgres-desktop`, create pg-boss tables, bundle a final Redis
runtime, or claim Redis-free readiness.

The first acceptance candidate is Microsoft Garnet `1.1.10`, supplied by the
test harness outside the immutable Twenty payload. Garnet is MIT licensed,
runs on Windows/.NET, implements RESP, and supports Lua when explicitly
enabled. Its own documentation states that it is not a perfect Redis drop-in,
so those facts are eligibility for testing, not compatibility evidence.

The candidate may be used only when all of these gates pass for the exact
binary hash used by acceptance:

- loopback-only bind on an AppHost-reserved port;
- exact version/hash and MIT license inventory;
- no persistent data outside the disposable acceptance root;
- AppHost-owned PID/creation-time and Job membership;
- the fork's exact BullMQ/ioredis clients can create a queue, load required Lua
  scripts, enqueue a unique job, receive it in a worker, observe completion,
  and close without residue; and
- the complete real Twenty server/worker canary gate passes.

The release manifest derives a static fingerprint inventory of the pinned
BullMQ version and every command, Lua script, blocking operation, event path,
retry/stall transition, delayed-job primitive, and cleanup operation reachable
from Twenty's registered queue driver. Candidate preflight must exercise that
inventory through the exact installed clients, and the acceptance canary must
traverse the real supervised worker path. A small enqueue/dequeue smoke alone
cannot establish the bounded compatibility claimed by this phase.

Failure of candidate identity, containment, or static/client preflight yields
`compatibility-runtime-dependency-unavailable` before either real role can be
ready. Failure of the later real-worker canary yields the same classification
after internal role readiness but before AppHost publishes the accepted
application-ready result. Neither case authorizes weakening BullMQ tests,
silently choosing a different backend, or describing Garnet as the final Local
Desktop architecture.

## Amendment 7: application canaries and residue proof

After both real roles are ready, AppHost launches an acceptance orchestrator as
a third one-shot child from an exact normalized manifest entrypoint. It uses an
ordinary-file payload closure, the same immutable configuration bootstrap, a
minimal allowlisted environment, its own AppHost bootstrap lease and deadline,
and the AppHost Job. AppHost verifies PID/creation time, executable and
entrypoint identity, Job membership, and the final bounded result; the
orchestrator cannot start processes or extend its deadline. It performs these
ordered operations through real application boundaries:

1. fetch the served frontend index and at least one manifest-declared asset;
2. create a uniquely named disposable workspace through the real signup/API
   flow;
3. create, read, and update one disposable CRM record inside that workspace
   through the real authenticated API;
4. request a complete controlled server/worker restart through the authenticated
   AppHost acceptance boundary, without starting a process itself, and read the
   updated record again to prove PostgreSQL persistence after both roles return
   to verified readiness;
5. delete that record through the real API;
6. enqueue one uniquely identified BullMQ canary only after post-restart worker
   readiness;
7. prove the supervised worker's dedicated acceptance processor completed that
   exact job once and returned the expected bounded result; and
8. remove the job/events/queue keys and all disposable application state.

BullMQ is at-least-once under failure. Therefore an exactly-once claim is
limited to the observed acceptance canary: a unique job ID, one processor-side
receipt, zero retry/stall count, one completed result, and no duplicate receipt.
The phase does not claim general exactly-once queue delivery.

The acceptance processor is not enabled by an environment value alone. Its
module registration requires an unforgeable in-process acceptance construction
created only by the trusted desktop worker bootstrap after it has validated the
AppHost role, build, protocol, generation, acceptance lease, and exact processor
registry digest. The processor is a named member of that release-pinned worker
registry, accepts only the fixed bounded canary schema and current lease ID, and
is absent from ordinary production construction. Unknown processors, an
acceptance-looking environment variable without the trusted construction, or a
registry digest mismatch prevents worker readiness.

The workspace database/profile is disposable and never points at the user's
real `%LOCALAPPDATA%\HowardLab\eBayCRM` tree. Final cleanup proves zero retained
AppHost, PostgreSQL, Node, migration, or compatibility-endpoint process;
zero retained database/profile/queue residue; unchanged immutable root; and no
registered secret or canary value in arguments, diagnostics, logs, payloads,
or persisted artifacts.

## Acceptance implications

The Phase 1C-B implementation plan must preserve every original mandatory gate
and add explicit tasks for the amended boundaries above. It must define the
single Phase 1C-B decision-token family; this amendment intentionally defines
no outcome token so the family cannot be duplicated.

No adoption result is supportable unless the final candidate passes the whole
matrix with zero unexpected skips. If Garnet or any other acceptance-only
dependency fails the exact BullMQ/real-Twenty proof, the honest result is a
revision outcome, not an adoption claim and not a Redis-free claim.

## Primary external references

- Current Twenty source and production commands:
  <https://github.com/twentyhq/twenty>
- Current upstream identity inspected during validation:
  <https://github.com/twentyhq/twenty/commit/1b9152d4c5eed4213be364f93532940bb80c37c6>
- Official Node `24.18.0` distribution checksums:
  <https://nodejs.org/dist/v24.18.0/SHASUMS256.txt>
- Nest application lifecycle and standalone contexts:
  <https://docs.nestjs.com/fundamentals/lifecycle-events>
- BullMQ graceful worker shutdown and job completion behavior:
  <https://docs.bullmq.io/guide/workers/graceful-shutdown>
- TypeORM migration setup and transaction modes:
  <https://typeorm.io/docs/migrations/setup>
- Yarn focused production installs:
  <https://yarnpkg.com/cli/workspaces/focus>
- Garnet Windows support, API compatibility, and license:
  <https://microsoft.github.io/garnet/docs/getting-started>
  <https://microsoft.github.io/garnet/docs/welcome/compatibility>
  <https://github.com/microsoft/garnet/blob/main/LICENSE>
