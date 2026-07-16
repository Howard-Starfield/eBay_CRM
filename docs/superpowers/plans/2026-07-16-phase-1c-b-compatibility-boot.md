# Phase 1C-B Compatibility Boot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Boot the real Twenty server and worker through the reviewed Windows AppHost from a trusted installed-like payload, run supervised fresh-database setup and migrations against AppHost-owned PostgreSQL, prove real frontend/workspace/CRM persistence and one BullMQ worker canary through an explicitly named `RedisCompatibility` candidate, and make no Redis-free claim.

**Architecture:** A generated Windows payload is built with one exact official Node distribution, physically materialized after Yarn production focus, and authenticated by an AppHost-anchored canonical manifest digest. Thin Node role wrappers reuse the reviewed authenticated control client while real Nest bootstraps expose readiness and concrete drain adapters; AppHost remains sole process, Job, database, migration, compatibility-endpoint, restart, and acceptance owner. A typed Npgsql verifier treats PostgreSQL state as migration truth, while Garnet remains an acceptance-only dependency whose exact package, process, BullMQ script surface, and real-worker behavior must all pass before the phase can be adopted.

**Tech Stack:** Windows 11 x64; Windows PowerShell 5.1-compatible release scripts (also exercised under PowerShell 7); Node.js 24.18.0 win-x64; Yarn 4.13.0; TypeScript 5.9.3; NestJS 11; BullMQ from the pinned lockfile; .NET SDK 10.0.302 / `net10.0-windows`; private .NET Runtime 8.0.29 win-x64 for the Garnet tool only; Npgsql 10.0.3; PostgreSQL 16.14; xUnit 2.9.3; Garnet `garnet-server` 1.1.10 as an acceptance-only candidate.

## Global Constraints

- Work only in `.worktrees/phase-1c-b-compatibility-boot` on `codex/phase-1c-b-compatibility-boot`, based on `b58cd17818ca1af2adce080ac37bca14d0940841`.
- Preserve the approved amendment `docs/superpowers/specs/2026-07-16-phase-1c-b-compatibility-boot-design-amendment.md` and every Phase 1C-A trust, control, readiness, drain, Job, migration-marker, cleanup, and diagnostic invariant.
- The runtime backend is exactly `RedisCompatibility`; never choose `PostgresDesktop`, create pg-boss product tables, silently fall back, or claim Redis-free readiness.
- Use only official `https://nodejs.org/dist/v24.18.0/node-v24.18.0-win-x64.zip`, archive SHA-256 `0AE68406B42D7725661DA979B1403EC9926DA205C6770827F33AAC9D8F26E821`, extracted `node.exe` SHA-256 `9A4EB5F1C29C6A2E93852EAD46B999E284A6A5CA8BAB4D4E241D587D025A52DE`, and a valid OpenJS Foundation Authenticode chain.
- The extracted Node file that runs the build must be byte-identical to the manifested runtime Node file. Runtime may not invoke Yarn, npm, npx, Corepack, or a system Node.
- Use repository Yarn `node .yarn/releases/yarn-4.13.0.cjs`; all repository installs use `--immutable`. Verification restores use `--locked-mode`; Task 7 may run one reviewed `--force-evaluate` restore solely to add exact Npgsql 10.0.3 to the lockfile, immediately followed by a locked restore.
- Pin Npgsql `10.0.3` in the Windows project lockfile. Use `NpgsqlDataSource`, async opens with cancellation, explicit transactions where needed, typed reads, and bound parameters; SQL text is compiled static source only.
- The Garnet candidate is `garnet-server` 1.1.10 from the official NuGet flat-container URL. Its NuGet catalog SHA-512 is `6ae1EHr76KhprSWzY+HEvL4idOqmPn/iHjciAFdAJjdPcm5CdhXDD5G154MPJ1HxL47apNEsR+V5K80e2ukQ9Q==` and its license expression is `MIT`.
- Garnet's selected `tools/net8.0/any/GarnetServer.dll` is framework-dependent. Its only accepted host is the official private `dotnet-runtime-8.0.29-win-x64.zip` from `https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.29/dotnet-runtime-8.0.29-win-x64.zip`, SHA-512 `e3f31d298a2b674b54c7fc89fb3f06d9645fc5879a54f2ebf2ea20e9ee7ae55f1bfe3284c1f90a591d6be2d6bcd251790ddc27771d65303e7a6a56d331df4632`, under the official .NET MIT license. Never use a system `dotnet`, SDK, `dotnet tool install`, or roll-forward outside the cataloged private 8.0.29 runtime; patch resolution is constrained to that private root.
- Never execute SQL, commands, paths, scripts, or environment keys supplied by a model, document, profile, database row, or manifest. Release manifests contain data identities and hashes, not executable text.
- Never log a secret value, full environment block, full child command line, API token, generated password, canary body, or raw exception that may contain one. Register every generated secret before launch.
- Never use the real `%LOCALAPPDATA%\HowardLab\eBayCRM` profile in tests. Every destructive test must prove a canonical local disposable root, reject every reparse component, own exact PIDs and creation times, and clean only those owned objects.
- The final payload contains ordinary files/directories only. Reject symlinks, junctions, mount points, other reparse points, path escapes, alternate data streams, case collisions, untrusted write/delete ACLs, and native addons that are not PE x64 or resolve outside the payload.
- Generated `node_modules`, `.tools`, `.superpowers`, `desktop/windows/artifacts`, downloaded archives, build roots, logs, profiles, database data, Garnet files, and evidence scratch files must remain ignored and uncommitted.
- No task may weaken a failing gate or convert an unexpected skip into success. A candidate dependency failure is evidence for revision, not permission to substitute another backend.
- Each task uses red-green-refactor, then a fresh specification review and a separate quality review. The primary agent reruns the focused commands, fixes all findings with the same implementer, and commits only after both reviews approve.

## Per-task subagent protocol

For every numbered task, the primary agent writes the ignored ledger at `.superpowers/sdd/progress.md` and sends one fresh implementer a Task Brief containing the exact task text, current HEAD, allowed file set, required failing/passing commands, inherited global constraints, and instruction not to commit. The implementer returns a Task Report with changed files, red evidence, green evidence, unresolved risks, and `git diff --check` result.

The primary agent independently inspects the diff and reruns the focused commands, then sends a Review Package—Task Brief, Task Report, current diff, command evidence, governing amendment sections, and prior finding history—to a fresh specification reviewer. Only after specification PASS does a separate fresh reviewer assess architecture, code quality, security, test quality, and regression risk. Any finding returns to the same implementer with exact evidence; the primary repeats focused verification and both required review gates until PASS/APPROVED. The primary then makes the task commit, records its hash/counts in the ledger, and starts the next task with a new implementer.

## Decision-token policy

This plan owns the only Phase 1C-B token family:

- `ADOPT_REAL_TWENTY_COMPATIBILITY_BOOT`
- `REVISE_REAL_TWENTY_COMPATIBILITY_BOOT`
- `REJECT_REAL_TWENTY_COMPATIBILITY_BOOT`

The final evidence report must contain exactly one member of that family. Adoption requires the complete matrix with zero unexpected skips and unchanged trusted payload. Revision covers a bounded implementation or candidate-compatibility failure that leaves the safety model intact. Rejection requires evidence that a mandatory invariant is unsafe or impossible without changing the approved architecture. Do not emit any Phase 1C-A outcome token.

## File responsibility map

### Payload trust and build

- `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload/ProductionPayloadManifestV2.cs`: immutable v2 header/file records and hard bounds.
- `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload/ProductionPayloadCanonicalizer.cs`: one canonical UTF-8 serialization and full semantic digest.
- `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload/ProductionPayloadValidator.cs`: streaming manifest/filesystem equality, canonical path, ACL, native-addon, and artifact-lease validation.
- `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload/ProductionReleaseCatalog.cs`: read the AppHost-embedded accepted digest and compatibility tuple.
- `desktop/windows/tools/HowardLab.EbayCrm.PayloadTool/`: generate/verify production manifest and release-catalog resource without becoming runtime payload.
- `desktop/windows/scripts/Build-Phase1CBPayload.ps1`: exact Node acquisition, clean staged full build, production refocus, physical materialization, payload generation, and AppHost publish.

### Twenty bootstrap and drain

- `packages/twenty-server/src/desktop/immutable-desktop-mode.ts`: explicit immutable-mode predicate and dotenv guard.
- `packages/twenty-server/src/desktop/server-admission-gate.ts`: stop HTTP admission and count admitted requests.
- `packages/twenty-server/src/desktop/server-drain-controller.ts`: bounded server drain and Nest close.
- `packages/twenty-server/src/desktop/worker-drain-controller.ts`: pause registered BullMQ workers, observe active handlers, and close queue/application objects.
- `packages/twenty-server/src/desktop/acceptance/`: trusted acceptance construction, fixed canary processor, and registry digest.
- `desktop/windows/node/src/production/`: generic controlled-role runner, real server/worker wrappers, one-shot setup/migration wrappers, compatibility preflight, and acceptance orchestrator.

### AppHost production ownership

- `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/ProductionTwentyRoleLaunchPlanProvider.cs`: exact real-role entrypoints, environments, leases, readiness tuple, and post-exit closure.
- `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/ProductionDatabaseCoordinator.cs`: backup, advisory lock, ordered one-shots, state classification, and authorization to start roles.
- `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Postgres/ProductionDatabaseStateVerifier.cs`: static Npgsql catalog queries and semantic comparison.
- `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/GarnetCompatibilityEndpoint.cs`: acceptance-only candidate identity, launch, Job ownership, readiness, and cleanup.
- `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/Phase1CBAcceptanceCoordinator.cs`: frontend/API/restart/CRUD/canary ordering and final bounded result.

### Evidence and gates

- `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/`: real database, role, compatibility, restart, and residue acceptance suites.
- `desktop/windows/scripts/Verify-Phase1CB.ps1`: focused and full gates with unconditional restoration/cleanup.
- `desktop/windows/scripts/Invoke-Phase1CBColdClosure.ps1`: fail-closed Windows Sandbox clean-account boot with no repository, Yarn, global modules, or system Node.
- `desktop/windows/scripts/Test-Phase1CBCleanup.ps1`: exact owned-process, artifact, profile, queue, and immutable-root audit.
- `docs/architecture/phase-1c-b-compatibility-boot-report.md`: final commands, counts, hashes, limitations, and exactly one decision token.

---

### Task 1: Canonical production manifest and AppHost trust anchor

**Files:**

- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload/ProductionPayloadManifestV2.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload/ProductionPayloadCanonicalizer.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload/ProductionPayloadValidator.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload/ProductionReleaseCatalog.cs`
- Create: `desktop/windows/runtime/production/empty-release-catalog-v1.json`
- Create: `desktop/windows/runtime/production/production-entrypoints-v1.json`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Payload/ProductionPayloadCanonicalizerTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Payload/ProductionPayloadValidatorTests.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/HowardLab.EbayCrm.AppHost.csproj`

**Interfaces:**

- Produces: `ProductionPayloadManifestV2`, `ProductionPayloadHeader`, `ProductionPayloadFileRecord`, `ProductionPayloadBounds`, `ProductionPayloadCanonicalizer.ComputeDigest(...)`, `ProductionPayloadValidator.Validate(...)`, and `ProductionReleaseCatalog.Load(Assembly)`.
- Produces: `ValidatedProductionPayload` with exact normalized entrypoints, build/compatibility identities, `OpenLifetimeLease()`, `OpenBootstrapLease(string entrypoint)`, and `VerifyClosure()`.
- Produces a strict, intentionally staged `ProductionEntrypointInventoryV1` source artifact with a discriminated schema: every `launchExecutableJs` record binds a manifest launch role, source wrapper, emitted payload-relative JavaScript path, and dotenv/import-safety classification; every `importRootJs` record binds a non-launch dynamically imported Twenty target source/emitted path and classification but is explicitly excluded from manifest-header equality; and the single `frontendAsset` record binds the frontend header role to its literal payload-relative `dist/front/index.html` path and build provenance without pretending it is an importable module. Unknown kinds/fields, incomplete records, duplicate source/emitted paths, or path aliases always fail. The checked-in list grows only in the tasks that create roles; completeness against every final launch role is enforced by Task 10/default full publish, so Task 1's schema artifact does not pretend later wrappers already exist.
- Consumes: existing `TrustedApplicationRootSecurity`, `NodePayloadPath`, `TrustedNodePayloadArtifactLease`, and Windows file identity helpers without modifying manifest v1 behavior.

- [ ] **Step 1: Write failing canonicalization and trust tests**

