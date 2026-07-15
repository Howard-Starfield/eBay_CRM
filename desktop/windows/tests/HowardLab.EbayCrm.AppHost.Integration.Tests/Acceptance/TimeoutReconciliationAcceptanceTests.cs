using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Processes;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

public sealed class TimeoutReconciliationAcceptanceTests
{
    [PostgresTheory, Trait("Category", "Acceptance")]
    [InlineData(RuntimeRole.Server)]
    [InlineData(RuntimeRole.Worker)]
    public async Task LateFixtureStart_ReconcilesTheRetainedProcessIdentity(RuntimeRole role)
    {
        using var layout = TestLayout.CreateReal("ebaycrm-task2-late-start");
        var boundary = new BlockingRoleOperationBoundary(role, RoleOperationBoundaryPoint.StartIdentityRetained);
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
        }
        _blocked.TrySetResult();
        await _release.Task.WaitAsync(roleLifetimeToken);
    }

    internal Task WaitUntilBlockedAsync(TimeSpan timeout) => _blocked.Task.WaitAsync(timeout);

    internal void Release() => _release.TrySetResult();

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
