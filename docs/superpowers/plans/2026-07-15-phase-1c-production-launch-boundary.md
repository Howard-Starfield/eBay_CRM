# Phase 1C Production Launch Boundary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> `superpowers:subagent-driven-development` and
> `superpowers:test-driven-development`. Implement one task at a time. A task is
> complete only after specification review, code-quality review, and primary
> agent verification.

**Goal:** Replace the fixture-hardwired server/worker launch composition with a
trusted production-shaped launch boundary, prove the existing AppHost protocol
with controlled Node children, and fail closed while the Redis-free runtime is
incomplete.

**Architecture:** A trusted provider creates a static role plan before AppHost
creates the generation-bound control channel. AppHost rejects reserved-key
collisions, merges its control environment, launches the child in the existing
Job Object, and retains all lifecycle responsibility. Both fixture and Node
children use the versioned challenge-first named-pipe control exchange and the
existing identity-bound HTTP health payload. Phase 1C-A does not import full
Twenty or enable pg-boss.

**Tech stack:** .NET 10 LTS, C# 14, xUnit 2.9, Node 24, TypeScript 5.9,
`node:test`, Windows named pipes/Job Objects, PostgreSQL 16.14, existing
AppHost control protocol.

## Global constraints

- Work on `codex/phase-1c-production-launch-boundary`, based on `118e722d`.
- Preserve PostgreSQL and every existing Twenty schema.
- Do not add Redis, Docker, a Windows service, Electron, Tauri, or a second
  supervisor.
- Do not enable the pg-boss logical overlay or change queue semantics.
- Do not import the full NestJS server or worker during Phase 1C-A.
- Do not pass secrets in arguments, profile files, packaged `.env` files,
  diagnostic fields, or exception text.
- Provider environment cannot declare or override AppHost control keys.
- Preserve the current Health payload and existing Hello identity fields while
  version 2 adds only the identity challenge/echo. Preserve the exact drain sequence:
  `DrainAccepted`, `NoNewWorkAcquisition`, one `ActiveWorkRemaining`,
  `Drained`.
- An identical duplicate drain replays the complete cached sequence without
  executing drain twice.
- Keep all tests focused on the changed boundary. Run the recorded Phase 1B
  partitions before an adoption claim.
- Commit after every reviewed task so later agents receive one stable base.

---

## Task 1: Extract the trusted role-launch contract

**Goal:** Make fixture launching one provider implementation while preserving
all Phase 1B behavior.

**Expected files:**