Write `AnySemanticMutationChangesCanonicalDigest` as an exhaustive theory whose
case source is mechanically compared with the declared header property set, so
a new property without a mutation case fails. Cover manifest schema version,
source commit, build identity, Node/Yarn versions, target RID, protocol and
generation identities, server/worker/setup/instance-command/acceptance/
acceptance-cleanup/compatibility-preflight/frontend entrypoints, database and
frontend-configuration digests, and every bound. Each row builds one valid
fixture, clones it with only the named field changed to a second valid value,
canonicalizes both, and asserts different lowercase SHA-256 digests. Write
`RedirectToAnotherDeclaredFileFailsBeforeLeaseOpen` with two declared equal-size
fixture files: mutate only the entrypoint header after anchoring the digest,
assert `production-manifest-digest-mismatch`, and assert the lease factory mock
was never invoked. Write `EnumerationMustEqualEveryDeclaredOrdinaryFile` as two
fresh fixtures, one with a declared file removed and one with an undeclared file
added, asserting the exact missing/extra reason and zero lease opens.

Cover UTF-8 without BOM, ordinal record order, case collision, duplicate ordinal, path escape, ADS syntax, device names, every ancestor reparse point, file-count/path/manifest/aggregate bounds, length/hash mismatch, self-digest mismatch, untrusted ACL, and critical handle no-delete sharing.

- [ ] **Step 2: Run the tests and verify red**

Run:

```powershell
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/HowardLab.EbayCrm.AppHost.Windows.Tests.csproj --configuration Release --filter FullyQualifiedName~ProductionPayload --nologo
```

Expected: build/test failure because the v2 types do not exist.

- [ ] **Step 3: Implement the bounded canonical model and validator**

Use sealed records and fixed bounds:

```csharp
public sealed record ProductionPayloadBounds(
    int MaxFiles = 500_000,
    int MaxRelativePathChars = 512,
    long MaxManifestBytes = 128L * 1024 * 1024,
    long MaxAggregateBytes = 8L * 1024 * 1024 * 1024);

public sealed record ProductionPayloadFileRecord(
    int Ordinal,
    string RelativePath,
    long Length,
    string Sha256);

public sealed record ValidatedProductionPayload(
    string Root,
    ProductionPayloadHeader Header,
    IReadOnlyList<ProductionPayloadFileRecord> Files,
    Func<IDisposable> OpenLifetimeLease,
    Func<string, IDisposable> OpenBootstrapLease,
    Action VerifyClosure);
```

The 500,000-file/128 MiB manifest bounds are deliberate: the fresh full
development install contains 370,859 files and 3,232,660,273 bytes before
production focus, so the bounds leave measured headroom while remaining finite.

Canonicalize every header field in a fixed order, then every sorted record, exclude only the digest value slot from its own calculation, require the stored digest to equal the AppHost-embedded digest, and verify the complete filesystem independently. Never deserialize an executable path and use it before the canonical digest succeeds.

- [ ] **Step 4: Add the generated release-catalog embedding boundary**

Make `HowardLab.EbayCrm.AppHost.csproj` embed a supplied `ProductionReleaseCatalogPath` during the real acceptance publish. Ordinary builds embed a checked-in empty rejection catalog whose only valid result is `production-release-catalog-unavailable`; `controlled-twenty` may never run against it.

- [ ] **Step 5: Run focused and regression tests**

Run:

```powershell
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/HowardLab.EbayCrm.AppHost.Windows.Tests.csproj --configuration Release --filter "FullyQualifiedName~ProductionPayload|FullyQualifiedName~TrustedNodePayload" --nologo
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter FullyQualifiedName~PublishedNodeProbe --nologo
```

Expected: all selected tests pass; manifest v1 probe behavior is unchanged.

- [ ] **Step 6: Review and commit**

After fresh specification and quality approvals:

```powershell
git add desktop/windows/runtime/production/empty-release-catalog-v1.json desktop/windows/runtime/production/production-entrypoints-v1.json desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Payload desktop/windows/src/HowardLab.EbayCrm.AppHost/HowardLab.EbayCrm.AppHost.csproj
git commit -m "feat(windows): trust production payload manifests"
```

### Task 2: Exact Windows build toolchain and installed-like payload

**Files:**

- Create: `desktop/windows/tools/HowardLab.EbayCrm.PayloadTool/HowardLab.EbayCrm.PayloadTool.csproj`
- Create: `desktop/windows/tools/HowardLab.EbayCrm.PayloadTool/Program.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload/ProductionPayloadBuilder.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Payload/ProductionPayloadBuilderTests.cs`
- Create: `desktop/windows/scripts/Build-Phase1CBPayload.ps1`
- Create: `packages/twenty-server/scripts/copy-build-assets.mjs`
- Create: `packages/twenty-server/scripts/copy-build-assets.test.mjs`
- Modify: `packages/twenty-server/project.json`
- Modify: `desktop/windows/EbayCrm.Desktop.sln`
- Modify: `.gitignore`

**Interfaces:**

- Consumes: Task 1 manifest/canonicalizer, the strict checked-in `production-entrypoints-v1.json`, and the exact global toolchain constraints.
- Produces: `ProductionPayloadBuilder.Build(ProductionPayloadBuildRequest)` and a CLI `manifest create|verify --root <absolute> --catalog-output <absolute>`.
- Produces generated roots `desktop/windows/artifacts/phase-1c-b/build`, `payload`, `catalog`, and `apphost`; none are source-controlled.

- [ ] **Step 1: Write failing builder boundary tests**

Test exact archive/executable hashes, Authenticode subject, clean copied build input, rejection of source `node_modules`, reparse materialization only inside build root, final reparse/ADS/case scan, PE x64 native addons, absence of Yarn/npm runtime entrypoints, frontend marker, physical workspace packages, and manifest equality. Add a fake process runner so unit tests assert exact argument vectors without downloading or building Twenty. Node tests run the portable asset copier in a disposable tree, prove byte-identical package/dist copies, reject missing/extra/reparse inputs, and prove it never invokes a shell or ambient `mkdir`/`cp`/`xcopy`/`robocopy`.

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/HowardLab.EbayCrm.AppHost.Windows.Tests.csproj --configuration Release --filter FullyQualifiedName~ProductionPayloadBuilder --nologo
node --test packages/twenty-server/scripts/copy-build-assets.test.mjs
```

Expected: build/test failure because `ProductionPayloadBuilder` is absent.

- [ ] **Step 3: Implement exact acquisition and clean staging**

`Build-Phase1CBPayload.ps1` must:

```powershell
param(
    [string] $RepositoryRoot = (Resolve-Path "$PSScriptRoot\..\..\.."),
    [string] $OutputRoot = "$RepositoryRoot\desktop\windows\artifacts\phase-1c-b",
    [string] $NodeArchivePath,
    [string] $CandidateCatalogPath,
    [switch] $Offline,
    [switch] $ClosureOnly
)
```

Resolve every root canonically; reject any reparse component; download only the pinned official URL when not offline; hash before extraction; validate `node.exe` hash and Authenticode; copy tracked source/build inputs into a clean generated root while excluding `.git`, `.worktrees`, `.superpowers`, `.tools`, all `node_modules`, and artifacts; prove no resulting resolved path points into the source checkout.

`-ClosureOnly` does not consume a candidate catalog. The default full publish
requires `-CandidateCatalogPath` from a successful current Task 6A staging run,
validates its canonical digest and every referenced file/ACL, and embeds that
exact catalog alongside the production release catalog; missing, stale, or
untrusted candidate input fails before AppHost publish.

- [ ] **Step 4: Implement full build then production prune**

Use the extracted payload Node for every command:

```powershell
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs install --immutable
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:lingui:extract
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:lingui:compile
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs nx run twenty-emails:lingui:extract
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs nx run twenty-emails:lingui:compile
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:build
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs nx run twenty-front:lingui:extract
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs nx run twenty-front:lingui:compile
$env:NODE_OPTIONS='--max-old-space-size=8192'
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs nx build twenty-front
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs exec tsc --project desktop/windows/node/tsconfig.publish.json --outDir $CompiledDesktopNodeRoot
& $PinnedNode .yarn/releases/yarn-4.13.0.cjs workspaces focus --production twenty-emails twenty-shared twenty-client-sdk twenty-server
```

First replace only the POSIX asset-copy command in `twenty-server`'s build target with `node scripts/copy-build-assets.mjs`; retain `rimraf`, Nest build, dependencies, outputs, and cache semantics. The helper uses only Node filesystem APIs with literal source/destination identities and canonical/reparse checks. A clean Windows test constrains `PATH` so no Unix copy tools are reachable and runs the real target through the exact pinned Node/Yarn, proving the server dist plus SDK assets are complete.

The publish TypeScript configuration is mandatory because the ordinary `desktop/windows/node/tsconfig.json` has `noEmit`; fail if any declared production wrapper is absent from `$CompiledDesktopNodeRoot`. Copy only root runtime metadata, physical production dependencies/workspaces, server `dist`, required assets, emitted desktop wrappers, exact `node.exe`, and frontend build to `packages/twenty-server/dist/front`. Delete no source path. Materialize internal workspace links as ordinary content, then reject every final reparse point and any native addon that fails PE x64/load probing with the exact Node.

- [ ] **Step 5: Generate the manifest and publish AppHost with its anchored catalog**

When every inventory record resolves to exactly one emitted payload file, require
the `launchExecutableJs` role/emitted-path set to equal the manifest header's
executable entrypoint set and the single `frontendAsset` record to equal its
frontend field. Require every `importRootJs` source/emitted path to be present,
classified, and reachable from an owning launch record, but exclude it from
header equality. Then invoke PayloadTool to write the canonical manifest and catalog fragment.
The builder reads `production-entrypoints-v1.json` directly and rejects any
unknown field, missing/extra role relative to the supplied manifest header,
missing/extra emitted wrapper among current records, source-path alias, or
classification mismatch; it has no duplicate entrypoint list. `-ClosureOnly`
validates only current records and never asserts future role completeness; the
default full publish in Tasks 10–11 requires the final exact set. Then
publish AppHost with:

```powershell
dotnet publish desktop/windows/src/HowardLab.EbayCrm.AppHost/HowardLab.EbayCrm.AppHost.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --nologo -p:ProductionReleaseCatalogPath=$CatalogPath -p:Phase1CBCandidateCatalogPath=$CandidateCatalogPath -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishAot=false --output $AppHostRoot
```

Apply and verify the read/execute installed ACL. Re-run manifest verification
after the ACL transition. `-ClosureOnly` is a development gate for this task:
it must stop after dependency/native/reparse validation, label its output
`untrusted-build-closure`, omit the production manifest/catalog, and be
unusable by `controlled-twenty`. Tasks 10–11 run the default only after all
real entrypoints exist.

- [ ] **Step 6: Run focused builder tests and one real clean build**

Run:

```powershell
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/HowardLab.EbayCrm.AppHost.Windows.Tests.csproj --configuration Release --filter FullyQualifiedName~ProductionPayloadBuilder --nologo
node --test packages/twenty-server/scripts/copy-build-assets.test.mjs
& .\desktop\windows\scripts\Build-Phase1CBPayload.ps1 -ClosureOnly
```

Expected: tests pass; the real closure build reports exact Node/Yarn/source
identities, no source-checkout resolution, zero final reparse points, and
successful native-addon load checks. Unit fixtures prove v2 manifest/catalog
equality. The final manifested build is intentionally deferred until Tasks
10–11 because its setup, migration, preflight, and acceptance entrypoints are
created in later tasks.

- [ ] **Step 7: Review and commit**

```powershell
git add .gitignore packages/twenty-server/project.json packages/twenty-server/scripts/copy-build-assets.mjs packages/twenty-server/scripts/copy-build-assets.test.mjs desktop/windows/EbayCrm.Desktop.sln desktop/windows/tools/HowardLab.EbayCrm.PayloadTool desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Payload/ProductionPayloadBuilder.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Payload/ProductionPayloadBuilderTests.cs desktop/windows/scripts/Build-Phase1CBPayload.ps1
git commit -m "build(windows): stage trusted Twenty payload"
```

### Task 3: Immutable desktop configuration and real Nest bootstrap seams

**Files:**

- Create: `packages/twenty-server/src/desktop/immutable-desktop-mode.ts`
- Create: `packages/twenty-server/src/desktop/__tests__/immutable-desktop-mode.spec.ts`
- Create: `packages/twenty-server/src/desktop/__tests__/production-dotenv-inventory.spec.ts`
- Modify: `packages/twenty-server/src/utils/generate-front-config.ts`
- Modify: `packages/twenty-server/src/utils/__test__/generate-front-config.spec.ts`
- Modify: `packages/twenty-server/src/database/typeorm/core/core.datasource.ts`
- Modify: `packages/twenty-server/src/database/typeorm/raw/raw.datasource.ts`
- Modify: `packages/twenty-server/src/main.ts`
- Modify: `packages/twenty-server/src/queue-worker/queue-worker.ts`
- Create: `packages/twenty-server/src/desktop/__tests__/bootstrap-import-safety.spec.ts`
- Modify: `desktop/windows/runtime/production/production-entrypoints-v1.json`

**Interfaces:**

- Produces: `isImmutableDesktopMode(environment?: NodeJS.ProcessEnv): boolean` and `loadDotEnvUnlessImmutableDesktop(...)`.
- Produces: `bootstrapTwentyServer(): Promise<NestExpressApplication>` and `bootstrapTwentyWorker(): Promise<INestApplicationContext>`; direct production entrypoints still call these once.
- Consumes: existing Nest modules/config behavior and Task 2's prebuilt empty frontend marker.

- [ ] **Step 1: Write failing immutable/import tests**

Create a reusable `assertProductionDotenvInventory(entrypoints)` graph walker over repository-resolved TypeScript modules. Its only roots are the `launchExecutableJs` and `importRootJs` records from strict checked-in `desktop/windows/runtime/production/production-entrypoints-v1.json`; tests separately validate the literal `frontendAsset` record and never import it. For each JavaScript record, resolve its source module, verify its declared emitted path, and update no data automatically. In Task 3, populate `importRootJs` records only for the real server and worker targets whose direct-entry adapters are made side-effect-free in this task, plus the frontend asset; defer setup/migration roots until Task 6 performs their guarded refactor, and add launch wrappers only when each is created. Enumerate every production-reachable `dotenv`, `dotenv/config`, or `config()` import/call; classify each discovered site as routed through `loadDotEnvUnlessImmutableDesktop` or explicitly unreachable; fail on an unclassified/new site; and never assert a fixed count. Tasks 5, 6, 8, and 9 must explicitly extend and commit the shared inventory with their newly created wrapper/one-shot/preflight/acceptance entrypoints and rerun this same test. Task 2's manifest builder and Task 10's final equality gate consume the same artifact; no C#/Node duplicate list is permitted. Assert that explicit `EBAYCRM_DESKTOP_IMMUTABLE_CONFIGURATION=1` suppresses the complete discovered inventory; absent mode preserves upstream behavior; immutable `generateFrontConfig()` validates exactly the empty `window._env_` marker without writing; unknown keys or unresolved substitutions fail; and importing each then-existing JavaScript root from a read-only tree performs zero write calls and does not bootstrap.

- [ ] **Step 2: Verify red**

Run:

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='immutable-desktop-mode|bootstrap-import-safety|production-dotenv-inventory|generate-front-config' --runInBand
```

