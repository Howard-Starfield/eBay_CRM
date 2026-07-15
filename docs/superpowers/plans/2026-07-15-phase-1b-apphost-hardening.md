# Phase 1B Windows AppHost Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three Phase 1A evidence gaps and produce an honest adopt/revise/reject decision for the .NET 10 Windows AppHost foundation.

**Architecture:** Extend the existing coordinator and executor with role-generic indeterminate-operation reconciliation, inject the existing bounded JSONL sink through a Windows current-user-only segment factory, and prove the global profile mutex through a one-shot same-user S4U scheduled-task contender. Keep all changes inside the existing AppHost/Windows/test boundaries and preserve PostgreSQL, Job Object containment, and the current control protocol.

**Tech Stack:** .NET 10 LTS, C# 14, xUnit 2.9, Windows Job Objects/named mutexes/Task Scheduler COM, PostgreSQL 16.14, existing AppHost protocol and JSONL diagnostics.

## Global Constraints

- Work from `codex/phase-1b-apphost-hardening`, based on `2507d8c92f3d2771ee803fb18feb757396a740c0`.
- Keep PostgreSQL and the existing Twenty-compatible schema architecture unchanged.
- Do not add Redis, Docker, a permanent Windows service, a second supervisor, or a second logging framework.
- Production code must not invoke PowerShell, `cmd.exe`, `schtasks.exe`, `npm`, or `npx`.
- The deterministic role-operation seam must be internal-only and unreachable through CLI, environment, or profile configuration.
- Diagnostic files are fixed-name, current-user-only, reparse-safe, non-blocking, and bounded to four 1 MiB segments.
- Never log secrets, environment blocks, full command lines, exception messages/stacks, eBay/customer/order content, or knowledge-file content.
- A skipped S4U test cannot produce `ADOPT_DOTNET_APPHOST_FOUNDATION` in the release acceptance command.
- Tests stay focused on the changed lifecycle, OS-security, diagnostics, and acceptance boundaries; do not add the full Twenty test suites.

---

## File Structure

New focused units:

- `desktop/windows/src/HowardLab.EbayCrm.AppHost.Core/Diagnostics/DiagnosticSecretRegistry.cs` — thread-safe registration and snapshot of secret canaries.
- `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/IRoleOperationBoundary.cs` — internal-only deterministic late-operation seam and production no-op.
- `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Diagnostics/WindowsDiagnosticSegmentFactory.cs` — secure, fixed-slot JSONL stream creation.
- `desktop/windows/tests/HowardLab.EbayCrm.AppHost.AcceptanceBroker/` — password-free same-user session contender helper.
- `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Acceptance/TaskSchedulerS4uRunner.cs` — one-shot task registration, execution, collection, and cleanup.
- `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Acceptance/CrossSessionOwnershipAcceptanceTests.cs` — published cross-session ownership gate.
- `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Diagnostics/WindowsDiagnosticSegmentFactoryTests.cs` — ACL/path/rotation stream tests.

Existing files changed together:

- lifecycle contract/state/tests;
- executor/composition/orchestrator and AppHost integration tests;
- diagnostics sink/launcher composition and canary acceptance tests;
- solution/project files, README commands, and Phase 1A evidence report.

---

### Task 1: Add role-generic reconciliation to the lifecycle core

**Files:**

- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Core/Lifecycle/LifecycleCommand.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Core/Lifecycle/RuntimeState.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Core/Lifecycle/LifecycleCoordinator.cs`
- Modify: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Core.Tests/Lifecycle/LifecycleCoordinatorTests.cs`

**Interfaces:**

- Produces `LifecycleCommandType.ReconcileRoleStart` and `LifecycleCommandType.ReconcileRoleStop`.
- Produces `LifecycleDeadlineKey.RoleReconciliation`.
- Produces `RuntimeState.ReconcilingRoleStart` and `RuntimeState.ReconcilingRoleStop`.
- Preserves the existing `OperationTimedOut` and `Reconciled` events.

- [ ] **Step 1: Write failing coordinator tests for late server/worker start**

Add table-driven tests which drive the coordinator to `StartingServer` and
`StartingWorker`, dispatch:

```csharp
var timeout = await coordinator.DispatchAsync(
    new OperationTimedOut(generation, generation.OperationId));

Assert.Equal(RuntimeState.ReconcilingRoleStart, timeout.Current);
var command = Assert.Single(timeout.Commands);
Assert.Equal(LifecycleCommandType.ReconcileRoleStart, command.Type);
Assert.Equal(generation, command.Generation);
Assert.Equal(LifecycleDeadlineKey.RoleReconciliation, command.DeadlineKey);
```

Then prove `ReconciledState.Running` returns to the matching
`WaitingForServer`/`WaitingForWorker` state and emits exactly one matching
`WaitForServer`/`WaitForWorker` command. Prove a stale generation or operation
is ignored and cannot advance the state.

- [ ] **Step 2: Write failing coordinator tests for late server/worker stop**

Start a fully ready coordinator, dispatch `StopRequested`, then dispatch an
`OperationTimedOut` for each role's stop command. Require:

```csharp
Assert.Equal(RuntimeState.ReconcilingRoleStop, timeout.Current);
Assert.Equal(
    LifecycleCommandType.ReconcileRoleStop,
    Assert.Single(timeout.Commands).Type);
```

Prove `ReconciledState.Stopped` returns to `Stopping`; `Unknown` remains
reconciling so shutdown must fault/escalate; and a late result from the old
generation does not affect a newer one.

- [ ] **Step 3: Run the focused tests and confirm they fail**

Run:

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Core.Tests\HowardLab.EbayCrm.AppHost.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~LifecycleCoordinatorTests" --nologo
```

Expected: failures because the new commands/states and transitions do not yet
exist.

- [ ] **Step 4: Implement the minimal coordinator contract**

Add the enum values above. Track the one active role reconciliation explicitly:

```csharp
private ProcessGeneration? _reconcilingRole;
private RoleReconciliationKind? _roleReconciliationKind;

private enum RoleReconciliationKind
{
    Start,
    Stop,
}
```

For server/worker start timeouts, set the retained generation/kind, enter
`ReconcilingRoleStart`, and emit `ReconcileRoleStart`. For server/worker stop
timeouts while `Stopping`, enter `ReconcilingRoleStop` and emit
`ReconcileRoleStop`. Accept a `Reconciled` result only when role, generation,
operation ID, and kind match. Clear these fields on every terminal or successful
transition.

If a late start reconciles stopped, feed the conclusive exit through the
existing bounded restart policy; create at most one next generation per
accepted recovery transition and fault when the restart budget is exhausted.
If late stop reconciles unknown, retain `ReconcilingRoleStop` for the existing
bounded shutdown escalation.

Refactor `RecoverServer` and `RecoverWorker` to emit only their `Start*`
command while in `StartingServer`/`StartingWorker`. Let `RoleStartedTransition`
emit the single `Wait*` command, matching initial startup. This removes stale
pre-queued waits during reconciliation.

- [ ] **Step 5: Run the lifecycle core tests**

Run the command from Step 3.

Expected: all `LifecycleCoordinatorTests` pass.

- [ ] **Step 6: Commit Task 1**

```powershell
git add desktop/windows/src/HowardLab.EbayCrm.AppHost.Core/Lifecycle desktop/windows/tests/HowardLab.EbayCrm.AppHost.Core.Tests/Lifecycle/LifecycleCoordinatorTests.cs
git commit -m "feat: reconcile late AppHost role operations"
```

---

### Task 2: Preserve and reconcile real server/worker process identity

**Files:**

- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/IRoleOperationBoundary.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AppHostComposition.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/LifecycleCommandExecutor.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/RuntimeOrchestrator.cs`
- Modify: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Acceptance/TimeoutReconciliationAcceptanceTests.cs`
- Modify: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/AppHost/AppHostShutdownTests.cs`

**Interfaces:**

- Consumes the commands and states from Task 1.
- Produces:

```csharp
internal enum RoleOperationBoundaryPoint
{
    StartIdentityRetained,
    StopAccepted,
}

internal interface IRoleOperationBoundary
{
    ValueTask PauseAsync(
        RoleOperationBoundaryPoint point,
        ProcessGeneration generation,
        Guid operationId,
        CancellationToken roleLifetimeToken);
}
```

- `AppHostComposition.CreateForTests` accepts an optional internal boundary;
  public `Create` always uses the no-op boundary.

- [ ] **Step 1: Write failing real-composition late-start tests**