- Add: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/IRoleLaunchPlanProvider.cs`
- Add: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/RoleLaunchPlan.cs`
- Add: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/FixtureRoleLaunchPlanProvider.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/LifecycleCommandExecutor.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AppHostComposition.cs`
- Add/modify focused tests under
  `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/AppHost/`
- Modify only fixture-provider wiring, not assertions or lifecycle semantics, in:
  - `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Acceptance/TimeoutReconciliationAcceptanceTests.cs`
  - `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/AppHost/AppHostRecoveryTests.cs`
  - `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/AppHost/AppHostShutdownTests.cs`

**Contract:**

- A generic request contains only role and complete process generation. Fixture
  behavior is injected into the fixture provider by internal test composition;
  it never appears in the generic request or executor.
- The static provider result remains bound to role and complete generation and
  contains application path, ordered arguments, working directory, ordinary
  environment, secret environment, build identity, readiness strategy,
  optional health port, output-drain timeout, and a bootstrap artifact-lease
  factory.
- The provider-supplied ordinary and secret environment maps contain no pipe,
  nonce, role, generation, operation, or AppHost build reserved keys. The plan
  itself remains bound to the request's role and complete generation.
- `LifecycleCommandExecutor` obtains the static plan first, creates the control
  channel with its build identity, rejects key collisions, merges child control
  environment, opens the artifact lease, and launches through the existing
  `IProcessLauncher`.
- The provider does not own Job Objects, channels, process identity, health
  polling, reconciliation, or disposal.
- Plan validation compares Windows environment names case-insensitively. It
  rejects duplicates within either map, collisions between ordinary and secret
  maps, and every AppHost-reserved key. Every provider/plan validation failure
  maps to stable reason code `role-launch-plan-invalid` before launch.

**Task-specific prohibitions:** Do not change `AppHostOptions`, CLI parsing,
control protocol, health payload/semantics, lifecycle coordinator, PostgreSQL,
runtime-backend selection, Node files, or the existing fixture trust algorithm.

- [ ] Write failing tests that assert provider output is used for server and
  worker executable/arguments/working directory/build identity.
- [ ] Write failing tests that ordinary and secret reserved-key collisions fail
  before `IProcessLauncher.LaunchAsync`.
- [ ] Write failing tests that provider failure opens no lease and launches no
  process, and that an opened artifact lease spans launch and is disposed after
  launch success, launch failure, and cancellation.
- [ ] Implement the minimal contract and fixture provider.
- [ ] Remove fixture-specific launch construction and fixture-named reason codes
  from generic executor paths where the reason is role-generic.
- [ ] Run focused integration tests and the full AppHost unit/integration
  solution partitions.
- [ ] Run `git diff --check` and a secret/control-key search.

**Primary verification:** Run the new provider-focused tests first, then the
authoritative ordinary and destructive partitions separately:

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Core.Tests\HowardLab.EbayCrm.AppHost.Core.Tests.csproj --configuration Release --no-restore --nologo
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Windows.Tests\HowardLab.EbayCrm.AppHost.Windows.Tests.csproj --configuration Release --no-restore --nologo
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --filter "Category!=DestructiveContainment" --nologo
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --filter "Category=DestructiveContainment" -- RunConfiguration.DisableAppDomain=true
```

**Commit:** `refactor: extract trusted role launch provider`

---

## Task 2: Add cross-language protocol golden vectors

**Goal:** Freeze the current C#/Node protocol shape before implementing the Node
control client.

**Expected files:**

- Add: `desktop/windows/protocol/control-protocol-v2.golden.json`
- Add: `desktop/windows/node/package.json`
- Add: `desktop/windows/node/tsconfig.json`
- Add: `desktop/windows/node/src/protocol/*.ts`
- Add: `desktop/windows/node/test/protocol-golden.test.ts`
- Modify: root `package.json`
- Modify: root `yarn.lock`
- Modify: protocol and fixture C# files/projects required for version 2
- Modify/add C# tests under
  `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Core.Tests/Protocol/`

**Toolchain:** `desktop/windows/node` is intentionally not an independent Yarn
workspace and has no lockfile. Root Yarn owns Node 24, TypeScript, `tsx`, and a
direct root `@types/node` development dependency. Commands execute through the
root lockfile:

```powershell
yarn exec tsc --project desktop/windows/node/tsconfig.json --noEmit
yarn exec tsx --test desktop/windows/node/test/**/*.test.ts
```

**Golden data:**

- Envelope framing uses the current little-endian 32-bit byte length and JSON
  serialization rules.
- Increment the protocol version and add AppHost-to-child
  `IdentityChallenge`. Version-2 Hello adds only the echoed challenge ID to the
  existing Hello data fields.
- `WindowsControlChannel` verifies the connected pipe client PID, exact creation
  time, and Job membership before sending the challenge. The fixture and Node
  client echo the challenged PID/ticks/ID. Creation ticks are a bounded,
  canonical unsigned decimal string in JSON and are parsed invariantly to
  .NET `long`/Node `bigint`; they are never represented as a JavaScript number.
  No JavaScript time approximation is permitted.
- Challenge IDs are cryptographically random per endpoint. Exactly one
  challenge is allowed, frame/direction/size budgets apply, v1 has no fallback,
  and authentication is published only after valid HelloV2.