Expected: selected tests fail against current unconditional dotenv/write/direct-bootstrap behavior.

- [ ] **Step 3: Implement the explicit guard and read-only frontend validation**

Use one predicate everywhere:

```typescript
export function isImmutableDesktopMode(
  environment: NodeJS.ProcessEnv = process.env,
): boolean {
  return environment.EBAYCRM_DESKTOP_IMMUTABLE_CONFIGURATION === '1';
}

export function loadDotEnvUnlessImmutableDesktop(): void {
  if (!isImmutableDesktopMode()) {
    config({
      path: process.env.NODE_ENV === 'test' ? '.env.test' : '.env',
      override: true,
    });
  }
}
```

Call it before reading datasource environment. In immutable mode, `generateFrontConfig()` reads and validates only; it never catches validation failure and never writes.

- [ ] **Step 4: Export real bootstraps without import side effects**

Move current server and worker bodies into the exported functions, return initialized application objects, preserve logging/error behavior, and gate direct invocation with the repository's CommonJS-compatible `require.main === module` pattern. Do not add desktop process supervision to Twenty.

- [ ] **Step 5: Run focused and current server tests**

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='immutable-desktop-mode|bootstrap-import-safety|production-dotenv-inventory|generate-front-config' --runInBand
node .yarn/releases/yarn-4.13.0.cjs nx run twenty-server:test-runtime-contract
```

Expected: all selected tests pass with non-desktop behavior retained.

- [ ] **Step 6: Review and commit**

```powershell
git add packages/twenty-server/src/desktop packages/twenty-server/src/utils/generate-front-config.ts packages/twenty-server/src/utils/__test__/generate-front-config.spec.ts packages/twenty-server/src/database/typeorm/core/core.datasource.ts packages/twenty-server/src/database/typeorm/raw/raw.datasource.ts packages/twenty-server/src/main.ts packages/twenty-server/src/queue-worker/queue-worker.ts desktop/windows/runtime/production/production-entrypoints-v1.json
git commit -m "feat(server): add immutable desktop bootstraps"
```

### Task 4: Concrete HTTP and BullMQ drain adapters

**Files:**

- Create: `packages/twenty-server/src/desktop/server-admission-gate.ts`
- Create: `packages/twenty-server/src/desktop/server-drain-controller.ts`
- Create: `packages/twenty-server/src/desktop/worker-drain-controller.ts`
- Create: `packages/twenty-server/src/desktop/__tests__/server-drain-controller.spec.ts`
- Create: `packages/twenty-server/src/desktop/__tests__/worker-drain-controller.spec.ts`
- Create: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/bullmq.driver.desktop-drain.spec.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/bullmq.driver.ts`
- Modify: `packages/twenty-server/src/main.ts`

**Interfaces:**

- Produces: `ServerDrainController.closeAdmissionAndDrain(deadline: AbortSignal): Promise<void>` and `activeRequestCount(): number`.
- Produces: `BullMQDriver.pauseForDesktopDrain(): Promise<void>`, `getDesktopActiveHandlerCount(): number`, and `closeForDesktopDrain(signal: AbortSignal): Promise<void>`.
- Produces: `WorkerDrainController.drain(signal: AbortSignal): Promise<void>` over the release-pinned registry.

- [ ] **Step 1: Write failing HTTP and local-object drain tests**

Tests must open a real loopback Nest HTTP listener and prove one admitted request finishes, listener admission closes, a late connection is refused or receives the fixed 503 shutdown body, keep-alive reuse cannot admit a new request, and drained is impossible while the counter is nonzero. Before Task 8 provides a trusted compatibility endpoint, BullMQ drain tests use deterministic injected Worker/Queue objects at the exact driver construction and processor-wrapper seams; they prove pause prevents acquisition, one active handler completes, registry active count reaches zero, queues/workers close, and deadline cancellation reports failure rather than drained. These tests may not select or skip the environment-gated runtime contract. Task 8's AppHost-owned `Invoke-Phase1CBBullMQContract.ps1` is the mandatory real BullMQ/Garnet, zero-skip drain proof.

- [ ] **Step 2: Verify red**

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='server-drain-controller|worker-drain-controller|bullmq.driver.desktop-drain' --runInBand
```

Expected: tests fail because admission and worker-drain APIs are absent.

- [ ] **Step 3: Implement server admission accounting**

Use middleware that increments before passing to the application, decrements exactly once on response finish/close, and switches atomically to the fixed shutdown response. Stop accepting new sockets before awaiting zero. Make close idempotent and reject readiness once drain begins.

- [ ] **Step 4: Implement BullMQ registry accounting**

Track every Worker at construction, increment/decrement an in-memory active-handler counter in the actual processor wrapper, call `worker.pause(true)` on each registered worker before waiting, then close every worker/queue and the Nest context. Preserve existing bounded AI-stream cancellation behavior inside the shared deadline.

- [ ] **Step 5: Run focused tests**

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='server-drain-controller|worker-drain-controller|bullmq.driver' --runInBand
```

Expected: all selected non-skipping local tests pass with zero open handles; no real compatibility claim is made until Task 8.

- [ ] **Step 6: Review and commit**

```powershell
git add packages/twenty-server/src/desktop packages/twenty-server/src/engine/core-modules/message-queue/drivers/bullmq.driver.ts packages/twenty-server/src/engine/core-modules/message-queue/drivers/bullmq.driver.desktop-drain.spec.ts packages/twenty-server/src/main.ts
git commit -m "feat(server): expose bounded desktop drains"
```

### Task 5: Real controlled wrappers and AppHost role launch

**Files:**

- Create: `desktop/windows/node/src/production/controlled-role-runner.ts`
- Create: `desktop/windows/node/src/production/production-compatibility-tuple.ts`
- Create: `desktop/windows/node/src/production/twenty-server-role.ts`
- Create: `desktop/windows/node/src/production/twenty-worker-role.ts`
- Create: `desktop/windows/node/test/controlled-role-runner.test.ts`
- Create: `desktop/windows/node/test/production-compatibility-tuple.test.ts`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/ProductionCompatibilityTuple.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/ProductionTwentyRoleLaunchPlanProvider.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionCompatibilityTupleTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionTwentyRoleLaunchPlanProviderTests.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AppHostOptions.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AppHostComposition.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AllowlistedRoleEnvironmentBuilder.cs`
- Modify: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/AppHost/AppHostStartupTests.cs`
- Modify: `desktop/windows/runtime/production/production-entrypoints-v1.json`

**Interfaces:**

- Produces Node `runControlledProductionRole(options): Promise<ProductionRoleResult>` where `initialize`, `verifyReady`, `activeWork`, `drain`, and `shutdown` are injected real-role callbacks.
- Produces C# `ProductionTwentyRoleLaunchPlanProvider : IRoleLaunchPlanProvider` over `ValidatedProductionPayload`.
- Consumes Tasks 1–4, existing `AppHostControlClient`, `IdentityHealthServer`, `TrustedNodeRoleLaunchPlanProvider`, Job/process launcher, and four-frame drain behavior.

- [ ] **Step 1: Write failing wrapper/provider tests**

Node tests prove health binds before Hello; every compatibility-tuple member mutation changes the canonical tuple digest; a wrapper cannot send Hello until it independently derives that digest from its immutable manifested/runtime inputs and constant-time compares it with `buildIdentity`; role mismatch fails; readiness waits for the real callback; active-work health count is real; drain completes before `Drained`; shutdown closes the app; diagnostics are sanitized; and cleanup is idempotent. C# tests prove the embedded release catalog independently derives the same digest, makes it the expected `buildIdentity`, rejects every tuple-member mutation at Hello, and accepts `controlled-twenty` only with `acceptance-run-once`, `RedisCompatibility`, release acceptance gate, production root, and an embedded real catalog. Extend the strict shared inventory with `launchExecutableJs` records for the controlled wrappers and retain distinct `importRootJs` records for their dynamically imported Twenty server/worker targets; the role/source/emitted-path graph must prove both layers and ownership reachability, while only launch records participate in header equality.

- [ ] **Step 2: Verify red**

```powershell
node .yarn/releases/yarn-4.13.0.cjs exec tsx --test desktop/windows/node/test/controlled-role-runner.test.ts desktop/windows/node/test/production-compatibility-tuple.test.ts
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~ProductionCompatibilityTuple|FullyQualifiedName~ProductionTwentyRoleLaunchPlanProvider|FullyQualifiedName~AppHostStartupTests" --nologo
```

Expected: selected tests fail because production target/wrappers do not exist.

- [ ] **Step 3: Implement thin wrappers over exported Twenty bootstraps**

Keep protocol v2's strict Hello schema unchanged and give its existing `buildIdentity` field one exact Phase 1C-B meaning: `phase1cb/` plus the lowercase SHA-256 of the canonical tuple `releaseCatalogDigest`, `payloadRootDigest`, `controlProtocolVersion`, `roleBootstrapApiVersion`, `nodeVersion`, `databaseManifestDigest`, `bullmqVersion`, `compatibilityCandidateVersion`, and `compatibilityCandidateCatalogDigest`, serialized with fixed field order and UTF-8 framing. AppHost derives the expected value only from its embedded release catalog. Each wrapper independently derives the same value from the already manifest-validated immutable payload header, compiled protocol/bootstrap constants, actual `process.version`, pinned package metadata, and manifested candidate/database identities, then constant-time compares it with the control environment's `buildIdentity` before constructing `AppHostControlClient` or sending Hello. Tests cover every field, ordering, case, unknown fields, and C#/Node golden equality; no independent version values are accepted.

The server wrapper then dynamically imports only the exact manifested server module, calls `bootstrapTwentyServer`, verifies PostgreSQL identity/catalog, compatibility endpoint, loopback application health, tuple/generation, and delegates drain to `ServerDrainController`. The worker wrapper imports `bootstrapTwentyWorker`, verifies the exact processor registry/queue connection, and delegates drain to `WorkerDrainController`. Neither wrapper starts a sibling or dependency.

