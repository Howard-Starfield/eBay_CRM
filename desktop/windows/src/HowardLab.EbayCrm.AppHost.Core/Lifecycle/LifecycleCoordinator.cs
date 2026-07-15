using System.Collections.Concurrent;
using HowardLab.EbayCrm.AppHost.Core.Time;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Lifecycle;

public sealed class LifecycleCoordinator
{
    private readonly IClock _clock;
    private readonly RestartBudget _restartBudget;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly ConcurrentDictionary<RuntimeRole, ProcessGeneration> _generations = [];
    private readonly HashSet<RuntimeRole> _ownedRoles = [];
    private readonly HashSet<FailureKey> _failures = [];
    private readonly HashSet<ProcessGeneration> _requestedExits = [];
    private readonly Dictionary<ProcessGeneration, RecoveryStopState> _recoveryStops = [];
    private Guid _currentOperationId;
    private ProcessGeneration? _reconcilingRole;
    private RoleReconciliationKind? _roleReconciliationKind;
    private bool _stopAccepted;
    private bool _faultEmitted;

    public LifecycleCoordinator(IClock clock, RestartBudget restartBudget)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _restartBudget = restartBudget ?? throw new ArgumentNullException(nameof(restartBudget));
    }

    public RuntimeState State { get; private set; } = RuntimeState.Stopped;

    public ProcessGeneration CurrentGeneration(RuntimeRole role)
    {
        return _generations.TryGetValue(role, out var generation)
            ? generation
            : throw new InvalidOperationException($"No generation exists for role '{role}'.");
    }

    public async ValueTask<TransitionResult> DispatchAsync(
        LifecycleEvent @event,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Transition(@event);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private TransitionResult Transition(LifecycleEvent @event)
    {
        var previous = State;
        if (@event.Generation is { } eventGeneration &&
            !IsCurrent(eventGeneration) &&
            !((@event is OperationTimedOut or Reconciled) &&
                _recoveryStops.ContainsKey(eventGeneration)))
        {
            return Ignored(previous, "StaleGeneration");
        }

        return @event switch
        {
            StartRequested value => Start(previous, value),
            StopRequested value => Stop(previous, value),
            InstanceAcquired value => AdvanceOperation(previous, value.OperationId, RuntimeState.AcquiringInstance, RuntimeState.ValidatingPayload, LifecycleCommandType.ValidatePayload, LifecycleDeadlineKey.PayloadValidation),
            PayloadValidated value => AdvanceOperation(previous, value.OperationId, RuntimeState.ValidatingPayload, RuntimeState.PreparingRuntime, LifecycleCommandType.PrepareRuntime, LifecycleDeadlineKey.RuntimePreparation),
            RuntimePrepared value => StartRoleAfterOperation(previous, value.OperationId, RuntimeState.PreparingRuntime, RuntimeRole.Database, RuntimeState.StartingDatabase, LifecycleCommandType.StartDatabase, LifecycleDeadlineKey.DatabaseStart),
            RoleStarted value => RoleStartedTransition(previous, value.Value),
            RoleReady value => RoleReadyTransition(previous, value.Value),
            MigrationCompleted value => StartRoleAfterOperation(previous, value.OperationId, RuntimeState.Migrating, RuntimeRole.Server, RuntimeState.StartingServer, LifecycleCommandType.StartServer, LifecycleDeadlineKey.ServerStart),
            RoleExited value => Recover(previous, value.Value),
            HealthFailed value => Recover(previous, value.Value),
            ControlDisconnected value => Recover(previous, value.Value),
            OperationTimedOut value => Timeout(previous, value),
            Reconciled value => Reconcile(previous, value),
            ShutdownCompleted value => ShutdownCompletedTransition(previous, value),
            ShutdownFailed value => ShutdownFailedTransition(previous, value),
            RecoveryFailed value => RecoveryFailedTransition(previous, value),
            _ => Ignored(previous, "UnexpectedEvent"),
        };
    }

    private TransitionResult Start(RuntimeState previous, StartRequested @event)
    {
        if (_stopAccepted)
        {
            return Ignored(previous, "StopAlreadyAccepted");
        }

        if (State != RuntimeState.Stopped)
        {
            return Ignored(previous, "AlreadyStarted");
        }

        ClearRoleReconciliation();
        _recoveryStops.Clear();
        _requestedExits.Clear();
        _currentOperationId = @event.OperationId;
        State = RuntimeState.AcquiringInstance;
        return Accepted(previous, Command(LifecycleCommandType.AcquireInstance, null, @event.OperationId, LifecycleDeadlineKey.InstanceAcquisition));
    }

    private TransitionResult Stop(RuntimeState previous, StopRequested @event)
    {
        if (State == RuntimeState.Stopped)
        {
            return Ignored(previous, "AlreadyStopped");
        }

        if (_stopAccepted)
        {
            return Ignored(previous, "StopAlreadyAccepted");
        }

        _stopAccepted = true;
        ClearRoleReconciliation();
        _recoveryStops.Clear();
        _requestedExits.Clear();
        _currentOperationId = @event.OperationId;
        RetargetOwnedGenerations(@event.OperationId);
        var commands = new List<LifecycleCommand>();
        if (_ownedRoles.Contains(RuntimeRole.Worker))
        {
            var worker = _generations[RuntimeRole.Worker];
            commands.Add(Command(LifecycleCommandType.DrainWorker, worker, @event.OperationId, LifecycleDeadlineKey.WorkerDrain));
            _requestedExits.Add(worker);
            commands.Add(Command(LifecycleCommandType.StopWorker, worker, @event.OperationId, LifecycleDeadlineKey.WorkerStop));
        }

        if (_ownedRoles.Contains(RuntimeRole.Server))
        {
            var server = _generations[RuntimeRole.Server];
            _requestedExits.Add(server);
            commands.Add(Command(LifecycleCommandType.StopServer, server, @event.OperationId, LifecycleDeadlineKey.ServerStop));
        }

        if (_ownedRoles.Contains(RuntimeRole.Database))
        {
            var database = _generations[RuntimeRole.Database];
            commands.Add(Command(LifecycleCommandType.StopDatabaseFast, database, @event.OperationId, LifecycleDeadlineKey.DatabaseStop));
        }

        commands.Add(Command(LifecycleCommandType.ReleaseInstance, null, @event.OperationId, LifecycleDeadlineKey.None));
        State = RuntimeState.Stopping;
        return Result(previous, commands, false, "Accepted");
    }

    private TransitionResult AdvanceOperation(
        RuntimeState previous,
        Guid operationId,
        RuntimeState expected,
        RuntimeState next,
        LifecycleCommandType commandType,
        LifecycleDeadlineKey deadlineKey)
    {
        if (operationId != _currentOperationId)
        {
            return Ignored(previous, "StaleOperation");
        }

        if (State != expected || _stopAccepted)
        {
            return Ignored(previous, _stopAccepted ? "StopAlreadyAccepted" : "UnexpectedEvent");
        }

        State = next;
        return Accepted(previous, Command(commandType, null, operationId, deadlineKey));
    }

    private TransitionResult StartRoleAfterOperation(
        RuntimeState previous,
        Guid operationId,
        RuntimeState expected,
        RuntimeRole role,
        RuntimeState next,
        LifecycleCommandType commandType,
        LifecycleDeadlineKey deadlineKey)
    {
        if (operationId != _currentOperationId)
        {
            return Ignored(previous, "StaleOperation");
        }

        if (State != expected)
        {
            return Ignored(previous, "UnexpectedEvent");
        }

        var generation = CreateGeneration(role, operationId);
        State = next;
        return Accepted(previous, Command(commandType, generation, operationId, deadlineKey));
    }

    private TransitionResult RoleStartedTransition(RuntimeState previous, ProcessGeneration generation)
    {
        var expected = generation.Role switch
        {
            RuntimeRole.Database => RuntimeState.StartingDatabase,
            RuntimeRole.Server => RuntimeState.StartingServer,
            RuntimeRole.Worker => RuntimeState.StartingWorker,
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
        if (State != expected)
        {
            return Ignored(previous, "UnexpectedEvent");
        }

        return WaitForRoleAfterStart(previous, generation);
    }

    private TransitionResult WaitForRoleAfterStart(RuntimeState previous, ProcessGeneration generation)
    {
        State = generation.Role switch
        {
            RuntimeRole.Database => RuntimeState.WaitingForDatabase,
            RuntimeRole.Server => RuntimeState.WaitingForServer,
            RuntimeRole.Worker => RuntimeState.WaitingForWorker,
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
        var type = generation.Role switch
        {
            RuntimeRole.Database => LifecycleCommandType.WaitForDatabase,
            RuntimeRole.Server => LifecycleCommandType.WaitForServer,
            RuntimeRole.Worker => LifecycleCommandType.WaitForWorker,
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
        var deadline = generation.Role switch
        {
            RuntimeRole.Database => LifecycleDeadlineKey.DatabaseReadiness,
            RuntimeRole.Server => LifecycleDeadlineKey.ServerReadiness,
            RuntimeRole.Worker => LifecycleDeadlineKey.WorkerReadiness,
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
        return Accepted(previous, Command(type, generation, generation.OperationId, deadline));
    }

    private TransitionResult RoleReadyTransition(RuntimeState previous, ProcessGeneration generation)
    {
        if (generation.Role == RuntimeRole.Database && State == RuntimeState.WaitingForDatabase)
        {
            _restartBudget.RecordStable(generation.Role, _clock.UtcNow);
            State = RuntimeState.Migrating;
            return Accepted(previous, Command(LifecycleCommandType.RunMigrations, null, generation.OperationId, LifecycleDeadlineKey.Migration));
        }

        if (generation.Role == RuntimeRole.Server && State == RuntimeState.WaitingForServer)
        {
            _restartBudget.RecordStable(generation.Role, _clock.UtcNow);
            var worker = CreateGeneration(RuntimeRole.Worker, _currentOperationId);
            State = RuntimeState.StartingWorker;
            return Accepted(previous, Command(LifecycleCommandType.StartWorker, worker, worker.OperationId, LifecycleDeadlineKey.WorkerStart));
        }

        if (generation.Role == RuntimeRole.Worker && State == RuntimeState.WaitingForWorker)
        {
            _restartBudget.RecordStable(generation.Role, _clock.UtcNow);
            State = RuntimeState.Ready;
            return Accepted(previous);
        }

        return Ignored(previous, "UnexpectedEvent");
    }

    private TransitionResult Recover(RuntimeState previous, ProcessGeneration generation)
    {
        if (_requestedExits.Contains(generation))
        {
            return Ignored(previous, "RequestedExit");
        }

        if (_stopAccepted || State is RuntimeState.Stopping or RuntimeState.Stopped or RuntimeState.Faulted)
        {
            return Ignored(previous, _stopAccepted ? "StopAlreadyAccepted" : "RecoveryNotAllowed");
        }

        var failure = new FailureKey(generation.Role, generation.Value, generation.OperationId, generation.Role);
        if (!_failures.Add(failure))
        {
            return Ignored(previous, "DuplicateFailure");
        }

        if (_restartBudget.TryConsume(generation.Role, _clock.UtcNow) == RestartBudgetResult.Exhausted)
        {
            return Fault(previous, generation, "RestartBudgetExhausted");
        }

        return generation.Role switch
        {
            RuntimeRole.Worker => RecoverWorker(previous),
            RuntimeRole.Server => RecoverServer(previous),
            RuntimeRole.Database => RecoverDatabase(previous, generation),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
    }

    private TransitionResult RecoverWorker(RuntimeState previous)
    {
        var operationId = Guid.NewGuid();
        _currentOperationId = operationId;
        var worker = CreateGeneration(RuntimeRole.Worker, operationId);
        State = RuntimeState.StartingWorker;
        return Accepted(previous, Command(LifecycleCommandType.StartWorker, worker, operationId, LifecycleDeadlineKey.WorkerStart));
    }

    private TransitionResult RecoverServer(RuntimeState previous)
    {
        var operationId = Guid.NewGuid();
        _currentOperationId = operationId;
        var server = CreateGeneration(RuntimeRole.Server, operationId);
        var commands = new List<LifecycleCommand>();
        if (_ownedRoles.Contains(RuntimeRole.Worker)
            && _generations.TryGetValue(RuntimeRole.Worker, out var previousWorker))
        {
            commands.Add(RecoveryStopCommand(
                LifecycleCommandType.StopWorker,
                previousWorker,
                operationId,
                LifecycleDeadlineKey.WorkerStop));
        }

        commands.Add(Command(LifecycleCommandType.StartServer, server, operationId, LifecycleDeadlineKey.ServerStart));
        State = RuntimeState.StartingServer;
        return Result(previous, commands, false, "Accepted");
    }

    private TransitionResult RecoverDatabase(RuntimeState previous, ProcessGeneration generation)
    {
        _currentOperationId = generation.OperationId;
        var commands = new List<LifecycleCommand>();
        if (_ownedRoles.Contains(RuntimeRole.Worker)
            && _generations.TryGetValue(RuntimeRole.Worker, out var worker))
        {
            commands.Add(RecoveryStopCommand(
                LifecycleCommandType.StopWorker,
                worker,
                generation.OperationId,
                LifecycleDeadlineKey.WorkerStop));
        }

        if (_ownedRoles.Contains(RuntimeRole.Server)
            && _generations.TryGetValue(RuntimeRole.Server, out var server))
        {
            commands.Add(RecoveryStopCommand(
                LifecycleCommandType.StopServer,
                server,
                generation.OperationId,
                LifecycleDeadlineKey.ServerStop));
        }

        commands.Add(Command(LifecycleCommandType.ReconcileDatabaseStart, generation, generation.OperationId, LifecycleDeadlineKey.DatabaseReconciliation));
        State = RuntimeState.ReconcilingDatabaseStart;
        return Result(previous, commands, false, "Accepted");
    }

    private TransitionResult Timeout(RuntimeState previous, OperationTimedOut @event)
    {
        if (@event.OperationId != @event.Value.OperationId)
        {
            return Ignored(previous, "StaleOperation");
        }

        if (_recoveryStops.TryGetValue(@event.Value, out var recoveryStopState))
        {
            if (recoveryStopState == RecoveryStopState.Pending)
            {
                _recoveryStops[@event.Value] = RecoveryStopState.Reconciling;
                return Accepted(previous, Command(
                    LifecycleCommandType.ReconcileRoleStop,
                    @event.Value,
                    @event.OperationId,
                    LifecycleDeadlineKey.RoleReconciliation));
            }

            _recoveryStops.Remove(@event.Value);
            _requestedExits.Remove(@event.Value);
            return FaultRecoveryStop(previous, @event.Value, "RoleStopReconciliationTimedOut");
        }

        if (@event.OperationId != _currentOperationId)
        {
            return Ignored(previous, "StaleOperation");
        }

        if (@event.Value.Role == RuntimeRole.Database)
        {
            return DatabaseTimeout(previous, @event);
        }

        if (@event.Value.Role is RuntimeRole.Server or RuntimeRole.Worker)
        {
            return RoleTimeout(previous, @event);
        }

        return Ignored(previous, "UnexpectedEvent");
    }

    private TransitionResult DatabaseTimeout(RuntimeState previous, OperationTimedOut @event)
    {
        if (State is RuntimeState.StartingDatabase or RuntimeState.WaitingForDatabase)
        {
            State = RuntimeState.ReconcilingDatabaseStart;
            return Accepted(previous, Command(LifecycleCommandType.ReconcileDatabaseStart, @event.Value, @event.OperationId, LifecycleDeadlineKey.DatabaseReconciliation));
        }

        if (State == RuntimeState.ReconcilingDatabaseStart)
        {
            return Fault(previous, @event.Value, "DatabaseStartReconciliationTimedOut");
        }

        if (State == RuntimeState.Stopping)
        {
            State = RuntimeState.ReconcilingDatabaseStop;
            return Accepted(previous, Command(LifecycleCommandType.ReconcileDatabaseStop, @event.Value, @event.OperationId, LifecycleDeadlineKey.DatabaseReconciliation));
        }

        if (State == RuntimeState.ReconcilingDatabaseStop)
        {
            State = RuntimeState.Faulted;
            if (_faultEmitted)
            {
                return Ignored(previous, "FaultAlreadyEmitted");
            }

            _faultEmitted = true;
            return Result(
                previous,
                [
                    Command(LifecycleCommandType.EscalateJob, @event.Value, @event.OperationId, LifecycleDeadlineKey.None),
                    Command(LifecycleCommandType.EnterFault, @event.Value, @event.OperationId, LifecycleDeadlineKey.None, "database-stop-reconciliation-timed-out"),
                ],
                false,
                "DatabaseStopReconciliationTimedOut");
        }

        return Ignored(previous, "UnexpectedEvent");
    }

    private TransitionResult RoleTimeout(RuntimeState previous, OperationTimedOut @event)
    {
        var startStateMatches = (@event.Value.Role == RuntimeRole.Server &&
                State is RuntimeState.StartingServer or RuntimeState.WaitingForServer)
            || (@event.Value.Role == RuntimeRole.Worker &&
                State is RuntimeState.StartingWorker or RuntimeState.WaitingForWorker);
        if (startStateMatches)
        {
            BeginRoleReconciliation(@event.Value, RoleReconciliationKind.Start);
            State = RuntimeState.ReconcilingRoleStart;
            return Accepted(previous, Command(
                LifecycleCommandType.ReconcileRoleStart,
                @event.Value,
                @event.OperationId,
                LifecycleDeadlineKey.RoleReconciliation));
        }

        if (State == RuntimeState.ReconcilingRoleStart)
        {
            if (!MatchesRoleReconciliation(@event.Value, RoleReconciliationKind.Start))
            {
                return Ignored(previous, "StaleReconciliation");
            }

            return Fault(previous, @event.Value, "RoleStartReconciliationTimedOut");
        }

        if (State == RuntimeState.Stopping)
        {
            BeginRoleReconciliation(@event.Value, RoleReconciliationKind.Stop);
            State = RuntimeState.ReconcilingRoleStop;
            return Accepted(previous, Command(
                LifecycleCommandType.ReconcileRoleStop,
                @event.Value,
                @event.OperationId,
                LifecycleDeadlineKey.RoleReconciliation));
        }

        if (State == RuntimeState.ReconcilingRoleStop)
        {
            if (!MatchesRoleReconciliation(@event.Value, RoleReconciliationKind.Stop))
            {
                return Ignored(previous, "StaleReconciliation");
            }

            State = RuntimeState.Faulted;
            ClearRoleReconciliation();
            if (_faultEmitted)
            {
                return Ignored(previous, "FaultAlreadyEmitted");
            }

            _faultEmitted = true;
            return Result(
                previous,
                [
                    Command(LifecycleCommandType.EscalateJob, @event.Value, @event.OperationId, LifecycleDeadlineKey.None),
                    Command(LifecycleCommandType.EnterFault, @event.Value, @event.OperationId, LifecycleDeadlineKey.None, "role-stop-reconciliation-timed-out"),
                ],
                false,
                "RoleStopReconciliationTimedOut");
        }

        return Ignored(previous, "UnexpectedEvent");
    }

    private TransitionResult Reconcile(RuntimeState previous, Reconciled @event)
    {
        if (_recoveryStops.TryGetValue(@event.Value, out var recoveryStopState))
        {
            if (recoveryStopState != RecoveryStopState.Reconciling)
            {
                return Ignored(previous, "RecoveryStopNotReconciling");
            }

            _recoveryStops.Remove(@event.Value);
            _requestedExits.Remove(@event.Value);
            return @event.State == ReconciledState.Stopped
                ? Accepted(previous)
                : FaultRecoveryStop(previous, @event.Value, "RoleStopReconciliationTimedOut");
        }

        if (State == RuntimeState.ReconcilingRoleStart)
        {
            if (!MatchesRoleReconciliation(@event.Value, RoleReconciliationKind.Start))
            {
                return Ignored(previous, "StaleReconciliation");
            }

            if (@event.State == ReconciledState.Running)
            {
                ClearRoleReconciliation();
                return WaitForRoleAfterStart(previous, @event.Value);
            }

            if (@event.State == ReconciledState.Stopped)
            {
                ClearRoleReconciliation();
                return Recover(previous, @event.Value);
            }

            return Accepted(previous);
        }

        if (State == RuntimeState.ReconcilingRoleStop)
        {
            if (!MatchesRoleReconciliation(@event.Value, RoleReconciliationKind.Stop))
            {
                return Ignored(previous, "StaleReconciliation");
            }

            if (@event.State == ReconciledState.Stopped)
            {
                ClearRoleReconciliation();
                State = RuntimeState.Stopping;
                return Accepted(previous);
            }

            return Accepted(previous);
        }

        if (State == RuntimeState.ReconcilingDatabaseStart)
        {
            if (@event.Value.Role != RuntimeRole.Database)
            {
                return Ignored(previous, "StaleReconciliation");
            }

            if (@event.State == ReconciledState.Running)
            {
                State = RuntimeState.WaitingForDatabase;
                return Accepted(previous, Command(LifecycleCommandType.WaitForDatabase, @event.Value, @event.Value.OperationId, LifecycleDeadlineKey.DatabaseReadiness));
            }

            if (@event.State == ReconciledState.Stopped)
            {
                var operationId = Guid.NewGuid();
                _currentOperationId = operationId;
                var database = CreateGeneration(RuntimeRole.Database, operationId);
                State = RuntimeState.StartingDatabase;
                return Accepted(previous, Command(
                    LifecycleCommandType.StartDatabase,
                    database,
                    operationId,
                    LifecycleDeadlineKey.DatabaseStart));
            }

            return Accepted(previous);
        }

        if (State == RuntimeState.ReconcilingDatabaseStop)
        {
            if (@event.Value.Role != RuntimeRole.Database)
            {
                return Ignored(previous, "StaleReconciliation");
            }

            if (@event.State == ReconciledState.Stopped)
            {
                State = RuntimeState.Stopped;
                ClearRoleReconciliation();
                _ownedRoles.Clear();
                return Accepted(previous, Command(LifecycleCommandType.ReleaseInstance, null, _currentOperationId, LifecycleDeadlineKey.None));
            }

            return Accepted(previous);
        }

        return Ignored(previous, "UnexpectedEvent");
    }

    private TransitionResult ShutdownCompletedTransition(RuntimeState previous, ShutdownCompleted @event)
    {
        if (@event.OperationId != _currentOperationId)
        {
            return Ignored(previous, "StaleOperation");
        }

        if (State != RuntimeState.Stopping)
        {
            return Ignored(previous, "UnexpectedEvent");
        }

        State = RuntimeState.Stopped;
        ClearRoleReconciliation();
        _ownedRoles.Clear();
        return Accepted(previous);
    }

    private TransitionResult ShutdownFailedTransition(RuntimeState previous, ShutdownFailed @event)
    {
        if (@event.OperationId != _currentOperationId)
        {
            return Ignored(previous, "StaleOperation");
        }

        if (State is not (RuntimeState.Stopping or RuntimeState.ReconcilingDatabaseStop or RuntimeState.ReconcilingRoleStop))
        {
            return Ignored(previous, "UnexpectedEvent");
        }

        State = RuntimeState.Faulted;
        ClearRoleReconciliation();
        if (_faultEmitted)
        {
            return Ignored(previous, "FaultAlreadyEmitted");
        }

        _faultEmitted = true;
        return Result(
            previous,
            [Command(LifecycleCommandType.EnterFault, null, @event.OperationId, LifecycleDeadlineKey.None, @event.ReasonCode)],
            false,
            @event.ReasonCode);
    }

    private TransitionResult RecoveryFailedTransition(RuntimeState previous, RecoveryFailed @event)
    {
        if (_stopAccepted || State is RuntimeState.Stopped or RuntimeState.Stopping or RuntimeState.Faulted)
        {
            return Ignored(previous, _stopAccepted ? "StopAlreadyAccepted" : "RecoveryNotAllowed");
        }

        return Fault(previous, null, @event.ReasonCode);
    }

    private TransitionResult Fault(RuntimeState previous, ProcessGeneration? generation, string reasonCode)
    {
        State = RuntimeState.Faulted;
        ClearRoleReconciliation();
        if (_faultEmitted)
        {
            return Ignored(previous, "FaultAlreadyEmitted");
        }

        _faultEmitted = true;
        return Result(previous, [Command(LifecycleCommandType.EnterFault, generation, _currentOperationId, LifecycleDeadlineKey.None, reasonCode)], false, reasonCode);
    }

    private TransitionResult FaultRecoveryStop(
        RuntimeState previous,
        ProcessGeneration generation,
        string reasonCode)
    {
        State = RuntimeState.Faulted;
        ClearRoleReconciliation();
        _recoveryStops.Clear();
        _requestedExits.Clear();
        if (_faultEmitted)
        {
            return Ignored(previous, "FaultAlreadyEmitted");
        }

        _faultEmitted = true;
        return Result(
            previous,
            [
                Command(LifecycleCommandType.EscalateJob, generation, generation.OperationId, LifecycleDeadlineKey.None),
                Command(LifecycleCommandType.EnterFault, generation, generation.OperationId, LifecycleDeadlineKey.None, "role-stop-reconciliation-timed-out"),
            ],
            false,
            reasonCode);
    }

    private void BeginRoleReconciliation(ProcessGeneration generation, RoleReconciliationKind kind)
    {
        _reconcilingRole = generation;
        _roleReconciliationKind = kind;
    }

    private bool MatchesRoleReconciliation(ProcessGeneration generation, RoleReconciliationKind kind)
    {
        return _reconcilingRole == generation
            && _roleReconciliationKind == kind
            && generation.OperationId == _currentOperationId;
    }

    private void ClearRoleReconciliation()
    {
        _reconcilingRole = null;
        _roleReconciliationKind = null;
    }

    private ProcessGeneration CreateGeneration(RuntimeRole role, Guid operationId)
    {
        var value = _generations.TryGetValue(role, out var current) ? current.Value + 1 : 1;
        _requestedExits.RemoveWhere(generation => generation.Role == role && generation.Value < value);
        foreach (var staleGeneration in _recoveryStops.Keys
            .Where(generation => generation.Role == role && generation.Value < value)
            .ToArray())
        {
            _recoveryStops.Remove(staleGeneration);
        }
        var generation = new ProcessGeneration(role, value, operationId);
        _generations[role] = generation;
        _ownedRoles.Add(role);
        return generation;
    }

    private void RetargetOwnedGenerations(Guid operationId)
    {
        foreach (var role in _ownedRoles)
        {
            _generations[role] = _generations[role] with { OperationId = operationId };
        }
    }

    private LifecycleCommand RecoveryStopCommand(
        LifecycleCommandType type,
        ProcessGeneration retainedGeneration,
        Guid operationId,
        LifecycleDeadlineKey deadlineKey)
    {
        _requestedExits.Add(retainedGeneration);
        var recoveryGeneration = retainedGeneration with { OperationId = operationId };
        _requestedExits.Add(recoveryGeneration);
        _recoveryStops[recoveryGeneration] = RecoveryStopState.Pending;
        return Command(type, recoveryGeneration, operationId, deadlineKey);
    }

    private bool IsCurrent(ProcessGeneration generation)
    {
        return _generations.TryGetValue(generation.Role, out var current) && current == generation;
    }

    private TransitionResult Accepted(RuntimeState previous, params LifecycleCommand[] commands)
    {
        return Result(previous, commands, false, "Accepted");
    }

    private TransitionResult Ignored(RuntimeState previous, string reasonCode)
    {
        return Result(previous, [], true, reasonCode);
    }

    private TransitionResult Result(RuntimeState previous, IReadOnlyList<LifecycleCommand> commands, bool ignored, string reasonCode)
    {
        return new TransitionResult(previous, State, commands, ignored, reasonCode);
    }

    private static LifecycleCommand Command(
        LifecycleCommandType type,
        ProcessGeneration? generation,
        Guid operationId,
        LifecycleDeadlineKey deadlineKey,
        string? reasonCode = null)
    {
        return new LifecycleCommand(type, generation, operationId, deadlineKey, reasonCode);
    }

    private readonly record struct FailureKey(
        RuntimeRole Role,
        long Value,
        Guid OperationId,
        RuntimeRole RecoveryKind);

    private enum RoleReconciliationKind
    {
        Start,
        Stop,
    }

    private enum RecoveryStopState
    {
        Pending,
        Reconciling,
    }
}
