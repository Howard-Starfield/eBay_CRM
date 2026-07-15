using System.Net;
using System.Net.Sockets;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Fixture;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Core.Time;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

public sealed class AppHostRecoveryTests
{
    [Fact, Trait("Category", "AppHost")]
    public async Task BackgroundRecoveryFailure_FaultsOnceCleansUpAndWakesRunLoop()
    {
        var executor = new RecoveryFailingExecutor();
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor);
        using var stopping = new CancellationTokenSource();
        var running = orchestrator.RunUntilStoppedAsync(stopping.Token);
        await executor.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await orchestrator.HandleEventAsync(new RoleExited(executor.WorkerGeneration, 91));
        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() => running);

        Assert.Equal("background-recovery-failed", error.ReasonCode);
        Assert.Equal(RuntimeState.Faulted, orchestrator.State);
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.EnterFault));
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.EscalateJob));
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.ReleaseInstance));
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task FaultDiagnosticFailure_DoesNotSkipContainmentOrOwnershipRelease()
    {
        var executor = new RecoveryFailingExecutor
        {
            ThrowOnCommand = LifecycleCommandType.EnterFault,
        };
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor);
        using var stopping = new CancellationTokenSource();
        var running = orchestrator.RunUntilStoppedAsync(stopping.Token);
        await executor.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await orchestrator.HandleEventAsync(new RoleExited(executor.WorkerGeneration, 91));
        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("background-recovery-failed", error.ReasonCode);
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.EnterFault));
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.EscalateJob));
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.ReleaseInstance));
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task NormalRestartBudgetFault_EscalatesReleasesAndWakesRunLoop()
    {
        var executor = new RecoveryFailingExecutor { FailRecoveryStart = false };
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor);
        using var stopping = new CancellationTokenSource();
        var running = orchestrator.RunUntilStoppedAsync(stopping.Token);
        await executor.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await orchestrator.HandleEventAsync(new RoleExited(executor.WorkerGeneration, 91));
        }

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("background-recovery-failed", error.ReasonCode);
        Assert.Equal(RuntimeState.Faulted, orchestrator.State);
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.EnterFault));
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.EscalateJob));
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.ReleaseInstance));
    }

    [PostgresTheory, Trait("Category", "AppHost")]
    [InlineData("control-disconnect")]
    [InlineData("health-drop")]
    [InlineData("health-stale-build")]
    [InlineData("health-stale-protocol")]
    [InlineData("health-stale-generation")]
    [InlineData("health-stale-nonce")]
    [InlineData("health-unhealthy")]
    public async Task LiveWorkerSignalFailure_IsGenerationFencedIntoWorkerRecovery(string fixtureMode)
    {
        var profile = Path.Combine(Path.GetTempPath(), $"ebaycrm-task9-disconnect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var runtime = AppHostComposition.CreateForTests(CreateOptions(profile));
        runtime.Executor.WorkerFixtureModeForTests = fixtureMode;
        try
        {
            await runtime.Orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            var disconnected = runtime.Executor.SnapshotForTests();

            await WaitForReadyCountAsync(runtime, 2, TimeSpan.FromSeconds(30));
            var recovered = runtime.Executor.SnapshotForTests();

            Assert.Equal(disconnected.DatabaseProcessId, recovered.DatabaseProcessId);
            Assert.Equal(disconnected.ServerProcessId, recovered.ServerProcessId);
            Assert.NotEqual(disconnected.WorkerProcessId, recovered.WorkerProcessId);
        }
        finally
        {
            await runtime.Orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            await runtime.Orchestrator.DisposeAsync();
            Directory.Delete(profile, recursive: true);
        }
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task HealthyRuntime_RemainsReadyBeyondTwoControlOperationDeadlines()
    {
        var profile = Path.Combine(Path.GetTempPath(), $"ebaycrm-task9-steady-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var runtime = AppHostComposition.CreateForTests(CreateOptions(profile));
        try
        {
            await runtime.Orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            var initial = runtime.Executor.SnapshotForTests();

            await Task.Delay(TimeSpan.FromSeconds(21));

            Assert.Equal(RuntimeState.Ready, runtime.Orchestrator.State);
            Assert.Equal(1, runtime.Orchestrator.StateHistory.Count(state => state == RuntimeState.Ready));
            Assert.Equal(initial, runtime.Executor.SnapshotForTests());
        }
        finally
        {
            await runtime.Orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            await runtime.Orchestrator.DisposeAsync();
            Directory.Delete(profile, recursive: true);
        }
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task DependencyInspection_PropagatesCallerCancellationBeforeRecoveryDispatch()
    {
        var profile = Path.Combine(Path.GetTempPath(), $"ebaycrm-task9-cancel-inspection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var runtime = AppHostComposition.CreateForTests(CreateOptions(profile));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runtime.Executor.InspectDependencyFailureAsync(
                new RoleExited(new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid()), 1),
                cancellation.Token));

        await runtime.Orchestrator.DisposeAsync();
        Directory.Delete(profile, recursive: true);
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task SimultaneousFailures_CoalesceWithoutDuplicateReadyTransitions()
    {
        var profile = Path.Combine(Path.GetTempPath(), $"ebaycrm-task9-simultaneous-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var options = CreateOptions(profile);
        var runtime = AppHostComposition.CreateForTests(options);
        try
        {
            await runtime.Orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            var initial = runtime.Executor.SnapshotForTests();

            await runtime.Executor.CrashAllRolesSimultaneouslyForTestsAsync();
            await WaitForReadyCountAsync(runtime, 2, TimeSpan.FromSeconds(60));

            var recovered = runtime.Executor.SnapshotForTests();
            Assert.Equal(2, runtime.Orchestrator.StateHistory.Count(state => state == RuntimeState.Ready));
            Assert.NotEqual(initial.DatabaseProcessId, recovered.DatabaseProcessId);
            Assert.NotEqual(initial.ServerProcessId, recovered.ServerProcessId);
            Assert.NotEqual(initial.WorkerProcessId, recovered.WorkerProcessId);
        }
        finally
        {
            if (runtime.Orchestrator.State != RuntimeState.Stopped)
            {
                await runtime.Orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            }

            await runtime.Orchestrator.DisposeAsync();
            Directory.Delete(profile, recursive: true);
        }
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task RetainedProcessFailures_RestartOnlyTheirDependencyClosure()
    {
        var profile = Path.Combine(Path.GetTempPath(), $"ebaycrm-task9-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var options = CreateOptions(profile);
        var runtime = AppHostComposition.CreateForTests(options);
        try
        {
            await runtime.Orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            var initial = runtime.Executor.SnapshotForTests();

            runtime.Executor.CrashRoleForTests(RuntimeRole.Worker);
            await WaitForReadyCountAsync(runtime, 2, TimeSpan.FromSeconds(30));
            var workerRecovered = runtime.Executor.SnapshotForTests();
            Assert.Equal(initial.DatabaseProcessId, workerRecovered.DatabaseProcessId);
            Assert.Equal(initial.ServerProcessId, workerRecovered.ServerProcessId);
            Assert.NotEqual(initial.WorkerProcessId, workerRecovered.WorkerProcessId);

            runtime.Executor.CrashRoleForTests(RuntimeRole.Server);
            await WaitForReadyCountAsync(runtime, 3, TimeSpan.FromSeconds(30));
            var serverRecovered = runtime.Executor.SnapshotForTests();
            Assert.Equal(workerRecovered.DatabaseProcessId, serverRecovered.DatabaseProcessId);
            Assert.NotEqual(workerRecovered.ServerProcessId, serverRecovered.ServerProcessId);
            Assert.NotEqual(workerRecovered.WorkerProcessId, serverRecovered.WorkerProcessId);

            runtime.Executor.CrashRoleForTests(RuntimeRole.Database);
            await WaitForReadyCountAsync(runtime, 4, TimeSpan.FromSeconds(60));
            var databaseRecovered = runtime.Executor.SnapshotForTests();
            Assert.NotEqual(serverRecovered.DatabaseProcessId, databaseRecovered.DatabaseProcessId);
            Assert.NotEqual(serverRecovered.ServerProcessId, databaseRecovered.ServerProcessId);
            Assert.NotEqual(serverRecovered.WorkerProcessId, databaseRecovered.WorkerProcessId);

            runtime.Executor.CrashRoleForTests(RuntimeRole.Database);
            await WaitForReadyCountAsync(runtime, 5, TimeSpan.FromSeconds(60));
            var databaseRecoveredAgain = runtime.Executor.SnapshotForTests();
            Assert.NotEqual(databaseRecovered.DatabaseGeneration, databaseRecoveredAgain.DatabaseGeneration);
            Assert.NotEqual(databaseRecovered.DatabaseProcessId, databaseRecoveredAgain.DatabaseProcessId);
            Assert.NotEqual(databaseRecovered.ServerProcessId, databaseRecoveredAgain.ServerProcessId);
            Assert.NotEqual(databaseRecovered.WorkerProcessId, databaseRecoveredAgain.WorkerProcessId);
        }
        finally
        {
            if (runtime.Orchestrator.State != RuntimeState.Stopped)
            {
                await runtime.Orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            }

            await runtime.Orchestrator.DisposeAsync();
            Directory.Delete(profile, recursive: true);
        }
    }

    private static AppHostOptions CreateOptions(string profile) => new(
        profile,
        Environment.GetEnvironmentVariable("EBAYCRM_POSTGRES_BIN")!,
        Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe"),
        ReserveLoopbackPort(),
        AppHostMode.Run);

    private static async Task WaitForReadyCountAsync(
        AppHostTestRuntime runtime,
        int expectedCount,
        TimeSpan timeout)
    {
        var orchestrator = runtime.Orchestrator;
        if (orchestrator.StateHistory.Count(state => state == RuntimeState.Ready) >= expectedCount)
        {
            return;
        }

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(RuntimeState state)
        {
            if (state == RuntimeState.Ready &&
                orchestrator.StateHistory.Count(value => value == RuntimeState.Ready) >= expectedCount)
            {
                reached.TrySetResult();
            }
        }

        orchestrator.StateChanged += OnStateChanged;
        try
        {
            if (orchestrator.StateHistory.Count(state => state == RuntimeState.Ready) >= expectedCount)
            {
                return;
            }

            try
            {
                await reached.Task.WaitAsync(timeout);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException(
                    $"Ready count {expectedCount} not reached. State={orchestrator.State}; " +
                    $"History={string.Join(',', orchestrator.StateHistory)}; " +
                    $"RecoveryFailure={runtime.Orchestrator.LastFatalExceptionForTests}",
                    error);
            }
        }
        finally
        {
            orchestrator.StateChanged -= OnStateChanged;
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class RecoveryFailingExecutor : ILifecycleCommandExecutor
    {
        private bool _startupComplete;

        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<LifecycleCommandType> Commands { get; } = [];
        internal ProcessGeneration WorkerGeneration { get; private set; }
        internal LifecycleCommandType? ThrowOnCommand { get; init; }
        internal bool FailRecoveryStart { get; init; } = true;

        public Task<LifecycleEvent?> ExecuteAsync(
            LifecycleCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.Type);
            if (command.Type == ThrowOnCommand)
            {
                throw new InvalidOperationException("injected-cleanup-failure");
            }

            if (_startupComplete && FailRecoveryStart && command.Type == LifecycleCommandType.StartWorker)
            {
                throw new InvalidOperationException("injected-recovery-start-failure");
            }

            LifecycleEvent? result = command.Type switch
            {
                LifecycleCommandType.AcquireInstance => new InstanceAcquired(command.OperationId),
                LifecycleCommandType.ValidatePayload => new PayloadValidated(command.OperationId),
                LifecycleCommandType.PrepareRuntime => new RuntimePrepared(command.OperationId),
                LifecycleCommandType.StartDatabase or LifecycleCommandType.StartServer or LifecycleCommandType.StartWorker =>
                    new RoleStarted(command.Generation!.Value),
                LifecycleCommandType.WaitForDatabase or LifecycleCommandType.WaitForServer =>
                    new RoleReady(command.Generation!.Value),
                LifecycleCommandType.WaitForWorker => CompleteStartup(command.Generation!.Value),
                LifecycleCommandType.RunMigrations => new MigrationCompleted(command.OperationId),
                _ => null,
            };
            return Task.FromResult(result);
        }

        private LifecycleEvent CompleteStartup(ProcessGeneration generation)
        {
            WorkerGeneration = generation;
            _startupComplete = true;
            Ready.TrySetResult();
            return new RoleReady(generation);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
