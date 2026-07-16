# Phase 1C Published Node Probe Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the Phase 1C-A evidence gap by staging dependency-closed JavaScript Node probes beside the self-contained AppHost and running one complete start-ready-stop lifecycle through the published AppHost executable.

**Architecture:** Add an acceptance-only `controlled-node-probe` role target and `acceptance-run-once` mode, both fail-closed unless `EBAYCRM_RELEASE_ACCEPTANCE=1`. The release script compiles the existing TypeScript probe sources to JavaScript, copies the selected `node.exe`, creates a bounded hash manifest, and launches the published AppHost against that payload. This does not expose the future trusted Twenty target, weaken its ACL validator, or claim a full Twenty/Redis boot.

**Tech Stack:** .NET 10 LTS, C# 14, xUnit 2.9, Windows Job Objects and named pipes, Node 24, TypeScript 5.9, PowerShell 5.1-compatible release scripting, PostgreSQL 16.14.

## Global Constraints

- Preserve PostgreSQL and explicit `RedisCompatibility`; `postgres-desktop` remains fail-closed.
- The new mode/target pair is accepted only when `EBAYCRM_RELEASE_ACCEPTANCE` is exactly `1`; it cannot be combined with the fixture target or normal run mode.
- Reuse the existing protocol, lifecycle coordinator, process containment, and Node probe state machine.
- Do not put secrets in command-line arguments, manifests, logs, or generated payload files.
- Generated Node payload files remain under `desktop/windows/artifacts/win-x64` and are never committed.
- Use focused red/green tests and affected suites; do not repeat the full acceptance matrix unless a changed boundary invalidates its prior evidence.

---

### Task 1: Add the acceptance-only published probe launch surface

**Files:**
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AppHostOptions.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AppHostComposition.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Program.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/PublishedNodeProbeRoleLaunchPlanProvider.cs`
- Test: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/AppHost/AppHostStartupTests.cs`
- Test: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Node/PublishedNodeProbeRoleLaunchPlanProviderTests.cs`

**Interfaces:**
- Consumes: existing `IRoleLaunchPlanProvider`, `RoleLaunchPlan`, Node control protocol, and runtime orchestration.
- Produces: `AppHostMode.AcceptanceRunOnce`, `AppHostRoleTarget.ControlledNodeProbe`, optional `AppHostOptions.NodeProbeRoot`, and a provider that launches exact `node.exe`, `app/probes/server-probe.js`, and `app/probes/worker-probe.js` paths without `tsx`.

- [ ] **Step 1: Write failing option and combination tests**

  Cover exact parsing of `--mode acceptance-run-once --role-target controlled-node-probe --node-probe-root <absolute-root>` when the release-acceptance variable is present. Cover missing variable, missing root, fixture/target mismatch, normal-run/probe mismatch, duplicate option, relative/UNC/root-with-reparse input, and unknown target. Assert stable reason codes and no payload/process launch.

- [ ] **Step 2: Run the focused parser tests and confirm red**

  Run:

  ```powershell
  dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~AppHostStartupTests" --nologo
  ```

  Expected: only the new acceptance mode/target cases fail because the enum values and target-specific option are absent.

- [ ] **Step 3: Implement conditional option parsing and run-once orchestration**

  Keep the existing fixture arguments compatible. Add `NodeProbeRoot` as a nullable final record property. Require an absolute local existing root only for the controlled Node target. In `Program.cs`, start the orchestrator, require `RuntimeState.Ready`, then call `StopAsync` and require `RuntimeState.Stopped`; normal `run` retains `RunUntilStoppedAsync`.

- [ ] **Step 4: Write failing provider tests**

  Prove exact `.js` entrypoints and direct Node arguments, per-role health ports, root containment, no reparse-point bootstrap file, correct build identity, bounded bootstrap-file leases, and rejection of `.ts`, `tsx`, outside-root, missing, duplicate, malformed, or swapped artifacts.

- [ ] **Step 5: Implement the published provider**

  Validate the exact generated layout, snapshot manifest build identity and SHA-256 values, and reverify the declared closure after role exit. Open read-only handles without delete sharing for `node.exe`, the selected entrypoint, and manifest during launch. Do not reuse or weaken `TrustedNodePayloadValidator`; that validator remains the production ACL boundary.

- [ ] **Step 6: Run focused provider and option tests**

  Run:

  ```powershell
  dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~PublishedNodeProbeRoleLaunchPlanProviderTests|FullyQualifiedName~AppHostStartupTests" --nologo
  ```

  Expected: zero failures and zero unexpected skips.

- [ ] **Step 7: Request independent code review before committing**

  The reviewer must check that the acceptance gate cannot select a real production target, that no command-line secret surface was added, and that file/path leases preserve the existing post-exit verification contract.

---

### Task 2: Stage the generated Node payload and execute the published host

**Files:**
- Create: `desktop/windows/node/tsconfig.publish.json`
- Modify: `desktop/windows/scripts/Verify-Phase1C.ps1`
- Modify: `desktop/windows/scripts/Test-Phase1CCleanup.ps1`
- Test: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Node/PublishedNodeProbeAppHostSmokeTests.cs`

