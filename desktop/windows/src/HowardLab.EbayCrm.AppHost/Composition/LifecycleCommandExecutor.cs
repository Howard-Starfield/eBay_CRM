using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Migrations;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Control;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;
using HowardLab.EbayCrm.AppHost.Windows.Processes;

namespace HowardLab.EbayCrm.AppHost.Composition;

public interface ILifecycleCommandExecutor : IAsyncDisposable
{
    Task<LifecycleEvent?> ExecuteAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken = default);

    Task RollbackAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public interface IDependencyFailureInspector
{
    Task<LifecycleEvent> InspectDependencyFailureAsync(
        LifecycleEvent failure,
        CancellationToken cancellationToken = default);
}

public sealed class LifecycleCommandExecutor : ILifecycleCommandExecutor, IDependencyFailureInspector
{
    private static readonly TimeSpan ControlDeadline = TimeSpan.FromSeconds(10);
    private const int MaxHealthResponseBytes = 8 * 1024;
    private readonly AppHostOptions _options;
    private readonly ValidatedAppHostPayload _payload;
    private readonly IProfileRuntimeIdentityStore _identityStore;
    private readonly IProcessLauncher _launcher;
    private readonly OwnershipGatedDiagnosticSink _diagnosticSink;
    private readonly DiagnosticSecretRegistry _diagnosticSecrets;
    private readonly Action<SecretValue>? _diagnosticSecretObserver;
    private readonly IRoleOperationBoundary _roleOperationBoundary;
    private readonly RoleOperationDeadlines _roleOperationDeadlines;
    private readonly IRoleLaunchPlanProvider _roleLaunchPlanProvider;
    private readonly Func<HttpMessageHandler> _roleHealthMessageHandlerFactory;
    private UserProfileInstanceLock? _instanceLock;
    private Guid? _instanceOperationId;
    private ProfileRuntimeIdentity? _profileIdentity;
    private WindowsJobObject? _databaseJob;
    private PostgresRuntime? _databaseRuntime;
    private PostgresInstanceIdentity? _databaseIdentity;
    private Guid? _databaseOperationId;
    private RoleResource? _server;
    private RoleResource? _worker;
    private int _disposed;
    private int _faultDiagnosticWritten;
    private int _releaseInstanceCountForTests;
    private int _readinessTimeoutDiagnosticCountForTests;

    internal LifecycleCommandExecutor(
        AppHostOptions options,
        ValidatedAppHostPayload payload,
        IRoleLaunchPlanProvider roleLaunchPlanProvider,
        IProfileRuntimeIdentityStore? identityStore = null)
        : this(
            options,
            payload,
            identityStore,
            NoopRoleOperationBoundary.Instance,
            RoleOperationDeadlines.Production,
            CreateFallbackDiagnosticComposition(),
            roleLaunchPlanProvider)
    {
    }

    private LifecycleCommandExecutor(
        AppHostOptions options,
        ValidatedAppHostPayload payload,
        IProfileRuntimeIdentityStore? identityStore,
        IRoleOperationBoundary roleOperationBoundary,
        RoleOperationDeadlines roleOperationDeadlines,
        DiagnosticComposition diagnostics,
        IRoleLaunchPlanProvider roleLaunchPlanProvider)
        : this(
            options,
            payload,
            identityStore,
            roleOperationBoundary,
            roleOperationDeadlines,
            diagnostics.Sink,
            diagnostics.Registry,
            diagnosticSecretObserver: null,
            roleLaunchPlanProvider)
    {
    }

    internal LifecycleCommandExecutor(
        AppHostOptions options,
        ValidatedAppHostPayload payload,
        IProfileRuntimeIdentityStore? identityStore,
        IRoleOperationBoundary roleOperationBoundary,
        RoleOperationDeadlines roleOperationDeadlines,
        OwnershipGatedDiagnosticSink diagnosticSink,
        DiagnosticSecretRegistry diagnosticSecrets,
        Action<SecretValue>? diagnosticSecretObserver,
        IRoleLaunchPlanProvider roleLaunchPlanProvider,
        IProcessLauncher? launcher = null,
        Func<HttpMessageHandler>? roleHealthMessageHandlerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _identityStore = identityStore ?? new ProfileRuntimeIdentityStore();
        _roleOperationBoundary = roleOperationBoundary ?? throw new ArgumentNullException(nameof(roleOperationBoundary));
        _roleOperationDeadlines = roleOperationDeadlines ?? throw new ArgumentNullException(nameof(roleOperationDeadlines));
        _diagnosticSink = diagnosticSink ?? throw new ArgumentNullException(nameof(diagnosticSink));
        _diagnosticSecrets = diagnosticSecrets ?? throw new ArgumentNullException(nameof(diagnosticSecrets));
        _diagnosticSecretObserver = diagnosticSecretObserver;
        _roleLaunchPlanProvider = roleLaunchPlanProvider ??
            throw new ArgumentNullException(nameof(roleLaunchPlanProvider));
        _launcher = launcher ?? new WindowsProcessLauncher(_diagnosticSink);
        _roleHealthMessageHandlerFactory = roleHealthMessageHandlerFactory ?? CreateRoleHealthMessageHandler;
    }

    public Func<LifecycleEvent, CancellationToken, Task>? EventSink { get; set; }

    internal Action<RuntimeRole, WindowsJobObject, WindowsSupervisedProcess>? RoleLaunchedForTests { get; set; }

    internal Func<RuntimeRole, Task>? ReadinessValidatedForTests { get; set; }

    internal Func<RuntimeRole, Task>? ControlMonitorDisconnectObservedForTests { get; set; }

    internal Func<RuntimeRole, Task>? RoleTeardownStartedForTests { get; set; }

    internal Func<RuntimeRole, Task>? StopMonitoringCompletedForTests { get; set; }

    internal Func<RuntimeRole, Task>? RoleTransportFailureObservedForTests { get; set; }

    internal Func<RuntimeRole, Task, TimeSpan, Task>? NativeExitWaitForTests { get; set; }

    internal Func<string, ValueTask>? PayloadPostExitFailureObservedForTests { get; set; }

    internal Action? ReadinessLoserObservedForTests { get; set; }

    internal int ReleaseInstanceCountForTests => Volatile.Read(ref _releaseInstanceCountForTests);

    internal long DiagnosticSinkFailureCountForTests => _diagnosticSink.SinkFailureCount;

    internal long DiagnosticCompletionTimeoutCountForTests => _diagnosticSink.CompletionTimeoutCount;

    internal int ReadinessTimeoutDiagnosticCountForTests =>
        Volatile.Read(ref _readinessTimeoutDiagnosticCountForTests);