- [ ] **Step 4: Implement the release-gated launch provider**

Add `AppHostRoleTarget.ControlledTwenty` and `--production-root`. The provider uses payload `node.exe` directly, normalized manifested wrapper paths, exact working directories, ordinary variables including `EBAYCRM_DESKTOP_IMMUTABLE_CONFIGURATION=1`, and secret variables for PostgreSQL/compatibility credentials. Clear `NODE_OPTIONS`, `NODE_PATH`, npm/Yarn/Corepack variables, proxy injection, and inherited application variables. Reject `.env` and `.env.*` in payload and effective working directories before launch.

- [ ] **Step 5: Run focused and protocol regressions**

```powershell
node .yarn/releases/yarn-4.13.0.cjs exec tsc --project desktop/windows/node/tsconfig.json --noEmit
node .yarn/releases/yarn-4.13.0.cjs exec tsx --test desktop/windows/node/test/**/*.test.ts
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='production-dotenv-inventory' --runInBand
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~ProductionTwentyRoleLaunchPlanProvider|FullyQualifiedName~RoleReadiness|FullyQualifiedName~AppHostStartup" --nologo
```

Expected: all selected tests pass; existing 61 Node tests remain green before new-test count is added.

- [ ] **Step 6: Review and commit**

```powershell
git add desktop/windows/runtime/production/production-entrypoints-v1.json desktop/windows/node/src/production desktop/windows/node/test/controlled-role-runner.test.ts desktop/windows/node/test/production-compatibility-tuple.test.ts desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionCompatibilityTupleTests.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionTwentyRoleLaunchPlanProviderTests.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/AppHost/AppHostStartupTests.cs
git commit -m "feat(windows): launch real Twenty roles"
```

### Task 6: Shell-free Twenty setup and instance-command one-shots

**Files:**

- Create: `packages/twenty-server/src/database/scripts/setup-database.service.ts`
- Create: `packages/twenty-server/src/database/scripts/__tests__/setup-database.service.spec.ts`
- Create: `packages/twenty-server/src/database/migration-boundary-observer.ts`
- Create: `packages/twenty-server/src/database/__tests__/migration-boundary-observer.spec.ts`
- Modify: `packages/twenty-server/src/database/scripts/setup-db.ts`
- Create: `packages/twenty-server/src/database/commands/run-instance-commands.service.ts`
- Create: `packages/twenty-server/src/database/commands/__tests__/run-instance-commands.service.spec.ts`
- Create: `packages/twenty-server/src/database/commands/desktop-migration-command.module.ts`
- Create: `packages/twenty-server/src/database/commands/desktop-migration-application.ts`
- Create: `packages/twenty-server/src/database/commands/__tests__/desktop-migration-application.spec.ts`
- Modify: `packages/twenty-server/src/database/commands/run-instance-commands.command.ts`
- Modify: `packages/twenty-server/src/database/commands/database-command.module.ts`
- Create: `desktop/windows/node/src/production/one-shot-environment.ts`
- Create: `desktop/windows/node/src/production/migration-boundary-observer.ts`
- Create: `desktop/windows/node/src/production/setup-database-role.ts`
- Create: `desktop/windows/node/src/production/run-instance-commands-role.ts`
- Create: `desktop/windows/node/test/production-one-shots.test.ts`
- Create: `desktop/windows/node/test/migration-boundary-observer.test.ts`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/ProductionDatabaseBootstrapper.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionDatabaseBootstrapperTests.cs`
- Modify: `desktop/windows/runtime/production/production-entrypoints-v1.json`

**Interfaces:**

- Produces server-owned structural `MigrationBoundaryObserver` with the four fixed async methods and exported frozen `NOOP_MIGRATION_BOUNDARY_OBSERVER`; Twenty code never imports a desktop/windows module.
- Produces: `setupDatabase(dataSource, { fdwEnabled: false }, observer = NOOP_MIGRATION_BOUNDARY_OBSERVER): Promise<void>`.
- Produces: `RunInstanceCommandsService.run({ force: true, includeSlow: true }, observer = NOOP_MIGRATION_BOUNDARY_OBSERVER): Promise<void>`.
- Produces: side-effect-free `createDesktopMigrationApplication()` returning an explicit minimal Nest application context from which `RunInstanceCommandsService` is resolved and which the caller must close; it never constructs the broad `CommandModule`/`AppModule` graph.
- Produces: `ProductionDatabaseBootstrapper.RunSetupAsync(...)` and `RunInstanceCommandsAsync(...)`, each returning only bounded process observation; authorization remains Task 7 database verification.
- Produces a desktop structural implementation of the server-owned observer contract; only a later exact AppHost-authenticated, PID/creation/lease-bound test construction can activate it.

- [ ] **Step 1: Write failing service and one-shot tests**

Assert setup issues only the fixed schema/extension/function statements, propagates errors, always destroys its datasource, and refuses FDW in desktop mode. Assert the extracted instance service executes workspace safety bypass, legacy TypeORM migrations with transaction `each`, every fast command, every slow command, status-cache invalidation, and error propagation. Server-side observer tests prove both ordinary CLI adapters omit the parameter and therefore receive the frozen no-op, while explicit structural observers are called inside the real setup transaction and immediately after the real migration/command boundaries. Factory tests prove importing setup, migration service, minimal module, factory, and both wrapper modules performs no bootstrap, process exit, database open, command parsing, or network access; explicit factory creation imports only the exact TypeORM, workspace-version, and upgrade provider modules needed by `RunInstanceCommandsService`, resolves every dependency, excludes unrelated command/AppModule providers, and closes on success/failure/cancellation. One-shot tests prove pre-import immutable configuration, exact role/build/lease validation, no arbitrary arguments, sanitized stderr, nonzero exit on failure, no environment-only barrier activation, and exact fixed observer calls at `beforeSetupCommit`, `afterSetupCommit`, `afterLegacyMigrationsCommitted`, and `afterExactTargetBeforeExit`.

After the guarded setup/command refactor is green, extend Task 3's shared
inventory in this commit with `launchExecutableJs` records for both new desktop
one-shot wrappers and `importRootJs` records for their side-effect-free setup
service and minimal migration-application targets. Prove each import root is
reachable from its owning wrapper. Do not inventory the ordinary CLI adapters as
production launch roots. The focused tests fail if either launch/import root or
any newly reachable dotenv site is missing.

- [ ] **Step 2: Verify red**

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='setup-database.service|run-instance-commands.service|desktop-migration-application|migration-boundary-observer|production-dotenv-inventory' --runInBand
node .yarn/releases/yarn-4.13.0.cjs exec tsx --test desktop/windows/node/test/production-one-shots.test.ts desktop/windows/node/test/migration-boundary-observer.test.ts
```

Expected: selected tests fail because the callable services/one-shots are absent.

- [ ] **Step 3: Extract callable services without changing normal commands**

Keep setup SQL as source constants with no interpolated input and execute the setup set in one explicit transaction so `beforeSetupCommit` and `afterSetupCommit` surround the real commit. Make current `setup-db.ts` a direct-entrypoint adapter guarded by an explicit direct-entrypoint check that awaits the service and sets a nonzero exit on error. Move current `RunInstanceCommandsCommand.run` body into the injected service. Register and export that service from `DatabaseCommandModule` for the ordinary CLI. Separately make `DesktopMigrationCommandModule` import only the exact TypeORM, workspace-version, upgrade registry/runner/status, and cache/message-queue providers required by the service; it must not import `CommandModule`, `AppModule`, unrelated commands, cron modules, marketplace, billing jobs, or HTTP modules. `createDesktopMigrationApplication()` constructs that module with `NestFactory.createApplicationContext` but does not run a command. The ordinary command adapter passes parsed booleans and the no-op observer, while the desktop one-shot establishes immutable configuration, creates the minimal application, resolves the service, calls literal `{ force: true, includeSlow: true }`, signals `afterLegacyMigrationsCommitted` immediately after the real `transaction: 'each'` TypeORM call and before upgrade commands, signals `afterExactTargetBeforeExit` only after all commands commit, and closes in `finally`. Neither imported module may auto-run.

- [ ] **Step 4: Implement AppHost one-shot launch ownership**

Use the payload Node and exact manifested entrypoints, an AppHost bootstrap artifact lease, the existing Job/process launcher, minimal allowlisted environment, secret registry, exact PID/creation identity, output bounds, and hard deadline. Do not invoke a shell, Yarn, command string, or document/profile value.

- [ ] **Step 5: Run focused tests**

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='setup-database.service|run-instance-commands|desktop-migration-application|migration-boundary-observer|production-dotenv-inventory' --runInBand
node .yarn/releases/yarn-4.13.0.cjs exec tsx --test desktop/windows/node/test/production-one-shots.test.ts desktop/windows/node/test/migration-boundary-observer.test.ts
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter FullyQualifiedName~ProductionDatabaseBootstrapper --nologo
```

Expected: all selected tests pass.

- [ ] **Step 6: Review and commit**

```powershell
git add packages/twenty-server/src/database/scripts packages/twenty-server/src/database/commands packages/twenty-server/src/database/migration-boundary-observer.ts packages/twenty-server/src/database/__tests__/migration-boundary-observer.spec.ts desktop/windows/runtime/production/production-entrypoints-v1.json desktop/windows/node/src/production desktop/windows/node/test/production-one-shots.test.ts desktop/windows/node/test/migration-boundary-observer.test.ts desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/ProductionDatabaseBootstrapper.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionDatabaseBootstrapperTests.cs
git commit -m "feat(windows): supervise Twenty database bootstraps"
```

### Task 6A: Verified compatibility runtime prerequisite

**Files:**

- Create: `desktop/windows/scripts/Stage-Phase1CBGarnet.ps1`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/GarnetCompatibilityEndpoint.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/GarnetCompatibilityEndpointTests.cs`
- Modify: `desktop/windows/scripts/Build-Phase1CBPayload.ps1`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/HowardLab.EbayCrm.AppHost.csproj`

**Interfaces:**

- Produces an acceptance-only, read/execute Garnet plus private .NET runtime
  closure and AppHost-embedded candidate catalog.
- Produces `Stage-Phase1CBGarnet.ps1 -PassThru` with a bounded nonsecret object
  containing only canonical catalog path/digest and version identities; ordinary
  output remains sanitized.
- Produces `GarnetCompatibilityEndpoint.StartAsync(...)` as the only candidate
  lifecycle owner; Tasks 7â€“11 consume it before any Redis-dependent Twenty
  application context is constructed.

- [ ] **Step 1: Write failing identity, trust, and lifecycle tests**

Tests reject wrong package/runtime hashes, package/license/tool-settings changes,
untrusted or reparse roots, every missing/extra/misdirected managed or native
asset named by `GarnetServer.deps.json`, every altered runtimeconfig framework or
roll-forward field, undeclared app-base/probing files, .NET injection environment,
non-loopback binds, missing Lua, system runtime resolution, wrong PID/creation or
Job identity, and residue. A mutation-race test pauses after catalog validation,
attempts to replace every cataloged file, and proves the lifetime lease blocks
replacement or launch fails before managed code loads. A hostile-machine test
places the installed .NET 10 host/runtime first on `PATH` and proves it cannot win.

- [ ] **Step 2: Verify red**

```powershell
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter FullyQualifiedName~GarnetCompatibilityEndpoint --nologo
```

Expected: selected tests fail because the staging/lifecycle types are absent.

- [ ] **Step 3: Stage the exact immutable candidate closure**

Implement the exact package/private-runtime acquisition, hash/license/tool
selection, full catalog, canonical/reparse validation, and read/execute ACL
transition specified in the global constraints. Strictly parse the selected
`tools/net8.0/any/GarnetServer.deps.json` and `.runtimeconfig.json`: each managed
runtime asset and native runtime target must map once to a canonical cataloged
file under either the Garnet root or the private-runtime root, and every staged
app-base/runtime file must be justified by that dependency graph or the explicit
host/framework bootstrap set. Reject missing, extra, aliased, app-base fallback,
or probing-path files; no ambient NuGet cache or shared store is permitted. Bind
the parsed dependency/runtimeconfig canonical digest and exact asset counts into
the candidate catalog rather than trusting a hand-maintained list. Hold no-delete
handles for every cataloged file from final validation through process shutdown.
No Garnet/runtime file enters the Twenty payload.

- [ ] **Step 4: Implement the AppHost-owned host-plus-DLL lifecycle**

Launch only cataloged private `dotnet.exe` plus cataloged net8 Garnet DLL and
fixed loopback/Lua arguments under the AppHost Job. Use an exact allowlisted
environment; set private `DOTNET_ROOT`, `DOTNET_MULTILEVEL_LOOKUP=0`, and the
runtimeconfig's cataloged roll-forward policy; clear/reject `DOTNET_STARTUP_HOOKS`,
`DOTNET_ADDITIONAL_DEPS`, `DOTNET_SHARED_STORE`, `DOTNET_HOST_PATH`, host tracing
outputs, profiler/diagnostic injection, and inherited SDK/MSBuild variables.
Use the read-only cataloged Garnet directory as working directory, not the
writable acceptance root; route only explicit temp/log/data locations to the
disposable root and keep storage/AOF disabled. Constrain DLL search inputs and
`PATH` to the private closure plus canonical System32, then inventory loaded
modules and accept only cataloged files or Microsoft-signed canonical System32
modules. Before the ordinary endpoint launch, run the cataloged private
`dotnet.exe --list-runtimes` under the same constrained root and a separate
bounded preflight launch with the .NET 8 host's `COREHOST_TRACE=1` and
`COREHOST_TRACE_VERBOSITY=4`, captured only through the
AppHost-owned bounded in-memory stderr channel. Parse the host trace to require
nonempty recognized host records and that `Microsoft.NETCore.App` resolves
exactly to the cataloged private 8.0.29
win-x64 root/version and that no system, SDK, cache, probing, or unlisted root was
considered; immediately discard raw trace bytes and retain only a nonsecret
identity digest/reason. Clear/reject both `COREHOST_TRACE*` and
`DOTNET_HOST_TRACE*` families for the ordinary launch, and mutation-test both
families so a future host cannot silently inherit an output path or trace mode.
Loaded-module inventory remains a separate native-closure proof. Verify exact
runtime/candidate identity and RESP readiness, then after shutdown revalidate the
complete catalog before releasing handles. Failure is always
`compatibility-runtime-dependency-unavailable`.

- [ ] **Step 5: Run focused gates and review/commit**

```powershell
& .\desktop\windows\scripts\Stage-Phase1CBGarnet.ps1
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter FullyQualifiedName~GarnetCompatibilityEndpoint --nologo
git add desktop/windows/scripts/Stage-Phase1CBGarnet.ps1 desktop/windows/scripts/Build-Phase1CBPayload.ps1 desktop/windows/src/HowardLab.EbayCrm.AppHost/HowardLab.EbayCrm.AppHost.csproj desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/GarnetCompatibilityEndpoint.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/GarnetCompatibilityEndpointTests.cs
git commit -m "feat(windows): stage trusted compatibility runtime"
```

### Task 7: PostgreSQL state truth, advisory lock, and owned recovery

**Files:**

- Create: `desktop/windows/runtime/twenty/production-database-manifest-v1.json`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Postgres/ProductionDatabaseManifest.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Postgres/ProductionDatabaseStateVerifier.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Postgres/ProductionWorkspaceSchemaTemplate.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Postgres/WorkspaceSchemaName.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Postgres/PostgresAdvisoryLease.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Postgres/OwnedPostgresBackup.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/ProductionDatabaseCoordinator.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/ProductionMigrationBarrierServer.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Postgres/ProductionDatabaseManifestTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Postgres/DatabaseManifestCaptureTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Postgres/WorkspaceSchemaNameTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionDatabaseStateVerifierTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionDatabaseCoordinatorTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionDatabaseInterruptionTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionMigrationBarrierServerTests.cs`
- Modify: `desktop/windows/tools/HowardLab.EbayCrm.PayloadTool/Program.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/HowardLab.EbayCrm.AppHost.Windows.csproj`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/packages.lock.json`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/HowardLab.EbayCrm.AppHost.csproj`

