using System.Diagnostics;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Time;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using System.Net;
using System.Net.Sockets;
using HowardLab.EbayCrm.AppHost.Fixture;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

public sealed class AppHostShutdownTests
{
    [PostgresFact, Trait("Category", "AppHost")]
    public async Task IgnoredWorkerShutdown_UsesTestBudgetAndLeavesNoRuntimeOrOwnershipLeak()
    {
        var profile = Path.Combine(Path.GetTempPath(), $"ebaycrm-task9-ignore-shutdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var options = new AppHostOptions(
            profile,
            Environment.GetEnvironmentVariable("EBAYCRM_POSTGRES_BIN")!,
            Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe"),
            ReserveLoopbackPort(),
            AppHostMode.Run);
        var budget = new ShutdownBudget(
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(60),
            TimeSpan.FromMilliseconds(120));
        var runtime = AppHostComposition.CreateForTests(options, budget);
        runtime.Executor.WorkerFixtureModeForTests = "ignore-shutdown";
        try
        {
            await runtime.Orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            var started = runtime.Executor.SnapshotForTests();
            var stopwatch = Stopwatch.StartNew();

            await runtime.Orchestrator.StopAsync();

            stopwatch.Stop();
            Assert.Equal(RuntimeState.Faulted, runtime.Orchestrator.State);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(280), TimeSpan.FromSeconds(2));
            Assert.False(File.Exists(Path.Combine(profile, "postgres-data", "postmaster.pid")));
            var fault = await File.ReadAllTextAsync(Path.Combine(profile, "runtime", "apphost-fault-v1.json"));
            Assert.Contains("shutdown-budget-exhausted", fault, StringComparison.Ordinal);
            AssertProcessExited(started.ServerProcessId);
            AssertProcessExited(started.WorkerProcessId);
        }
        finally
        {
            await runtime.Orchestrator.DisposeAsync();
            Directory.Delete(profile, recursive: true);
        }
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task RemotelyHeldWorkerJobHandle_UsesRealEscalationWithinTheSharedBudget()
    {
        var profile = Path.Combine(Path.GetTempPath(), $"ebaycrm-task9-held-job-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        var runtime = AppHostComposition.CreateForTests(
            new AppHostOptions(
                profile,
                Environment.GetEnvironmentVariable("EBAYCRM_POSTGRES_BIN")!,
                Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe"),
                ReserveLoopbackPort(),
                AppHostMode.Run),
            new ShutdownBudget(
                TimeSpan.FromMilliseconds(300),
                TimeSpan.FromMilliseconds(120),
                TimeSpan.FromMilliseconds(60),
                TimeSpan.FromMilliseconds(120)));
        runtime.Executor.WorkerFixtureModeForTests = "ignore-shutdown";
        runtime.Executor.RoleLaunchedForTests =
            LifecycleCommandExecutor.RetainWorkerJobHandleInRoleForTests;
        try
        {
            await runtime.Orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            var started = runtime.Executor.SnapshotForTests();
            var stopwatch = Stopwatch.StartNew();

            await runtime.Orchestrator.StopAsync();

            stopwatch.Stop();
            Assert.Equal(RuntimeState.Faulted, runtime.Orchestrator.State);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(280), TimeSpan.FromSeconds(2));
            AssertProcessExited(started.ServerProcessId);
            AssertProcessExited(started.WorkerProcessId);
            Assert.False(File.Exists(Path.Combine(profile, "postgres-data", "postmaster.pid")));
        }
        finally
        {
            await runtime.Orchestrator.DisposeAsync();
            Directory.Delete(profile, recursive: true);
        }
    }

    private static void AssertProcessExited(int? processId)
    {
        Assert.NotNull(processId);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId.Value));
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task Stop_UsesOneMonotonicBudgetThenEscalatesExactlyOnceAndStaysFaulted()
    {
        var executor = new HangingShutdownExecutor();
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        var budget = new ShutdownBudget(
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(40));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor, budget);
        await orchestrator.StartAsync();

        var stopwatch = Stopwatch.StartNew();
        await orchestrator.StopAsync();
        stopwatch.Stop();

        Assert.Equal(RuntimeState.Faulted, orchestrator.State);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(110), TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.EscalateJob));
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.EnterFault));
        Assert.Equal(LifecycleCommandType.ReleaseInstance, executor.Commands[^1]);
        Assert.DoesNotContain(LifecycleCommandType.StartWorker, executor.Commands[executor.StartupCommandCount..]);

        var commandCount = executor.Commands.Count;
        await orchestrator.StopAsync();
        Assert.Equal(commandCount, executor.Commands.Count);
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task ShutdownDiagnosticFailure_StillReleasesProfileOwnership()
    {
        var executor = new HangingShutdownExecutor { ThrowOnEnterFault = true };
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        var budget = new ShutdownBudget(
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(40));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor, budget);
        await orchestrator.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.StopAsync());

        Assert.Equal(LifecycleCommandType.ReleaseInstance, executor.Commands[^1]);
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.ReleaseInstance));
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task DatabaseStopTimeout_DispatchesReconciliationAndUnknownResultFaults()
    {
        var executor = new HangingShutdownExecutor { DatabaseStopTimesOut = true };
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        var budget = new ShutdownBudget(
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(40));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor, budget);
        await orchestrator.StartAsync();

        await orchestrator.StopAsync();

        Assert.Equal(RuntimeState.Faulted, orchestrator.State);
        Assert.Contains(LifecycleCommandType.ReconcileDatabaseStop, executor.Commands);
        Assert.Equal(1, executor.Commands.Count(type => type == LifecycleCommandType.EscalateJob));
        Assert.Equal(LifecycleCommandType.ReleaseInstance, executor.Commands[^1]);
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task BlockedPostEscalationCleanup_IsBoundedAndStillReleasesOwnership()
    {
        var executor = new HangingShutdownExecutor { HangOnEscalation = true };
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        var budget = new ShutdownBudget(
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(40));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor, budget);
        await orchestrator.StartAsync();
        executor.HangOnDrain = true;
        var stopwatch = Stopwatch.StartNew();

        await orchestrator.StopAsync();

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5));
        Assert.Equal(RuntimeState.Faulted, orchestrator.State);
        Assert.Equal(LifecycleCommandType.ReleaseInstance, executor.Commands[^1]);
    }

    private sealed class HangingShutdownExecutor : ILifecycleCommandExecutor
    {
        internal List<LifecycleCommandType> Commands { get; } = [];
        internal int StartupCommandCount { get; private set; }
        internal bool ThrowOnEnterFault { get; init; }
        internal bool DatabaseStopTimesOut { get; init; }
        internal bool HangOnEscalation { get; init; }
        internal bool HangOnDrain { get; set; }

        public async Task<LifecycleEvent?> ExecuteAsync(
            LifecycleCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.Type);
            if (command.Type == LifecycleCommandType.EnterFault && ThrowOnEnterFault)
            {
                throw new InvalidOperationException("injected-fault-diagnostic-failure");
            }

            if (command.Type == LifecycleCommandType.EscalateJob && HangOnEscalation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            LifecycleEvent? result = command.Type switch
            {
                LifecycleCommandType.AcquireInstance => new InstanceAcquired(command.OperationId),
                LifecycleCommandType.ValidatePayload => new PayloadValidated(command.OperationId),
                LifecycleCommandType.PrepareRuntime => new RuntimePrepared(command.OperationId),
                LifecycleCommandType.StartDatabase or LifecycleCommandType.StartServer or LifecycleCommandType.StartWorker =>
                    new RoleStarted(command.Generation!.Value),
                LifecycleCommandType.WaitForDatabase or LifecycleCommandType.WaitForServer or LifecycleCommandType.WaitForWorker =>
                    new RoleReady(command.Generation!.Value),
                LifecycleCommandType.RunMigrations => new MigrationCompleted(command.OperationId),
                LifecycleCommandType.StopDatabaseFast when DatabaseStopTimesOut =>
                    new OperationTimedOut(command.Generation!.Value, command.OperationId),
                LifecycleCommandType.ReconcileDatabaseStop when DatabaseStopTimesOut =>
                    new Reconciled(command.Generation!.Value, ReconciledState.Unknown),
                _ => null,
            };
            if (command.Type == LifecycleCommandType.DrainWorker &&
                (HangOnDrain || !DatabaseStopTimesOut))
            {
                StartupCommandCount = Commands.Count - 1;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (command.Type == LifecycleCommandType.ReleaseInstance)
            {
                StartupCommandCount = Math.Max(StartupCommandCount, 0);
            }

            return result;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
}
