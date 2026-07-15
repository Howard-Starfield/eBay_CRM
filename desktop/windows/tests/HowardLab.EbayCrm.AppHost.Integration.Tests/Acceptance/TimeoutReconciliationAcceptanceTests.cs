using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

public sealed class TimeoutReconciliationAcceptanceTests
{
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