**Interfaces:**

- Consumes: Task 6 ordered one-shots, Task 6A verified compatibility endpoint, existing AppHost-control migration runner, AppHost-owned PostgreSQL 16.14 identity, and Npgsql 10.0.3.
- Produces: `DatabaseAcceptanceState` values `ExactStart`, `SetupTarget`, `ExactTarget`, and `UnsafePartial`.
- Produces: `ProductionDatabaseStateVerifier.ClassifyAsync(NpgsqlDataSource, ProductionDatabaseManifest, CancellationToken)`.
- Produces: `PostgresAdvisoryLease.AcquireAsync(...)` holding one dedicated connection and `ProductionDatabaseCoordinator.PrepareAsync(...)` as the only authorization to start real roles.

- [ ] **Step 1: Write failing manifest/verifier/coordinator tests**

Unit tests reject SQL text in the manifest, duplicate/unknown catalog entries, invalid semantic hashes, noncanonical workspace schema names, a missing/unknown workspace-schema template algorithm or object-shape digest, and any desktop product version above zero. Add cross-language golden UUIDs proving C# `WorkspaceSchemaName.FromWorkspaceId(Guid)` returns `workspace_` plus Twenty's exact lowercase base-36 conversion: all-zero → `workspace_0`, `00000000-0000-0000-0000-000000000001` → `workspace_1`, `550e8400-e29b-41d4-a716-446655440000` → `workspace_51a37iakuf5nuuphr0fx89og0`, and all-`f` → `workspace_f5lxx1zz5pnorynqglhzmsp33`. Capture CLI tests are red first and prove fixed stage names only, secret connection input only, no SQL in output, no overwrite before all six stage captures plus two template captures agree, and deterministic normalization. Real PostgreSQL tests cover empty start, setup target, complete target, partial setup, TypeORM prefix, unknown upgrade command, failed/nonterminal/duplicate attempt, timestamp outside execution window, interruption before transaction commit, interruption after durable commit but before process observation, exact-state reconciliation for both, extra namespace/extension/object, missing constraint/index/function, workspace-row/schema mismatch both directions, newer database, advisory-lock contention/loss, cancellation after each boundary, backup path reparse, and restore to any non-owned path.

- [ ] **Step 2: Add Npgsql and verify red with locked restore**

Add `<PackageReference Include="Npgsql" Version="10.0.3" />`, regenerate only
the affected lockfile once, inspect that its only new package closure is
Npgsql 10.0.3 and declared dependencies, then prove locked restore:

```powershell
dotnet restore desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/HowardLab.EbayCrm.AppHost.Windows.csproj --force-evaluate --runtime win-x64 --nologo
dotnet restore desktop/windows/EbayCrm.Desktop.sln --locked-mode --runtime win-x64 --nologo
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~ProductionDatabaseStateVerifier|FullyQualifiedName~ProductionDatabaseCoordinator|FullyQualifiedName~ProductionDatabaseInterruption|FullyQualifiedName~ProductionMigrationBarrierServer" --nologo
```

Expected: tests fail because verifier/coordinator types are absent; locked restore succeeds after lockfile update.

- [ ] **Step 3: Implement static typed catalog queries**

Create `NpgsqlDataSource` from the secret connection string, open with cancellation, and keep every query string as a private source constant. Bind every dynamic value with `NpgsqlParameter`. Compare system/non-system namespace universe, extension name/version, function definition digest, tables/columns/types/defaults/nullability, constraints, indexes, TypeORM order, upgrade command semantic identity/status/attempt/time window, workspace version, canonical workspace-schema bijection, and every existing workspace schema against the pinned semantic object-shape template.

- [ ] **Step 4: Capture the deterministic target manifest**

First add a PayloadTool `database-manifest capture --stage
ExactStart|SetupTarget|ExactTarget` subcommand that accepts the connection only
from registered secret environment `EBAYCRM_CAPTURE_DATABASE_URL`, runs the
verifier's compiled static catalog queries, semantically normalizes generated
UUIDs/timestamps, and writes data identities/hashes but no SQL. Bootstrap two
independent disposable PostgreSQL 16.14 clusters through Task 6 and capture all
three states on each cluster at the actual boundaries: six captures total.
Require cluster A/B byte identity separately for ExactStart, SetupTarget, and
ExactTarget, and prove each state classifies only as its named state.

Before constructing the Task 6 command application, AppHost starts and
preflights the exact Task 6A endpoint and supplies its registered-secret URL;
the test proves the command context never resolves an ambient/system endpoint.
Because a fresh ExactTarget contains zero workspace schemas and a real API
workspace cannot be created until Task 8 completes BullMQ preflight and Task 9
supplies the trusted application flow, Task 7 implements and red/green tests the source-controlled
`workspace-schema-template capture` contract but does not fabricate a template.
Its intermediate catalog has an explicit fail-closed `RejectAllWorkspaces`
policy: zero workspace rows/schemas classify normally, while any workspace row
or schema is `UnsafePartial`. Task 9 must replace that policy before its final
commit by running the capture twice against independent real API-created
workspaces. Only the agreeing three-state manifests are eligible for the Task 7
commit; review the complete JSON diff, and retain the mandatory template capture
as an unresolved, fail-closed Task 9 gate in the ledger.

- [ ] **Step 5: Implement lock and backup/recovery**

Acquire a fixed two-int `pg_try_advisory_lock` on a dedicated open connection before setup and retain it through both one-shots and all verification. For non-disposable exact-start upgrades, stop owned PostgreSQL, copy only the canonical non-reparse data root into a staged sibling backup, hash/inventory, atomically rename, restart, acquire the lock, then migrate. Restore only with PostgreSQL stopped and only to the same owned data identity. Disposable acceptance roots may be discarded instead.

- [ ] **Step 6: Implement state-driven coordination**

The coordinator sequence is fixed:

```text
verify exact start -> owned backup when required -> acquire lock
-> run setup -> verify setup target
-> run force+include-slow commands -> verify exact target
-> authorize server/worker launch
```

After every exit, timeout, cancellation, restart, or recovery, classify database state again. Only exact start may retry from setup, setup target may proceed to commands, and exact target may proceed to roles. Every other state fails closed without repair SQL.

`ProductionMigrationBarrierServer` is an AppHost-created ACL-restricted named
pipe enabled only by the integration-test composition, never a product
environment flag. The one-shot wrapper may create a live observer only after
validating the pipe peer, operation/run/lease, exact own PID/creation/Job
identity, one-time nonce, and one of the four compiled boundary names; frames
are bounded and secrets are removed from environment before import. Ordinary
composition always injects the sealed no-op observer. Tests reject spoofed
peers, replay, wrong boundary/order/identity, environment-only activation, and
late signals.

`ProductionDatabaseInterruptionTests` launches the real owned setup/migration
one-shots under AppHost against disposable PostgreSQL plus Task 6A. The fixed
authenticated barriers stop the exact PID/creation identity before transaction
commit, after durable commit but before AppHost receives exit observation, at a
hard deadline, and after a deliberately partial committed step. AppHost must
reclassify from catalogs: retry only exact pre-commit state, accept an observed
ExactTarget despite lost exit, continue only from SetupTarget, and reject every
partial/unknown state. The tests verify Job containment, lock disposition,
repeat/idempotent reconciliation, and zero unexpected skips.

- [ ] **Step 7: Run focused real-PostgreSQL tests**

```powershell
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/HowardLab.EbayCrm.AppHost.Windows.Tests.csproj --configuration Release --filter FullyQualifiedName~ProductionDatabaseManifest --nologo
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~ProductionDatabaseStateVerifier|FullyQualifiedName~ProductionDatabaseCoordinator|FullyQualifiedName~ProductionDatabaseInterruption|FullyQualifiedName~ProductionMigrationBarrierServer" --nologo
```

Expected: all selected tests pass against PostgreSQL 16.14 with no unexpected skips.

- [ ] **Step 8: Review and commit**

```powershell
git add desktop/windows/runtime/twenty desktop/windows/tools/HowardLab.EbayCrm.PayloadTool/Program.cs desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows desktop/windows/src/HowardLab.EbayCrm.AppHost/Production desktop/windows/src/HowardLab.EbayCrm.AppHost/HowardLab.EbayCrm.AppHost.csproj desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Postgres desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionDatabaseStateVerifierTests.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionDatabaseCoordinatorTests.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionDatabaseInterruptionTests.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionMigrationBarrierServerTests.cs
git commit -m "feat(windows): verify Twenty database state"
```

### Task 8: Acceptance-only Garnet identity and exact BullMQ preflight

**Files:**

- Create: `desktop/windows/scripts/Invoke-Phase1CBBullMQContract.ps1`
- Create: `desktop/windows/node/src/production/bullmq-compatibility-inventory.ts`
- Create: `desktop/windows/node/src/production/bullmq-compatibility-preflight.ts`
- Create: `desktop/windows/node/test/bullmq-compatibility-preflight.test.ts`
- Modify: `desktop/windows/runtime/production/production-entrypoints-v1.json`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/GarnetCompatibilityEndpoint.cs`
- Modify: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/GarnetCompatibilityEndpointTests.cs`

