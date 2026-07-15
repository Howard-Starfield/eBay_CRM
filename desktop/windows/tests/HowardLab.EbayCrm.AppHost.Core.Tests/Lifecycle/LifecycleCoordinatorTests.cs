using System.Collections.Concurrent;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Tests.Lifecycle;

public sealed class LifecycleCoordinatorTests
{
    [Fact]
    public async Task StartupTraversesTheApprovedSequenceAndAdvancesOnlyForTheExpectedGeneration()
    {
        var harness = CoordinatorHarness.Create();
        var operationId = Guid.Parse("10000000-0000-0000-0000-000000000001");

        await harness.ExpectAsync(new StartRequested(operationId), RuntimeState.AcquiringInstance, LifecycleCommandType.AcquireInstance);
        await harness.ExpectAsync(new InstanceAcquired(operationId), RuntimeState.ValidatingPayload, LifecycleCommandType.ValidatePayload);
        await harness.ExpectAsync(new PayloadValidated(operationId), RuntimeState.PreparingRuntime, LifecycleCommandType.PrepareRuntime);
        var startDatabase = await harness.ExpectAsync(new RuntimePrepared(operationId), RuntimeState.StartingDatabase, LifecycleCommandType.StartDatabase);
        var database = Assert.IsType<ProcessGeneration>(Assert.Single(startDatabase.Commands).Generation);

        var stale = database with { Value = database.Value - 1 };
        var ignored = await harness.Coordinator.DispatchAsync(new RoleReady(stale));
        Assert.True(ignored.Ignored);
        Assert.Equal("StaleGeneration", ignored.ReasonCode);
        Assert.Equal(RuntimeState.StartingDatabase, harness.Coordinator.State);

        await harness.ExpectAsync(new RoleStarted(database), RuntimeState.WaitingForDatabase, LifecycleCommandType.WaitForDatabase);
        await harness.ExpectAsync(new RoleReady(database), RuntimeState.Migrating, LifecycleCommandType.RunMigrations);
        var startServer = await harness.ExpectAsync(new MigrationCompleted(operationId), RuntimeState.StartingServer, LifecycleCommandType.StartServer);
        var server = Assert.IsType<ProcessGeneration>(Assert.Single(startServer.Commands).Generation);
        await harness.ExpectAsync(new RoleStarted(server), RuntimeState.WaitingForServer, LifecycleCommandType.WaitForServer);
        await harness.ExpectAsync(new RoleReady(server), RuntimeState.StartingWorker, LifecycleCommandType.StartWorker);
        var worker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
        await harness.ExpectAsync(new RoleStarted(worker), RuntimeState.WaitingForWorker, LifecycleCommandType.WaitForWorker);
        var ready = await harness.Coordinator.DispatchAsync(new RoleReady(worker));

        Assert.False(ready.Ignored);
        Assert.Empty(ready.Commands);
        Assert.Equal(RuntimeState.Ready, ready.Current);
    }

    [Fact]
    public async Task GenerationEventsAreFencedByRoleValueAndOperationIdBeforeStateIsConsulted()
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        var candidates = new[]
        {
            server with { Role = RuntimeRole.Worker, Value = server.Value + 100 },
            server with { Value = server.Value - 1 },
            server with { OperationId = Guid.NewGuid() },
        };

        foreach (var candidate in candidates)
        {
            var result = await harness.Coordinator.DispatchAsync(new HealthFailed(candidate, "probe-failed"));
            Assert.True(result.Ignored);
            Assert.Equal("StaleGeneration", result.ReasonCode);
            Assert.Empty(result.Commands);
        }