    internal static HttpClientHandler CreateRoleHealthMessageHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
    };

    internal ValueTask WriteDiagnosticForTestsAsync(DiagnosticEvent diagnosticEvent) =>
        _diagnosticSink.WriteAsync(diagnosticEvent);

    internal static void RetainWorkerJobHandleInRoleForTests(
        RuntimeRole role,
        WindowsJobObject job,
        WindowsSupervisedProcess process)
    {
        if (role == RuntimeRole.Worker)
        {
            job.DuplicateIntoProcessForTests(process.ProcessHandle);
        }
    }

    internal static bool TryTakeExactRoleResource<T>(ref T? current, T expected)
        where T : class =>
        ReferenceEquals(Interlocked.CompareExchange(ref current, null, expected), expected);

    internal Task DisposeRoleForTests(RuntimeRole role) => DisposeRoleAsync(role);

    internal AppHostRuntimeSnapshot SnapshotForTests() => new(
        _databaseIdentity?.ProcessId,
        _databaseIdentity?.Generation,
        _server?.Process.Identity.ProcessId,
        _server?.Generation,
        _worker?.Process.Identity.ProcessId,
        _worker?.Generation);

    internal void CrashRoleForTests(RuntimeRole role)
    {
        switch (role)
        {
            case RuntimeRole.Database:
                (_databaseJob ?? throw new AppHostExecutionException("postgres-job-missing")).Dispose();
                break;
            case RuntimeRole.Server:
                (_server ?? throw new AppHostExecutionException("server-resource-missing")).Job.Dispose();
                break;
            case RuntimeRole.Worker:
                (_worker ?? throw new AppHostExecutionException("worker-resource-missing")).Job.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(role));
        }
    }

    internal async Task DisconnectRoleControlForTestsAsync(RuntimeRole role) =>
        await GetRole(role).Channel.DisposeAsync().ConfigureAwait(false);

    internal async Task WaitForControlDisconnectForTestsAsync(RuntimeRole role)
    {
        var disconnected = await (GetRole(role).ControlDisconnectTask ??
            throw new AppHostExecutionException("role-control-watch-missing")).ConfigureAwait(false);
        if (!disconnected)
        {
            throw new AppHostExecutionException("role-control-watch-canceled");
        }
    }

    internal async Task CrashAllRolesSimultaneouslyForTestsAsync()
    {
        var database = _databaseIdentity ?? throw new AppHostExecutionException("postgres-identity-missing");
        var worker = _worker ?? throw new AppHostExecutionException("worker-resource-missing");
        var server = _server ?? throw new AppHostExecutionException("server-resource-missing");
        worker.Job.Dispose();
        server.Job.Dispose();
        (_databaseJob ?? throw new AppHostExecutionException("postgres-job-missing")).Dispose();
        var workerExit = worker.Process.Completion;
        var serverExit = server.Process.Completion;
        await Task.WhenAll(
            workerExit,
            serverExit,
            database.WaitForExitAsync()).ConfigureAwait(false);
    }

    public async Task<LifecycleEvent> InspectDependencyFailureAsync(
        LifecycleEvent failure,
        CancellationToken cancellationToken = default)
    {
        if (failure.Generation is not { } generation ||
            generation.Role == RuntimeRole.Database)
        {
            return failure;
        }

        // A dependency closure can collapse within the same scheduling turn.
        // Inspect retained handles for a short bounded coalescing window and
        // report the deepest failed role; no process-name polling is involved.
        var candidates = new List<Task>();
        if (_databaseIdentity is { } database && !database.HasExited)
        {
            candidates.Add(database.WaitForExitAsync());
        }

        if (generation.Role == RuntimeRole.Worker &&
            _server is { } server &&
            !server.Process.Completion.IsCompleted)
        {
            candidates.Add(server.Process.Completion);
        }

        if (candidates.Count > 0)
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(TimeSpan.FromMilliseconds(100));
            try
            {
                var winner = await Task.WhenAny(
                    Task.WhenAll(candidates),
                    Task.Delay(Timeout.InfiniteTimeSpan, window.Token)).ConfigureAwait(false);
                await winner.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_databaseIdentity is { HasExited: true } exitedDatabase)
        {
            return new RoleExited(exitedDatabase.Generation, -1);
        }

        if (generation.Role == RuntimeRole.Worker &&
            _server is { } exitedServer &&
            exitedServer.Process.Completion.IsCompleted)
        {
            return new RoleExited(exitedServer.Generation, -1);
        }

        return failure;
    }

    public async Task<LifecycleEvent?> ExecuteAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Type switch
        {
            LifecycleCommandType.AcquireInstance => await AcquireInstanceAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.ValidatePayload => ValidatePayload(command),
            LifecycleCommandType.PrepareRuntime => await PrepareRuntimeAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.StartDatabase => await StartDatabaseAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.WaitForDatabase => await WaitForDatabaseAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.RunMigrations => await RunMigrationsAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.StartServer or LifecycleCommandType.StartWorker =>
                await StartRoleAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.WaitForServer or LifecycleCommandType.WaitForWorker =>
                await WaitForRoleAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.DrainWorker => await DrainWorkerAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.StopWorker => await StopRoleAsync(RuntimeRole.Worker, command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.StopServer => await StopRoleAsync(RuntimeRole.Server, command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.StopDatabaseFast => await StopDatabaseAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.ReconcileDatabaseStart => await ReconcileDatabaseStartAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.ReconcileDatabaseStop => await ReconcileDatabaseStopAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.ReconcileRoleStart => await ReconcileRoleStartAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.ReconcileRoleStop => await ReconcileRoleStopAsync(command, cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.ReleaseInstance => await ReleaseInstanceAsync().ConfigureAwait(false),
            LifecycleCommandType.EscalateJob => await EscalateAsync(cancellationToken).ConfigureAwait(false),
            LifecycleCommandType.EnterFault => await EnterFaultAsync(command).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
    }

    private async Task<LifecycleEvent> AcquireInstanceAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        _instanceLock = await UserProfileInstanceLock.TryAcquireAsync(
            _payload.Profile,
            cancellationToken).ConfigureAwait(false)
            ?? throw new AppHostExecutionException("profile-already-owned");
        _instanceOperationId = command.OperationId;
        _diagnosticSink.Activate();
        return new InstanceAcquired(command.OperationId);
    }

    private LifecycleEvent ValidatePayload(LifecycleCommand command)
    {
        if (_options.RuntimeBackend == AppHostRuntimeBackend.PostgresDesktop)
        {
            throw new AppHostExecutionException("postgres-desktop-runtime-incomplete");
        }

        AppHostComposition.EnsurePortAvailable(_options.Port);
        return new PayloadValidated(command.OperationId);
    }

    private async Task<LifecycleEvent> PrepareRuntimeAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var existingCluster = Directory.Exists(_payload.PostgresPaths.DataDirectory) &&
            Directory.EnumerateFileSystemEntries(_payload.PostgresPaths.DataDirectory).Any();
        _profileIdentity = await _identityStore.OpenOrCreateAsync(
            _payload.Profile,
            existingCluster,
            cancellationToken).ConfigureAwait(false);
        RegisterDiagnosticSecret(_profileIdentity.Password);
        return new RuntimePrepared(command.OperationId);
    }

    private async Task<LifecycleEvent> StartDatabaseAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var generation = RequireGeneration(command, RuntimeRole.Database);
        await DisposeDatabaseAsync().ConfigureAwait(false);
        _databaseJob = WindowsJobObject.CreateKillOnClose();
        _databaseRuntime = CreateDatabaseRuntime(generation, _databaseJob);
        _databaseOperationId = command.OperationId;
        if (await _databaseRuntime.InitializeAsync(cancellationToken).ConfigureAwait(false) != PostgreSqlOperationOutcome.Completed)
        {
            throw new AppHostExecutionException("postgres-initialize-failed");
        }

        var result = await _databaseRuntime.StartAsync(cancellationToken).ConfigureAwait(false);
        if (result.Outcome == PostgreSqlOperationOutcome.Failed &&
            result.ReasonCode == "postmaster-pid-stale" &&
            await _databaseRuntime.RepairConclusiveStalePidFileAsync(cancellationToken).ConfigureAwait(false))
        {
            result = await _databaseRuntime.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (result.Outcome != PostgreSqlOperationOutcome.Completed || result.Identity is null)
        {
            if (result.Outcome == PostgreSqlOperationOutcome.TimedOutIndeterminate)
            {
                _databaseIdentity = result.Identity;
                return new OperationTimedOut(generation, command.OperationId);
            }

            throw new AppHostExecutionException(result.ReasonCode ?? "postgres-start-failed");
        }

        _databaseIdentity = result.Identity;
        return new RoleStarted(generation);
    }

    private PostgresRuntime CreateDatabaseRuntime(ProcessGeneration generation, WindowsJobObject job) =>
        new(
            _payload.PostgresLayout,
            _payload.PostgresPaths,
            generation,
            _options.Port,
            _profileIdentity?.Password ?? throw new AppHostExecutionException("profile-runtime-identity-unavailable"),
            _launcher,
            job,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(20));

    private async Task<LifecycleEvent> WaitForDatabaseAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var generation = RequireGeneration(command, RuntimeRole.Database);
        var probe = await RequireDatabaseRuntime().ProbeAsync(
            RequireDatabaseIdentity(),
            cancellationToken).ConfigureAwait(false);
        if (probe.SelectOne != "1" ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(probe.ReportedDataDirectory)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_payload.PostgresPaths.DataDirectory))))
        {
            throw new AppHostExecutionException("postgres-readiness-mismatch");
        }

        MonitorDatabase(generation, RequireDatabaseIdentity());
        return new RoleReady(generation);
    }

    private async Task<LifecycleEvent> RunMigrationsAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var runner = new PostgresMigrationRunner(
            RequireDatabaseRuntime(),
            RequireDatabaseIdentity(),
            new AtomicMigrationAttemptStore(_payload.Profile.CanonicalPath),
            _profileIdentity?.ClusterId ?? throw new AppHostExecutionException("profile-runtime-identity-unavailable"),
            new Version(1, 0, 0),
            0,
            1,
            _payload.MigrationPath,
            TimeSpan.FromMinutes(2));
        var result = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
        if (result.Outcome != MigrationOutcome.Succeeded)
        {
            throw new AppHostExecutionException(result.ReasonCode);
        }

        return new MigrationCompleted(command.OperationId);
    }

    private async Task<LifecycleEvent> StartRoleAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var generation = command.Generation
            ?? throw new AppHostExecutionException("role-generation-missing");
        if (generation.Role is not (RuntimeRole.Server or RuntimeRole.Worker))
        {
            throw new AppHostExecutionException("role-invalid");
        }

        var request = new RoleLaunchRequest(generation.Role, generation);
        RoleLaunchPlan plan;
        try
        {
            plan = _roleLaunchPlanProvider.Create(request);
            plan.ValidateFor(request);
        }
        catch (Exception)
        {
            throw new AppHostExecutionException("role-launch-plan-invalid");
        }

        await DisposeRoleAsync(generation.Role).ConfigureAwait(false);
        var channel = WindowsControlChannel.CreateBeforeLaunch(
            generation.Role,
            generation.Value,
            generation.OperationId,
            plan.BuildIdentity,
            ControlDeadline);
        var job = WindowsJobObject.CreateKillOnClose();
        LaunchedRoleProcess? launched = null;
        RoleResource? resource = null;
        try
        {
            var child = channel.CreateChildEnvironment();
            var environment = new Dictionary<string, string>(plan.Environment, StringComparer.OrdinalIgnoreCase);
            var secretEnvironment = new Dictionary<string, SecretValue>(
                plan.SecretEnvironment,
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in child.Environment)
            {
                if (!environment.TryAdd(pair.Key, pair.Value))
                {
                    throw new AppHostExecutionException("role-launch-plan-invalid");
                }
            }

            foreach (var pair in child.SecretEnvironment)
            {
                if (environment.ContainsKey(pair.Key) ||
                    !secretEnvironment.TryAdd(pair.Key, pair.Value))
                {
                    throw new AppHostExecutionException("role-launch-plan-invalid");
                }
            }

            foreach (var secret in secretEnvironment.Values)
            {
                RegisterDiagnosticSecret(secret);
            }

            var specification = new LaunchSpecification(
                generation.Role,
                generation,
                plan.ApplicationPath,
                plan.Arguments,
                plan.WorkingDirectory,
                environment,
                secretEnvironment,
                plan.OutputDrainTimeout);
            launched = await LaunchWithArtifactLeaseAsync(
                plan,
                specification,
                channel,
                job,
                cancellationToken).ConfigureAwait(false);

            if (launched.Process is not WindowsSupervisedProcess windowsProcess ||
                launched.PayloadPostExitBoundary is null ||
                launched.NativeExitObservation is null)
            {
                throw new AppHostExecutionException("role-process-type-invalid");
            }

            RoleLaunchedForTests?.Invoke(generation.Role, job, windowsProcess);
            resource = new RoleResource(
                generation,
                command.OperationId,
                plan.HealthPort!.Value,
                windowsProcess,
                channel,
                job,
                launched.NativeExitObservation,
                launched.PayloadPostExitBoundary);
            SetRole(resource);
            resource.AcceptTask = AcceptRoleAsync(resource);
            using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            commandDeadline.CancelAfter(_roleOperationDeadlines.StartCommand);
            try
            {
                await resource.AcceptTask.WaitAsync(commandDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !resource.RoleLifetimeCancellation.IsCancellationRequested &&
                commandDeadline.IsCancellationRequested)
            {
                return new OperationTimedOut(generation, command.OperationId);
            }

            return new RoleStarted(generation);
        }
        catch (Exception primaryError)
        {
            var cleanupErrors = new List<Exception>();
            if (resource is not null)
            {
                await CaptureCleanupAsync(
                    () => DisposeRoleAsync(resource),
                    cleanupErrors).ConfigureAwait(false);
            }
            else
            {
                if (launched?.Process is WindowsSupervisedProcess windowsProcess &&
                    launched.PayloadPostExitBoundary is not null &&
                    launched.NativeExitObservation is not null)
                {
                    await CaptureCleanupAsync(
                        () => CleanupUnownedWindowsChildAsync(
                            windowsProcess,
                            channel,
                            job,
                            launched.NativeExitObservation,
                            launched.PayloadPostExitBoundary),
                        cleanupErrors).ConfigureAwait(false);
                }
                else if (launched?.Process is { } process)
                {
                    await CaptureCleanupAsync(
                        () => process.DisposeAsync().AsTask(),
                        cleanupErrors).ConfigureAwait(false);

                    await CaptureCleanupAsync(
                        () => channel.DisposeAsync().AsTask(),
                        cleanupErrors).ConfigureAwait(false);
                    try
                    {
                        job.Dispose();
                    }
                    catch (Exception cleanupError)
                    {
                        cleanupErrors.Add(cleanupError);
                    }

                    if (cleanupErrors.Count == 0 && launched.PayloadLifetimeLease is not null)
                    {
                        await CaptureCleanupAsync(
                            () => DisposePayloadLifetimeLeaseAsync(launched.PayloadLifetimeLease),
                            cleanupErrors).ConfigureAwait(false);
                    }
                    // A non-Windows launcher result is a contract breach. If
                    // any containment cleanup is uncertain, deliberately retain
                    // the payload lease rather than release executable inputs
                    // while an unobserved process could still be alive.
                }
                else
                {
                    await CaptureCleanupAsync(
                        () => channel.DisposeAsync().AsTask(),
                        cleanupErrors).ConfigureAwait(false);
                    try
                    {
                        job.Dispose();
                    }
                    catch (Exception cleanupError)
                    {
                        cleanupErrors.Add(cleanupError);
                    }
                }
            }

            if (cleanupErrors.Count == 0)
            {
                ExceptionDispatchInfo.Capture(primaryError).Throw();
            }

            throw new AggregateException([primaryError, .. cleanupErrors]);
        }
    }

    private async ValueTask<LaunchedRoleProcess> LaunchWithArtifactLeaseAsync(
        RoleLaunchPlan plan,
        LaunchSpecification specification,
        WindowsControlChannel channel,
        WindowsJobObject job,
        CancellationToken cancellationToken)
    {
        IDisposable? payloadLifetimeLease;
        try
        {
            payloadLifetimeLease = plan.OpenPayloadLifetimeLease();
        }
        catch
        {
            throw new AppHostExecutionException("role-payload-trust-failed");
        }

        if (payloadLifetimeLease is null)
        {
            throw new AppHostExecutionException("role-launch-plan-invalid");
        }

        IDisposable? artifactLease;
        try
        {
            artifactLease = plan.OpenBootstrapArtifactLease();
        }
        catch (AppHostOptionsException error)
        {
            DisposePayloadLifetimeLeaseNoThrow(payloadLifetimeLease);
            throw new AppHostExecutionException(error.ReasonCode);
        }
        catch (Exception)
        {
            DisposePayloadLifetimeLeaseNoThrow(payloadLifetimeLease);
            throw new AppHostExecutionException("role-payload-trust-failed");
        }

        if (artifactLease is null)
        {
            DisposePayloadLifetimeLeaseNoThrow(payloadLifetimeLease);
            throw new AppHostExecutionException("role-launch-plan-invalid");
        }

        ISupervisedProcess? launchedProcess = null;
        ExceptionDispatchInfo? launchFailure = null;
        try
        {
            launchedProcess = await _launcher.LaunchAsync(
                specification,
                job,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            launchFailure = ExceptionDispatchInfo.Capture(error);
        }

        var windowsProcess = launchedProcess as WindowsSupervisedProcess;
        var payloadLifetimeBoundaryObservation = windowsProcess?.NativeExitObservation
            ?? (launchFailure?.SourceException as ProcessCleanupException)?
                .PayloadLifetimeBoundaryObservation;
        var payloadClosureVerifier = payloadLifetimeBoundaryObservation is null
            ? null
            : new OneShotPayloadClosureVerifier(plan.VerifyPayloadClosureAfterShutdown);
        var payloadPostExitBoundary = payloadLifetimeBoundaryObservation is null ||
            payloadClosureVerifier is null
            ? null
            : PayloadPostExitBoundary.Start(
                payloadLifetimeBoundaryObservation,
                payloadClosureVerifier,
                payloadLifetimeLease,
                RecordPayloadPostExitFailureAsync);
        if (payloadPostExitBoundary is not null)
        {
            payloadLifetimeLease = null;
        }

        try
        {
            artifactLease.Dispose();
        }
        catch (Exception) when (launchFailure is not null)
        {
            // Launch/cancellation is the authoritative failure. Lease details
            // are untrusted and must not replace or decorate that exception.
        }
        catch (Exception)
        {
            if (windowsProcess is not null && payloadPostExitBoundary is not null)
            {
                try
                {
                    await CleanupUnownedWindowsChildAsync(
                        windowsProcess,
                        channel,
                        job,
                        windowsProcess.NativeExitObservation,
                        payloadPostExitBoundary).ConfigureAwait(false);
                }
                catch
                {
                    // The enclosing role job remains the cleanup authority.
                }
            }
            else if (launchedProcess is not null)
            {
                var cleanupCertain = true;
                try
                {
                    await launchedProcess.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    cleanupCertain = false;
                }

                try
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    cleanupCertain = false;
                }

                try
                {
                    job.Dispose();
                }
                catch
                {
                    cleanupCertain = false;
                }

                if (cleanupCertain)
                {
                    DisposePayloadLifetimeLeaseNoThrow(payloadLifetimeLease);
                    payloadLifetimeLease = null;
                }
            }
            else
            {
                DisposePayloadLifetimeLeaseNoThrow(payloadLifetimeLease);
                payloadLifetimeLease = null;
            }

            throw new AppHostExecutionException("role-payload-trust-failed");
        }

        if (launchFailure is not null)
        {
            DisposePayloadLifetimeLeaseNoThrow(payloadLifetimeLease);
            launchFailure.Throw();
        }

        if (windowsProcess is not null && payloadPostExitBoundary is not null)
        {
            return new LaunchedRoleProcess(
                windowsProcess,
                windowsProcess.NativeExitObservation,
                payloadPostExitBoundary,
                PayloadLifetimeLease: null);
        }

        if (launchedProcess is null)
        {
            DisposePayloadLifetimeLeaseNoThrow(payloadLifetimeLease);
            throw new AppHostExecutionException("role-process-type-invalid");
        }

        var result = new LaunchedRoleProcess(
            launchedProcess,
            NativeExitObservation: null,
            PayloadPostExitBoundary: null,
            payloadLifetimeLease);
        payloadLifetimeLease = null;
        return result;
    }

    private async ValueTask RecordPayloadPostExitFailureAsync(string reasonCode)
    {
        var sanitizedReasonCode = string.Equals(
            reasonCode,
            "role-payload-trust-failed",
            StringComparison.Ordinal)
            ? reasonCode
            : "role-payload-trust-failed";
        try
        {
            if (PayloadPostExitFailureObservedForTests is { } observer)
            {
                await observer(sanitizedReasonCode).ConfigureAwait(false);
            }

            var diagnostic = DiagnosticEvent.Create("role.payload_post_exit_failed")
                .With("reason_code", DiagnosticField.ReasonCode(sanitizedReasonCode));
            await _diagnosticSink.WriteAsync(diagnostic).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics must never impede post-exit trust cleanup.
        }
    }

    private static Task DisposePayloadLifetimeLeaseAsync(IDisposable payloadLifetimeLease)
    {
        try
        {
            payloadLifetimeLease.Dispose();
            return Task.CompletedTask;
        }
        catch
        {
            return Task.FromException(new AppHostExecutionException("role-payload-trust-failed"));
        }
    }

    private static void DisposePayloadLifetimeLeaseNoThrow(IDisposable? payloadLifetimeLease)
    {
        try
        {
            payloadLifetimeLease?.Dispose();
        }
        catch
        {
            // A primary launch failure remains authoritative and sanitized.
        }
    }

    private async Task AcceptRoleAsync(RoleResource resource)
    {
        var roleLifetimeToken = resource.RoleLifetimeCancellation.Token;
        var acceptTask = resource.Channel.AcceptAsync(
            resource.Process,
            resource.Job,
            roleLifetimeToken);
        await _roleOperationBoundary.PauseAsync(
            RoleOperationBoundaryPoint.StartAcceptInFlight,
            resource.Generation,
            resource.StartupOperationId,
            acceptTask,
            roleLifetimeToken).ConfigureAwait(false);
        await acceptTask.ConfigureAwait(false);
        resource.Authenticated = true;
        resource.ControlDisconnectTask = WatchForReadinessControlLossAsync(
            resource.Channel,
            resource.MonitorCancellation.Token);
    }

    private async Task<LifecycleEvent> WaitForRoleAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var generation = command.Generation
            ?? throw new AppHostExecutionException("role-generation-missing");
        var resource = GetRole(generation.Role);
        if (resource.Generation != generation || resource.Process.Completion.IsCompleted)
        {
            throw new AppHostExecutionException("role-retained-identity-mismatch");
        }

        if (resource.ReadinessConfirmed)
        {
            var controlDisconnect = resource.ControlDisconnectTask
                ?? throw new AppHostExecutionException("role-control-watch-missing");
            if (controlDisconnect.IsCompleted &&
                await controlDisconnect.ConfigureAwait(false))
            {
                throw new AppHostExecutionException("role-control-disconnected-before-ready");
            }

            MonitorRole(resource);
            return new RoleReady(generation);
        }

        var outcome = await PollRoleReadinessAsync(
            resource,
            _roleOperationDeadlines.Readiness,
            cancellationToken).ConfigureAwait(false);
        if (outcome == RoleReadinessOutcome.TimedOut)
        {
            await RecordRoleReadinessTimeoutAsync(resource).ConfigureAwait(false);
            return new OperationTimedOut(generation, command.OperationId);
        }

        return outcome switch
        {
            RoleReadinessOutcome.Ready => CompleteRoleReadiness(resource, generation),
            RoleReadinessOutcome.ProcessExited => throw new AppHostExecutionException("role-process-exited-before-ready"),
            RoleReadinessOutcome.ControlDisconnected => throw new AppHostExecutionException("role-control-disconnected-before-ready"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
    }

    private LifecycleEvent CompleteRoleReadiness(RoleResource resource, ProcessGeneration generation)
    {
        resource.ReadinessConfirmed = true;
        MonitorRole(resource);
        return new RoleReady(generation);
    }

    private async Task RecordRoleReadinessTimeoutAsync(RoleResource resource)
    {
        if (!resource.TryRecordReadinessTimeout())
        {
            return;
        }

        var diagnostic = DiagnosticEvent.Create("role.readiness_timeout")
            .With("reason_code", DiagnosticField.ReasonCode("role-readiness-timeout"))
            .With("role", DiagnosticField.String(resource.Generation.Role.ToString()))
            .With("generation", DiagnosticField.Integer(resource.Generation.Value))
            .With("operation_id", DiagnosticField.Guid(resource.Generation.OperationId));
        await _diagnosticSink.WriteAsync(diagnostic).ConfigureAwait(false);
        Interlocked.Increment(ref _readinessTimeoutDiagnosticCountForTests);
    }

    private async Task<RoleReadinessOutcome> PollRoleReadinessAsync(
        RoleResource resource,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var http = new HttpClient(_roleHealthMessageHandlerFactory(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var controlLoss = resource.ControlDisconnectTask
            ?? throw new AppHostExecutionException("role-control-watch-missing");
        var observedNotReady = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resource.Process.Completion.IsCompleted)
            {
                return RoleReadinessOutcome.ProcessExited;
            }

            if (controlLoss.IsCompleted && await controlLoss.ConfigureAwait(false))
            {
                return RoleReadinessOutcome.ControlDisconnected;
            }

            var remaining = deadline - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return observedNotReady
                    ? RoleReadinessOutcome.TimedOut
                    : throw new AppHostExecutionException("role-health-request-timed-out");
            }

            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var requestReachedReadinessDeadline = remaining <= ControlDeadline;
            requestCancellation.CancelAfter(requestReachedReadinessDeadline ? remaining : ControlDeadline);
            using var request = CreateHealthRequest(resource);
            var requestTask = http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);
            var completed = await Task.WhenAny(
                requestTask,
                resource.Process.Completion,
                controlLoss).ConfigureAwait(false);
            if (completed != requestTask)
            {
                requestCancellation.Cancel();
                await ObserveCanceledRequestAsync(requestTask).ConfigureAwait(false);
                ReadinessLoserObservedForTests?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (resource.Process.Completion.IsCompleted)
                {
                    return RoleReadinessOutcome.ProcessExited;
                }

                if (await controlLoss.ConfigureAwait(false))
                {
                    return RoleReadinessOutcome.ControlDisconnected;
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await requestTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return observedNotReady && requestReachedReadinessDeadline
                    ? RoleReadinessOutcome.TimedOut
                    : throw new AppHostExecutionException("role-health-request-timed-out");
            }
            catch (HttpRequestException error)
            {
                throw new AppHostExecutionException("role-health-request-failed", error);
            }

            using (response)
            {
                var validationTask = ValidateHealthResponseAsync(
                    resource,
                    response,
                    requestCancellation.Token);
                completed = await Task.WhenAny(
                    validationTask,
                    resource.Process.Completion,
                    controlLoss).ConfigureAwait(false);
                if (completed != validationTask)
                {
                    requestCancellation.Cancel();
                    await ObserveCanceledValidationAsync(validationTask).ConfigureAwait(false);
                    ReadinessLoserObservedForTests?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (resource.Process.Completion.IsCompleted)
                    {
                        return RoleReadinessOutcome.ProcessExited;
                    }

                    if (await controlLoss.ConfigureAwait(false))
                    {
                        return RoleReadinessOutcome.ControlDisconnected;
                    }
                }

                RoleHealthStatus health;
                try
                {
                    health = await validationTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return observedNotReady && requestReachedReadinessDeadline
                        ? RoleReadinessOutcome.TimedOut
                        : throw new AppHostExecutionException("role-health-request-timed-out");
                }

                if (health == RoleHealthStatus.Ready)
                {
                    if (ReadinessValidatedForTests is { } readinessValidated)
                    {
                        await readinessValidated(resource.Generation.Role).ConfigureAwait(false);
                    }

                    if (resource.Process.Completion.IsCompleted)
                    {
                        return RoleReadinessOutcome.ProcessExited;
                    }

                    if (controlLoss.IsCompleted && await controlLoss.ConfigureAwait(false))
                    {
                        return RoleReadinessOutcome.ControlDisconnected;
                    }

                    return RoleReadinessOutcome.Ready;
                }

                observedNotReady = true;
            }

            remaining = deadline - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return RoleReadinessOutcome.TimedOut;
            }

            var delay = remaining < _roleOperationDeadlines.ReadinessPollInterval
                ? remaining
                : _roleOperationDeadlines.ReadinessPollInterval;
            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(delay, delayCancellation.Token);
            completed = await Task.WhenAny(
                delayTask,
                resource.Process.Completion,
                controlLoss).ConfigureAwait(false);
            if (completed != delayTask)
            {
                delayCancellation.Cancel();
                await ObserveCanceledDelayAsync(delayTask).ConfigureAwait(false);
                ReadinessLoserObservedForTests?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (resource.Process.Completion.IsCompleted)
                {
                    return RoleReadinessOutcome.ProcessExited;
                }

                if (await controlLoss.ConfigureAwait(false))
                {
                    return RoleReadinessOutcome.ControlDisconnected;
                }
            }

            await delayTask.ConfigureAwait(false);
        }
    }

    private static async Task<RoleHealthStatus> ValidateHealthResponseAsync(
        RoleResource resource,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new AppHostExecutionException("role-health-http-status-invalid");
        }

        var json = await ReadBoundedHealthBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (json.Length == 0)
        {
            throw new AppHostExecutionException("role-health-empty");
        }

        HealthPayload health;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            var expectedProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "protocolVersion",
                "buildIdentity",
                "generation",
                "generationNonce",
                "status",
                "activeWorkRemaining",
            };
            var observedProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!expectedProperties.Contains(property.Name) ||
                    !observedProperties.Add(property.Name))
                {
                    throw new JsonException();
                }
            }

            if (observedProperties.Count != expectedProperties.Count)
            {
                throw new JsonException();
            }

            health = JsonSerializer.Deserialize<HealthPayload>(
                json,
                ControlFrameCodec.SerializerOptions)
                ?? throw new AppHostExecutionException("role-health-empty");
        }
        catch (JsonException error)
        {
            throw new AppHostExecutionException("role-health-malformed", error);
        }

        var endpoint = resource.Channel.EndpointIdentity;
        if (health.ProtocolVersion != ControlProtocolConstants.CurrentVersion ||
            health.BuildIdentity != endpoint.ExpectedBuildIdentity ||
            health.Generation != resource.Generation.Value ||
            health.GenerationNonce != endpoint.CapabilityNonce ||
            health.ActiveWorkRemaining is < 0 or > ControlProtocolConstants.MaxActiveWorkRemaining)
        {
            throw new AppHostExecutionException("role-health-identity-mismatch");
        }

        return health.Status switch
        {
            "ready" => RoleHealthStatus.Ready,
            "not-ready" => RoleHealthStatus.NotReady,
            _ => throw new AppHostExecutionException("role-health-status-invalid"),
        };
    }

    private static async Task<byte[]> ReadBoundedHealthBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxHealthResponseBytes)
            {
                throw new AppHostExecutionException("role-health-malformed");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static async Task<bool> WatchForReadinessControlLossAsync(
        WindowsControlChannel channel,
        CancellationToken cancellationToken)
    {
        try
        {
            await channel.WaitForDisconnectAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static async Task ObserveCanceledRequestAsync(Task<HttpResponseMessage> request)
    {
        try
        {
            using var response = await request.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (
            error is not StackOverflowException and not OutOfMemoryException)
        {
        }
    }

    private static async Task ObserveCanceledDelayAsync(Task delay)
    {
        try
        {
            await delay.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ObserveCanceledValidationAsync(Task<RoleHealthStatus> validation)
    {
        try
        {
            _ = await validation.ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is not StackOverflowException and not OutOfMemoryException)
        {
        }
    }

    private static HttpRequestMessage CreateHealthRequest(RoleResource resource)
    {
        var endpoint = resource.Channel.EndpointIdentity;
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://127.0.0.1:{resource.HealthPort}/health");
        request.Headers.TryAddWithoutValidation("X-AppHost-Protocol", ControlProtocolConstants.CurrentVersion.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-AppHost-Build", endpoint.ExpectedBuildIdentity);
        request.Headers.TryAddWithoutValidation("X-AppHost-Generation", resource.Generation.Value.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-AppHost-Nonce", endpoint.CapabilityNonce);
        return request;
    }

    private async Task<LifecycleEvent?> DrainWorkerAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        if (_worker is null)
        {
            return null;
        }

        if (_worker.Drained)
        {
            return null;
        }

        await StopMonitoringAsync(_worker).ConfigureAwait(false);

        try
        {
            await _worker.Channel.SendAsync(
                Empty(ControlMessageType.Drain, command.OperationId, _worker.Generation),
                cancellationToken).ConfigureAwait(false);
            var expected = new[]
            {
                ControlMessageType.DrainAccepted,
                ControlMessageType.NoNewWorkAcquisition,
                ControlMessageType.ActiveWorkRemaining,
                ControlMessageType.Drained,
            };
            foreach (var type in expected)
            {
                var reply = await _worker.Channel.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (reply.Type != type || reply.OperationId != command.OperationId)
                {
                    throw new AppHostExecutionException("worker-drain-sequence-invalid");
                }
            }
        }
        catch (Exception error) when (IsRoleTransportFailure(error))
        {
            return await AwaitAuthoritativeRoleExitAfterTransportFailureWithFreshDeadlineAsync(
                _worker,
                command,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        _worker.Drained = true;
        _worker.DrainOperationId = command.OperationId;

        return null;
    }

    private async Task<LifecycleEvent?> StopRoleAsync(
        RuntimeRole role,
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var commandGeneration = RequireGeneration(command, role);
        var resource = role == RuntimeRole.Server ? _server : _worker;
        if (resource is null)
        {
            return null;
        }

        if (resource.Generation.Role != commandGeneration.Role ||
            resource.Generation.Value != commandGeneration.Value)
        {
            throw new AppHostExecutionException("role-retained-identity-mismatch");
        }

        await StopMonitoringAsync(resource).ConfigureAwait(false);

        if (resource.Process.HasExited)
        {
            await DisposeRoleAsync(resource).ConfigureAwait(false);
            return null;
        }

        if (role == RuntimeRole.Worker && !resource.Drained)
        {
            var drainResult = await DrainWorkerAsync(command, cancellationToken).ConfigureAwait(false);
            if (drainResult is not null)
            {
                return drainResult;
            }

            if (resource.Process.HasExited)
            {
                await DisposeRoleAsync(resource).ConfigureAwait(false);
                return null;
            }
        }

        try
        {
            await resource.Channel.SendAsync(
                Empty(ControlMessageType.Shutdown, command.OperationId, resource.Generation),
                cancellationToken).ConfigureAwait(false);
            var accepted = await resource.Channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (accepted.Type != ControlMessageType.ShutdownAccepted || accepted.OperationId != command.OperationId)
            {
                throw new AppHostExecutionException("role-shutdown-sequence-invalid");
            }
        }
        catch (Exception error) when (IsRoleTransportFailure(error))
        {
            return await AwaitAuthoritativeRoleExitAfterTransportFailureWithFreshDeadlineAsync(
                resource,
                command,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        resource.ShutdownOperationId = command.OperationId;
        resource.StopCompletionTask = CompleteRoleStopAsync(resource, command.OperationId);
        using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandDeadline.CancelAfter(_roleOperationDeadlines.StopCommand);
        try
        {
            await resource.StopCompletionTask.WaitAsync(commandDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsRoleTransportFailure(error))
        {
            return await AwaitAuthoritativeRoleExitAfterTransportFailureAsync(
                resource,
                command,
                cancellationToken,
                commandDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            commandDeadline.IsCancellationRequested &&
            !resource.RoleLifetimeCancellation.IsCancellationRequested)
        {
            return new OperationTimedOut(commandGeneration, command.OperationId);
        }

        await DisposeRoleAsync(resource).ConfigureAwait(false);
        return null;
    }

    private async Task<LifecycleEvent?> AwaitAuthoritativeRoleExitAfterTransportFailureWithFreshDeadlineAsync(
        RoleResource resource,
        LifecycleCommand command,
        CancellationToken callerCancellation)
    {
        using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        commandDeadline.CancelAfter(_roleOperationDeadlines.StopCommand);
        return await AwaitAuthoritativeRoleExitAfterTransportFailureAsync(
            resource,
            command,
            callerCancellation,
            commandDeadline.Token).ConfigureAwait(false);
    }

    private async Task<LifecycleEvent?> AwaitAuthoritativeRoleExitAfterTransportFailureAsync(
        RoleResource resource,
        LifecycleCommand command,
        CancellationToken callerCancellation,
        CancellationToken commandCancellation)
    {
        MarkRoleStopIndeterminate(resource, command.OperationId);
        if (RoleTransportFailureObservedForTests is { } transportFailureObserved)
        {
            await transportFailureObserved(resource.Generation.Role).ConfigureAwait(false);
        }

        try
        {
            if (!resource.Process.HasExited)
            {
                await resource.NativeExitObservation.WaitAsync(commandCancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            return new OperationTimedOut(
                RequireGeneration(command, resource.Generation.Role),
                command.OperationId);
        }

        await DisposeRoleAsync(resource).ConfigureAwait(false);
        return null;
    }

    private static void MarkRoleStopIndeterminate(RoleResource resource, Guid operationId)
    {
        resource.ShutdownOperationId = operationId;
        resource.StopCompletionTask = resource.NativeExitObservation;
    }

    internal static bool IsRoleTransportFailure(Exception error) =>
        error is not InvalidDataException &&
        (error is InvalidOperationException or IOException ||
         error is ControlProtocolException
         {
             Code: ControlProtocolErrorCode.TruncatedPrefix or ControlProtocolErrorCode.TruncatedPayload,
             InnerException: EndOfStreamException,
         });

    private async Task CompleteRoleStopAsync(RoleResource resource, Guid operationId)
    {
        var roleLifetimeToken = resource.RoleLifetimeCancellation.Token;
        var stoppedTask = resource.Channel.ReadAsync(roleLifetimeToken);
        await _roleOperationBoundary.PauseAsync(
            RoleOperationBoundaryPoint.StopAccepted,
            resource.Generation,
            operationId,
            stoppedTask,
            roleLifetimeToken).ConfigureAwait(false);
        var stopped = await stoppedTask.ConfigureAwait(false);
        if (stopped.Type != ControlMessageType.Stopped || stopped.OperationId != operationId)
        {
            throw new AppHostExecutionException("role-shutdown-sequence-invalid");
        }

        _ = await resource.Process.Completion.WaitAsync(roleLifetimeToken).ConfigureAwait(false);
    }

    private async Task<LifecycleEvent?> StopDatabaseAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        if (_databaseRuntime is null || _databaseIdentity is null)
        {
            return null;
        }

        var outcome = await _databaseRuntime.StopFastAsync(
            _databaseIdentity,
            cancellationToken).ConfigureAwait(false);
        if (outcome == PostgreSqlOperationOutcome.TimedOutIndeterminate)
        {
            return new OperationTimedOut(command.Generation ?? _databaseIdentity.Generation, command.OperationId);
        }

        if (outcome is not (PostgreSqlOperationOutcome.Completed or PostgreSqlOperationOutcome.ReconciledStopped))
        {
            throw new AppHostExecutionException("postgres-stop-failed");
        }

        await DisposeDatabaseAsync().ConfigureAwait(false);
        return null;
    }

    private async Task<LifecycleEvent> ReconcileDatabaseStartAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var generation = RequireGeneration(command, RuntimeRole.Database);
        if (_databaseRuntime is not null)
        {
            var result = await _databaseRuntime.ReconcileStartAsync(cancellationToken).ConfigureAwait(false);
            if (result.Outcome == PostgreSqlOperationOutcome.ReconciledRunning && result.Identity is not null)
            {
                _databaseIdentity = result.Identity;
                return new Reconciled(generation, ReconciledState.Running);
            }

            if (result.Outcome == PostgreSqlOperationOutcome.TimedOutIndeterminate)
            {
                _databaseIdentity = result.Identity ?? _databaseIdentity;
                return new OperationTimedOut(generation, command.OperationId);
            }

            if (result.Outcome != PostgreSqlOperationOutcome.ReconciledStopped)
            {
                throw new AppHostExecutionException(result.ReasonCode ?? "postgres-start-reconciliation-failed");
            }
        }

        await DisposeDatabaseAsync().ConfigureAwait(false);
        return new Reconciled(generation, ReconciledState.Stopped);
    }

    private async Task<LifecycleEvent> ReconcileDatabaseStopAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var generation = RequireGeneration(command, RuntimeRole.Database);
        var outcome = _databaseRuntime is null || _databaseIdentity is null
            ? PostgreSqlOperationOutcome.ReconciledStopped
            : await _databaseRuntime.ReconcileStopAsync(
                _databaseIdentity,
                cancellationToken).ConfigureAwait(false);
        if (outcome == PostgreSqlOperationOutcome.ReconciledStopped)
        {
            await DisposeDatabaseAsync().ConfigureAwait(false);
            return new Reconciled(generation, ReconciledState.Stopped);
        }

        return new Reconciled(generation, ReconciledState.Unknown);
    }

    private async Task<LifecycleEvent> ReconcileRoleStartAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var generation = command.Generation
            ?? throw new AppHostExecutionException("role-generation-missing");
        if (generation.Role is not (RuntimeRole.Server or RuntimeRole.Worker))
        {
            throw new AppHostExecutionException("role-invalid");
        }

        var resource = generation.Role == RuntimeRole.Server ? _server : _worker;
        if (resource is null || resource.Generation != generation)
        {
            return new Reconciled(generation, ReconciledState.Stopped);
        }

        var acceptTask = resource.AcceptTask
            ?? throw new AppHostExecutionException("role-accept-task-missing");
        if (resource.Process.Completion.IsCompleted)
        {
            await DisposeRoleAsync(resource).ConfigureAwait(false);
            return new Reconciled(generation, ReconciledState.Stopped);
        }

        using var reconciliationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reconciliationDeadline.CancelAfter(_roleOperationDeadlines.Reconciliation);
        try
        {
            await acceptTask.WaitAsync(reconciliationDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            !resource.RoleLifetimeCancellation.IsCancellationRequested &&
            reconciliationDeadline.IsCancellationRequested)
        {
            if (acceptTask.IsCompleted || resource.Process.Completion.IsCompleted)
            {
                await DisposeRoleAsync(resource).ConfigureAwait(false);
                return new Reconciled(generation, ReconciledState.Stopped);
            }

            return new OperationTimedOut(generation, command.OperationId);
        }
        catch (Exception) when (
            acceptTask.IsCompleted || resource.Process.Completion.IsCompleted)
        {
            await DisposeRoleAsync(resource).ConfigureAwait(false);
            return new Reconciled(generation, ReconciledState.Stopped);
        }

        if (!resource.Authenticated || resource.Process.Completion.IsCompleted)
        {
            await DisposeRoleAsync(resource).ConfigureAwait(false);
            return new Reconciled(generation, ReconciledState.Stopped);
        }

        var remaining = _roleOperationDeadlines.Reconciliation - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return new OperationTimedOut(generation, command.OperationId);
        }

        var readiness = await PollRoleReadinessAsync(
            resource,
            remaining,
            cancellationToken).ConfigureAwait(false);
        if (readiness == RoleReadinessOutcome.Ready)
        {
            resource.ReadinessConfirmed = true;
            MonitorRole(resource);
            return new Reconciled(generation, ReconciledState.Running);
        }

        if (readiness == RoleReadinessOutcome.TimedOut)
        {
            await RecordRoleReadinessTimeoutAsync(resource).ConfigureAwait(false);
            return new OperationTimedOut(generation, command.OperationId);
        }

        await DisposeRoleAsync(resource).ConfigureAwait(false);
        return new Reconciled(generation, ReconciledState.Stopped);
    }

    private async Task<LifecycleEvent> ReconcileRoleStopAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var generation = command.Generation
            ?? throw new AppHostExecutionException("role-generation-missing");
        if (generation.Role is not (RuntimeRole.Server or RuntimeRole.Worker))
        {
            throw new AppHostExecutionException("role-invalid");
        }

        var resource = generation.Role == RuntimeRole.Server ? _server : _worker;
        if (resource is null ||
            resource.Generation.Role != generation.Role ||
            resource.Generation.Value != generation.Value)
        {
            return new Reconciled(generation, ReconciledState.Stopped);
        }

        if (resource.ShutdownOperationId != command.OperationId)
        {
            throw new AppHostExecutionException("role-shutdown-operation-mismatch");
        }

        var stopCompletionTask = resource.StopCompletionTask
            ?? throw new AppHostExecutionException("role-stop-task-missing");
        using var reconciliationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reconciliationDeadline.CancelAfter(_roleOperationDeadlines.Reconciliation);
        try
        {
            await stopCompletionTask.WaitAsync(reconciliationDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            reconciliationDeadline.IsCancellationRequested &&
            !resource.RoleLifetimeCancellation.IsCancellationRequested)
        {
            return new Reconciled(generation, ReconciledState.Unknown);
        }

        await DisposeRoleAsync(resource).ConfigureAwait(false);
        return new Reconciled(generation, ReconciledState.Stopped);
    }

    private async Task<LifecycleEvent?> ReleaseInstanceAsync()
    {
        var errors = new List<Exception>();
        await CaptureCleanupAsync(
            () => _diagnosticSink.DisposeAsync().AsTask(),
            errors).ConfigureAwait(false);
        if (_instanceLock is not null)
        {
            await CaptureCleanupAsync(
                () => _instanceLock.DisposeAsync().AsTask(),
                errors).ConfigureAwait(false);
            _instanceLock = null;
            _instanceOperationId = null;
            Interlocked.Increment(ref _releaseInstanceCountForTests);
        }

        ThrowCleanupErrors(errors);
        return null;
    }

    private Task<LifecycleEvent?> EnterFaultAsync(LifecycleCommand command)
    {
        if (Interlocked.Exchange(ref _faultDiagnosticWritten, 1) != 0)
        {
            return Task.FromResult<LifecycleEvent?>(null);
        }

        var path = Path.Combine(_payload.PostgresPaths.RuntimeDirectory, "apphost-fault-v1.json");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(_payload.PostgresPaths.RuntimeDirectory);
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                version = 1,
                operationId = command.OperationId,
                reasonCode = command.ReasonCode ?? "apphost-faulted",
            });
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return Task.FromResult<LifecycleEvent?>(null);
    }

    private async Task<LifecycleEvent?> EscalateAsync(CancellationToken cancellationToken)
    {
        var worker = Interlocked.Exchange(ref _worker, null);
        var server = Interlocked.Exchange(ref _server, null);
        var roleResources = new[] { worker, server }
            .Where(value => value is not null)
            .Cast<RoleResource>()
            .ToArray();
        var errors = new List<Exception>();

        // Close every containment boundary before awaiting any descendant.
        foreach (var resource in roleResources)
        {
            try
            {
                resource.RoleLifetimeCancellation.Cancel();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                resource.MonitorCancellation.Cancel();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                var termination = resource.Job.TerminateTree(exitCode: 1);
                if (!termination.Succeeded)
                {
                    errors.Add(new AppHostExecutionException(
                        $"role-job-termination-failed-{termination.ErrorCode.ToString(CultureInfo.InvariantCulture)}"));
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        foreach (var resource in roleResources)
        {
            try { resource.Job.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        try { _databaseJob?.Dispose(); } catch (Exception error) { errors.Add(error); }

        var roleTeardowns = new List<Task>(roleResources.Length);
        foreach (var resource in roleResources)
        {
            roleTeardowns.Add(resource.GetOrStartTeardown(
                () => EscalateRoleResourceCoreAsync(resource, cancellationToken)));
        }

        if (_databaseIdentity is { } identity && _databaseRuntime is { } runtime)
        {
            await CaptureCleanupAsync(
                () => identity.WaitForExitAsync().WaitAsync(cancellationToken),
                errors).ConfigureAwait(false);
            if (identity.HasExited)
            {
                await CaptureCleanupAsync(
                    async () => { _ = await runtime.RepairExitedRetainedPidFileAsync(identity, cancellationToken).ConfigureAwait(false); },
                    errors).ConfigureAwait(false);
            }
        }

        try
        {
            _databaseRuntime?.ForceCloseAfterJobClose();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        try
        {
            _databaseIdentity?.Dispose();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        _databaseRuntime = null;
        _databaseIdentity = null;
        _databaseJob = null;
        _databaseOperationId = null;
        foreach (var teardown in roleTeardowns)
        {
            await CaptureCleanupAsync(() => teardown, errors).ConfigureAwait(false);
        }

        ThrowCleanupErrors(errors);
        return null;
    }

    private async Task EscalateRoleResourceCoreAsync(
        RoleResource resource,
        CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        resource.RoleLifetimeCancellation.Cancel();
        await CaptureCleanupAsync(
            () => StopMonitoringAsync(resource),
            errors).ConfigureAwait(false);
        try
        {
            resource.Channel.ForceCloseAfterJobClose();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        try
        {
            resource.Process.TerminateAndForceCloseAfterJobClose(cancellationToken);
        }
        catch (Exception error)
        {
            errors.Add(error);
            try
            {
                resource.Process.ForceCloseAfterJobClose();
            }
            catch (Exception forceCloseError)
            {
                errors.Add(forceCloseError);
            }
        }

        resource.DisposeMonitorCancellationWhenQuiescent();
        resource.DisposeRoleLifetimeCancellationWhenQuiescent();
        await ConfirmNativeExitAndVerifyPayloadAsync(resource, errors).ConfigureAwait(false);
        ThrowCleanupErrors(errors);
    }

    public async Task RollbackAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = new List<Exception>();
        if (_worker?.StartupOperationId == operationId)
        {
            await CaptureCleanupAsync(() => DisposeRoleAsync(RuntimeRole.Worker), errors).ConfigureAwait(false);
        }

        if (_server?.StartupOperationId == operationId)
        {
            await CaptureCleanupAsync(() => DisposeRoleAsync(RuntimeRole.Server), errors).ConfigureAwait(false);
        }

        if (_databaseOperationId == operationId)
        {
            await CaptureCleanupAsync(DisposeDatabaseAsync, errors).ConfigureAwait(false);
        }

        if (_instanceOperationId == operationId)
        {
            await CaptureCleanupAsync(
                async () => { _ = await ReleaseInstanceAsync().ConfigureAwait(false); },
                errors).ConfigureAwait(false);
        }

        ThrowCleanupErrors(errors);
    }

    private void MonitorRole(RoleResource resource)
    {
        if (!resource.TryStartMonitoring())
        {
            return;
        }

        var monitorToken = resource.MonitorCancellation.Token;
        _ = ObserveAsync(resource.Process.Completion, resource.Generation);
        resource.ControlMonitor = ObserveControlAsync(resource);
        resource.HealthMonitor = ObserveHealthAsync(resource, monitorToken);
    }

    private async Task ObserveControlAsync(RoleResource resource)
    {
        var disconnected = await (resource.ControlDisconnectTask ??
            throw new AppHostExecutionException("role-control-watch-missing")).ConfigureAwait(false);
        if (disconnected && !resource.MonitorCancellation.IsCancellationRequested)
        {
            if (ControlMonitorDisconnectObservedForTests is { } observed)
            {
                await observed(resource.Generation.Role).ConfigureAwait(false);
            }

            if (EventSink is { } sink)
            {
                _ = sink(new ControlDisconnected(resource.Generation), CancellationToken.None);
            }
        }
    }

    private async Task ObserveHealthAsync(RoleResource resource, CancellationToken monitorToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(monitorToken).ConfigureAwait(false))
            {
                using var http = new HttpClient(
                    _roleHealthMessageHandlerFactory(),
                    disposeHandler: true)
                {
                    Timeout = ControlDeadline,
                };
                using var request = CreateHealthRequest(resource);
                using var response = await http.SendAsync(request, monitorToken).ConfigureAwait(false);
                var health = await ValidateHealthResponseAsync(
                    resource,
                    response,
                    monitorToken).ConfigureAwait(false);
                if (health != RoleHealthStatus.Ready)
                {
                    throw new AppHostExecutionException("role-health-status-invalid");
                }
            }
        }
        catch (OperationCanceledException) when (monitorToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (!monitorToken.IsCancellationRequested)
        {
            if (EventSink is { } sink)
            {
                _ = sink(new HealthFailed(resource.Generation, "role-health-monitor-failed"), CancellationToken.None);
            }
        }
    }

    private async Task StopMonitoringAsync(RoleResource resource)
    {
        resource.MonitorCancellation.Cancel();
        var monitors = new[]
            {
                resource.ControlDisconnectTask,
                resource.ControlMonitor,
                resource.HealthMonitor,
            }
            .Where(task => task is not null)
            .Cast<Task>();
        await Task.WhenAll(monitors).ConfigureAwait(false);
        if (StopMonitoringCompletedForTests is { } completed)
        {
            await completed(resource.Generation.Role).ConfigureAwait(false);
        }
    }

    private void MonitorDatabase(ProcessGeneration generation, PostgresInstanceIdentity identity) =>
        _ = ObserveDatabaseAsync(generation, identity);

    private async Task ObserveDatabaseAsync(ProcessGeneration generation, PostgresInstanceIdentity identity)
    {
        try
        {
            await identity.WaitForExitAsync().ConfigureAwait(false);
            if (EventSink is { } sink)
            {
                _ = sink(
                    new RoleExited(generation, -1),
                    CancellationToken.None);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ObserveAsync(Task<int> completion, ProcessGeneration generation)
    {
        try
        {
            var exitCode = await completion.ConfigureAwait(false);
            if (EventSink is { } sink)
            {
                _ = sink(
                    new RoleExited(generation, exitCode),
                    CancellationToken.None);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task DisposeRoleAsync(RuntimeRole role)
    {
        var resource = role == RuntimeRole.Server
            ? Volatile.Read(ref _server)
            : Volatile.Read(ref _worker);
        if (resource is null)
        {
            return;
        }

        await DisposeRoleAsync(resource).ConfigureAwait(false);
    }

    private async Task DisposeRoleAsync(RoleResource resource)
    {
        try
        {
            await DisposeRoleResourceAsync(resource).ConfigureAwait(false);
        }
        finally
        {
            _ = resource.Generation.Role == RuntimeRole.Server
                ? TryTakeExactRoleResource(ref _server, resource)
                : TryTakeExactRoleResource(ref _worker, resource);
        }
    }

    private Task DisposeRoleResourceAsync(RoleResource resource) =>
        resource.GetOrStartTeardown(() => DisposeRoleResourceCoreAsync(resource));

    private async Task DisposeRoleResourceCoreAsync(RoleResource resource)
    {
        var errors = new List<Exception>();
        if (RoleTeardownStartedForTests is { } started)
        {
            await started(resource.Generation.Role).ConfigureAwait(false);
        }

        resource.RoleLifetimeCancellation.Cancel();
        await CaptureCleanupAsync(
            () => StopMonitoringAsync(resource),
            errors).ConfigureAwait(false);
        await CaptureCleanupAsync(
            () => resource.Channel.DisposeAsync().AsTask(),
            errors).ConfigureAwait(false);
        await CaptureCleanupAsync(
            () => resource.Process.DisposeAsync().AsTask(),
            errors).ConfigureAwait(false);
        try
        {
            resource.Job.Dispose();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        resource.DisposeMonitorCancellationWhenQuiescent();
        resource.DisposeRoleLifetimeCancellationWhenQuiescent();
        await ConfirmNativeExitAndVerifyPayloadAsync(resource, errors).ConfigureAwait(false);
        ThrowCleanupErrors(errors);
    }

    private async Task ConfirmNativeExitAndVerifyPayloadAsync(
        RoleResource resource,
        ICollection<Exception> errors)
    {
        try
        {
            await WaitForNativeExitAsync(
                resource.Generation.Role,
                resource.NativeExitObservation).ConfigureAwait(false);
        }
        catch
        {
            errors.Add(new AppHostExecutionException("role-process-exit-unconfirmed"));
            return;
        }

        await CaptureCleanupAsync(
            () => resource.PayloadPostExitBoundary,
            errors).ConfigureAwait(false);
    }

    private async Task CleanupUnownedWindowsChildAsync(
        WindowsSupervisedProcess process,
        WindowsControlChannel channel,
        WindowsJobObject job,
        Task nativeExitObservation,
        Task payloadPostExitBoundary)
    {
        var errors = new List<Exception>();
        await CaptureCleanupAsync(
            () => channel.DisposeAsync().AsTask(),
            errors).ConfigureAwait(false);
        await CaptureCleanupAsync(
            () => process.DisposeAsync().AsTask(),
            errors).ConfigureAwait(false);
        try
        {
            job.Dispose();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        try
        {
            await WaitForNativeExitAsync(
                process.Identity.Role,
                nativeExitObservation).ConfigureAwait(false);
        }
        catch
        {
            errors.Add(new AppHostExecutionException("role-process-exit-unconfirmed"));
            ThrowCleanupErrors(errors);
            return;
        }

        await CaptureCleanupAsync(
            () => payloadPostExitBoundary,
            errors).ConfigureAwait(false);
        ThrowCleanupErrors(errors);
    }

    private Task WaitForNativeExitAsync(RuntimeRole role, Task nativeExitObservation) =>
        NativeExitWaitForTests is { } wait
            ? wait(role, nativeExitObservation, _roleOperationDeadlines.StopCommand)
            : nativeExitObservation.WaitAsync(_roleOperationDeadlines.StopCommand);

    private async Task DisposeDatabaseAsync()
    {
        var errors = new List<Exception>();
        if (_databaseRuntime is not null)
        {
            await CaptureCleanupAsync(
                () => _databaseRuntime.DisposeAsync().AsTask(),
                errors).ConfigureAwait(false);
            _databaseRuntime = null;
        }

        try
        {
            _databaseIdentity?.Dispose();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        _databaseIdentity = null;
        try
        {
            _databaseJob?.Dispose();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        _databaseJob = null;
        _databaseOperationId = null;
        ThrowCleanupErrors(errors);
    }

    private static async Task CaptureCleanupAsync(Func<Task> cleanup, ICollection<Exception> errors)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    private static void ThrowCleanupErrors(IReadOnlyCollection<Exception> errors)
    {
        if (errors.Count == 1)
        {
            throw errors.First();
        }

        if (errors.Count > 1)
        {
            throw new AggregateException(errors);
        }
    }

    private RoleResource GetRole(RuntimeRole role) => role switch
    {
        RuntimeRole.Server => _server ?? throw new AppHostExecutionException("server-resource-missing"),
        RuntimeRole.Worker => _worker ?? throw new AppHostExecutionException("worker-resource-missing"),
        _ => throw new AppHostExecutionException("role-invalid"),
    };

    private void SetRole(RoleResource resource)
    {
        if (resource.Generation.Role == RuntimeRole.Server)
        {
            _server = resource;
        }
        else
        {
            _worker = resource;
        }
    }

    private static ProcessGeneration RequireGeneration(LifecycleCommand command, RuntimeRole role)
    {
        if (command.Generation is not { } generation || generation.Role != role)
        {
            throw new AppHostExecutionException("command-generation-invalid");
        }

        return generation;
    }

    private PostgresRuntime RequireDatabaseRuntime() =>
        _databaseRuntime ?? throw new AppHostExecutionException("postgres-runtime-missing");

    private PostgresInstanceIdentity RequireDatabaseIdentity() =>
        _databaseIdentity ?? throw new AppHostExecutionException("postgres-identity-missing");

    private static ControlEnvelope Empty(
        ControlMessageType type,
        Guid operationId,
        ProcessGeneration generation) =>
        new(
            ControlProtocolConstants.CurrentVersion,
            operationId,
            generation.Role,
            generation.Value,
            type,
            JsonSerializer.SerializeToElement(new { }, ControlFrameCodec.SerializerOptions));

    private void RegisterDiagnosticSecret(SecretValue secret)
    {
        _diagnosticSecrets.Register(secret);
        _diagnosticSecretObserver?.Invoke(secret);
    }

    private static DiagnosticComposition CreateFallbackDiagnosticComposition()
    {
        var registry = new DiagnosticSecretRegistry();
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(Stream.Null),
            new HowardLab.EbayCrm.AppHost.Core.Time.SystemClock(),
            registry);
        return new DiagnosticComposition(
            new OwnershipGatedDiagnosticSink(sink, TimeSpan.FromSeconds(1)),
            registry);
    }

    private sealed record DiagnosticComposition(
        OwnershipGatedDiagnosticSink Sink,
        DiagnosticSecretRegistry Registry);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var errors = new List<Exception>();
        await CaptureCleanupAsync(() => DisposeRoleAsync(RuntimeRole.Worker), errors).ConfigureAwait(false);
        await CaptureCleanupAsync(() => DisposeRoleAsync(RuntimeRole.Server), errors).ConfigureAwait(false);
        await CaptureCleanupAsync(DisposeDatabaseAsync, errors).ConfigureAwait(false);
        await CaptureCleanupAsync(
            async () => { _ = await ReleaseInstanceAsync().ConfigureAwait(false); },
            errors).ConfigureAwait(false);
        ThrowCleanupErrors(errors);
    }

    private enum RoleHealthStatus
    {
        Ready,
        NotReady,
    }

    private enum RoleReadinessOutcome
    {
        Ready,
        TimedOut,
        ProcessExited,
        ControlDisconnected,
    }

    private sealed record RoleResource(
        ProcessGeneration Generation,
        Guid StartupOperationId,
        int HealthPort,
        WindowsSupervisedProcess Process,
        WindowsControlChannel Channel,
        WindowsJobObject Job,
        Task NativeExitObservation,
        Task PayloadPostExitBoundary)
    {
        private readonly object _teardownGate = new();
        private int _monitorCancellationDisposalStarted;
        private int _roleLifetimeCancellationDisposalStarted;
        private int _monitoringStarted;
        private int _readinessTimeoutRecorded;

        internal CancellationTokenSource MonitorCancellation { get; } = new();

        internal CancellationTokenSource RoleLifetimeCancellation { get; } = new();

        internal Task? AcceptTask { get; set; }

        internal Task? StopCompletionTask { get; set; }

        internal bool Authenticated { get; set; }

        internal Guid? ShutdownOperationId { get; set; }

        internal Task? ControlMonitor { get; set; }

        internal Task<bool>? ControlDisconnectTask { get; set; }

        internal Task? HealthMonitor { get; set; }

        internal bool Drained { get; set; }

        internal Guid? DrainOperationId { get; set; }

        internal bool ReadinessConfirmed { get; set; }

        private Task? TeardownCompletion { get; set; }

        internal Task GetOrStartTeardown(Func<Task> teardown)
        {
            ArgumentNullException.ThrowIfNull(teardown);
            TaskCompletionSource completion;
            lock (_teardownGate)
            {
                if (TeardownCompletion is not null)
                {
                    return TeardownCompletion;
                }

                completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                TeardownCompletion = completion.Task;
            }

            _ = CompleteTeardownAsync(teardown, completion);
            return completion.Task;
        }

        private static async Task CompleteTeardownAsync(
            Func<Task> teardown,
            TaskCompletionSource completion)
        {
            try
            {
                await teardown().ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }

        internal bool TryStartMonitoring() =>
            Interlocked.CompareExchange(ref _monitoringStarted, 1, 0) == 0;

        internal bool TryRecordReadinessTimeout() =>
            Interlocked.CompareExchange(ref _readinessTimeoutRecorded, 1, 0) == 0;

        internal void DisposeMonitorCancellationWhenQuiescent()
        {
            if (Interlocked.Exchange(ref _monitorCancellationDisposalStarted, 1) != 0)
            {
                return;
            }

            _ = DisposeMonitorCancellationWhenQuiescentAsync();
        }

        private async Task DisposeMonitorCancellationWhenQuiescentAsync()
        {
            try
            {
                await Task.WhenAll(
                    new Task?[] { ControlDisconnectTask, ControlMonitor, HealthMonitor }
                        .Where(task => task is not null)
                        .Cast<Task>()).ConfigureAwait(false);
            }
            catch
            {
                // Monitor faults are translated into lifecycle events by the monitor methods.
            }
            finally
            {
                MonitorCancellation.Dispose();
            }
        }

        internal void DisposeRoleLifetimeCancellationWhenQuiescent()
        {
            if (Interlocked.Exchange(ref _roleLifetimeCancellationDisposalStarted, 1) != 0)
            {
                return;
            }

            _ = DisposeRoleLifetimeCancellationWhenQuiescentAsync();
        }

        private async Task DisposeRoleLifetimeCancellationWhenQuiescentAsync()
        {
            try
            {
                await Task.WhenAll(
                    new[] { AcceptTask, StopCompletionTask }
                        .Where(task => task is not null)
                        .Cast<Task>()).ConfigureAwait(false);
            }
            catch
            {
                // Role tasks already reported their terminal outcome to the lifecycle executor.
            }
            finally
            {
                RoleLifetimeCancellation.Dispose();
            }
        }
    }

    private sealed record LaunchedRoleProcess(
        ISupervisedProcess Process,
        Task? NativeExitObservation,
        Task? PayloadPostExitBoundary,
        IDisposable? PayloadLifetimeLease);

}

public sealed class AppHostExecutionException : Exception
{
    public AppHostExecutionException(string reasonCode, Exception? innerException = null)
        : base(reasonCode, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}

internal sealed record AppHostRuntimeSnapshot(
    int? DatabaseProcessId,
    ProcessGeneration? DatabaseGeneration,
    int? ServerProcessId,
    ProcessGeneration? ServerGeneration,
    int? WorkerProcessId,
    ProcessGeneration? WorkerGeneration);