**Interfaces:**

- Consumes Task 6A's immutable candidate/private-runtime catalog and AppHost-owned lifecycle.
- Produces: `deriveBullMqCompatibilityInventory(bullMqRoot): CompatibilityInventory` and `runBullMqCompatibilityPreflight(options): Promise<CompatibilityResult>`.

- [ ] **Step 1: Write failing full compatibility-preflight tests**

Retain Task 6A identity/containment regression coverage. Node tests use the pinned BullMQ/ioredis closure and cover every reachable shipped Lua file digest, queue/events creation, script load/evalsha, unique job, delayed promotion, retry/stall observation, completion via QueueEvents, remove-on-complete, queue-key cleanup, bounded drain, and bounded close.

- [ ] **Step 2: Verify red**

```powershell
node .yarn/releases/yarn-4.13.0.cjs exec tsx --test desktop/windows/node/test/bullmq-compatibility-preflight.test.ts
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='production-dotenv-inventory' --runInBand
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter FullyQualifiedName~GarnetCompatibilityEndpoint --nologo
```

Expected: Node tests fail because inventory/preflight code is absent; Task 6A endpoint regressions remain green.

- [ ] **Step 3: Revalidate the exact candidate closure before clients**

Before static inventory, preflight, or contract clients run, require Task 6A's
catalog digest, ACL, lifetime lease, exact host-plus-DLL process, and post-run
revalidation. No client can accept an endpoint address without that proof.

- [ ] **Step 4: Implement the AppHost-owned real BullMQ contract launcher**

`Invoke-Phase1CBBullMQContract.ps1` asks AppHost to start the Task 6A endpoint,
then launches only the exact pinned Jest runtime-contract child with its generated
loopback URL and explicit BullMQ selector. AppHost owns stop/revalidation and the
script fails on any skip, leak, or candidate classification.

- [ ] **Step 5: Implement the reachable BullMQ inventory and preflight**

Derive the inventory only from the pinned installed BullMQ package and Twenty BullMQ driver imports; hash all reachable command/Lua resources and record the exact BullMQ/ioredis versions. Exercise the fixed operations with unique run IDs and prove zero residual keys. Treat any unclassified new resource or behavior as incompatibility.

Extend Task 3's dotenv/import-graph inventory with the preflight entrypoint in
this commit and prove importing it alone opens no socket or process.

- [ ] **Step 6: Run focused compatibility tests**

```powershell
& .\desktop\windows\scripts\Stage-Phase1CBGarnet.ps1
node .yarn/releases/yarn-4.13.0.cjs exec tsx --test desktop/windows/node/test/bullmq-compatibility-preflight.test.ts
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='production-dotenv-inventory' --runInBand
& .\desktop\windows\scripts\Invoke-Phase1CBBullMQContract.ps1
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter FullyQualifiedName~GarnetCompatibilityEndpoint --nologo
```

`Invoke-Phase1CBBullMQContract.ps1` must have AppHost launch the exact private-runtime/candidate pair, then launch the pinned Jest contract child with only `RUNTIME_CONTRACT_DRIVER=bullmq` and the generated loopback `RUNTIME_CONTRACT_REDIS_URL`; it fails unless the BullMQ contract suite executes with zero skips and AppHost proves cleanup. Expected: identity/containment tests pass. Behavioral preflight either passes completely or returns the explicit dependency-unavailable classification; it may not be skipped or weakened.

- [ ] **Step 7: Review and commit**

```powershell
git add desktop/windows/runtime/production/production-entrypoints-v1.json desktop/windows/scripts/Invoke-Phase1CBBullMQContract.ps1 desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/GarnetCompatibilityEndpoint.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/GarnetCompatibilityEndpointTests.cs desktop/windows/node/src/production/bullmq-compatibility-inventory.ts desktop/windows/node/src/production/bullmq-compatibility-preflight.ts desktop/windows/node/test/bullmq-compatibility-preflight.test.ts
git commit -m "test(windows): gate Garnet BullMQ compatibility"
```

### Task 9: Trusted canary processor and AppHost-owned application acceptance

**Files:**

- Create: `packages/twenty-server/src/desktop/acceptance/desktop-acceptance-construction.ts`
- Create: `packages/twenty-server/src/desktop/acceptance/desktop-acceptance.processor.ts`
- Create: `packages/twenty-server/src/desktop/acceptance/desktop-acceptance.module.ts`
- Create: `packages/twenty-server/src/desktop/acceptance/desktop-acceptance-worker-registrar.ts`
- Create: `packages/twenty-server/src/desktop/acceptance/desktop-acceptance-cleanup.service.ts`
- Create: `packages/twenty-server/src/desktop/acceptance/desktop-acceptance-cleanup.module.ts`
- Create: `packages/twenty-server/src/desktop/acceptance/desktop-acceptance-cleanup-application.ts`
- Create: `packages/twenty-server/src/desktop/acceptance/__tests__/desktop-acceptance.processor.spec.ts`
- Create: `packages/twenty-server/src/desktop/acceptance/__tests__/desktop-acceptance-worker-registrar.spec.ts`
- Create: `packages/twenty-server/src/desktop/acceptance/__tests__/desktop-acceptance-cleanup.service.spec.ts`
- Create: `packages/twenty-server/src/desktop/acceptance/__tests__/desktop-acceptance-cleanup-application.spec.ts`
- Modify: `packages/twenty-server/src/queue-worker/queue-worker.ts`
- Modify: `packages/twenty-server/src/queue-worker/queue-worker.module.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/message-queue.constants.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/message-queue-worker-options.constant.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/interfaces/message-queue-job.interface.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/interfaces/message-queue-driver.interface.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/services/message-queue.service.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/bullmq.driver.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss.driver.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/sync.driver.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/bullmq.driver.contract-spec.ts`
- Modify: `packages/twenty-server/src/engine/core-modules/message-queue/drivers/pg-boss.driver.contract-spec.ts`
- Modify: `packages/twenty-server/src/desktop/worker-drain-controller.ts`
- Modify: `desktop/windows/node/src/production/twenty-worker-role.ts`
- Create: `desktop/windows/node/src/production/acceptance-cleanup-role.ts`
- Create: `desktop/windows/node/test/twenty-worker-role.test.ts`
- Create: `desktop/windows/node/test/acceptance-cleanup-role.test.ts`
- Create: `desktop/windows/node/src/production/acceptance-control-client.ts`
- Create: `desktop/windows/node/src/production/acceptance-worker-control-server.ts`
- Create: `desktop/windows/node/src/production/acceptance-orchestrator.ts`
- Create: `desktop/windows/node/test/acceptance-orchestrator.test.ts`
- Create: `desktop/windows/node/test/acceptance-worker-control-server.test.ts`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/AcceptanceControlServer.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/AcceptanceWorkerControlClient.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/Phase1CBAcceptanceCoordinator.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/ProductionAcceptanceCleanupCoordinator.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/ProductionTwentyRoleLaunchPlanProvider.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/Phase1CBAcceptanceCoordinatorTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/AcceptanceWorkerControlClientTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionAcceptanceCleanupCoordinatorTests.cs`
- Modify: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionTwentyRoleLaunchPlanProviderTests.cs`
- Modify: `desktop/windows/runtime/twenty/production-database-manifest-v1.json`
- Modify: `desktop/windows/runtime/production/production-entrypoints-v1.json`

**Interfaces:**

- Produces: `createDesktopAcceptanceConstruction(validatedLease): DesktopAcceptanceConstruction`; construction is not derivable from environment alone, and `bootstrapTwentyWorker({ desktopAcceptanceConstruction })` is the only worker bootstrap that can install it.
- Produces fixed queue/job `desktop-acceptance` / `phase-1c-b-canary` with bounded `{ leaseId, runId, nonceDigest }` input and `{ runId, receiptOrdinal, resultDigest }` output.
- Produces an orchestrator-side pinned BullMQ `Queue`/`QueueEvents` client that can enqueue only that fixed job, observe/remove only its exact unique job ID, and after retirement call only `Queue.obliterate({ force: false })` on the fixed dedicated queue; it has no worker or endpoint lifecycle authority and `force: true` is forbidden.
- Produces: authenticated bounded orchestrator/AppHost pipe messages `restartRoles`, `rolesReady`, `retireAcceptanceWorker`, `acceptanceWorkerRetired`, and `complete`; AppHost owns every restart and retirement decision.
- Produces a separate worker-owned named-pipe control server plus AppHost client, bound to the exact worker PID/creation, role generation, acceptance lease, one-time nonce, and ACL. Its only request/response is `retireExactAcceptanceWorker` / `retired`; it does not modify protocol v2's reviewed Hello/readiness/drain frames or accept environment-only authority.
- Produces a separate manifested, lease-bound `acceptanceCleanupEntrypoint` one-shot; AppHost alone may launch it after the public soft-delete mutation and it accepts only the exact run workspace/user identities from the authenticated acceptance session.
- Produces `retireDesktopAcceptanceWorker(construction)` on the BullMQ driver/drain boundary; only the unforgeable acceptance construction may pause, drain, close, assert zero active jobs, and remove that one fixed worker plus its exact driver-owned acceptance `Queue` and inspection/options state from the central `workerMap`/`queueMap`, while leaving every ordinary worker/queue registered.
- Consumes and must close: Task 7's fail-closed `RejectAllWorkspaces` template-capture gate.
- Consumes: real `/metadata` signup operations and real `/graphql` Person record operations, Tasks 5–8 readiness, and Task 4 drain.

- [ ] **Step 1: Write failing trust and application-flow tests**

Processor tests reject environment-only enablement, wrong role/build/protocol/generation/lease, unknown registry digest, wrong queue/job/body, duplicate receipt, and oversized result. Driver/registrar tests prove the acceptance-only handler result is returned through BullMQ, is bounded to the approved result type, participates in the central worker registry/drain, and is never registered by the ordinary explorer or ordinary worker bootstrap; PgBoss and Sync remain type-correct but are never selected for this acceptance queue. Retirement tests refuse queue obliteration while its worker is active, then prove the construction-bound operation pauses, drains, closes, asserts zero active jobs, closes and removes only the exact acceptance worker and driver-owned acceptance `Queue` plus related map/inspection/options state, and is idempotently unavailable afterward while every ordinary worker/queue remains registered. Worker-control transport and launch-provider tests prove AppHost supplies the pipe name plus lease/generation through allowlisted ordinary identity fields and the nonce through the registered-secret handoff only for an acceptance-enabled worker launch; the wrapper consumes and deletes those values from `process.env` before importing Twenty or opening the pipe. They reject absent/extra/replayed/wrong-role secrets, wrong ACL/nonce/PID/creation/generation/lease, wrong direction/frame/length, environment-only activation without the validated lease construction, and any method except exact retirement; cancellation and ordinary drain races remain bounded, and ordinary worker launches receive no acceptance fields. Real-wrapper tests prove `twenty-worker-role.ts` creates the trusted construction and separate worker-control server only after control/lease validation and passes the construction to `bootstrapTwentyWorker`; ordinary construction passes nothing and opens no acceptance pipe. Orchestrator tests assert frontend index plus a manifest-declared asset, signup in a uniquely named workspace, login-token exchange, workspace activation, authenticated Person create/read/update, AppHost-requested full role restart, post-restart read persistence, Person deletion, unique canary enqueue after worker readiness, one completion with zero retry/stall/duplicate receipt, ordered acceptance-worker retirement/client close/dedicated-queue `obliterate({ force: false })`, rejection of `force: true`, public workspace soft deletion, trusted hard cleanup, and zero workspace/user/userWorkspace/queue/profile residue while the ordinary shared-queue worker remains active. AppHost tests prove the orchestrator and cleanup role are separately manifested Job children with exact PID/creation, bootstrap leases, deadlines, authenticated identities, and no process-launch authority.

Extend Task 3's dotenv/import-graph inventory with `launchExecutableJs` records
for the acceptance orchestrator and cleanup one-shot, the updated acceptance-
enabled worker launch record, and an explicit `importRootJs` record for
`desktop-acceptance-cleanup-application.ts` owned by the cleanup wrapper. Require
pre-import immutable configuration and prove the cleanup application is reachable
only through that wrapper's trusted construction; importing any listed root
without explicit bootstrap performs no work.

