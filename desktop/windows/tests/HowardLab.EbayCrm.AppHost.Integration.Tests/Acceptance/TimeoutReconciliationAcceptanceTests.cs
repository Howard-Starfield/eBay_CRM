using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using HowardLab.EbayCrm.AppHost.Core.Time;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

public sealed class TimeoutReconciliationAcceptanceTests
{
    [Fact, Trait("Category", "Acceptance")]
    public async Task ExhaustedRoleStartReconciliation_ContainsBeforeReleaseWithoutStartupRollback()
    {
        var executor = new ExhaustedRoleStartReconciliationExecutor();
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor);

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.StartAsync());

        Assert.Equal(RuntimeState.Faulted, orchestrator.State);
        Assert.Equal(0, executor.RollbackCount);
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.EscalateJob));
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.ReleaseInstance));
        Assert.True(
            executor.Commands.IndexOf(LifecycleCommandType.EscalateJob) <
            executor.Commands.IndexOf(LifecycleCommandType.ReleaseInstance));
    }

    [PostgresTheory, Trait("Category", "Acceptance")]
    [InlineData(RuntimeRole.Server)]
    [InlineData(RuntimeRole.Worker)]
    public async Task LateFixtureStart_ReconcilesTheRetainedProcessIdentity(RuntimeRole role)
    {
        using var layout = TestLayout.CreateReal("ebaycrm-task2-late-start");
        var boundary = new BlockingRoleOperationBoundary(role, RoleOperationBoundaryPoint.StartAcceptInFlight);
        var runtime = AppHostComposition.CreateForTests(
            AppHostOptions.Parse(layout.Arguments("run")),
            roleOperationBoundary: boundary,
            roleOperationDeadlines: new RoleOperationDeadlines(
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30)));
        runtime.Executor.RoleLaunchedForTests = boundary.RecordLaunched;
        try
        {
            var startup = runtime.Orchestrator.StartAsync();
            await boundary.WaitUntilBlockedAsync(TimeSpan.FromSeconds(30));
            await WaitForStateAsync(
                runtime.Orchestrator,
                RuntimeState.ReconcilingRoleStart,
                TimeSpan.FromSeconds(10));
            var generation = boundary.ObservedGeneration;

            Assert.Contains(RuntimeState.ReconcilingRoleStart, runtime.Orchestrator.StateHistory);
            Assert.Equal(generation, GenerationFor(runtime.Executor.SnapshotForTests(), role));
            Assert.Equal(boundary.ObservedIdentity.ProcessId, ProcessIdFor(runtime.Executor.SnapshotForTests(), role));
            Assert.Equal(generation, boundary.ObservedIdentity.Generation);
            Assert.Equal(1, boundary.CountRoleLaunches(role));
            Assert.Equal(1, boundary.CountLaunches(generation));
            Assert.Equal(1, boundary.CountLiveFixtureProcesses(generation));

            boundary.Release();
            await startup.WaitAsync(TimeSpan.FromMinutes(2));

            Assert.Equal(RuntimeState.Ready, runtime.Orchestrator.State);
            Assert.Equal(generation, GenerationFor(runtime.Executor.SnapshotForTests(), role));
            Assert.Equal(1, boundary.CountRoleLaunches(role));
            Assert.Equal(1, boundary.CountLaunches(generation));
            Assert.Equal(1, boundary.CountLiveFixtureProcesses(generation));
        }
        finally
        {
            boundary.Release();
            await runtime.Orchestrator.DisposeAsync();
        }
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task CallerCancellationAfterWorkerIdentityRetention_ReconcilesTheRetainedAccept()
    {
        var boundary = new BlockingRoleOperationBoundary(
            RuntimeRole.Worker,
            RoleOperationBoundaryPoint.StartAcceptInFlight);
        await using var harness = RoleExecutorHarness.Create(
            boundary,
            new RoleOperationDeadlines(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30)));
        using var cancellation = new CancellationTokenSource();
        try
        {
            var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());
            var command = new LifecycleCommand(
                LifecycleCommandType.StartWorker,
                generation,
                generation.OperationId,
                LifecycleDeadlineKey.WorkerStart);
            var startup = harness.Executor.ExecuteAsync(command, cancellation.Token);
            await WaitUntilBlockedOrPropagateStartupAsync(boundary, startup);
            Assert.True(boundary.ObservedOperationWasPending);

            cancellation.Cancel();
            var timedOut = Assert.IsType<OperationTimedOut>(await startup);

            Assert.Equal(generation, timedOut.Value);
            Assert.Equal(generation.OperationId, timedOut.OperationId);
            Assert.Equal(generation, GenerationFor(harness.Executor.SnapshotForTests(), RuntimeRole.Worker));
            Assert.Equal(1, boundary.CountLaunches(generation));
            Assert.Equal(1, boundary.CountLiveFixtureProcesses(generation));

            boundary.Release();
            var reconciled = await harness.Executor.ExecuteAsync(
                new LifecycleCommand(
                    LifecycleCommandType.ReconcileRoleStart,
                    generation,
                    generation.OperationId,
                    LifecycleDeadlineKey.RoleReconciliation));

            Assert.Equal(ReconciledState.Running, Assert.IsType<Reconciled>(reconciled).State);
            Assert.Equal(generation, GenerationFor(harness.Executor.SnapshotForTests(), RuntimeRole.Worker));
            Assert.Equal(1, boundary.CountLaunches(generation));
        }
        finally
        {
            boundary.Release();
        }
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task ExitedLateWorkerStart_ReconcilesStoppedWithoutWaitingForBlockedAccept()
    {
        var boundary = new BlockingRoleOperationBoundary(
            RuntimeRole.Worker,
            RoleOperationBoundaryPoint.StartAcceptInFlight);
        await using var harness = RoleExecutorHarness.Create(
            boundary,
            new RoleOperationDeadlines(
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5)));
        try
        {
            var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());
            var command = new LifecycleCommand(
                LifecycleCommandType.StartWorker,
                generation,
                generation.OperationId,
                LifecycleDeadlineKey.WorkerStart);
            var startup = harness.Executor.ExecuteAsync(command);
            await WaitUntilBlockedOrPropagateStartupAsync(boundary, startup);
            Assert.IsType<OperationTimedOut>(await startup);

            boundary.Terminate(generation);
            await boundary.WaitUntilExitedAsync(generation, TimeSpan.FromSeconds(10));
            var reconciliation = harness.Executor.ExecuteAsync(
                new LifecycleCommand(
                    LifecycleCommandType.ReconcileRoleStart,
                    generation,
                    generation.OperationId,
                    LifecycleDeadlineKey.RoleReconciliation));
            var completed = await reconciliation.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(ReconciledState.Stopped, Assert.IsType<Reconciled>(completed).State);
            Assert.Null(GenerationFor(harness.Executor.SnapshotForTests(), RuntimeRole.Worker));
            Assert.Equal(1, boundary.CountLaunches(generation));
            Assert.Equal(0, boundary.CountLiveFixtureProcesses(generation));
        }
        finally
        {
            boundary.Release();
        }
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task FaultedAcceptWithLiveWorker_ReconcilesStoppedAndDisposesExactResource()
    {
        var boundary = new BlockingRoleOperationBoundary(
            RuntimeRole.Worker,
            RoleOperationBoundaryPoint.StopAccepted);
        await using var harness = RoleExecutorHarness.Create(
            boundary,
            new RoleOperationDeadlines(
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(20)));
        harness.FixtureRoleLaunchPlanProvider.WorkerModeForTests = "pipe-timeout";
        var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());
        var start = new LifecycleCommand(
            LifecycleCommandType.StartWorker,
            generation,
            generation.OperationId,
            LifecycleDeadlineKey.WorkerStart);

        Assert.IsType<OperationTimedOut>(await harness.Executor.ExecuteAsync(start));
        Assert.Equal(1, boundary.CountLiveFixtureProcesses(generation));

        var reconcile = new LifecycleCommand(
            LifecycleCommandType.ReconcileRoleStart,
            generation,
            generation.OperationId,
            LifecycleDeadlineKey.RoleReconciliation);
        var completed = await harness.Executor.ExecuteAsync(reconcile)
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(ReconciledState.Stopped, Assert.IsType<Reconciled>(completed).State);
        Assert.Null(GenerationFor(harness.Executor.SnapshotForTests(), RuntimeRole.Worker));
        Assert.Equal(0, boundary.CountLiveFixtureProcesses(generation));

        var duplicate = await harness.Executor.ExecuteAsync(reconcile);
        Assert.Equal(ReconciledState.Stopped, Assert.IsType<Reconciled>(duplicate).State);
        Assert.Equal(0, boundary.CountLiveFixtureProcesses(generation));
    }

    [PostgresFact, Trait("Category", "Acceptance")]
    public async Task LateStart_IsSingleFlightUntilAuthenticatedReconciliation()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync(
            startDeadline: TimeSpan.FromMilliseconds(1),
            reconciliationDeadline: TimeSpan.FromSeconds(30));

        var started = await cluster.Runtime.StartAsync();
        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate, started.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cluster.Runtime.StartAsync());
        await PostgresTestCluster.WaitForFileAsync(cluster.Paths.PostmasterPidFile, TimeSpan.FromSeconds(30));

        var reconciled = await cluster.Runtime.ReconcileStartAsync();
        cluster.Identity = Assert.IsType<PostgresInstanceIdentity>(reconciled.Identity);

        Assert.Equal(PostgreSqlOperationOutcome.ReconciledRunning, reconciled.Outcome);
        Assert.Equal("1", (await cluster.Runtime.ProbeAsync(cluster.Identity)).SelectOne);
    }

    [PostgresFact, Trait("Category", "Acceptance")]
    public async Task LateStop_IsSingleFlightUntilRetainedIdentitySignals()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync(
            stopDeadline: TimeSpan.FromMilliseconds(1),
            reconciliationDeadline: TimeSpan.FromSeconds(30));
        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;

        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate, await cluster.Runtime.StopFastAsync(identity));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cluster.Runtime.StopFastAsync(identity));
        await identity.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped, await cluster.Runtime.ReconcileStopAsync(identity));
    }

    [PostgresFact, Trait("Category", "Acceptance")]
    public async Task RealWorkerRestartBudget_FaultsOnceCleansUpAndDoesNotLoop()
    {
        using var layout = TestLayout.CreateReal("ebaycrm-task10-restart-budget");
        var runtime = AppHostComposition.CreateForTests(
            AppHostOptions.Parse(layout.Arguments("run")));
        try
        {
            await runtime.Orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            for (var expectedReadyCount = 2; expectedReadyCount <= 4; expectedReadyCount++)
            {
                runtime.Executor.CrashRoleForTests(RuntimeRole.Worker);
                await WaitForStateCountAsync(
                    runtime.Orchestrator,
                    RuntimeState.Ready,
                    expectedReadyCount,
                    TimeSpan.FromSeconds(30));
            }

            var beforeExhaustion = runtime.Executor.SnapshotForTests();
            runtime.Executor.CrashRoleForTests(RuntimeRole.Worker);
            await WaitForStateCountAsync(
                runtime.Orchestrator,
                RuntimeState.Faulted,
                1,
                TimeSpan.FromSeconds(30));
            await WaitForOwnershipReleaseAsync(layout.ProfileRoot, TimeSpan.FromSeconds(10));
            var historyCount = runtime.Orchestrator.StateHistory.Count;

            await Task.Delay(500);

            Assert.Equal(RuntimeState.Faulted, runtime.Orchestrator.State);
            Assert.Equal(4, runtime.Orchestrator.StateHistory.Count(state => state == RuntimeState.Ready));
            Assert.Equal(1, runtime.Orchestrator.StateHistory.Count(state => state == RuntimeState.Faulted));
            Assert.Equal(historyCount, runtime.Orchestrator.StateHistory.Count);
            Assert.True(File.Exists(Path.Combine(layout.ProfileRoot, "runtime", "apphost-fault-v1.json")));
            Assert.False(File.Exists(Path.Combine(layout.ProfileRoot, "postgres-data", "postmaster.pid")));
            AssertProcessExited(beforeExhaustion.DatabaseProcessId);
            AssertProcessExited(beforeExhaustion.ServerProcessId);
            AssertProcessExited(beforeExhaustion.WorkerProcessId);
        }
        finally
        {
            await runtime.Orchestrator.DisposeAsync();
        }
    }

    private static async Task WaitForStateCountAsync(
        RuntimeOrchestrator orchestrator,
        RuntimeState state,
        int expectedCount,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (orchestrator.StateHistory.Count(value => value == state) >= expectedCount) return;
            await Task.Delay(25);
        }

        Assert.Fail($"State {state} count {expectedCount} was not reached. History={string.Join(',', orchestrator.StateHistory)}");
    }

    private static async Task WaitUntilBlockedOrPropagateStartupAsync(
        BlockingRoleOperationBoundary boundary,
        Task<LifecycleEvent?> startup)
    {
        var blocked = boundary.WaitUntilBlockedAsync(TimeSpan.FromSeconds(30));
        if (await Task.WhenAny(blocked, startup) == startup)
        {
            _ = await startup;
            Assert.Fail("Role startup completed before the accept boundary blocked.");
        }

        await blocked;
    }

    internal static async Task WaitForStateAsync(
        RuntimeOrchestrator orchestrator,
        RuntimeState state,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (orchestrator.State == state) return;
            await Task.Delay(10);
        }

        Assert.Fail($"State {state} was not reached. History={string.Join(',', orchestrator.StateHistory)}");
    }

    internal static ProcessGeneration? GenerationFor(AppHostRuntimeSnapshot snapshot, RuntimeRole role) => role switch
    {
        RuntimeRole.Server => snapshot.ServerGeneration,
        RuntimeRole.Worker => snapshot.WorkerGeneration,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    internal static int? ProcessIdFor(AppHostRuntimeSnapshot snapshot, RuntimeRole role) => role switch
    {
        RuntimeRole.Server => snapshot.ServerProcessId,
        RuntimeRole.Worker => snapshot.WorkerProcessId,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static async Task WaitForOwnershipReleaseAsync(string profileRoot, TimeSpan timeout)
    {
        var profile = DataProfileIdentity.Create(profileRoot);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await UserProfileInstanceLock.TryAcquireAsync(profile, CancellationToken.None) is { } acquired)
            {
                await acquired.DisposeAsync();
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Profile ownership was not released after restart-budget exhaustion.");
    }

    private static void AssertProcessExited(int? processId)
    {
        Assert.NotNull(processId);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(processId.Value));
    }
}