Add a test boundary that blocks exactly once inside the retained accept task at
`StartIdentityRetained` and exposes the observed role/generation/PID. Use an
injected 1 ms command deadline so the command times out while that accept task
remains live, then release the boundary during the separate reconciliation
deadline. Run the real PostgreSQL plus Fixture composition twice, once for
server and once for worker. Assert:

```csharp
Assert.Contains(RuntimeState.ReconcilingRoleStart, runtime.Orchestrator.StateHistory);
Assert.Equal(RuntimeState.Ready, runtime.Orchestrator.State);
Assert.Equal(observedGeneration, runtime.Executor.SnapshotForTests().ServerGeneration); // or WorkerGeneration
Assert.Equal(1, CountLiveFixtureProcesses(observedGeneration));
```

Capture process identity by retained PID and creation time, not process name
alone. Assert no second generation appears before or after reconciliation.

- [ ] **Step 2: Write failing real-composition late-stop tests**

Pause inside the retained stop-completion task at `StopAccepted` for server and
worker. Use an injected 1 ms command deadline so the stop returns indeterminate,
then release the boundary and require the same retained process handle/task to
reconcile stopped within the remaining shutdown stage. Assert eventual
`Stopped`, one ownership release, no remaining Fixture or PostgreSQL identity
from the disposable profile, and no newer generation is terminated by a late
result.

- [ ] **Step 3: Run the focused tests and confirm they fail**

Run:

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --no-restore --filter "FullyQualifiedName~TimeoutReconciliationAcceptanceTests|FullyQualifiedName~AppHostShutdownTests" --nologo
```

Expected: compile/test failures because the seam and executor reconciliation do
not exist.

- [ ] **Step 4: Implement the internal-only boundary**

Create the interface/enums above and:

```csharp
internal sealed class NoopRoleOperationBoundary : IRoleOperationBoundary
{
    internal static NoopRoleOperationBoundary Instance { get; } = new();