- Include valid challenge/Hello, drain sequence, shutdown sequence, and Health
  payload.
- Include real-magnitude creation-tick strings plus Hello-before-challenge,
  duplicate/conflicting challenge/Hello, wrong identity, cross-generation,
  timeout, cancellation, and v1-downgrade rejection vectors.
- Include boundary-valid text/frame lengths and invalid oversize, stale
  generation, wrong operation, wrong build, wrong nonce, and out-of-order
  examples.
- Golden files contain synthetic non-secret values only.

- [ ] Add a failing C# golden-vector test that writes/reads the shared artifact.
- [ ] Add a failing Node golden-vector test using `node:test`.
- [ ] Implement a dependency-light Node frame codec and typed payload parser.
- [ ] Prove C#-encoded frames are Node-decodable and Node-encoded frames are
  C#-decodable byte-for-byte.
- [ ] Prove bounds are checked before allocation or JSON parsing.
- [ ] Run C# protocol tests, Node tests, TypeScript typecheck, and formatting.

**Commit:** `test: freeze cross-language AppHost protocol`

---

## Task 3: Implement the Node control shim and probes

**Goal:** Supervise controlled Node server and worker processes through the real
AppHost lifecycle.

**Expected files:**

- Add: `desktop/windows/node/src/control/apphost-control-client.ts`
- Add: `desktop/windows/node/src/control/identity-health-server.ts`
- Add: `desktop/windows/node/src/probes/server-probe.ts`
- Add: `desktop/windows/node/src/probes/worker-probe.ts`
- Add focused Node unit tests.
- Add Node-probe provider/composition files under the AppHost project.
- Add integration tests under
  `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Node/`

**Behavior:**

- Read control keys once, validate them, then remove secret values from
  `process.env` where practical.
- Connect only to the named pipe supplied by AppHost.
- Receive the version-2 identity challenge and echo its exact PID,
  creation-time ticks, and challenge ID in Hello. Do not use
  `process.uptime()`, WMI, PowerShell, or wall-clock approximation.
- Bind the loopback listener before Hello. AppHost performs bounded readiness
  polling after authentication; the bound endpoint returns not-ready until
  role initialization completes and then returns the current Health payload.
- Poll without overlapping requests until the existing role-readiness deadline.
  Retry only HTTP-success, identity-matching `not-ready`; fail immediately on
  malformed or mismatched identity, unexpected HTTP/status, process exit, or
  control loss. Deadline expiry records `role-readiness-timeout` and uses the
  existing indeterminate reconciliation path.
- Bind only `127.0.0.1` on the AppHost-reserved port and serve the current
  identity-bound Health payload.
- Implement the exact four-frame drain sequence and full cached replay for an
  identical duplicate operation.
- A stale/different operation, malformed frame, unexpected direction, oversized
  frame, or disconnect fails closed.
- Support trusted test-only delays, crash points, drain refusal, and bounded
  stdout/stderr output without exposing them to production CLI/profile/env.

- [ ] Write failing Node state-machine tests for normal, duplicate, stale,
  malformed, crash, and timeout behavior.
- [ ] Implement the minimal control client and health server.
- [ ] Write failing AppHost integration tests for Node server and worker start,
  ready-within-deadline, not-ready-beyond-deadline, drain, stop, late
  start/stop, crash restart, and output flooding.
- [ ] Publish probes with the repository-pinned Node/TypeScript toolchain.
- [ ] Run the published AppHost against the controlled Node probes.
- [ ] Assert no owned descendant remains after every destructive case.

**Commit:** `feat: supervise controlled Node roles`

---

## Task 4: Add production environment, trust, and runtime-readiness gates

**Goal:** Make the Node launch shape production-safe without claiming a full
Twenty boot.

**Expected files:**

- Add an allowlisted role environment builder under AppHost composition.
- Add a versioned Node payload-manifest model and Windows validator under the
  AppHost Windows project.