internal sealed class RoleExecutorHarness : IAsyncDisposable
{
    private readonly string _profileRoot;

    private RoleExecutorHarness(
        string profileRoot,
        LifecycleCommandExecutor executor,
        FixtureRoleLaunchPlanProvider fixtureRoleLaunchPlanProvider)
    {
        _profileRoot = profileRoot;
        Executor = executor;
        FixtureRoleLaunchPlanProvider = fixtureRoleLaunchPlanProvider;
    }

    internal LifecycleCommandExecutor Executor { get; }

    internal FixtureRoleLaunchPlanProvider FixtureRoleLaunchPlanProvider { get; }

    internal static RoleExecutorHarness Create(
        IRoleOperationBoundary boundary,
        RoleOperationDeadlines deadlines)
    {
        var profileRoot = Path.Combine(
            Path.GetTempPath(),
            $"ebaycrm-phase1b-role-executor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileRoot);
        var postgresBin = Path.Combine(profileRoot, "unused-postgres-bin");
        Directory.CreateDirectory(postgresBin);
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "HowardLab.EbayCrm.AppHost.Fixture.exe");
        var options = new AppHostOptions(
            profileRoot,
            postgresBin,
            fixturePath,
            Port: 15432,
            AppHostMode.Run);
        var postgresLayout = new PostgresBinaryLayout(
            postgresBin,
            Path.Combine(postgresBin, "initdb.exe"),
            Path.Combine(postgresBin, "pg_ctl.exe"),
            Path.Combine(postgresBin, "postgres.exe"),
            Path.Combine(postgresBin, "psql.exe"),
            Path.Combine(postgresBin, "pg_isready.exe"));
        var payload = new ValidatedAppHostPayload(
            DataProfileIdentity.Create(profileRoot),
            postgresLayout,
            PostgresClusterPaths.Create(profileRoot),
            Path.Combine(profileRoot, "unused-migration.sql"),
            ControlProtocolConstants.FixtureBuildIdentity);
        var secrets = new DiagnosticSecretRegistry();
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(Stream.Null),
            new SystemClock(),
            secrets);
        var roleLaunchPlanProvider = new FixtureRoleLaunchPlanProvider(options, payload);
        var executor = new LifecycleCommandExecutor(
            options,
            payload,
            identityStore: null,
            boundary,
            deadlines,
            new OwnershipGatedDiagnosticSink(sink, TimeSpan.FromSeconds(1)),
            secrets,
            diagnosticSecretObserver: null,
            roleLaunchPlanProvider);
        var harness = new RoleExecutorHarness(profileRoot, executor, roleLaunchPlanProvider);
        executor.RoleLaunchedForTests = boundary is BlockingRoleOperationBoundary blocking
            ? blocking.RecordLaunched
            : null;
        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        await Executor.DisposeAsync();
        if (Directory.Exists(_profileRoot))
        {
            Directory.Delete(_profileRoot, recursive: true);
        }
    }
}