- [ ] **Step 2: Verify red**

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='desktop-acceptance|production-dotenv-inventory' --runInBand
node .yarn/releases/yarn-4.13.0.cjs exec tsx --test desktop/windows/node/test/acceptance-orchestrator.test.ts desktop/windows/node/test/acceptance-worker-control-server.test.ts desktop/windows/node/test/twenty-worker-role.test.ts desktop/windows/node/test/acceptance-cleanup-role.test.ts
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~Phase1CBAcceptanceCoordinator|FullyQualifiedName~AcceptanceWorkerControlClient|FullyQualifiedName~ProductionAcceptanceCleanupCoordinator|FullyQualifiedName~ProductionTwentyRoleLaunchPlanProvider" --nologo
```

Expected: selected tests fail because acceptance components are absent.

- [ ] **Step 3: Implement trusted processor registration**

Create the in-process construction only after the worker wrapper validates exact AppHost control identity and acceptance lease. Refactor `queue-worker.ts` to export a testable `bootstrapTwentyWorker(options?)`; the ordinary entrypoint calls it with no options. Add a dynamic `QueueWorkerModule.registerDesktopAcceptance(construction)` path that imports the acceptance module only when the trusted construction object is supplied.

Add `MessageQueue.desktopAcceptanceQueue`, its fixed bounded-shutdown options, and a narrow `MessageQueueJobResult` contract. Generalize `MessageQueueDriver.work` and `MessageQueueService.work` just enough for a handler to return that bounded result, return it from the BullMQ worker callback, and keep PgBoss/Sync behavior compile- and contract-tested. Do not route this processor through `MessageQueueExplorer`, whose ordinary decorator path intentionally discards handler results. Instead, `DesktopAcceptanceWorkerRegistrar` injects the acceptance queue service and directly calls `work`; this still registers the BullMQ worker in the existing central driver map so Task 4 pause/drain/close covers it. Keep the module and registrar absent from ordinary production construction. Store one in-memory receipt per run and return only digests/bounded ordinals.

The acceptance orchestrator uses the payload's exact pinned BullMQ package to
open `Queue` and `QueueEvents` against the AppHost-supplied registered-secret
loopback endpoint, add exactly one fixed-name job with the unique run-scoped
job ID, attempts `1`, and bounded retention, then await that job's result through
the event path inventoried in Task 8. After it validates the bounded result and
retry/stall/receipt counts, it sends an authenticated bounded `retireAcceptanceWorker`
request to AppHost. AppHost connects through the separate acceptance-only worker
pipe, validates its exact ACL/nonce/PID/creation/generation/lease binding, and
sends its only legal `retireExactAcceptanceWorker` request; this does not extend
or bypass protocol v2. Only the trusted in-process construction then calls
`retireDesktopAcceptanceWorker`, which pauses/drains/closes the exact acceptance
worker, asserts zero active jobs, closes its exact driver-owned acceptance
`Queue`, and removes only those acceptance objects/state from the central maps.
After AppHost acknowledges retirement, the orchestrator closes `QueueEvents`,
uses the still-open exact dedicated `Queue` as the sole bounded management client
to call `obliterate({ force: false })` on only that fixed queue, proves its exact
prefix is absent, and only
then closes the `Queue`. Obliteration is forbidden while any worker, event
client, or client other than that exact management `Queue` bound to the dedicated
acceptance queue remains active; ordinary workers/queues on shared queue names
remain active for the later hard-cleanup jobs. It
may not create a Worker, discover another queue, flush the endpoint, touch a
shared queue, or own the candidate process.

- [ ] **Step 4: Implement the fixed real API flow**

Before the general acceptance run, start two independent disposable PostgreSQL
clusters through the exact Task 7 state sequence and the exact Task 8 candidate.
On each, use this same literal metadata API flow to create/activate one uniquely
named workspace, invoke Task 7's compiled `workspace-schema-template capture`,
call `deleteCurrentWorkspace`, run the trusted hard-cleanup one-shot, and prove
workspace/user/userWorkspace/schema/queue/profile residue is
zero. Normalize both complete object-shape captures, require byte identity, bind
the result to the schema-name algorithm/version, replace `RejectAllWorkspaces`
in `production-database-manifest-v1.json`, and rerun the manifest/verifier tests.
Regenerate the test release catalog and compatibility-tuple digest from that
final database manifest, republish the focused AppHost fixture, and repeat the
application/cleanup acceptance against the final digest. Delete the ignored
intermediate capture bundle. No installed-like or final acceptance build may
proceed with the intermediate policy or tuple.

Use literal GraphQL documents for `signUp`, `signUpInNewWorkspace`, `getAuthTokensFromLoginToken`, `activateWorkspace`, `createOnePerson`, `findUniquePerson`, `updateOnePerson`, `deleteOnePerson`, and `deleteCurrentWorkspace`; variable values are generated data, never query text. After `signUpInNewWorkspace`, pass its `loginToken` and returned workspace `subdomainUrl` as the fixed `origin` to `getAuthTokensFromLoginToken`, retain only the returned access token in registered secret memory, call `activateWorkspace(data: {})` with that token, verify the expected active workspace identity/status, and only then call authenticated `/graphql` CRM operations. Keep passwords/tokens only in registered secret memory. Validate every HTTP status, GraphQL error absence, exact IDs/values, post-restart persisted update, Person deletion, and disposable workspace identity.

After the canary completes and its queue keys are removed, call the real authenticated `deleteCurrentWorkspace` mutation and assert its actual contract: the run workspace/user membership is soft-deleted and the workspace schema still exists. The orchestrator sends only the exact workspace/user IDs and completion digest over its authenticated pipe; it does not claim hard deletion.

AppHost then launches the separate manifested cleanup one-shot with a bootstrap
lease binding those exact UUIDs, run/generation/catalog identity, and a hard
deadline. The wrapper creates an unforgeable cleanup construction and passes it
to `createDesktopAcceptanceCleanupApplication(construction)`, a minimal dynamic
module importing only the exact workspace service, core repositories, cache,
and selected BullMQ providers needed for hard cleanup; it never imports the
broad `AppModule` or command graph and registers/exports the cleanup service only
when that object is present. It resolves
`DesktopAcceptanceCleanupService` from the explicit Nest context; environment
values alone cannot enable it. The service first asserts the workspace is the
soft-deleted run workspace and that the disposable user has no membership
outside it, and captures the exact expected `userWorkspace` tombstone IDs before
hard deletion. It installs a construction-bound in-memory observation hook in the
selected BullMQ driver, calls the real `WorkspaceService.deleteWorkspace(id,
false)` hard-delete path, and captures the exact cleanup jobs added during that
call by queue/name/workspace ID/creation window. It waits for those exact job IDs
to succeed through the supervised ordinary worker, removes only those exact
job/event records from the shared delete-cascade queue, and proves the database
foreign-key cascade already removed every captured `userWorkspace` tombstone,
including `withDeleted`; it never directly deletes a membership or expects a
membership affected-row count. It then re-proves the disposable user has no
memberships and hard-deletes only that exact soft-deleted user. Unknown/extra
jobs, another membership, a failed job, a surviving tombstone, direct shared-
queue obliteration, or an identity mismatch fails closed.

Finally AppHost uses Task 7's compiled static verifier plus bounded filesystem
and queue polling to prove the exact `core.workspace`, `core.userWorkspace`, and
disposable `core.user` rows (including `withDeleted`), every manifest-known core
row with a foreign key to those run identities, and the canonical workspace
schema are absent; the disposable profile has no workspace files; the endpoint
has none of the exact canary or observed cleanup job IDs; and the immutable root
is unchanged. At final role drain, close every remaining ordinary worker and
client before Task 6A endpoint teardown, reassert those exact IDs/prefixes absent,
then stop the nonpersistent Garnet process so any unrelated in-memory shared
queue metadata is erased. The final residue scan proves no candidate files,
keys, or processes remain after endpoint teardown; it never claims shared-queue
obliteration while the production worker is active.
Timeout or residue is cleanup failure. This hard-cleanup gate must complete for
both template captures and before each same-database drift comparison.

- [ ] **Step 5: Implement authenticated restart ownership**

The one-shot orchestrator connects to an AppHost-created named pipe using a one-time nonce removed from its environment. AppHost verifies pipe ACL, process identity, Job membership, operation/lease IDs, and fixed frame bounds. On `restartRoles`, AppHost drains/stops/restarts both roles through the existing lifecycle coordinator, re-verifies readiness, and then sends `rolesReady`; the child never launches or kills a process.

- [ ] **Step 6: Run focused tests**

```powershell
node .yarn/releases/yarn-4.13.0.cjs nx test twenty-server --testPathPattern='desktop-acceptance|production-dotenv-inventory' --runInBand
node .yarn/releases/yarn-4.13.0.cjs exec tsx --test desktop/windows/node/test/acceptance-orchestrator.test.ts desktop/windows/node/test/acceptance-worker-control-server.test.ts desktop/windows/node/test/twenty-worker-role.test.ts desktop/windows/node/test/acceptance-cleanup-role.test.ts
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~Phase1CBAcceptanceCoordinator|FullyQualifiedName~AcceptanceWorkerControlClient|FullyQualifiedName~ProductionAcceptanceCleanupCoordinator|FullyQualifiedName~ProductionTwentyRoleLaunchPlanProvider" --nologo
```

Expected: all selected tests pass with sanitized diagnostics, the real wrapper
bridge enabled only by trusted construction, and zero retained canary,
workspace, user, membership, schema, queue, or profile state.

- [ ] **Step 7: Review and commit**

```powershell
git add packages/twenty-server/src/desktop/acceptance packages/twenty-server/src/desktop/worker-drain-controller.ts packages/twenty-server/src/queue-worker packages/twenty-server/src/engine/core-modules/message-queue desktop/windows/runtime/twenty/production-database-manifest-v1.json desktop/windows/runtime/production/production-entrypoints-v1.json desktop/windows/node/src/production desktop/windows/node/test/acceptance-orchestrator.test.ts desktop/windows/node/test/acceptance-worker-control-server.test.ts desktop/windows/node/test/twenty-worker-role.test.ts desktop/windows/node/test/acceptance-cleanup-role.test.ts desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/ProductionTwentyRoleLaunchPlanProvider.cs desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/AcceptanceControlServer.cs desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/AcceptanceWorkerControlClient.cs desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/Phase1CBAcceptanceCoordinator.cs desktop/windows/src/HowardLab.EbayCrm.AppHost/Production/ProductionAcceptanceCleanupCoordinator.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/AcceptanceWorkerControlClientTests.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/Phase1CBAcceptanceCoordinatorTests.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionAcceptanceCleanupCoordinatorTests.cs desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/ProductionTwentyRoleLaunchPlanProviderTests.cs
git commit -m "test(windows): prove real Twenty application flow"
```

### Task 10: Installed-like real boot, restart, failure, and residue matrix

**Files:**

- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/Phase1CBRealBootAcceptanceTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/Phase1CBFailureMatrixTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/Phase1CBAsymmetricRestartAcceptanceTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/Phase1CBTimingAcceptanceTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production/Phase1CBArtifactScanner.cs`
- Create: `desktop/windows/scripts/Invoke-Phase1CBColdClosure.ps1`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AppHostComposition.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/RuntimeOrchestrator.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/LifecycleCommandExecutor.cs`

**Interfaces:**

- Produces one full `AcceptanceRunOnce` path: validate payload/candidate/profile → start PostgreSQL → start Task 6A compatibility endpoint → run Task 8 identity/BullMQ preflight → verify/control Task 6/7 migrations through that endpoint → start server/worker → run Task 9 acceptance and hard cleanup → drain/stop all → verify closure/residue. No Redis-dependent Nest context is constructed before candidate preflight.
- Produces bounded reason codes for every failure boundary without changing the core lifecycle protocol.

- [ ] **Step 1: Write the full matrix before composition changes**

Add a real installed-like success test plus failures for manifest header/record mutation, ACL/reparse change, `.env`, ambient Node injection, wrong Node/candidate hash, candidate absent/unreachable before launch, candidate without Lua, compatibility script failure, PostgreSQL identity mismatch, setup exit-zero/wrong-state, migration timeout, interruption before commit, interruption after durable commit/before exit observation, migration partial state, lock contention/loss, server early exit/not-ready, worker registry/queue failure, HTTP late admission, active job drain deadline, restart generation mismatch, API persistence mismatch, duplicate canary receipt, orchestrator timeout/spoof, AppHost crash containment, immutable-root mutation, diagnostic secret/canary occurrence in in-memory child argv/environment/log/payload/artifact scans, pg-boss table appearance while `RedisCompatibility` is selected, and cleanup failure. Every test uses a fresh disposable root and exact process ledger; secret/canary scanners retain only pass/fail and nonsecret digests, never the inspected command/environment values.

The success matrix must separately prove: (1) a server-only restart preserves the exact PostgreSQL and worker PID/creation/generation identities and the unchanged worker completes a new canary while API persistence survives; (2) a worker-only restart preserves the exact PostgreSQL and server PID/creation/generation identities and the unchanged server remains API-ready; (3) three complete start/stop cycles against the same payload and database target produce no migration, namespace, extension, workspace-schema, manifest, or immutable-root drift; and (4) both start orders and both stop orders remain bounded. The failure matrix must stop PostgreSQL while both real roles are active, observe readiness withdrawal and bounded drain/containment, then prove exact-state recovery without a fallback backend.

Timing tests must exercise the real server and worker, not probe substitutes: stop during deliberately delayed server bootstrap, stop during deliberately delayed worker bootstrap, stop during an admitted active HTTP request, and stop during an active acceptance job. Each case proves no late readiness, exact control generation, the specified drain deadline, Job containment, and zero residue. Delays are fixed AppHost-owned test-harness barriers, never free-form commands or environment-driven product fallbacks.

The final manifest test parses the checked-in
`desktop/windows/runtime/production/production-entrypoints-v1.json` with its
strict discriminated schema. Its `launchExecutableJs` role/emitted-path pairs must have
exact record-for-record equality with the payload files at the declared
entrypoint paths and the manifest header's server, worker, setup, migration,
preflight, acceptance, and cleanup executable entrypoints. Separately, its one
`frontendAsset` path must exactly equal the manifested frontend entrypoint and
the emitted `dist/front/index.html` file. Every `importRootJs` record must resolve
to a payload file, be reachable from its owning launch record, remain excluded
from header equality and, like each launch source, have passing dotenv/import-safety graph
coverage; the frontend record receives asset/build-provenance checks, never an
import-graph test. A new manifested/emitted entrypoint without the correct typed
inventory record, or a record absent from payload/header, fails the build.

`Invoke-Phase1CBColdClosure.ps1` must create a `.wsb` configuration that maps
only a final read-only acceptance bundle (payload, published AppHost, exact
PostgreSQL 16.14 binaries, the separately cataloged Garnet candidate, and its
cataloged private .NET 8.0.29 win-x64 runtime) plus
a separate writable disposable acceptance root into Windows Sandbox, disables
networking after all inputs are pre-staged, sets a constrained `PATH`, clears
`NODE_PATH`, and runs the real boot from outside the mapped source tree. Absence
or failure of Windows Sandbox is a failed mandatory closure gate, never a skip.

- [ ] **Step 2: Verify the matrix is red**

```powershell
$candidate = & .\desktop\windows\scripts\Stage-Phase1CBGarnet.ps1 -PassThru
& .\desktop\windows\scripts\Build-Phase1CBPayload.ps1 -CandidateCatalogPath $candidate.CatalogPath
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~Phase1CBRealBootAcceptanceTests|FullyQualifiedName~Phase1CBFailureMatrixTests|FullyQualifiedName~Phase1CBAsymmetricRestartAcceptanceTests|FullyQualifiedName~Phase1CBTimingAcceptanceTests" --nologo
& .\desktop\windows\scripts\Invoke-Phase1CBColdClosure.ps1
```

Expected: composition tests fail until the full production sequence is wired.

- [ ] **Step 3: Wire the production sequence with no fallback**

Add only the `ControlledTwenty` acceptance branch. Maintain AppHost as sole Job owner; database target verification gates roles; candidate preflight gates roles; internal role readiness gates application acceptance; application acceptance gates success. On every failure, run the same bounded drain/stop/Job-close/identity-ledger/manifest-recheck cleanup and preserve the first sanitized reason plus cleanup status.

- [ ] **Step 4: Prove real success and every failure boundary**

```powershell
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~Phase1CBRealBootAcceptanceTests|FullyQualifiedName~Phase1CBFailureMatrixTests|FullyQualifiedName~Phase1CBAsymmetricRestartAcceptanceTests|FullyQualifiedName~Phase1CBTimingAcceptanceTests" --nologo
```

Expected: every selected test passes, zero unexpected skips, zero retained Job processes, zero compatibility/profile/queue residue, unchanged manifest digest, and no registered secret/canary occurrence.

- [ ] **Step 5: Run preexisting lifecycle/containment regressions**

```powershell
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Core.Tests/HowardLab.EbayCrm.AppHost.Core.Tests.csproj --configuration Release --nologo
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter "Category!=DestructiveContainment&FullyQualifiedName!~Phase1CB" --nologo
dotnet test desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --filter Category=DestructiveContainment --nologo -- RunConfiguration.DisableAppDomain=true
```

Expected: all existing partitions pass.

- [ ] **Step 6: Review and commit**

```powershell
git add desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Production desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition desktop/windows/scripts/Invoke-Phase1CBColdClosure.ps1
git commit -m "feat(windows): compose Phase 1C-B acceptance"
```

### Task 11: Reproducible verification, cleanup, and final evidence

**Files:**

- Create: `desktop/windows/scripts/Verify-Phase1CB.ps1`
- Create: `desktop/windows/scripts/Test-Phase1CBCleanup.ps1`
- Create: `desktop/windows/scripts/Test-VerifyPhase1CBEnvironmentRestoration.ps1`
- Create: `desktop/windows/scripts/Test-Phase1CBPowerShell51.ps1`
- Modify: `desktop/windows/scripts/Invoke-Phase1CBColdClosure.ps1`
- Modify: `desktop/windows/README.md`
- Create: `docs/architecture/phase-1c-b-compatibility-boot-report.md`

**Interfaces:**

- Produces focused switches `-PayloadOnly`, `-CompatibilityOnly`, `-RealBootOnly`, and a default complete gate; switches may narrow execution but never turn a required final skip into success.
- Produces an exact cleanup result and one final evidence report with exactly one Phase 1C-B token.

- [ ] **Step 1: Write script contract tests first**

`Test-VerifyPhase1CBEnvironmentRestoration.ps1` launches success/failure/cancellation attempts and proves current directory plus every touched process environment value is byte-for-byte restored. Cleanup tests prove canonical-root/reparse refusal, exact PID/creation ownership, no broad process-name kill, no deletion outside the generated root, and failure when an owned process/file/key remains. `Test-Phase1CBPowerShell51.ps1` enumerates every Phase 1C-B release script, parses each through the Windows PowerShell 5.1 parser with zero errors, rejects PowerShell 7-only syntax/cmdlets, and launches each script's bounded help/invalid-input/success/failure/restoration contracts through `powershell.exe -NoProfile`; the default final verification itself also runs under 5.1.

- [ ] **Step 2: Verify red**

```powershell
& .\desktop\windows\scripts\Test-VerifyPhase1CBEnvironmentRestoration.ps1
& .\desktop\windows\scripts\Test-Phase1CBPowerShell51.ps1
```

Expected: failure because the Phase 1C-B verification scripts do not exist.

- [ ] **Step 3: Implement top-level verification**

The default script performs immutable Yarn install, locked .NET restore, zero-warning Release build, and Node typecheck/tests; then stages and validates the exact Garnet/private-runtime catalog before invoking the payload/AppHost builder with that catalog path. Only after publish does it verify both embedded catalogs, run candidate/BullMQ preflight, database tests, full real-boot/failure matrix, the mandatory Windows Sandbox cold-closure boot, existing Core/Windows/integration/destructive regressions, PowerShell 5.1 parsing/contracts, environment restoration, and unconditional exact cleanup. `-PayloadOnly` preserves the same candidate-stage-before-publish order. It records exact safe top-level verification command vectors plus exits, durations, counts, nonsecret hashes, and sanitized reason codes. It never records secret-bearing child argv, generated credentials, full environment blocks, or raw process command lines.

- [ ] **Step 4: Perform whole-branch independent review and repair loop**

Give a fresh reviewer the approved amendment, this plan, every task report/review,
the committed branch diff `git diff b58cd178..HEAD`, the complete current working-
tree diff plus `git status --short`, and all task-level command evidence. Require
direct inspection of every Task 11 script/README/report path listed above even
though those files are intentionally uncommitted until Step 8. Fix every
Critical/Important finding with focused red-green tests, rerun affected
partitions, and repeat review until specification PASS and quality APPROVED.

- [ ] **Step 5: Run the complete final matrix fresh after review approval**

Only after Step 4 approves the repaired whole branch, run:

```powershell
node .yarn/releases/yarn-4.13.0.cjs install --immutable
dotnet restore desktop/windows/EbayCrm.Desktop.sln --locked-mode --runtime win-x64 --nologo
dotnet build desktop/windows/EbayCrm.Desktop.sln --configuration Release --no-restore --nologo
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\desktop\windows\scripts\Verify-Phase1CB.ps1
pwsh -NoProfile -File .\desktop\windows\scripts\Verify-Phase1CB.ps1 -PayloadOnly
```

Expected: install/restore/build pass, build reports zero warnings/errors, every mandatory partition passes with zero unexpected skips, real frontend/workspace/CRM persistence and one BullMQ worker canary pass, cleanup passes, and immutable payload before/after digests match. If the exact compatibility candidate fails, preserve the failure and select revision rather than rerunning with weakened coverage.

- [ ] **Step 6: Write the final evidence report**

Record base/head commits, the exact safe top-level verification commands, exact Node/.NET-runtime/Garnet/Npgsql/PostgreSQL identities, payload/catalog/database/BullMQ inventory digests, test counts/skips, process ledger, API/canary proof by nonsecret run digest, immutable-root comparison, cleanup result, limitations, and exactly one token selected by the policy above. Do not copy child launch argv, secret-bearing commands, environment blocks, secrets, canary bodies, or raw logs into the report.

- [ ] **Step 7: Independently review the decision-bearing evidence**

Give a fresh reviewer the final report, the complete Step 5 matrix evidence, every
task and whole-branch review, the approved amendment/plan, the committed
`git diff b58cd178..HEAD`, the complete current working-tree diff/status, and
direct copies of every uncommitted Task 11 file. The reviewer must independently verify that the single
decision token follows the stated policy and is supported by exact command/test
counts and skips, artifact/catalog/runtime/database/BullMQ hashes, process
PID/creation and cleanup identities, immutable-root/result digests, limitations,
and sanitized diagnostics. They must also prove the report contains no secret,
full environment, raw child argv/command line, canary body, or raw trace/log.
Repair every evidence/report finding and repeat this review with a fresh reviewer
until approved. If the evidence review discovers any Critical/Important product,
acceptance, or cleanup defect rather than a report-only defect, return to Step 4,
repair and re-review the whole branch, rerun Step 5 fresh, regenerate Step 6, and
repeat Step 7; never preserve a stale adoption token across that loop.

- [ ] **Step 8: Verify and commit scripts/report**

Run:

```powershell
git diff --check
rg -n "(ADOPT|REVISE|REJECT)_REAL_TWENTY_COMPATIBILITY_BOOT" docs/architecture/phase-1c-b-compatibility-boot-report.md
git status --short
```

Expected: diff check passes; the report contains exactly one token match; status contains only intended source/docs.

Then commit:

```powershell
git add desktop/windows/scripts/Verify-Phase1CB.ps1 desktop/windows/scripts/Test-Phase1CBCleanup.ps1 desktop/windows/scripts/Test-VerifyPhase1CBEnvironmentRestoration.ps1 desktop/windows/scripts/Test-Phase1CBPowerShell51.ps1 desktop/windows/scripts/Invoke-Phase1CBColdClosure.ps1 desktop/windows/README.md docs/architecture/phase-1c-b-compatibility-boot-report.md
git commit -m "docs(windows): record Phase 1C-B evidence"
```

## Final integration and local handoff

Only if the report selects adoption, all independent reviews approve, every mandatory test passed with zero unexpected skips, the worktree is clean, and local `main` still has the expected relationship to the feature branch:

1. verify `git merge-base --is-ancestor main codex/phase-1c-b-compatibility-boot`;
2. fast-forward local `main` with `git merge --ff-only codex/phase-1c-b-compatibility-boot`;
3. from local `main`, run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\desktop\windows\scripts\Verify-Phase1CB.ps1 -RealBootOnly`, then `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\desktop\windows\scripts\Test-Phase1CBCleanup.ps1`, `git diff --check`, and `git status --short`; require focused acceptance and cleanup success with a clean tree;
4. remove only the canonical non-reparse worktree through `git worktree remove`; and
5. delete the local feature branch only after the fast-forward and worktree removal succeed.

If any precondition fails, keep the branch/worktree and report the exact blocker. Do not push or open a pull request.
