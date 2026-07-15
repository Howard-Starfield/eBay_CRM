using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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
    private readonly AppHostOptions _options;
    private readonly ValidatedAppHostPayload _payload;
    private readonly IProfileRuntimeIdentityStore _identityStore;
    private readonly WindowsProcessLauncher _launcher;
    private readonly OwnershipGatedDiagnosticSink _diagnosticSink;
    private readonly DiagnosticSecretRegistry _diagnosticSecrets;
    private readonly Action<SecretValue>? _diagnosticSecretObserver;
    private readonly IRoleOperationBoundary _roleOperationBoundary;
    private readonly RoleOperationDeadlines _roleOperationDeadlines;
    private UserProfileInstanceLock? _instanceLock;
    private Guid? _instanceOperationId;
    private ProfileRuntimeIdentity? _profileIdentity;
    private WindowsJobObject? _databaseJob;
    private PostgresRuntime? _databaseRuntime;
    private PostgresInstanceIdentity? _databaseIdentity;
    private Guid? _databaseOperationId;
    private RoleResource? _server;
    private RoleResource? _worker;
    private string? _workerFixtureModeForTests;
    private int _disposed;
    private int _faultDiagnosticWritten;
    private int _releaseInstanceCountForTests;

    public LifecycleCommandExecutor(
        AppHostOptions options,
        ValidatedAppHostPayload payload,
        IProfileRuntimeIdentityStore? identityStore = null)
        : this(
            options,
            payload,
            identityStore,
            NoopRoleOperationBoundary.Instance,
            RoleOperationDeadlines.Production,
            CreateFallbackDiagnosticComposition())
    {
    }

    private LifecycleCommandExecutor(
        AppHostOptions options,
        ValidatedAppHostPayload payload,
        IProfileRuntimeIdentityStore? identityStore,
        IRoleOperationBoundary roleOperationBoundary,
        RoleOperationDeadlines roleOperationDeadlines,
        DiagnosticComposition diagnostics)
        : this(
            options,
            payload,
            identityStore,
            roleOperationBoundary,
            roleOperationDeadlines,
            diagnostics.Sink,
            diagnostics.Registry,
            diagnosticSecretObserver: null)
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
        Action<SecretValue>? diagnosticSecretObserver)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _identityStore = identityStore ?? new ProfileRuntimeIdentityStore();
        _roleOperationBoundary = roleOperationBoundary ?? throw new ArgumentNullException(nameof(roleOperationBoundary));
        _roleOperationDeadlines = roleOperationDeadlines ?? throw new ArgumentNullException(nameof(roleOperationDeadlines));
        _diagnosticSink = diagnosticSink ?? throw new ArgumentNullException(nameof(diagnosticSink));
        _diagnosticSecrets = diagnosticSecrets ?? throw new ArgumentNullException(nameof(diagnosticSecrets));
        _diagnosticSecretObserver = diagnosticSecretObserver;
        _launcher = new WindowsProcessLauncher(_diagnosticSink);
    }

    public Func<LifecycleEvent, CancellationToken, Task>? EventSink { get; set; }

    internal string? WorkerFixtureModeForTests
    {
        get => _workerFixtureModeForTests;
        set => _workerFixtureModeForTests = value;
    }

    internal Action<RuntimeRole, WindowsJobObject, WindowsSupervisedProcess>? RoleLaunchedForTests { get; set; }

    internal int ReleaseInstanceCountForTests => Volatile.Read(ref _releaseInstanceCountForTests);

    internal long DiagnosticSinkFailureCountForTests => _diagnosticSink.SinkFailureCount;

    internal long DiagnosticCompletionTimeoutCountForTests => _diagnosticSink.CompletionTimeoutCount;

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
            ?? throw new AppHostExecutionException("fixture-generation-missing");
        if (generation.Role is not (RuntimeRole.Server or RuntimeRole.Worker))
        {
            throw new AppHostExecutionException("fixture-role-invalid");
        }

        await DisposeRoleAsync(generation.Role).ConfigureAwait(false);
        var channel = WindowsControlChannel.CreateBeforeLaunch(
            generation.Role,
            generation.Value,
            generation.OperationId,
            _payload.FixtureBuildIdentity,
            ControlDeadline);
        var job = WindowsJobObject.CreateKillOnClose();
        ISupervisedProcess? process = null;
        RoleResource? resource = null;
        try
        {
            var child = channel.CreateChildEnvironment();
            foreach (var secret in child.SecretEnvironment.Values)
            {
                RegisterDiagnosticSecret(secret);
            }

            var environment = new Dictionary<string, string>(child.Environment, StringComparer.Ordinal)
            {
                ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")
                    ?? throw new AppHostExecutionException("system-root-unavailable"),
            };
            var healthPort = ReserveLoopbackPort();
            var testFixtureMode = generation.Role == RuntimeRole.Worker
                ? Interlocked.Exchange(ref _workerFixtureModeForTests, null)
                : null;
            var fixtureMode = testFixtureMode is not null
                ? testFixtureMode
                : generation.Role.ToString().ToLowerInvariant();
            var specification = new LaunchSpecification(
                generation.Role,
                generation,
                _options.FixturePath,
                [fixtureMode, healthPort.ToString(CultureInfo.InvariantCulture)],
                Path.GetDirectoryName(_options.FixturePath)!,
                environment,
                child.SecretEnvironment,
                TimeSpan.FromMilliseconds(100));
            using var fixtureLease = AppHostComposition.OpenTrustedFixtureArtifacts(_options.FixturePath);
            process = await _launcher.LaunchAsync(specification, job, cancellationToken).ConfigureAwait(false);
            if (process is not WindowsSupervisedProcess windowsProcess)
            {
                throw new AppHostExecutionException("fixture-process-type-invalid");
            }

            RoleLaunchedForTests?.Invoke(generation.Role, job, windowsProcess);
            resource = new RoleResource(
                generation,
                command.OperationId,
                healthPort,
                process,
                channel,
                job);
            SetRole(resource);
            resource.AcceptTask = AcceptRoleAsync(resource);
            using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            commandDeadline.CancelAfter(_roleOperationDeadlines.StartCommand);
            try
            {
                await resource.AcceptTask.WaitAsync(commandDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && commandDeadline.IsCancellationRequested)
            {
                return new OperationTimedOut(generation, command.OperationId);
            }

            return new RoleStarted(generation);
        }
        catch
        {
            if (resource is not null)
            {
                await DisposeRoleAsync(resource).ConfigureAwait(false);
            }
            else
            {
                if (process is not null)
                {
                    await process.DisposeAsync().ConfigureAwait(false);
                }

                await channel.DisposeAsync().ConfigureAwait(false);
                job.Dispose();
            }
            throw;
        }
    }

    private async Task AcceptRoleAsync(RoleResource resource)
    {
        var roleLifetimeToken = resource.RoleLifetimeCancellation.Token;
        await _roleOperationBoundary.PauseAsync(
            RoleOperationBoundaryPoint.StartIdentityRetained,
            resource.Generation,
            resource.StartupOperationId,
            roleLifetimeToken).ConfigureAwait(false);
        await resource.Channel.AcceptAsync(
            resource.Process,
            resource.Job,
            roleLifetimeToken).ConfigureAwait(false);
        resource.Authenticated = true;
    }

    private async Task<LifecycleEvent> WaitForRoleAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var generation = command.Generation
            ?? throw new AppHostExecutionException("fixture-generation-missing");
        var resource = GetRole(generation.Role);
        if (resource.Generation != generation || resource.Process.Completion.IsCompleted)
        {
            throw new AppHostExecutionException("fixture-retained-identity-mismatch");
        }

        using var http = new HttpClient { Timeout = ControlDeadline };
        using var request = CreateHealthRequest(resource);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ValidateHealthResponseAsync(resource, response, cancellationToken).ConfigureAwait(false);

        MonitorRole(resource);
        return new RoleReady(generation);
    }

    private static async Task ValidateHealthResponseAsync(
        RoleResource resource,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        var health = await response.Content.ReadFromJsonAsync<HealthPayload>(
            ControlFrameCodec.SerializerOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new AppHostExecutionException("fixture-health-empty");
        var endpoint = resource.Channel.EndpointIdentity;
        if (health.ProtocolVersion != ControlProtocolConstants.CurrentVersion ||
            health.BuildIdentity != endpoint.ExpectedBuildIdentity ||
            health.Generation != resource.Generation.Value ||
            health.GenerationNonce != endpoint.CapabilityNonce ||
            health.Status != "ready")
        {
            throw new AppHostExecutionException("fixture-health-identity-mismatch");
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
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            // A coalesced dependency failure can close the authenticated pipe
            // just before recovery drains the worker. The retained process
            // handle, not the pipe error, is authoritative: wait for the
            // already-contained role to signal before continuing.
            await _worker.Process.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
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
            throw new AppHostExecutionException("fixture-retained-identity-mismatch");
        }

        await StopMonitoringAsync(resource).ConfigureAwait(false);

        if (resource.Process.Completion.IsCompleted)
        {
            await DisposeRoleAsync(resource).ConfigureAwait(false);
            return null;
        }

        if (role == RuntimeRole.Worker && !resource.Drained)
        {
            await DrainWorkerAsync(command, cancellationToken).ConfigureAwait(false);
        }

        await resource.Channel.SendAsync(
            Empty(ControlMessageType.Shutdown, command.OperationId, resource.Generation),
            cancellationToken).ConfigureAwait(false);
        var accepted = await resource.Channel.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (accepted.Type != ControlMessageType.ShutdownAccepted || accepted.OperationId != command.OperationId)
        {
            throw new AppHostExecutionException("fixture-shutdown-sequence-invalid");
        }

        resource.ShutdownOperationId = command.OperationId;
        resource.StopCompletionTask = CompleteRoleStopAsync(resource, command.OperationId);
        using var commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandDeadline.CancelAfter(_roleOperationDeadlines.StopCommand);
        try
        {
            await resource.StopCompletionTask.WaitAsync(commandDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!resource.RoleLifetimeCancellation.IsCancellationRequested)
        {
            return new OperationTimedOut(commandGeneration, command.OperationId);
        }

        await DisposeRoleAsync(resource).ConfigureAwait(false);
        return null;
    }

    private async Task CompleteRoleStopAsync(RoleResource resource, Guid operationId)
    {
        var roleLifetimeToken = resource.RoleLifetimeCancellation.Token;
        await _roleOperationBoundary.PauseAsync(
            RoleOperationBoundaryPoint.StopAccepted,
            resource.Generation,
            operationId,
            roleLifetimeToken).ConfigureAwait(false);
        var stopped = await resource.Channel.ReadAsync(roleLifetimeToken).ConfigureAwait(false);
        if (stopped.Type != ControlMessageType.Stopped || stopped.OperationId != operationId)
        {
            throw new AppHostExecutionException("fixture-shutdown-sequence-invalid");
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
        var generation = command.Generation
            ?? throw new AppHostExecutionException("fixture-generation-missing");
        if (generation.Role is not (RuntimeRole.Server or RuntimeRole.Worker))
        {
            throw new AppHostExecutionException("fixture-role-invalid");
        }

        var resource = generation.Role == RuntimeRole.Server ? _server : _worker;
        if (resource is null || resource.Generation != generation)
        {
            return new Reconciled(generation, ReconciledState.Stopped);
        }

        var acceptTask = resource.AcceptTask
            ?? throw new AppHostExecutionException("fixture-accept-task-missing");
        using var reconciliationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reconciliationDeadline.CancelAfter(_roleOperationDeadlines.Reconciliation);
        try
        {
            await acceptTask.WaitAsync(reconciliationDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!resource.RoleLifetimeCancellation.IsCancellationRequested)
        {
            return new OperationTimedOut(generation, command.OperationId);
        }

        if (!resource.Authenticated || resource.Process.Completion.IsCompleted)
        {
            await DisposeRoleAsync(resource).ConfigureAwait(false);
            return new Reconciled(generation, ReconciledState.Stopped);
        }

        return new Reconciled(generation, ReconciledState.Running);
    }

    private async Task<LifecycleEvent> ReconcileRoleStopAsync(
        LifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var generation = command.Generation
            ?? throw new AppHostExecutionException("fixture-generation-missing");
        if (generation.Role is not (RuntimeRole.Server or RuntimeRole.Worker))
        {
            throw new AppHostExecutionException("fixture-role-invalid");
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
            throw new AppHostExecutionException("fixture-shutdown-operation-mismatch");
        }

        var stopCompletionTask = resource.StopCompletionTask
            ?? throw new AppHostExecutionException("fixture-stop-task-missing");
        using var reconciliationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reconciliationDeadline.CancelAfter(_roleOperationDeadlines.Reconciliation);
        try
        {
            await stopCompletionTask.WaitAsync(reconciliationDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!resource.RoleLifetimeCancellation.IsCancellationRequested)
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
        var errors = new List<Exception>();

        // Close every containment boundary before awaiting any descendant.
        foreach (var resource in new[] { worker, server }.Where(value => value is not null).Cast<RoleResource>())
        {
            resource.RoleLifetimeCancellation.Cancel();
            resource.MonitorCancellation.Cancel();
            var termination = resource.Job.TerminateTree(exitCode: 1);
            if (!termination.Succeeded)
            {
                errors.Add(new AppHostExecutionException(
                    $"fixture-job-termination-failed-{termination.ErrorCode.ToString(CultureInfo.InvariantCulture)}"));
            }
        }
        foreach (var resource in new[] { worker, server }.Where(value => value is not null).Cast<RoleResource>())
        {
            try { resource.Job.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        try { _databaseJob?.Dispose(); } catch (Exception error) { errors.Add(error); }

        foreach (var resource in new[] { worker, server }.Where(value => value is not null).Cast<RoleResource>())
        {
            try { resource.Channel.ForceCloseAfterJobClose(); } catch (Exception error) { errors.Add(error); }
            try
            {
                if (resource.Process is WindowsSupervisedProcess process)
                {
                    process.TerminateAndForceCloseAfterJobClose(cancellationToken);
                }
                else
                {
                    throw new AppHostExecutionException("fixture-process-type-invalid");
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            resource.DisposeMonitorCancellationWhenQuiescent();
            resource.DisposeRoleLifetimeCancellationWhenQuiescent();
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
        ThrowCleanupErrors(errors);
        return null;
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
        var monitorToken = resource.MonitorCancellation.Token;
        _ = ObserveAsync(resource.Process.Completion, resource.Generation);
        resource.ControlMonitor = ObserveControlAsync(resource, monitorToken);
        resource.HealthMonitor = ObserveHealthAsync(resource, monitorToken);
    }

    private async Task ObserveControlAsync(RoleResource resource, CancellationToken monitorToken)
    {
        try
        {
            while (true)
            {
                await resource.Channel.WaitForDisconnectAsync(monitorToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (monitorToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (!monitorToken.IsCancellationRequested)
        {
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
                using var http = new HttpClient { Timeout = ControlDeadline };
                using var request = CreateHealthRequest(resource);
                using var response = await http.SendAsync(request, monitorToken).ConfigureAwait(false);
                await ValidateHealthResponseAsync(
                    resource,
                    response,
                    monitorToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (monitorToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (!monitorToken.IsCancellationRequested)
        {
            if (EventSink is { } sink)
            {
                _ = sink(new HealthFailed(resource.Generation, "fixture-health-monitor-failed"), CancellationToken.None);
            }
        }
    }

    private static async Task StopMonitoringAsync(RoleResource resource)
    {
        resource.MonitorCancellation.Cancel();
        var monitors = new[] { resource.ControlMonitor, resource.HealthMonitor }
            .Where(task => task is not null)
            .Cast<Task>();
        await Task.WhenAll(monitors).ConfigureAwait(false);
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
            ? Interlocked.Exchange(ref _server, null)
            : Interlocked.Exchange(ref _worker, null);
        if (resource is null)
        {
            return;
        }

        await DisposeRoleResourceAsync(resource).ConfigureAwait(false);
    }

    private async Task DisposeRoleAsync(RoleResource resource)
    {
        var removed = resource.Generation.Role == RuntimeRole.Server
            ? TryTakeExactRoleResource(ref _server, resource)
            : TryTakeExactRoleResource(ref _worker, resource);
        if (!removed)
        {
            return;
        }

        await DisposeRoleResourceAsync(resource).ConfigureAwait(false);
    }

    private static async Task DisposeRoleResourceAsync(RoleResource resource)
    {
        var errors = new List<Exception>();
        resource.RoleLifetimeCancellation.Cancel();
        await StopMonitoringAsync(resource).ConfigureAwait(false);
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
        ThrowCleanupErrors(errors);
    }

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
        _ => throw new AppHostExecutionException("fixture-role-invalid"),
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

    private sealed record RoleResource(
        ProcessGeneration Generation,
        Guid StartupOperationId,
        int HealthPort,
        ISupervisedProcess Process,
        WindowsControlChannel Channel,
        WindowsJobObject Job)
    {
        private int _monitorCancellationDisposalStarted;
        private int _roleLifetimeCancellationDisposalStarted;

        internal CancellationTokenSource MonitorCancellation { get; } = new();

        internal CancellationTokenSource RoleLifetimeCancellation { get; } = new();

        internal Task? AcceptTask { get; set; }

        internal Task? StopCompletionTask { get; set; }

        internal bool Authenticated { get; set; }

        internal Guid? ShutdownOperationId { get; set; }

        internal Task? ControlMonitor { get; set; }

        internal Task? HealthMonitor { get; set; }

        internal bool Drained { get; set; }

        internal Guid? DrainOperationId { get; set; }

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
                    new[] { ControlMonitor, HealthMonitor }
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
