using System.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Composition;

public sealed class RuntimeOrchestrator : IAsyncDisposable
{
    private readonly LifecycleCoordinator _coordinator;
    private readonly ILifecycleCommandExecutor _executor;
    private readonly ShutdownBudget _shutdownBudget;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly List<RuntimeState> _stateHistory = [];
    private readonly TaskCompletionSource<string> _fatal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public RuntimeOrchestrator(
        LifecycleCoordinator coordinator,
        ILifecycleCommandExecutor executor,
        ShutdownBudget? shutdownBudget = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _shutdownBudget = shutdownBudget ?? ShutdownBudget.Production;
    }

    public RuntimeState State => _coordinator.State;

    public IReadOnlyList<RuntimeState> StateHistory => _stateHistory;

    public event Action<RuntimeState>? StateChanged;

    internal Exception? LastFatalExceptionForTests { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RuntimeState.Ready)
            {
                return;
            }

            if (State != RuntimeState.Stopped)
            {
                throw new InvalidOperationException($"Cannot start from state '{State}'.");
            }

            var operationId = Guid.NewGuid();
            try
            {
                await ProcessAsync(
                    await _coordinator.DispatchAsync(new StartRequested(operationId), cancellationToken)
                    .ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                if (State != RuntimeState.Ready)
                {
                    throw new InvalidOperationException($"Startup ended in state '{State}'.");
                }
            }
            catch (Exception startupFailure)
            {
                try
                {
                    await _executor.RollbackAsync(operationId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(startupFailure, rollbackFailure);
                }

                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RuntimeState.Stopped)
            {
                return;
            }

            var operationId = Guid.NewGuid();
            var transition = await _coordinator.DispatchAsync(
                new StopRequested(operationId),
                cancellationToken).ConfigureAwait(false);
            if (transition.Ignored)
            {
                return;
            }

            PublishState(transition);
            var faulted = await ExecuteShutdownAsync(
                transition.Commands,
                operationId).ConfigureAwait(false);
            var release = transition.Commands.Single(
                command => command.Type == LifecycleCommandType.ReleaseInstance);
            var terminalEvent = faulted
                ? (LifecycleEvent)new ShutdownFailed(operationId, "shutdown-budget-exhausted")
                : new ShutdownCompleted(operationId);
            var terminalErrors = new List<Exception>();
            try
            {
                await ProcessAsync(
                    await _coordinator.DispatchAsync(terminalEvent, CancellationToken.None).ConfigureAwait(false),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not StackOverflowException and not OutOfMemoryException)
            {
                terminalErrors.Add(error);
            }

            try
            {
                await _executor.ExecuteAsync(release, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not StackOverflowException and not OutOfMemoryException)
            {
                terminalErrors.Add(error);
            }

            if (terminalErrors.Count == 1) throw terminalErrors[0];
            if (terminalErrors.Count > 1) throw new AggregateException(terminalErrors);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<bool> ExecuteShutdownAsync(
        IReadOnlyList<LifecycleCommand> commands,
        Guid operationId)
    {
        var stopwatch = Stopwatch.StartNew();
        var stageStarts = new Dictionary<RuntimeRole, TimeSpan>();
        var faulted = false;
        foreach (var command in commands.Where(command => command.Type != LifecycleCommandType.ReleaseInstance))
        {
            var role = ShutdownRole(command.Type);
            if (role is { } stageRole && !stageStarts.ContainsKey(stageRole))
            {
                stageStarts.Add(stageRole, stopwatch.Elapsed);
            }

            var remaining = Remaining(command.Type, stopwatch.Elapsed, stageStarts);
            using var stageCancellation = new CancellationTokenSource();
            if (remaining <= TimeSpan.Zero)
            {
                stageCancellation.Cancel();
            }
            else
            {
                stageCancellation.CancelAfter(remaining);
            }

            try
            {
                var result = await _executor.ExecuteAsync(command, stageCancellation.Token)
                    .ConfigureAwait(false);
                if (result is not null)
                {
                    var next = await _coordinator.DispatchAsync(result, CancellationToken.None).ConfigureAwait(false);
                    var transitionRemaining = Remaining(command.Type, stopwatch.Elapsed, stageStarts);
                    using var transitionCancellation = new CancellationTokenSource();
                    if (transitionRemaining <= TimeSpan.Zero) transitionCancellation.Cancel();
                    else transitionCancellation.CancelAfter(transitionRemaining);
                    await ProcessAsync(
                        next,
                        transitionCancellation.Token).ConfigureAwait(false);
                    if (State == RuntimeState.ReconcilingDatabaseStop)
                    {
                        faulted = true;
                    }
                }
            }
            catch (Exception error) when (error is not StackOverflowException and not OutOfMemoryException)
            {
                faulted = true;
            }
        }

        if (faulted)
        {
            var totalRemaining = _shutdownBudget.Total - stopwatch.Elapsed;
            if (totalRemaining > TimeSpan.Zero)
            {
                await Task.Delay(totalRemaining, CancellationToken.None).ConfigureAwait(false);
            }

            using var escalationCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await _executor.ExecuteAsync(
                new LifecycleCommand(
                    LifecycleCommandType.EscalateJob,
                    null,
                    operationId,
                    LifecycleDeadlineKey.None),
                    escalationCleanup.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not StackOverflowException and not OutOfMemoryException)
            {
                // Containment boundaries were closed before reconciliation;
                // terminal fault processing and ownership release must continue.
            }
        }

        return faulted;
    }

    private TimeSpan Remaining(
        LifecycleCommandType type,
        TimeSpan elapsed,
        IReadOnlyDictionary<RuntimeRole, TimeSpan> stageStarts)
    {
        var total = _shutdownBudget.Total - elapsed;
        var role = ShutdownRole(type);
        if (role is null)
        {
            return total;
        }

        var allocation = role.Value switch
        {
            RuntimeRole.Worker => _shutdownBudget.Worker,
            RuntimeRole.Server => _shutdownBudget.Server,
            RuntimeRole.Database => _shutdownBudget.Database,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        var stage = allocation - (elapsed - stageStarts[role.Value]);
        return total <= stage ? total : stage;
    }

    private static RuntimeRole? ShutdownRole(LifecycleCommandType type) => type switch
    {
        LifecycleCommandType.DrainWorker or LifecycleCommandType.StopWorker => RuntimeRole.Worker,
        LifecycleCommandType.StopServer => RuntimeRole.Server,
        LifecycleCommandType.StopDatabaseFast or LifecycleCommandType.ReconcileDatabaseStop => RuntimeRole.Database,
        _ => null,
    };

    public async Task RunUntilStoppedAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var canceled = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(canceled, _fatal.Task).ConfigureAwait(false);
        if (completed == _fatal.Task)
        {
            throw new AppHostExecutionException(await _fatal.Task.ConfigureAwait(false));
        }

        try
        {
            await canceled.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task HandleEventAsync(
        LifecycleEvent @event,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var inspected = _executor is IDependencyFailureInspector inspector
                    ? await inspector.InspectDependencyFailureAsync(@event, cancellationToken).ConfigureAwait(false)
                    : @event;
                await ProcessAsync(
                    await _coordinator.DispatchAsync(inspected, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                if (State == RuntimeState.Faulted && !_fatal.Task.IsCompleted)
                {
                    await FinalizeFatalContainmentAsync(
                        "background-recovery-failed",
                        []).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await EnterFatalRecoveryAsync(error).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task EnterFatalRecoveryAsync(Exception error)
    {
        const string reasonCode = "background-recovery-failed";
        var failures = new List<Exception> { error };
        try
        {
            var transition = await _coordinator.DispatchAsync(
                new RecoveryFailed(reasonCode),
                CancellationToken.None).ConfigureAwait(false);
            PublishState(transition);
            foreach (var command in transition.Commands)
            {
                await CaptureFatalCleanupAsync(command, failures).ConfigureAwait(false);
            }
        }
        catch (Exception transitionFailure) when (transitionFailure is not StackOverflowException and not OutOfMemoryException)
        {
            failures.Add(transitionFailure);
        }

        await FinalizeFatalContainmentAsync(reasonCode, failures).ConfigureAwait(false);
    }

    private async Task FinalizeFatalContainmentAsync(
        string reasonCode,
        List<Exception> failures)
    {
        using var escalationCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await CaptureFatalCleanupAsync(
            new LifecycleCommand(
                LifecycleCommandType.EscalateJob,
                null,
                Guid.NewGuid(),
                LifecycleDeadlineKey.None),
            failures,
            escalationCleanup.Token).ConfigureAwait(false);
        await CaptureFatalCleanupAsync(
            new LifecycleCommand(
                LifecycleCommandType.ReleaseInstance,
                null,
                Guid.NewGuid(),
                LifecycleDeadlineKey.None),
            failures).ConfigureAwait(false);
        LastFatalExceptionForTests = failures.Count == 1 ? failures[0] : new AggregateException(failures);
        _fatal.TrySetResult(reasonCode);
    }

    private async Task CaptureFatalCleanupAsync(
        LifecycleCommand command,
        ICollection<Exception> failures,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (cleanupFailure is not StackOverflowException and not OutOfMemoryException)
        {
            failures.Add(cleanupFailure);
        }
    }

    private async Task ProcessAsync(
        TransitionResult initial,
        CancellationToken cancellationToken)
    {
        var transitions = new Queue<TransitionResult>();
        transitions.Enqueue(initial);
        while (transitions.TryDequeue(out var transition))
        {
            PublishState(transition);
            foreach (var command in transition.Commands)
            {
                var nextEvent = await _executor.ExecuteAsync(command, cancellationToken)
                    .ConfigureAwait(false);
                if (nextEvent is not null)
                {
                    transitions.Enqueue(await _coordinator.DispatchAsync(nextEvent, cancellationToken)
                        .ConfigureAwait(false));
                }
            }
        }
    }

    private void PublishState(TransitionResult transition)
    {
        if (transition.Previous == transition.Current)
        {
            return;
        }

        _stateHistory.Add(transition.Current);
        StateChanged?.Invoke(transition.Current);
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (State != RuntimeState.Stopped)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _executor.DisposeAsync().ConfigureAwait(false);
        _operationGate.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}

public sealed record ShutdownBudget
{
    public static ShutdownBudget Production { get; } = new(
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20));

    public ShutdownBudget(
        TimeSpan total,
        TimeSpan worker,
        TimeSpan server,
        TimeSpan database)
    {
        if (total <= TimeSpan.Zero || worker <= TimeSpan.Zero || server <= TimeSpan.Zero || database <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(total));
        }

        if (worker + server + database > total)
        {
            throw new ArgumentException("Shutdown stage allocations cannot exceed the total budget.");
        }

        Total = total;
        Worker = worker;
        Server = server;
        Database = database;
    }

    public TimeSpan Total { get; }

    public TimeSpan Worker { get; }

    public TimeSpan Server { get; }

    public TimeSpan Database { get; }
}