    public ValueTask PauseAsync(
        RoleOperationBoundaryPoint point,
        ProcessGeneration generation,
        Guid operationId,
        CancellationToken roleLifetimeToken)
    {
        roleLifetimeToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
```

Keep every type `internal`. Do not read an environment variable or command-line
flag to select it.

- [ ] **Step 5: Make role resources indeterminate-safe**

Extend `RoleResource` with one role-lifetime `CancellationTokenSource`, one
retained `AcceptTask`, optional retained `StopCompletionTask`, authenticated
state, and shutdown operation ID. After `SetRole(resource)`, start exactly one
task that pauses at `StartIdentityRetained` and then calls `AcceptAsync` with the
role-lifetime token. Await that task through an internal command-deadline token,
not by passing the deadline token into `AcceptAsync`. If the command deadline
expires, return `OperationTimedOut` without disposing process, channel, Job, or
accept task. Reconciliation awaits the original task.

In the catch path, clean up only when the resource was not deliberately
retained for reconciliation. Do not clear a retained role from a timeout path.

After reading `ShutdownAccepted`, record the stop operation and start exactly
one role-lifetime task that pauses at `StopAccepted`, reads `Stopped`, and waits
the retained process handle. Await it through the internal stop-command
deadline. Expiry returns `OperationTimedOut` without disposing the resource.
Normal or reconciled completion awaits the same task and disposes once.

- [ ] **Step 6: Implement executor reconciliation commands**

Add dispatch cases and methods with these contracts:

```csharp
private Task<LifecycleEvent> ReconcileRoleStartAsync(
    LifecycleCommand command,
    CancellationToken cancellationToken);

private Task<LifecycleEvent> ReconcileRoleStopAsync(
    LifecycleCommand command,
    CancellationToken cancellationToken);
```

Start reconciliation matches the complete immutable launch generation and
awaits the one retained accept task with a fresh bounded reconciliation token.
Stop reconciliation matches immutable role plus generation value, then
separately requires `command.OperationId == resource.ShutdownOperationId`; it
returns the coordinator's retargeted command generation in the result. A
signaled handle means dispose once and return stopped; live at the bounded
deadline means unknown. It never starts a second accept or sends a second
shutdown to a newer generation.

Add an internal `RoleOperationDeadlines` value to `CreateForTests` and the
executor. Production uses a 10-second start command deadline, 5-second stop
command deadline, and 30-second role-reconciliation deadline. Tests inject
1 ms command deadlines. These internal deadlines expire before the outer
45-second shutdown budget, leaving bounded time for reconciliation.

- [ ] **Step 7: Make shutdown recognize role reconciliation**

Map `ReconcileRoleStop` to its role in `RuntimeOrchestrator.ShutdownRole`. When
an executor returns `OperationTimedOut`, dispatch it with
`CancellationToken.None`; execute only the resulting reconciliation command
with the fresh bounded role-reconciliation token. Do not rollback startup while
that retained operation is indeterminate. During shutdown, cap reconciliation
to the remaining stage/total budget. Treat a coordinator that remains in
`ReconcilingRoleStop` after the bounded command as faulted so the existing
single total-budget escalation runs. Permit `ShutdownFailedTransition` from
`ReconcilingRoleStop`.

- [ ] **Step 8: Run focused and solution tests**

Run the command from Step 3, then:

```powershell
dotnet test desktop\windows\EbayCrm.Desktop.sln --no-restore --nologo
```

Expected: all non-environment-skipped tests pass; the new real role tests pass
when `EBAYCRM_POSTGRES_BIN` is configured.

- [ ] **Step 9: Commit Task 2**

```powershell
git add desktop/windows/src/HowardLab.EbayCrm.AppHost desktop/windows/src/HowardLab.EbayCrm.AppHost.Core desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests
git commit -m "fix: retain late AppHost role operations for reconciliation"
```

---

### Task 3: Wire secure bounded diagnostics into production composition

**Files:**

- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Core/Diagnostics/DiagnosticSecretRegistry.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Core/Diagnostics/JsonLinesDiagnosticSink.cs`
- Create: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Diagnostics/WindowsDiagnosticSegmentFactory.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Native/NativeMethods.Files.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/AppHostComposition.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition/LifecycleCommandExecutor.cs`
- Modify: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Core.Tests/Diagnostics/DiagnosticSafetyTests.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Diagnostics/WindowsDiagnosticSegmentFactoryTests.cs`
- Modify: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Acceptance/DiagnosticCanaryAcceptanceTests.cs`

**Interfaces:**

- Produces:

```csharp
public sealed class DiagnosticSecretRegistry
{
    public void Register(SecretValue secret);
    internal string Redact(string value);
}

public sealed class WindowsDiagnosticSegmentFactory
{
    public WindowsDiagnosticSegmentFactory(DataProfileIdentity profile);
    public ValueTask<Stream> OpenAsync(int slot, CancellationToken cancellationToken);
}
```

- `JsonLinesDiagnosticSink` consumes one shared `DiagnosticSecretRegistry` so
  secrets generated after sink construction can be registered before use.
- Production wraps the sink in an ownership gate; `Activate()` is internal and
  is called only after `UserProfileInstanceLock.TryAcquireAsync` succeeds.

- [ ] **Step 1: Write failing dynamic-redaction tests**

Construct a sink with an empty registry, register a `SecretValue` after
construction, write a field containing that canary split around a 4,096-byte
boundary, complete the sink, and assert the bytes contain `[REDACTED]` and not
the canary. Add concurrent register/write coverage and reject null/empty/weak
canaries through the existing `SecretCanary.Validate` rule.

- [ ] **Step 2: Write failing secure-segment tests**

Using a disposable fixed-drive profile, require `OpenAsync(0)` to create
`logs\apphost-0.jsonl`, truncate it on reuse, reject slots outside `0..3`, reject
a reparse-point logs directory, and expose a DACL containing the current user
as the only allowed SID. Filenames must remain exactly the four documented
values. Add a gated-sink test proving writes before activation create no
directory/file and writes after activation reach the inner sink. Pre-create a
directory and segment with inherited/broad ACLs and require fail-closed behavior
rather than reuse. Race a reparse-point replacement and prove the factory's
`FILE_FLAG_OPEN_REPARSE_POINT` handle validation rejects it without touching the
target.

- [ ] **Step 3: Run focused tests and confirm they fail**

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Core.Tests\HowardLab.EbayCrm.AppHost.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~DiagnosticSafetyTests" --nologo
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Windows.Tests\HowardLab.EbayCrm.AppHost.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~WindowsDiagnosticSegmentFactoryTests" --nologo
```

Expected: compile/test failures for the missing registry/factory.

- [ ] **Step 4: Implement the shared secret registry**

Store validated canaries in an immutable snapshot updated under a small lock,
sorted longest first. `Redact` reads one snapshot and performs ordinal
replacement. Change `JsonLinesDiagnosticSink` to use the registry in
`RedactAndTruncate`; adapt existing constructor callers without adding a second
redaction path.

Register the profile database password immediately after
`OpenOrCreateAsync`. Register every `SecretEnvironment` value returned by
`CreateChildEnvironment` before launching its child. No event may be emitted
between secret creation and registration.

- [ ] **Step 5: Implement native secure segment creation**

Add only the required `CreateDirectoryW`/`CreateFileW` constants and interop.
Use `NativeSecurityDescriptor.CreateForCurrentUserOnly()` in non-inheritable
security attributes. Validate the canonical profile and logs path with
`DataProfileIdentity.EnsureNoReparsePoints` before and after directory
creation. Open only the fixed slot path with create/truncate semantics, no
sharing that permits replacement, `FILE_ATTRIBUTE_NORMAL |
FILE_FLAG_OPEN_REPARSE_POINT`, and no inherited handle. Query and compare the
exact protected DACL on a pre-existing directory and on every opened segment;
only the current user SID is permitted. Reject inherited/broad/unreadable ACLs.
Re-check the opened file handle's attributes and reject a reparse point before
returning its `FileStream`.

- [ ] **Step 6: Wire one owned production sink**

In `AppHostComposition.CreateRuntime`, create one registry, one
`WindowsDiagnosticSegmentFactory`, one `JsonLinesDiagnosticSink`, and one
ownership-gated wrapper using:

```csharp
channelCapacity: 256,
maxFieldBytes: 4_096,
maxSegmentBytes: 1_048_576,
maxSegmentCount: 4
```

Inject the gated sink into `LifecycleCommandExecutor` and from there into its
single `WindowsProcessLauncher`. Remove the executor's private no-op sink. Call
`Activate()` only after both profile ownership handles are held in
`AcquireInstanceAsync`. A losing contender must dispose the unopened sink and
leave no `logs` directory or segment change. The executor owns sink
completion/disposal after role/process cleanup; capture sink disposal failure
with the existing cleanup aggregation without preventing ownership release.

Add a one-second production diagnostic-completion budget. Executor disposal
requests `CompleteAsync` through that budget. On expiry, cancel the writer,
increment a non-secret completion-timeout counter, and continue ownership
release without awaiting `DisposeAsync` or any stream operation that ignores
cancellation. Add a blocking stream whose write/flush/dispose never completes
and assert AppHost shutdown plus profile reacquisition completes within
1.5 seconds.

- [ ] **Step 7: Add production-composition acceptance coverage**

Run the real composition with a registered test canary, capture the actual
generated PostgreSQL password and role capability nonces through an
internal-only observer that receives `SecretValue` objects, force enough typed
events to rotate past 1 MiB, then assert at most four JSONL files and at most
4 MiB total. Scan actual JSONL plus any test-created ZIP/manifest for all actual
and static canaries through `AcceptanceArtifactScanner`. Start a losing
same-profile contender and prove it creates/truncates/modifies no segment.
Repeat with an unwritable/throwing test segment factory and prove
startup/shutdown and profile release remain correct while `SinkFailureCount`
increases.

- [ ] **Step 8: Run focused and solution tests**

Run both Step 3 commands and:

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --no-restore --filter "FullyQualifiedName~DiagnosticCanaryAcceptanceTests" --nologo
dotnet test desktop\windows\EbayCrm.Desktop.sln --no-restore --nologo
```

Expected: all focused tests pass; solution remains green apart from documented
environment-only skips.

- [ ] **Step 9: Commit Task 3**

```powershell
git add desktop/windows/src/HowardLab.EbayCrm.AppHost.Core/Diagnostics desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows desktop/windows/src/HowardLab.EbayCrm.AppHost/Composition desktop/windows/tests/HowardLab.EbayCrm.AppHost.Core.Tests/Diagnostics desktop/windows/tests/HowardLab.EbayCrm.AppHost.Windows.Tests/Diagnostics desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Acceptance/DiagnosticCanaryAcceptanceTests.cs
git commit -m "feat: enable bounded production AppHost diagnostics"
```

---

### Task 4: Automate same-user different-session ownership evidence

**Files:**

- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.AcceptanceBroker/HowardLab.EbayCrm.AppHost.AcceptanceBroker.csproj`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.AcceptanceBroker/Program.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Acceptance/TaskSchedulerS4uRunner.cs`
- Create: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/Acceptance/CrossSessionOwnershipAcceptanceTests.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Processes/WindowsJobObject.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/Native/NativeMethods.Processes.cs`
- Modify: `desktop/windows/src/HowardLab.EbayCrm.AppHost.Windows/AppHostAssembly.cs`
- Modify: `desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests/HowardLab.EbayCrm.AppHost.Integration.Tests.csproj`
- Modify: `desktop/windows/EbayCrm.Desktop.sln`

**Interfaces:**

- Task action passes only `--request <absolute-local-path>`. The current-user-
  only request JSON contains exact absolute paths, a 256-bit nonce, and the
  fixed result path. No password, token, environment block, or network path is
  accepted.

- Broker writes one atomic JSON result with version, nonce, SID, broker session
  ID, contender PID/session ID, exit code, exact bounded stdout/stderr,
  cumulative Job total-process count, and UTC timestamps.
- `TaskSchedulerS4uRunner.RunAsync` registers one uniquely named task through
  Task Scheduler COM, waits with a bounded deadline, returns parsed result, and
  deletes the task/folder/result in `finally`.

- [ ] **Step 1: Write the failing cross-session acceptance test**

Publish AppHost and broker to disposable directories. Start the owner AppHost
and wait for `Ready`. Run the broker through S4U and assert:

```csharp
Assert.Equal(ownerSid, result.UserSid);
Assert.NotEqual(ownerSessionId, result.BrokerSessionId);
Assert.Equal(result.BrokerSessionId, result.ContenderSessionId);
Assert.Equal(2, result.ExitCode);
Assert.Equal("profile-already-owned", result.StandardError.Trim());
Assert.Equal(RuntimeState.AcquiringInstance.ToString(), result.StandardOutput.Trim());
Assert.Equal(1u, result.TotalProcesses);
```

The broker atomically assigns the contender to its own Job at process creation.
Require cumulative `TotalProcesses == 1`, which detects transient children even
after they exit. Also prove the owner remains healthy and segment hashes/times
under the owned profile did not change because of the contender.

- [ ] **Step 2: Run the test and confirm it fails**

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --no-restore --filter "FullyQualifiedName~CrossSessionOwnershipAcceptanceTests" --nologo
```

Expected: compile failure because the broker and S4U runner do not exist.

- [ ] **Step 3: Implement the bounded acceptance broker**

Validate the request file and every absolute local path, nonce length, result
parent, and numeric value. Reject request/result reparse points. Record SID with
`WindowsIdentity.GetCurrent().User` and session with
`Process.GetCurrentProcess().SessionId`. Launch AppHost through the existing
`WindowsProcessLauncher` into a broker-owned `WindowsJobObject`, redirect and
cap stdout/stderr at 64 KiB, wait no more than 30 seconds, and terminate only
the retained contender identity/Job on timeout.

Serialize the result to `<result>.<nonce>.tmp`, flush, and atomically move to the
final fixed result path. Create temporary/result files current-user-only and
never include credentials or environment data.

Expose an internal Job accounting snapshot based on
`QueryInformationJobObject(JobObjectBasicAccountingInformation)`. Friend only
the acceptance broker assembly. Record cumulative `TotalProcesses` after the
contender exits and before closing the Job; do not infer child creation from a
post-exit process snapshot.

- [ ] **Step 4: Implement Task Scheduler S4U orchestration**

Use Task Scheduler COM (`Schedule.Service`) directly. Set a one-shot execution
time, `TASK_LOGON_S4U`, the current account identity, least privilege, a
30-second execution limit, and a direct executable action for the broker.
Never use `schtasks.exe`, a password, a network path, or a service.

Use a unique `\HowardLab\eBayCRM-Acceptance-<guid>` task name. Poll task state
and the nonce-bound result until the bounded deadline. Validate same SID,
different session ID, nonce, schema version, and process identity before
returning. Delete the task and any empty test folder in `finally`, including on
timeout or malformed results.

If S4U registration is blocked by Windows policy during an ordinary developer
run, throw an explicit `S4uEnvironmentUnavailableException` containing only a
stable reason code. The release gate converts that condition to failure, not
success.

- [ ] **Step 5: Prove cleanup and tamper rejection**

Add tests for wrong nonce, stale result, same-session result, malformed JSON,
task timeout, broker crash, and cleanup failure. Each test must verify no
scheduled test task, broker/AppHost contender, result file, or disposable
profile remains.

- [ ] **Step 6: Run focused and existing ownership tests**

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --no-restore --filter "FullyQualifiedName~CrossSessionOwnershipAcceptanceTests|FullyQualifiedName~SingleInstanceAcceptanceTests" --nologo
```

Expected: all ownership tests pass on the supported Windows 11 release
environment; ordinary development may show only the explicit policy skip.

- [ ] **Step 7: Commit Task 4**

```powershell
git add desktop/windows/EbayCrm.Desktop.sln desktop/windows/tests/HowardLab.EbayCrm.AppHost.AcceptanceBroker desktop/windows/tests/HowardLab.EbayCrm.AppHost.Integration.Tests
git commit -m "test: prove cross-session AppHost profile ownership"
```

---

### Task 5: Run adoption gates and update evidence

**Files:**

- Modify: `desktop/windows/README.md`
- Modify: `docs/architecture/phase-1a-windows-apphost-supervision-report.md`

**Interfaces:**

- Consumes all prior tasks.
- Produces exactly one final token:
  `ADOPT_DOTNET_APPHOST_FOUNDATION`, `REVISE_APPHOST_FOUNDATION`, or
  `REJECT_DOTNET_APPHOST_FOUNDATION`.

- [ ] **Step 1: Document exact restore/build/publish/test commands**

Add a Phase 1B section to `desktop/windows/README.md` with locked restore,
Release build, clean self-contained `win-x64` publish, focused role/diagnostic/
S4U filters, non-destructive integration partition, and destructive containment
partition. Include the environment variable required for the pinned PostgreSQL
16.14 binaries.

- [ ] **Step 2: Run clean restore and Release build**

```powershell
dotnet restore desktop\windows\EbayCrm.Desktop.sln --locked-mode
dotnet build desktop\windows\EbayCrm.Desktop.sln --configuration Release --no-restore --nologo
```

Expected: 0 warnings and 0 errors.

- [ ] **Step 3: Run the focused Phase 1B acceptance gates**

```powershell
dotnet test desktop\windows\tests\HowardLab.EbayCrm.AppHost.Integration.Tests\HowardLab.EbayCrm.AppHost.Integration.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~TimeoutReconciliationAcceptanceTests|FullyQualifiedName~DiagnosticCanaryAcceptanceTests|FullyQualifiedName~CrossSessionOwnershipAcceptanceTests" --logger "console;verbosity=detailed" --nologo
```

Expected: zero failures and zero skips for the four role boundaries,
production diagnostics, and S4U release gate.

- [ ] **Step 4: Run all AppHost tests in the documented partitions**

Run core, Windows, non-destructive integration, and destructive containment
commands from the updated README. Expected: zero failures; no environment skip
among Phase 1B acceptance gates.

- [ ] **Step 5: Audit processes and temporary artifacts**

Use retained test identities and exact disposable prefixes. Require zero
surviving AppHost, Fixture, broker, PostgreSQL helper/postmaster identities,
zero `eBayCRM-Acceptance-*` scheduled tasks, and zero Phase 1B disposable roots.
Do not kill or inspect unrelated user processes by name.

- [ ] **Step 6: Write the Phase 1B evidence addendum**

Append exact commit/tree, environment versions, commands, counts, role
generation/PID/creation-time evidence, segment counts/bytes/canary scan counts,
SID/session IDs, cleanup audit, and any honest limitation to
`phase-1a-windows-apphost-supervision-report.md`. Preserve the original Phase 1A
section. End with exactly one decision token based only on observed results.

- [ ] **Step 7: Verify the evidence tree and commit**

```powershell
git diff --check
git status --short
git add desktop/windows/README.md docs/architecture/phase-1a-windows-apphost-supervision-report.md
git commit -m "docs: record Phase 1B AppHost adoption evidence"
```

Expected: clean commit and no generated publish/test artifacts staged.

- [ ] **Step 8: Request final code review**

Invoke `superpowers:requesting-code-review` over the full Phase 1B diff. Address
only verified findings, rerun affected focused tests, then rerun the final
solution/acceptance commands before claiming completion.