**Interfaces:**
- Consumes: Task 1's acceptance mode/target and exact generated layout.
- Produces: generated `node-probe/node.exe`, `node-probe/app/**/*.js`, `node-probe/node-payload-manifest-v1.json`, and one external published-AppHost lifecycle result.

- [ ] **Step 1: Add a failing published inventory/smoke test**

  Assert the generated manifest is strict JSON without BOM, every declared artifact has the recorded byte length and uppercase SHA-256, no undeclared file exists, the JavaScript entrypoints are present, and the published AppHost exits `0` only after reporting `Ready` then `Stopped`. Retain exact PID/creation-time identities for AppHost, PostgreSQL, Node roles, and descendants and prove those identities have exited.

- [ ] **Step 2: Run only the new published smoke test and confirm red**

  Run:

  ```powershell
  dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~PublishedNodeProbeAppHostSmokeTests" --nologo
  ```

  Expected: fail because the generated payload and acceptance target are not yet staged in the published folder.

- [ ] **Step 3: Add the publish TypeScript configuration**

  Extend `tsconfig.json`, set `noEmit` to `false`, `rootDir` to `src`, exclude tests, and emit ES2024 NodeNext JavaScript into the release-script-provided `node-probe/app` output directory. Do not create a second lockfile or workspace.

- [ ] **Step 4: Stage the payload in the release script**

  After .NET publish and Node typecheck/tests, compile the sources, copy the resolved `node.exe`, enumerate every generated artifact, calculate byte length and SHA-256, and write a no-BOM manifest atomically. Reject reparse points and undeclared files. Generate a unique temporary profile and a reserved loopback PostgreSQL port, run the published AppHost with the acceptance mode/target, then remove the profile only after the process and retained children are stopped.

- [ ] **Step 5: Extend the cleanup audit**

  Require the Node executable, manifest, server/worker JavaScript entrypoints, and their imported JavaScript closure. Continue to describe the process substring scan as non-exhaustive; the smoke test supplies exact PID/creation-time evidence for this run.

- [ ] **Step 6: Run the focused published gate**

  Run the Node typecheck/tests, republish, stage the payload, and run only `PublishedNodeProbeAppHostSmokeTests`. Expected: TypeScript passes, 61 Node tests pass, the external host exits `0`, state order includes `Ready` before `Stopped`, and no retained identity remains alive.

- [ ] **Step 7: Request independent code review before committing**

  The reviewer must inspect PowerShell 5.1 compatibility, path deletion/staging safety, manifest closure, secret scanning, external process cleanup, and whether the test genuinely launches the published executable rather than the in-process composition seam.

---

### Task 3: Record corrected Phase 1C-A evidence

**Files:**
- Modify: `desktop/windows/README.md`
- Modify: `docs/architecture/phase-1c-production-launch-boundary-report.md`

**Interfaces:**
- Consumes: reviewed Task 1 and Task 2 commits plus their exact command output.
- Produces: one reproducible Phase 1C command and one decision token supported by published-host evidence.

- [ ] **Step 1: Run proportional final verification**

  Run a clean Release build, clean self-contained publish and payload staging, Node gates, Core suite, Windows suite, focused new integration tests, and isolated destructive containment. Reuse the previously recorded unchanged ordinary-integration total only if the affected integration project passes the new focused partitions and reviewers confirm no unrelated lifecycle boundary changed; otherwise rerun that partition once.

- [ ] **Step 2: Run exact cleanup and secret checks**

  Confirm the smoke test's retained identities have exited, no run-created `ebaycrm-*` directory remains, the publish manifest contains no secret canary, and the non-exhaustive matching scan is clean.

- [ ] **Step 3: Update README and report**

  Record the tested source commit, actual versions/counts, generated payload inventory, external state sequence, exact-identity cleanup evidence, limitations, and exactly one supported decision token. Continue to state that full Twenty server/worker, bundled Redis, eBay UI, installer, tray, updater, backup, and local LLM are outside this phase.

- [ ] **Step 4: Request final specification and code-quality reviews**

  Both reviews must pass with no important findings before the evidence commit.