        Assert.Equal(RuntimeState.Ready, harness.Coordinator.State);
    }

    [Fact]
    public async Task DatabaseFailureSignalsCoalesceEvenThoughReconciliationKeepsItsGenerationCurrent()
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);

        var first = await harness.Coordinator.DispatchAsync(new RoleExited(database, 1));
        var duplicate = await harness.Coordinator.DispatchAsync(new HealthFailed(database, "probe-failed"));

        Assert.Equal(3, first.Commands.Count);
        Assert.True(duplicate.Ignored);
        Assert.Equal("DuplicateFailure", duplicate.ReasonCode);
        Assert.Empty(duplicate.Commands);
    }

    [Fact]
    public async Task RepeatedFailureSignalsForOneGenerationCoalesceUnderConcurrentDispatch()
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        var worker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var events = new LifecycleEvent[]
        {
            new RoleExited(server, 1),
            new HealthFailed(server, "probe-failed"),
            new ControlDisconnected(server),
        };
        var dispatches = events.Select(@event => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref readyCount) == events.Length)
            {
                allReady.SetResult();
            }

            await release.Task;
            return await harness.Coordinator.DispatchAsync(@event);
        })).ToArray();
        await allReady.Task;
        release.SetResult();
        var results = await Task.WhenAll(dispatches);

        var recovery = Assert.Single(results, result => result.Commands.Count > 0);
        Assert.Equal(
            new[]
            {
                LifecycleCommandType.StopWorker,
                LifecycleCommandType.StartServer,
            },
            recovery.Commands.Select(command => command.Type));
        Assert.Equal(2, results.Count(result => result.Ignored));
        Assert.Equal(RuntimeState.StartingServer, harness.Coordinator.State);
        Assert.Equal(worker, recovery.Commands[0].Generation);

        var replacementServer = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        var serverStarted = await harness.Coordinator.DispatchAsync(new RoleStarted(replacementServer));
        var serverReady = await harness.Coordinator.DispatchAsync(new RoleReady(replacementServer));
        Assert.Equal(RuntimeState.StartingWorker, serverReady.Current);
        Assert.Equal(LifecycleCommandType.StartWorker, Assert.Single(serverReady.Commands).Type);
        var replacementWorker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
        var workerStarted = await harness.Coordinator.DispatchAsync(new RoleStarted(replacementWorker));
        var workerReady = await harness.Coordinator.DispatchAsync(new RoleReady(replacementWorker));

        Assert.Equal(LifecycleCommandType.WaitForWorker, Assert.Single(workerStarted.Commands).Type);
        Assert.Equal(RuntimeState.Ready, workerReady.Current);
        Assert.Equal(
            new[]
            {
                LifecycleCommandType.StopWorker,
                LifecycleCommandType.StartServer,
                LifecycleCommandType.WaitForServer,
                LifecycleCommandType.StartWorker,
                LifecycleCommandType.WaitForWorker,
            },
            recovery.Commands.Concat(serverStarted.Commands).Concat(serverReady.Commands).Concat(workerStarted.Commands).Select(command => command.Type));
    }

    [Fact]
    public async Task ServerFailureBeforeWorkerExistsRecoversWithoutThrowing()
    {
        var harness = CoordinatorHarness.Create();
        var operationId = Guid.Parse("81000000-0000-0000-0000-000000000001");
        await harness.AdvanceToServerStartAsync(operationId);
        var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);

        var result = await harness.Coordinator.DispatchAsync(new RoleExited(server, 1));

        Assert.Equal(RuntimeState.StartingServer, result.Current);
        Assert.Equal(LifecycleCommandType.StartServer, Assert.Single(result.Commands).Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseFailureBeforeAppTierExistsReconcilesWithoutThrowing(bool databaseStarted)
    {
        var harness = CoordinatorHarness.Create();
        var operationId = Guid.Parse("82000000-0000-0000-0000-000000000002");
        await harness.AdvanceToDatabaseStartAsync(operationId);
        var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);
        if (databaseStarted)
        {
            await harness.Coordinator.DispatchAsync(new RoleStarted(database));
        }

        var result = await harness.Coordinator.DispatchAsync(new RoleExited(database, 1));

        Assert.Equal(RuntimeState.ReconcilingDatabaseStart, result.Current);
        Assert.Equal(LifecycleCommandType.ReconcileDatabaseStart, Assert.Single(result.Commands).Type);
        Assert.Equal(database, result.Commands[0].Generation);
    }

    [Fact]
    public async Task RequestedAppTierExitsDuringDatabaseReconciliationDoNotRestartOrConsumeBudget()
    {
        var harness = await CoordinatorHarness.ReadyAsync(maxRetries: 1);
        var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);
        var worker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
        var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        await harness.Coordinator.DispatchAsync(new RoleExited(database, 1));

        var workerExit = await harness.Coordinator.DispatchAsync(new RoleExited(worker, 0));
        var serverExit = await harness.Coordinator.DispatchAsync(new RoleExited(server, 0));

        Assert.True(workerExit.Ignored);
        Assert.True(serverExit.Ignored);
        Assert.Equal("RequestedExit", workerExit.ReasonCode);
        Assert.Equal("RequestedExit", serverExit.ReasonCode);
        Assert.Empty(workerExit.Commands);
        Assert.Empty(serverExit.Commands);
        Assert.Equal(RuntimeState.ReconcilingDatabaseStart, harness.Coordinator.State);
        Assert.Equal(RestartBudgetResult.Allowed, harness.Budget.TryConsume(RuntimeRole.Worker, harness.Clock.UtcNow));
        Assert.Equal(RestartBudgetResult.Allowed, harness.Budget.TryConsume(RuntimeRole.Server, harness.Clock.UtcNow));
    }

    [Fact]
    public async Task WorkerExitRequestedByServerRecoveryDoesNotRestartOrConsumeBudget()
    {
        var harness = await CoordinatorHarness.ReadyAsync(maxRetries: 1);
        var worker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
        var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        await harness.Coordinator.DispatchAsync(new RoleExited(server, 1));

        var workerExit = await harness.Coordinator.DispatchAsync(new RoleExited(worker, 0));

        Assert.True(workerExit.Ignored);
        Assert.Equal("RequestedExit", workerExit.ReasonCode);
        Assert.Empty(workerExit.Commands);
        Assert.Equal(RuntimeState.StartingServer, harness.Coordinator.State);
        Assert.Equal(RestartBudgetResult.Allowed, harness.Budget.TryConsume(RuntimeRole.Worker, harness.Clock.UtcNow));
    }

    [Theory]
    [InlineData("HealthFailed")]
    [InlineData("ControlDisconnected")]
    public async Task RequestedWorkerFailureSignalsDuringServerRecoveryDoNotRestartOrConsumeBudget(string signalType)
    {
        var harness = await CoordinatorHarness.ReadyAsync(maxRetries: 1);
        var worker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
        var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        await harness.Coordinator.DispatchAsync(new RoleExited(server, 1));

        var result = await harness.Coordinator.DispatchAsync(CreateFailureSignal(signalType, worker));

        Assert.True(result.Ignored);
        Assert.Equal("RequestedExit", result.ReasonCode);
        Assert.Empty(result.Commands);
        Assert.Equal(RuntimeState.StartingServer, harness.Coordinator.State);
        Assert.Equal(RestartBudgetResult.Allowed, harness.Budget.TryConsume(RuntimeRole.Worker, harness.Clock.UtcNow));
    }

    [Theory]
    [InlineData("HealthFailed")]
    [InlineData("ControlDisconnected")]
    public async Task RequestedServerFailureSignalsDuringDatabaseReconciliationDoNotRestartOrConsumeBudget(string signalType)
    {
        var harness = await CoordinatorHarness.ReadyAsync(maxRetries: 1);
        var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);
        var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        await harness.Coordinator.DispatchAsync(new RoleExited(database, 1));

        var result = await harness.Coordinator.DispatchAsync(CreateFailureSignal(signalType, server));

        Assert.True(result.Ignored);
        Assert.Equal("RequestedExit", result.ReasonCode);
        Assert.Empty(result.Commands);
        Assert.Equal(RuntimeState.ReconcilingDatabaseStart, harness.Coordinator.State);
        Assert.Equal(RestartBudgetResult.Allowed, harness.Budget.TryConsume(RuntimeRole.Server, harness.Clock.UtcNow));
    }

    [Theory]
    [InlineData(RuntimeRole.Worker)]
    [InlineData(RuntimeRole.Server)]
    public async Task DatabaseRecoveryRestoresItsOperationAfterAppTierRecovery(RuntimeRole recoveredRole)
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);
        await harness.RecoverAppTierAndReturnReadyAsync(recoveredRole);
        Assert.NotEqual(database.OperationId, harness.Coordinator.CurrentGeneration(recoveredRole).OperationId);

        var databaseFailure = await harness.Coordinator.DispatchAsync(new RoleExited(database, 1));
        Assert.Equal(RuntimeState.ReconcilingDatabaseStart, databaseFailure.Current);
        var unrelatedTimeout = await harness.Coordinator.DispatchAsync(new OperationTimedOut(database, Guid.NewGuid()));
        Assert.True(unrelatedTimeout.Ignored);
        Assert.Equal("StaleOperation", unrelatedTimeout.ReasonCode);

        var reconciled = await harness.Coordinator.DispatchAsync(new Reconciled(database, ReconciledState.Running));
        var ready = await harness.Coordinator.DispatchAsync(new RoleReady(database));
        var migrated = await harness.Coordinator.DispatchAsync(new MigrationCompleted(database.OperationId));

        Assert.Equal(RuntimeState.WaitingForDatabase, reconciled.Current);
        Assert.Equal(LifecycleCommandType.WaitForDatabase, Assert.Single(reconciled.Commands).Type);
        Assert.Equal(RuntimeState.Migrating, ready.Current);
        Assert.Equal(LifecycleCommandType.RunMigrations, Assert.Single(ready.Commands).Type);
        Assert.Equal(RuntimeState.StartingServer, migrated.Current);
        Assert.Equal(LifecycleCommandType.StartServer, Assert.Single(migrated.Commands).Type);
    }

    [Fact]
    public async Task StableServerReadinessResetsItsExhaustedRetryWindow()
    {
        var harness = await CoordinatorHarness.ReadyAsync(
            maxRetries: 1,
            window: TimeSpan.FromMinutes(10),
            stableResetPeriod: TimeSpan.FromMinutes(5));
        var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        await harness.Coordinator.DispatchAsync(new RoleExited(server, 1));
        var replacementServer = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        var serverStarted = await harness.Coordinator.DispatchAsync(new RoleStarted(replacementServer));
        var serverReady = await harness.Coordinator.DispatchAsync(new RoleReady(replacementServer));
        var replacementWorker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
        await harness.Coordinator.DispatchAsync(new RoleStarted(replacementWorker));
        await harness.Coordinator.DispatchAsync(new RoleReady(replacementWorker));
        Assert.Equal(LifecycleCommandType.StartWorker, Assert.Single(serverReady.Commands).Type);
        Assert.Equal(RuntimeState.Ready, harness.Coordinator.State);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        var retryAfterStablePeriod = await harness.Coordinator.DispatchAsync(new RoleExited(replacementServer, 1));

        Assert.NotEqual(RuntimeState.Faulted, retryAfterStablePeriod.Current);
        Assert.Equal(
            new[]
            {
                LifecycleCommandType.StopWorker,
                LifecycleCommandType.StartServer,
            },
            retryAfterStablePeriod.Commands.Select(command => command.Type));
        Assert.Equal(LifecycleCommandType.WaitForServer, Assert.Single(serverStarted.Commands).Type);
    }

    [Fact]
    public async Task CurrentGenerationReadsRemainConsistentWhileWorkerGenerationAdvances()
    {
        const int generationAdvances = 20_000;
        var harness = await CoordinatorHarness.ReadyAsync(maxRetries: generationAdvances + 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = new ConcurrentQueue<string>();
        var done = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await release.Task;
            var previous = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
            while (Volatile.Read(ref done) == 0)
            {
                var current = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
                if (current.Role != RuntimeRole.Worker || current.Value <= 0 || current.OperationId == Guid.Empty)
                {
                    observations.Enqueue($"Invalid generation: {current}");
                }

                if (current.Value < previous.Value)
                {
                    observations.Enqueue($"Generation regressed from {previous} to {current}");
                }
                else if (current.Value == previous.Value && current.OperationId != previous.OperationId)
                {
                    observations.Enqueue($"Operation changed without generation advance: {previous} to {current}");
                }
                else if (current.Value > previous.Value && current.OperationId == previous.OperationId)
                {
                    observations.Enqueue($"Generation advanced without operation change: {previous} to {current}");
                }

                previous = current;
            }
        })).ToArray();
        var writer = Task.Run(async () =>
        {
            await release.Task;
            for (var index = 0; index < generationAdvances; index++)
            {
                var worker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
                await harness.Coordinator.DispatchAsync(new RoleExited(worker, 1));
            }

            Volatile.Write(ref done, 1);
        });

        release.SetResult();
        await Task.WhenAll(readers.Append(writer));

        Assert.Empty(observations);
        Assert.Equal(generationAdvances + 1, harness.Coordinator.CurrentGeneration(RuntimeRole.Worker).Value);
    }

    [Fact]
    public async Task WorkerFailureRestartsOnlyTheWorkerAndKeepsDatabaseAndServerGenerations()
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);
        var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        var worker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);

        var result = await harness.Coordinator.DispatchAsync(new RoleExited(worker, 7));

        Assert.Equal(LifecycleCommandType.StartWorker, Assert.Single(result.Commands).Type);
        Assert.Equal(database, harness.Coordinator.CurrentGeneration(RuntimeRole.Database));
        Assert.Equal(server, harness.Coordinator.CurrentGeneration(RuntimeRole.Server));
        Assert.Equal(worker.Value + 1, harness.Coordinator.CurrentGeneration(RuntimeRole.Worker).Value);
    }

    [Fact]
    public async Task DatabaseFailureStopsAppTierBeforeReconciliationWithoutStartingAnotherDatabase()
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);
        var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
        var worker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);

        var result = await harness.Coordinator.DispatchAsync(new ControlDisconnected(database));

        Assert.Equal(RuntimeState.ReconcilingDatabaseStart, result.Current);
        Assert.Equal(
            new[]
            {
                LifecycleCommandType.StopWorker,
                LifecycleCommandType.StopServer,
                LifecycleCommandType.ReconcileDatabaseStart,
            },
            result.Commands.Select(command => command.Type));
        Assert.DoesNotContain(result.Commands, command => command.Type == LifecycleCommandType.StartDatabase);
        Assert.Equal(database, harness.Coordinator.CurrentGeneration(RuntimeRole.Database));
        Assert.Equal(worker, result.Commands[0].Generation);
        Assert.Equal(server, result.Commands[1].Generation);
        Assert.Equal(database, result.Commands[2].Generation);
    }

    [Fact]
    public async Task DatabaseStartTimeoutRequiresBothOperationIdsAndReconcilesBeforeProgressing()
    {
        var harness = CoordinatorHarness.Create();
        var operationId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        await harness.AdvanceToDatabaseStartAsync(operationId);
        var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);
        await harness.Coordinator.DispatchAsync(new RoleStarted(database));

        var wrongOperation = await harness.Coordinator.DispatchAsync(new OperationTimedOut(database, Guid.NewGuid()));
        Assert.True(wrongOperation.Ignored);
        Assert.Equal("StaleOperation", wrongOperation.ReasonCode);

        var timedOut = await harness.Coordinator.DispatchAsync(new OperationTimedOut(database, operationId));
        Assert.Equal(RuntimeState.ReconcilingDatabaseStart, timedOut.Current);
        Assert.Equal(LifecycleCommandType.ReconcileDatabaseStart, Assert.Single(timedOut.Commands).Type);

        var reconciled = await harness.Coordinator.DispatchAsync(new Reconciled(database, ReconciledState.Running));
        Assert.Equal(RuntimeState.WaitingForDatabase, reconciled.Current);
        Assert.Equal(LifecycleCommandType.WaitForDatabase, Assert.Single(reconciled.Commands).Type);
    }

    [Theory]
    [InlineData(RuntimeRole.Server, RuntimeState.StartingServer, RuntimeState.WaitingForServer, LifecycleCommandType.WaitForServer, LifecycleDeadlineKey.ServerReadiness)]
    [InlineData(RuntimeRole.Worker, RuntimeState.StartingWorker, RuntimeState.WaitingForWorker, LifecycleCommandType.WaitForWorker, LifecycleDeadlineKey.WorkerReadiness)]
    public async Task LateRoleStartReconcilesRunningBeforeReadinessWait(
        RuntimeRole role,
        RuntimeState startingState,
        RuntimeState waitingState,
        LifecycleCommandType waitCommand,
        LifecycleDeadlineKey waitDeadline)
    {
        var harness = await CoordinatorHarness.AtRoleStartAsync(role);
        var generation = harness.Coordinator.CurrentGeneration(role);

        var wrongOperation = await harness.Coordinator.DispatchAsync(
            new OperationTimedOut(generation, Guid.NewGuid()));
        Assert.True(wrongOperation.Ignored);
        Assert.Equal("StaleOperation", wrongOperation.ReasonCode);
        Assert.Equal(startingState, wrongOperation.Current);

        var timeout = await harness.Coordinator.DispatchAsync(
            new OperationTimedOut(generation, generation.OperationId));

        Assert.Equal(RuntimeState.ReconcilingRoleStart, timeout.Current);
        var reconcile = Assert.Single(timeout.Commands);
        Assert.Equal(LifecycleCommandType.ReconcileRoleStart, reconcile.Type);
        Assert.Equal(generation, reconcile.Generation);
        Assert.Equal(LifecycleDeadlineKey.RoleReconciliation, reconcile.DeadlineKey);

        var staleGeneration = generation with { Value = generation.Value - 1 };
        var stale = await harness.Coordinator.DispatchAsync(
            new Reconciled(staleGeneration, ReconciledState.Running));
        Assert.True(stale.Ignored);
        Assert.Equal("StaleGeneration", stale.ReasonCode);
        Assert.Equal(RuntimeState.ReconcilingRoleStart, stale.Current);

        var reconciled = await harness.Coordinator.DispatchAsync(
            new Reconciled(generation, ReconciledState.Running));

        Assert.Equal(waitingState, reconciled.Current);
        var wait = Assert.Single(reconciled.Commands);
        Assert.Equal(waitCommand, wait.Type);
        Assert.Equal(generation, wait.Generation);
        Assert.Equal(waitDeadline, wait.DeadlineKey);
    }

    [Theory]
    [InlineData(RuntimeRole.Server, RuntimeState.StartingServer, LifecycleCommandType.StartServer, LifecycleDeadlineKey.ServerStart)]
    [InlineData(RuntimeRole.Worker, RuntimeState.StartingWorker, LifecycleCommandType.StartWorker, LifecycleDeadlineKey.WorkerStart)]
    public async Task LateRoleStartReconciledStoppedUsesBoundedRestartAndCreatesOneGeneration(
        RuntimeRole role,
        RuntimeState startingState,
        LifecycleCommandType startCommand,
        LifecycleDeadlineKey startDeadline)
    {
        var harness = await CoordinatorHarness.AtRoleStartAsync(role, maxRetries: 1);
        var timedOutGeneration = harness.Coordinator.CurrentGeneration(role);
        await harness.Coordinator.DispatchAsync(
            new OperationTimedOut(timedOutGeneration, timedOutGeneration.OperationId));

        var reconciled = await harness.Coordinator.DispatchAsync(
            new Reconciled(timedOutGeneration, ReconciledState.Stopped));

        Assert.Equal(startingState, reconciled.Current);
        var restart = Assert.Single(reconciled.Commands);
        Assert.Equal(startCommand, restart.Type);
        Assert.Equal(startDeadline, restart.DeadlineKey);
        var replacement = Assert.IsType<ProcessGeneration>(restart.Generation);
        Assert.Equal(timedOutGeneration.Value + 1, replacement.Value);
        Assert.NotEqual(timedOutGeneration.OperationId, replacement.OperationId);

        var duplicate = await harness.Coordinator.DispatchAsync(
            new Reconciled(timedOutGeneration, ReconciledState.Stopped));
        Assert.True(duplicate.Ignored);
        Assert.Equal("StaleGeneration", duplicate.ReasonCode);
        Assert.Empty(duplicate.Commands);
        Assert.Equal(replacement, harness.Coordinator.CurrentGeneration(role));
    }

    [Theory]
    [InlineData(RuntimeRole.Server)]
    [InlineData(RuntimeRole.Worker)]
    public async Task LateRoleStartReconciledStoppedFaultsWhenRestartBudgetIsExhausted(RuntimeRole role)
    {
        var harness = await CoordinatorHarness.AtRoleStartAsync(role, maxRetries: 0);
        var generation = harness.Coordinator.CurrentGeneration(role);
        await harness.Coordinator.DispatchAsync(
            new OperationTimedOut(generation, generation.OperationId));

        var reconciled = await harness.Coordinator.DispatchAsync(
            new Reconciled(generation, ReconciledState.Stopped));

        Assert.Equal(RuntimeState.Faulted, reconciled.Current);
        Assert.Equal("RestartBudgetExhausted", reconciled.ReasonCode);
        Assert.Equal(LifecycleCommandType.EnterFault, Assert.Single(reconciled.Commands).Type);
        Assert.Equal(generation, harness.Coordinator.CurrentGeneration(role));
    }

    [Fact]
    public async Task ThirdWorkerFailureFaultsExactlyOnceAfterTwoRetries()
    {
        var harness = await CoordinatorHarness.ReadyAsync();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var current = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
            var retry = await harness.Coordinator.DispatchAsync(new RoleExited(current, 1));
            Assert.Equal(LifecycleCommandType.StartWorker, Assert.Single(retry.Commands).Type);
        }

        var exhaustedGeneration = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
        var exhausted = await harness.Coordinator.DispatchAsync(new RoleExited(exhaustedGeneration, 1));
        var duplicate = await harness.Coordinator.DispatchAsync(new HealthFailed(exhaustedGeneration, "probe-failed"));

        Assert.Equal(RuntimeState.Faulted, exhausted.Current);
        Assert.Equal("RestartBudgetExhausted", exhausted.ReasonCode);
        Assert.Equal(LifecycleCommandType.EnterFault, Assert.Single(exhausted.Commands).Type);
        Assert.True(duplicate.Ignored);
        Assert.Empty(duplicate.Commands);
    }

    [Fact]
    public async Task StopRequestedOrdersOwnedShutdownAndPermanentlyFencesStartsAndRestarts()
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var stopOperation = Guid.Parse("40000000-0000-0000-0000-000000000004");

        var stop = await harness.Coordinator.DispatchAsync(new StopRequested(stopOperation));

        Assert.Equal(RuntimeState.Stopping, stop.Current);
        Assert.Equal(
            new[]
            {
                LifecycleCommandType.DrainWorker,
                LifecycleCommandType.StopWorker,
                LifecycleCommandType.StopServer,
                LifecycleCommandType.StopDatabaseFast,
                LifecycleCommandType.ReleaseInstance,
            },
            stop.Commands.Select(command => command.Type));
        Assert.All(stop.Commands, command => Assert.Equal(stopOperation, command.OperationId));
        Assert.NotNull(stop.Commands[0].Generation);
        Assert.NotNull(stop.Commands[2].Generation);
        Assert.NotNull(stop.Commands[3].Generation);
        Assert.Null(stop.Commands[4].Generation);

        var worker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
        var afterStop = await Task.WhenAll(
            harness.Coordinator.DispatchAsync(new RoleExited(worker, 1)).AsTask(),
            harness.Coordinator.DispatchAsync(new HealthFailed(worker, "probe-failed")).AsTask(),
            harness.Coordinator.DispatchAsync(new StopRequested(Guid.NewGuid())).AsTask());
        Assert.All(afterStop, result => Assert.True(result.Ignored));
        Assert.Empty(afterStop.SelectMany(result => result.Commands));

        var staleCompletion = await harness.Coordinator.DispatchAsync(new ShutdownCompleted(Guid.NewGuid()));
        Assert.True(staleCompletion.Ignored);
        var completed = await harness.Coordinator.DispatchAsync(new ShutdownCompleted(stopOperation));
        Assert.Equal(RuntimeState.Stopped, completed.Current);

        var restart = await harness.Coordinator.DispatchAsync(new StartRequested(Guid.NewGuid()));
        Assert.True(restart.Ignored);
        Assert.Equal("StopAlreadyAccepted", restart.ReasonCode);
        Assert.DoesNotContain(restart.Commands, command => IsStart(command.Type));
    }

    [Fact]
    public async Task StopDuringStartupStopsOnlyTheRoleWhoseStartWasEmitted()
    {
        var harness = CoordinatorHarness.Create();
        var startupOperation = Guid.Parse("50000000-0000-0000-0000-000000000005");
        await harness.AdvanceToDatabaseStartAsync(startupOperation);

        var stop = await harness.Coordinator.DispatchAsync(new StopRequested(Guid.NewGuid()));

        Assert.Equal(
            new[] { LifecycleCommandType.StopDatabaseFast, LifecycleCommandType.ReleaseInstance },
            stop.Commands.Select(command => command.Type));
    }

    [Fact]
    public async Task DatabaseStopTimeoutIsGenerationBoundAndReconcilesToStopped()
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var stopOperation = Guid.Parse("60000000-0000-0000-0000-000000000006");
        await harness.Coordinator.DispatchAsync(new StopRequested(stopOperation));
        var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);

        var timeout = await harness.Coordinator.DispatchAsync(new OperationTimedOut(database, stopOperation));
        Assert.Equal(RuntimeState.ReconcilingDatabaseStop, timeout.Current);
        Assert.Equal(LifecycleCommandType.ReconcileDatabaseStop, Assert.Single(timeout.Commands).Type);

        var reconciled = await harness.Coordinator.DispatchAsync(new Reconciled(database, ReconciledState.Stopped));
        Assert.Equal(RuntimeState.Stopped, reconciled.Current);
        Assert.Equal(LifecycleCommandType.ReleaseInstance, Assert.Single(reconciled.Commands).Type);
    }

    [Theory]
    [InlineData(RuntimeRole.Server)]
    [InlineData(RuntimeRole.Worker)]
    public async Task LateRoleStopReconcilesStoppedBackToStopping(RuntimeRole role)
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var stopOperation = Guid.NewGuid();
        await harness.Coordinator.DispatchAsync(new StopRequested(stopOperation));
        var generation = harness.Coordinator.CurrentGeneration(role);

        var timeout = await harness.Coordinator.DispatchAsync(
            new OperationTimedOut(generation, generation.OperationId));

        Assert.Equal(RuntimeState.ReconcilingRoleStop, timeout.Current);
        var reconcile = Assert.Single(timeout.Commands);
        Assert.Equal(LifecycleCommandType.ReconcileRoleStop, reconcile.Type);
        Assert.Equal(generation, reconcile.Generation);
        Assert.Equal(LifecycleDeadlineKey.RoleReconciliation, reconcile.DeadlineKey);

        var reconciled = await harness.Coordinator.DispatchAsync(
            new Reconciled(generation, ReconciledState.Stopped));
        Assert.Equal(RuntimeState.Stopping, reconciled.Current);
        Assert.Empty(reconciled.Commands);
    }

    [Theory]
    [InlineData(RuntimeRole.Server)]
    [InlineData(RuntimeRole.Worker)]
    public async Task UnknownLateRoleStopRemainsReconcilingUntilOuterTimeoutEscalates(RuntimeRole role)
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var stopOperation = Guid.NewGuid();
        await harness.Coordinator.DispatchAsync(new StopRequested(stopOperation));
        var generation = harness.Coordinator.CurrentGeneration(role);
        await harness.Coordinator.DispatchAsync(
            new OperationTimedOut(generation, generation.OperationId));

        var unknown = await harness.Coordinator.DispatchAsync(
            new Reconciled(generation, ReconciledState.Unknown));
        Assert.Equal(RuntimeState.ReconcilingRoleStop, unknown.Current);
        Assert.Empty(unknown.Commands);

        var outerTimeout = await harness.Coordinator.DispatchAsync(
            new OperationTimedOut(generation, generation.OperationId));
        Assert.Equal(RuntimeState.Faulted, outerTimeout.Current);
        Assert.Equal(
            new[] { LifecycleCommandType.EscalateJob, LifecycleCommandType.EnterFault },
            outerTimeout.Commands.Select(command => command.Type));
    }

    [Theory]
    [InlineData(RuntimeRole.Server)]
    [InlineData(RuntimeRole.Worker)]
    public async Task LateRoleStopResultFromOldGenerationCannotAdvanceNewerGeneration(RuntimeRole role)
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var oldGeneration = harness.Coordinator.CurrentGeneration(role);
        await harness.RecoverAppTierAndReturnReadyAsync(role);
        var replacementBeforeStop = harness.Coordinator.CurrentGeneration(role);
        Assert.True(replacementBeforeStop.Value > oldGeneration.Value);

        var stopOperation = Guid.NewGuid();
        await harness.Coordinator.DispatchAsync(new StopRequested(stopOperation));
        var stoppingGeneration = harness.Coordinator.CurrentGeneration(role);
        await harness.Coordinator.DispatchAsync(
            new OperationTimedOut(stoppingGeneration, stoppingGeneration.OperationId));

        var stale = await harness.Coordinator.DispatchAsync(
            new Reconciled(oldGeneration, ReconciledState.Stopped));
        Assert.True(stale.Ignored);
        Assert.Equal("StaleGeneration", stale.ReasonCode);
        Assert.Equal(RuntimeState.ReconcilingRoleStop, stale.Current);

        var current = await harness.Coordinator.DispatchAsync(
            new Reconciled(stoppingGeneration, ReconciledState.Stopped));
        Assert.Equal(RuntimeState.Stopping, current.Current);
    }

    [Fact]
    public async Task DatabaseStopOuterTimeoutEscalatesAndFaultsOnlyOnce()
    {
        var harness = await CoordinatorHarness.ReadyAsync();
        var stopOperation = Guid.Parse("70000000-0000-0000-0000-000000000007");
        await harness.Coordinator.DispatchAsync(new StopRequested(stopOperation));
        var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);
        await harness.Coordinator.DispatchAsync(new OperationTimedOut(database, stopOperation));
        await harness.Coordinator.DispatchAsync(new Reconciled(database, ReconciledState.Unknown));

        var outerTimeout = await harness.Coordinator.DispatchAsync(new OperationTimedOut(database, stopOperation));
        var duplicate = await harness.Coordinator.DispatchAsync(new OperationTimedOut(database, stopOperation));

        Assert.Equal(RuntimeState.Faulted, outerTimeout.Current);
        Assert.Equal(
            new[] { LifecycleCommandType.EscalateJob, LifecycleCommandType.EnterFault },
            outerTimeout.Commands.Select(command => command.Type));
        Assert.True(duplicate.Ignored);
        Assert.Empty(duplicate.Commands);
    }

    [Fact]
    public async Task StopRequestedWhileStoppedIsIdempotentlyIgnored()
    {
        var harness = CoordinatorHarness.Create();

        var result = await harness.Coordinator.DispatchAsync(new StopRequested(Guid.NewGuid()));

        Assert.True(result.Ignored);
        Assert.Equal(RuntimeState.Stopped, result.Current);
        Assert.Empty(result.Commands);
    }

    private static bool IsStart(LifecycleCommandType type)
    {
        return type is LifecycleCommandType.StartDatabase
            or LifecycleCommandType.StartServer
            or LifecycleCommandType.StartWorker;
    }

    private static LifecycleEvent CreateFailureSignal(string signalType, ProcessGeneration generation)
    {
        return signalType switch
        {
            "HealthFailed" => new HealthFailed(generation, "probe-failed"),
            "ControlDisconnected" => new ControlDisconnected(generation),
            _ => throw new ArgumentOutOfRangeException(nameof(signalType)),
        };
    }

    private sealed class CoordinatorHarness
    {
        private static readonly DateTimeOffset Epoch = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

        private CoordinatorHarness(LifecycleCoordinator coordinator, TestClock clock, RestartBudget budget)
        {
            Coordinator = coordinator;
            Clock = clock;
            Budget = budget;
        }

        public LifecycleCoordinator Coordinator { get; }

        public TestClock Clock { get; }

        public RestartBudget Budget { get; }

        public static CoordinatorHarness Create(
            int maxRetries = 2,
            TimeSpan? window = null,
            TimeSpan? stableResetPeriod = null)
        {
            var clock = new TestClock(Epoch);
            var budget = new RestartBudget(
                maxRetries,
                window ?? TimeSpan.FromMinutes(1),
                stableResetPeriod ?? TimeSpan.FromMinutes(5));
            return new CoordinatorHarness(new LifecycleCoordinator(clock, budget), clock, budget);
        }

        public static async Task<CoordinatorHarness> ReadyAsync(
            int maxRetries = 2,
            TimeSpan? window = null,
            TimeSpan? stableResetPeriod = null)
        {
            var harness = Create(maxRetries, window, stableResetPeriod);
            var operationId = Guid.Parse("30000000-0000-0000-0000-000000000003");
            await harness.AdvanceToDatabaseStartAsync(operationId);
            var database = harness.Coordinator.CurrentGeneration(RuntimeRole.Database);
            await harness.Coordinator.DispatchAsync(new RoleStarted(database));
            await harness.Coordinator.DispatchAsync(new RoleReady(database));
            await harness.Coordinator.DispatchAsync(new MigrationCompleted(operationId));
            var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
            await harness.Coordinator.DispatchAsync(new RoleStarted(server));
            await harness.Coordinator.DispatchAsync(new RoleReady(server));
            var worker = harness.Coordinator.CurrentGeneration(RuntimeRole.Worker);
            await harness.Coordinator.DispatchAsync(new RoleStarted(worker));
            await harness.Coordinator.DispatchAsync(new RoleReady(worker));
            Assert.Equal(RuntimeState.Ready, harness.Coordinator.State);
            return harness;
        }

        public static async Task<CoordinatorHarness> AtRoleStartAsync(
            RuntimeRole role,
            int maxRetries = 2)
        {
            if (role is not (RuntimeRole.Server or RuntimeRole.Worker))
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }

            var harness = Create(maxRetries);
            var operationId = Guid.Parse("83000000-0000-0000-0000-000000000003");
            await harness.AdvanceToServerStartAsync(operationId);
            if (role == RuntimeRole.Worker)
            {
                var server = harness.Coordinator.CurrentGeneration(RuntimeRole.Server);
                await harness.Coordinator.DispatchAsync(new RoleStarted(server));
                await harness.Coordinator.DispatchAsync(new RoleReady(server));
            }

            return harness;
        }

        public async Task AdvanceToServerStartAsync(Guid operationId)
        {
            await AdvanceToDatabaseStartAsync(operationId);
            var database = Coordinator.CurrentGeneration(RuntimeRole.Database);
            await Coordinator.DispatchAsync(new RoleStarted(database));
            await Coordinator.DispatchAsync(new RoleReady(database));
            await Coordinator.DispatchAsync(new MigrationCompleted(operationId));
            Assert.Equal(RuntimeState.StartingServer, Coordinator.State);
        }

        public async Task RecoverAppTierAndReturnReadyAsync(RuntimeRole role)
        {
            var generation = Coordinator.CurrentGeneration(role);
            await Coordinator.DispatchAsync(new RoleExited(generation, 1));
            if (role == RuntimeRole.Server)
            {
                var server = Coordinator.CurrentGeneration(RuntimeRole.Server);
                await Coordinator.DispatchAsync(new RoleStarted(server));
                await Coordinator.DispatchAsync(new RoleReady(server));
                var worker = Coordinator.CurrentGeneration(RuntimeRole.Worker);
                await Coordinator.DispatchAsync(new RoleStarted(worker));
                await Coordinator.DispatchAsync(new RoleReady(worker));
            }
            else
            {
                var worker = Coordinator.CurrentGeneration(RuntimeRole.Worker);
                await Coordinator.DispatchAsync(new RoleStarted(worker));
                await Coordinator.DispatchAsync(new RoleReady(worker));
            }

            Assert.Equal(RuntimeState.Ready, Coordinator.State);
        }

        public async Task AdvanceToDatabaseStartAsync(Guid operationId)
        {
            await Coordinator.DispatchAsync(new StartRequested(operationId));
            await Coordinator.DispatchAsync(new InstanceAcquired(operationId));
            await Coordinator.DispatchAsync(new PayloadValidated(operationId));
            await Coordinator.DispatchAsync(new RuntimePrepared(operationId));
            Assert.Equal(RuntimeState.StartingDatabase, Coordinator.State);
        }

        public async Task<TransitionResult> ExpectAsync(
            LifecycleEvent @event,
            RuntimeState expectedState,
            LifecycleCommandType expectedCommand)
        {
            var result = await Coordinator.DispatchAsync(@event);
            Assert.False(result.Ignored);
            Assert.Equal(expectedState, result.Current);
            Assert.Equal(expectedCommand, Assert.Single(result.Commands).Type);
            return result;
        }
    }
}