internal sealed class ExhaustedRoleStartReconciliationExecutor : ILifecycleCommandExecutor
{
    internal List<LifecycleCommandType> Commands { get; } = [];

    internal int RollbackCount { get; private set; }

    public Task<LifecycleEvent?> ExecuteAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Add(command.Type);
        LifecycleEvent? result = command.Type switch
        {
            LifecycleCommandType.AcquireInstance => new InstanceAcquired(command.OperationId),
            LifecycleCommandType.ValidatePayload => new PayloadValidated(command.OperationId),
            LifecycleCommandType.PrepareRuntime => new RuntimePrepared(command.OperationId),
            LifecycleCommandType.StartDatabase => new RoleStarted(command.Generation!.Value),
            LifecycleCommandType.WaitForDatabase => new RoleReady(command.Generation!.Value),
            LifecycleCommandType.RunMigrations => new MigrationCompleted(command.OperationId),
            LifecycleCommandType.StartServer => new OperationTimedOut(command.Generation!.Value, command.OperationId),
            LifecycleCommandType.ReconcileRoleStart => new OperationTimedOut(command.Generation!.Value, command.OperationId),
            _ => null,
        };
        return Task.FromResult(result);
    }

    public Task RollbackAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        RollbackCount++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class BlockingRoleOperationBoundary(
    RuntimeRole blockedRole,
    RoleOperationBoundaryPoint blockedPoint) : IRoleOperationBoundary
{
    private readonly object _gate = new();
    private readonly List<SupervisedProcessIdentity> _launches = [];
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pauseStarted;

    internal ProcessGeneration ObservedGeneration { get; private set; }

    internal SupervisedProcessIdentity ObservedIdentity { get; private set; } = null!;

    internal bool ObservedOperationWasPending { get; private set; }

    internal void RecordLaunched(
        RuntimeRole role,
        WindowsJobObject job,
        WindowsSupervisedProcess process)
    {
        lock (_gate)
        {
            _launches.Add(process.Identity);
        }
    }

    public async ValueTask PauseAsync(
        RoleOperationBoundaryPoint point,
        ProcessGeneration generation,
        Guid operationId,
        Task pendingOperation,
        CancellationToken roleLifetimeToken)
    {
        if (point != blockedPoint || generation.Role != blockedRole ||
            Interlocked.Exchange(ref _pauseStarted, 1) != 0)
        {
            roleLifetimeToken.ThrowIfCancellationRequested();
            return;
        }

        lock (_gate)
        {
            ObservedGeneration = generation;
            ObservedIdentity = _launches.Single(identity => identity.Generation == generation);
            ObservedOperationWasPending = !pendingOperation.IsCompleted;
        }
        _blocked.TrySetResult();
        await _release.Task.WaitAsync(roleLifetimeToken);
    }

    internal Task WaitUntilBlockedAsync(TimeSpan timeout) => _blocked.Task.WaitAsync(timeout);

    internal void Release() => _release.TrySetResult();

    internal void Terminate(ProcessGeneration generation)
    {
        SupervisedProcessIdentity identity;
        lock (_gate)
        {
            identity = _launches.Single(value => value.Generation == generation);
        }

        using var process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
        if (process.StartTime.ToUniversalTime() != identity.CreationTimeUtc.UtcDateTime)
        {
            throw new InvalidOperationException("The retained fixture process identity changed before termination.");
        }

        process.Kill(entireProcessTree: true);
    }

    internal async Task WaitUntilExitedAsync(ProcessGeneration generation, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CountLiveFixtureProcesses(generation) == 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Fixture generation {generation} did not exit.");
    }

    internal int CountLaunches(ProcessGeneration generation)
    {
        lock (_gate)
        {
            return _launches.Count(identity => identity.Generation == generation);
        }
    }

    internal int CountRoleLaunches(RuntimeRole role)
    {
        lock (_gate)
        {
            return _launches.Count(identity => identity.Role == role);
        }
    }

    internal int CountLiveFixtureProcesses(ProcessGeneration generation)
    {
        SupervisedProcessIdentity[] identities;
        lock (_gate)
        {
            identities = _launches.Where(identity => identity.Generation == generation).ToArray();
        }

        return identities.Count(IsLiveIdentity);
    }

    internal IReadOnlyList<SupervisedProcessIdentity> Launches()
    {
        lock (_gate)
        {
            return _launches.ToArray();
        }
    }

    internal static bool IsLiveIdentity(SupervisedProcessIdentity identity)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
            return process.StartTime.ToUniversalTime() == identity.CreationTimeUtc.UtcDateTime && !process.HasExited;
        }
        catch (Exception error) when (
            error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