- Add focused Windows trust/ACL/reparse tests.
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AppHostOptions.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AppHostComposition.cs`
- Modify: production role-provider/composition files created by earlier tasks.
- Modify: `packages/twenty-server/src/engine/core-modules/twenty-config/config-variables.ts`
- Modify runtime-backend configuration tests under `packages/twenty-server`.
- Modify AppHost probe/preflight tests.

**Behavior:**

- Build ordinary and secret environment separately.
- Ordinary allowlist includes only required OS/runtime keys and explicit role
  configuration.
- `PG_DATABASE_URL`, application/session secrets, and control pipe/nonce are
  secret values.
- Reject `.env`, `NODE_OPTIONS` preloads, command-line secrets, inherited Redis
  inference, and every reserved-key collision.
- Validate normalized manifest paths, length, SHA-256, root ownership/DACL, and
  every path component for reparse points.
- Lease the manifest, `node.exe`, and bootstrap-critical entrypoints through
  launch; re-verify the complete closure after shutdown.
- A real `postgres-desktop` launch returns
  `postgres-desktop-runtime-incomplete` before either Node role launches.
- Compatibility mode must be explicit. Missing or malformed backend values fail
  closed.
- Focused queue contract tests retain direct access to their experimental
  backend without using production AppHost composition.

- [ ] Write failing environment and trust tests first.
- [ ] Implement the minimal allowlist, manifest validator, and artifact lease.
- [ ] Write failing preflight tests proving no launcher call occurs for the
  incomplete runtime.
- [ ] Implement the runtime-readiness gate without changing queue semantics.
- [ ] Run boundary checks and targeted server configuration tests.

**Commit:** `feat: gate trusted production role launches`

---

## Task 5: Phase 1C-A acceptance and evidence

**Goal:** Produce an honest adopt/revise/reject decision for the production
launch boundary.

**Expected files:**

- Add: `docs/architecture/phase-1c-production-launch-boundary-report.md`
- Add/update release-verification scripts under `desktop/windows/scripts/`.
- Update `desktop/windows/README.md` with exact reproducible commands.

- [ ] Publish the AppHost and controlled Node probes from clean inputs.
- [ ] Run every authoritative non-overlapping Phase 1B partition and prove no
  recorded test disappeared.
- [ ] Run every Phase 1C Node/C#/integration/trust/preflight partition with zero
  failures and zero unexpected skips.
- [ ] Run destructive late-start, late-stop, crash, output-flood, and external
  AppHost termination tests.
- [ ] Scan arguments, output, diagnostics, manifests, and profile artifacts for
  registered secret canaries.
- [ ] Query exact executable identities and prove no AppHost, Node probe,
  PostgreSQL helper/postmaster/backend, `cmd.exe`, or `conhost.exe` owned by the
  run remains.
- [ ] Record commands, versions, counts, artifacts, limitations, and exactly one
  decision token.
- [ ] Request final specification and code-quality reviews.

**Outcome token:**

- `ADOPT_PRODUCTION_LAUNCH_BOUNDARY`, or
- `REVISE_PRODUCTION_LAUNCH_BOUNDARY`, or
- `REJECT_PRODUCTION_LAUNCH_BOUNDARY`.

**Commit:** `docs: record Phase 1C launch-boundary evidence`

---

## Deferred Phase 1C-B plan gate

Do not implement the real Twenty compatibility boot from this checklist alone.
After Phase 1C-A records `ADOPT_PRODUCTION_LAUNCH_BOUNDARY`, write a separate
execution plan covering Windows-native production dependency closure, immutable
payload construction, supervised and database-verified Twenty migrations,
test-managed Redis compatibility preflight, real server/worker readiness,
post-ready workspace/BullMQ canaries, schema drift checks, and cleanup evidence.

Phase 1C-B still does not authorize a Redis-free claim. Queue hardening and the
remaining Redis responsibility ports retain their own later acceptance gates.
