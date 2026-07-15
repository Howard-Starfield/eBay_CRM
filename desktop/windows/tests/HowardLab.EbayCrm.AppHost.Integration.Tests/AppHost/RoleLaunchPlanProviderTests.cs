using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Core.Time;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

public sealed class RoleLaunchPlanProviderTests
{
    [PostgresFact]
    public async Task CreateForTests_DefaultsToFixtureProviderAndAcceptsAnExplicitProvider()
    {
        using var layout = TestLayout.CreateReal("ebaycrm-provider-composition");
        var options = AppHostOptions.Parse(layout.Arguments("run"));
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var injected = new StubProvider(CreatePlan(generation));

        var defaultRuntime = AppHostComposition.CreateForTests(options);
        var injectedRuntime = AppHostComposition.CreateForTests(
            options,
            roleLaunchPlanProvider: injected);
        try
        {
            Assert.IsType<FixtureRoleLaunchPlanProvider>(defaultRuntime.ActiveRoleLaunchPlanProvider);
            Assert.Same(defaultRuntime.FixtureRoleLaunchPlanProvider, defaultRuntime.ActiveRoleLaunchPlanProvider);
            Assert.Same(injected, injectedRuntime.ActiveRoleLaunchPlanProvider);
            Assert.NotNull(injectedRuntime.FixtureRoleLaunchPlanProvider);
        }
        finally
        {
            await defaultRuntime.Orchestrator.DisposeAsync();
            await injectedRuntime.Orchestrator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Executor_RequiresAnExplicitRoleLaunchPlanProvider()
    {
        var launcher = new RecordingLauncher((_, _, _) => throw new InvalidOperationException("must not launch"));
        ExecutorHarness? harness = null;
        var error = Record.Exception(() => harness = CreateHarness(null!, launcher));
        if (harness is not null)
        {
            await harness.DisposeAsync();
        }

        var argument = Assert.IsType<ArgumentNullException>(error);
        Assert.Equal("roleLaunchPlanProvider", argument.ParamName);
    }

    [Theory]
    [InlineData(RuntimeRole.Server)]
    [InlineData(RuntimeRole.Worker)]
    public async Task StartRole_UsesTheProviderPlan(RuntimeRole role)
    {
        var generation = new ProcessGeneration(role, 7, Guid.NewGuid());
        var plan = CreatePlan(generation);
        var provider = new StubProvider(plan);
        var launcher = new RecordingLauncher((_, _, _) =>
            ValueTask.FromResult<ISupervisedProcess>(new StubProcess(generation)));
        await using var harness = CreateHarness(provider, launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-process-type-invalid", error.ReasonCode);
        var launched = Assert.Single(launcher.Specifications);
        Assert.Equal(plan.ApplicationPath, launched.ApplicationPath);
        Assert.Equal(plan.Arguments, launched.Arguments);
        Assert.Equal(plan.WorkingDirectory, launched.WorkingDirectory);
        Assert.All(plan.Environment, pair => Assert.Equal(pair.Value, launched.Environment[pair.Key]));
        Assert.All(plan.SecretEnvironment, pair => Assert.Same(pair.Value, launched.SecretEnvironment[pair.Key]));
        Assert.Equal(plan.OutputDrainTimeout, launched.OutputDrainTimeout);
        Assert.Equal(
            plan.BuildIdentity,
            launched.Environment[HowardLab.EbayCrm.AppHost.Windows.Control.WindowsControlChannel.BuildEnvironmentVariable]);
        Assert.Equal(new RoleLaunchRequest(role, generation), provider.LastRequest);
    }

    public static TheoryData<string, bool> ReservedEnvironmentKeys()
    {
        var data = new TheoryData<string, bool>();
        foreach (var key in RoleLaunchPlan.ReservedEnvironmentKeys)
        {
            data.Add(key.ToLowerInvariant(), false);
            data.Add(key.ToLowerInvariant(), true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ReservedEnvironmentKeys))]
    public async Task ReservedEnvironmentKey_FailsBeforeLeaseOrLaunch(
        string key,
        bool secret)
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var lease = new TrackingLease();
        var ordinary = secret
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(StringComparer.Ordinal) { [key] = "value" };
        var secrets = secret
            ? new Dictionary<string, SecretValue>(StringComparer.Ordinal) { [key] = new("secret-value") }
            : new Dictionary<string, SecretValue>();
        var plan = CreatePlan(generation, ordinary, secrets, () => lease.Open());
        var launcher = new RecordingLauncher((_, _, _) => throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(new StubProvider(plan), launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.False(lease.WasOpened);
        Assert.Empty(launcher.Specifications);
    }

    [Theory]
    [InlineData("duplicate-ordinary")]
    [InlineData("duplicate-secret")]
    [InlineData("cross-map")]
    public async Task CaseInsensitiveEnvironmentCollision_FailsBeforeLeaseOrLaunch(string kind)
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var ordinary = new Dictionary<string, string>(StringComparer.Ordinal);
        var secrets = new Dictionary<string, SecretValue>(StringComparer.Ordinal);
        switch (kind)
        {
            case "duplicate-ordinary":
                ordinary.Add("CUSTOM_KEY", "one");
                ordinary.Add("custom_key", "two");
                break;
            case "duplicate-secret":
                secrets.Add("CUSTOM_SECRET", new SecretValue("one-secret"));
                secrets.Add("custom_secret", new SecretValue("two-secret"));
                break;
            case "cross-map":
                ordinary.Add("CUSTOM_KEY", "one");
                secrets.Add("custom_key", new SecretValue("two-secret"));
                break;
        }

        var lease = new TrackingLease();
        var launcher = new RecordingLauncher((_, _, _) => throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(generation, ordinary, secrets, () => lease.Open())),
            launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.False(lease.WasOpened);
        Assert.Empty(launcher.Specifications);
    }

    [Fact]
    public async Task ProviderFailure_OpensNoLeaseAndLaunchesNoProcess()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var launcher = new RecordingLauncher((_, _, _) => throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(
            new StubProvider(new InvalidOperationException("provider detail must not escape")),
            launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.Empty(launcher.Specifications);
    }

    [Fact]
    public async Task ArtifactLeaseFactoryFailure_DoesNotExposeProviderDetailOrLaunch()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var plan = CreatePlan(
            generation,
            leaseFactory: () => throw new InvalidOperationException("sensitive artifact detail"));
        var launcher = new RecordingLauncher((_, _, _) => throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(new StubProvider(plan), launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.Empty(launcher.Specifications);
    }

    [Fact]
    public async Task NullArtifactLease_IsAnInvalidPlanAndDoesNotLaunch()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var plan = CreatePlan(generation, leaseFactory: () => null!);
        var launcher = new RecordingLauncher((_, _, _) => throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(new StubProvider(plan), launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.Empty(launcher.Specifications);
    }

    [Fact]
    public async Task FixtureTrustFailure_PreservesOnlyTheSafeReasonCode()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var plan = CreatePlan(
            generation,
            leaseFactory: () => throw new AppHostOptionsException(
                "fixture-build-mismatch",
                new InvalidOperationException("sensitive trust detail")));
        var launcher = new RecordingLauncher((_, _, _) => throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(new StubProvider(plan), launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("fixture-build-mismatch", error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.Empty(launcher.Specifications);
    }

    [Theory]
    [InlineData("application")]
    [InlineData("working-directory")]
    public async Task MissingLaunchPath_FailsBeforeLeaseOrLaunch(string kind)
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var missing = Path.Combine(Path.GetTempPath(), $"missing-role-plan-{Guid.NewGuid():N}");
        var lease = new TrackingLease();
        var plan = CreatePlan(
            generation,
            leaseFactory: () => lease.Open(),
            applicationPath: kind == "application" ? missing : null,
            workingDirectory: kind == "working-directory" ? missing : null);
        var launcher = new RecordingLauncher((_, _, _) => throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(new StubProvider(plan), launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.False(lease.WasOpened);
        Assert.Empty(launcher.Specifications);
    }

    [Theory]
    [InlineData("after-plan-creation")]
    [InlineData("during-lease-open")]
    public async Task PlanCollections_AreOwnedSnapshots(string mutationPoint)
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var arguments = new List<string> { "original-argument" };
        var ordinary = new Dictionary<string, string> { ["CUSTOM"] = "original-value" };
        var originalSecret = new SecretValue("original-secret");
        var secrets = new Dictionary<string, SecretValue> { ["CUSTOM_SECRET"] = originalSecret };
        void MutateSources()
        {
            arguments[0] = "mutated-argument";
            ordinary["CUSTOM"] = "mutated-value";
            secrets["CUSTOM_SECRET"] = new SecretValue("mutated-secret");
        }

        var plan = CreatePlan(
            generation,
            ordinary,
            secrets,
            () =>
            {
                if (mutationPoint == "during-lease-open")
                {
                    MutateSources();
                }

                return new TrackingLease().Open();
            },
            arguments: arguments);
        if (mutationPoint == "after-plan-creation")
        {
            MutateSources();
        }

        var launcher = new RecordingLauncher((_, _, _) =>
            ValueTask.FromResult<ISupervisedProcess>(new StubProcess(generation)));
        await using var harness = CreateHarness(new StubProvider(plan), launcher);

        _ = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        var launched = Assert.Single(launcher.Specifications);
        Assert.Equal("original-argument", Assert.Single(launched.Arguments));
        Assert.Equal("original-value", launched.Environment["CUSTOM"]);
        Assert.Same(originalSecret, launched.SecretEnvironment["CUSTOM_SECRET"]);
    }

    [Fact]
    public async Task LeaseDisposeFailureAfterSuccessfulLaunch_IsTrustFailureAndCleansProcess()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var lease = new TrackingLease(new InvalidOperationException("sensitive dispose detail"));
        var process = new StubProcess(generation);
        var launcher = new RecordingLauncher((_, _, _) =>
            ValueTask.FromResult<ISupervisedProcess>(process));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(generation, leaseFactory: () => lease.Open())),
            launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.True(lease.IsDisposed);
        Assert.True(process.IsDisposed);
    }

    [Fact]
    public async Task LaunchFailure_RemainsPrimaryWhenLeaseDisposeAlsoFails()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var primary = new ExpectedLaunchException();
        var lease = new TrackingLease(new InvalidOperationException("sensitive dispose detail"));
        var launcher = new RecordingLauncher((_, _, _) => throw primary);
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(generation, leaseFactory: () => lease.Open())),
            launcher);

        var actual = await Assert.ThrowsAsync<ExpectedLaunchException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Same(primary, actual);
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public async Task LaunchCancellation_RemainsPrimaryWhenLeaseDisposeAlsoFails()
    {
        var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());
        var lease = new TrackingLease(new InvalidOperationException("sensitive dispose detail"));
        var launcher = new RecordingLauncher(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new StubProcess(generation);
        });
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(generation, leaseFactory: () => lease.Open())),
            launcher);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(lease.IsDisposed);
    }

    [Theory]
    [InlineData("ordinary-null")]
    [InlineData("ordinary-nul")]
    [InlineData("secret-null")]
    [InlineData("build-oversize")]
    public async Task MalformedPlanValue_FailsBeforeLeaseOrLaunch(string kind)
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var lease = new TrackingLease();
        var plan = kind switch
        {
            "ordinary-null" => CreatePlan(
                generation,
                environment: new Dictionary<string, string> { ["CUSTOM"] = null! },
                leaseFactory: () => lease.Open()),
            "ordinary-nul" => CreatePlan(
                generation,
                environment: new Dictionary<string, string> { ["CUSTOM"] = "bad\0value" },
                leaseFactory: () => lease.Open()),
            "secret-null" => CreatePlan(
                generation,
                secretEnvironment: new Dictionary<string, SecretValue> { ["CUSTOM_SECRET"] = null! },
                leaseFactory: () => lease.Open()),
            "build-oversize" => CreatePlan(
                generation,
                leaseFactory: () => lease.Open(),
                buildIdentity: new string('b', ControlProtocolConstants.MaxTextFieldChars + 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var launcher = new RecordingLauncher((_, _, _) =>
            ValueTask.FromResult<ISupervisedProcess>(new StubProcess(generation)));
        await using var harness = CreateHarness(new StubProvider(plan), launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.False(lease.WasOpened);
        Assert.Empty(launcher.Specifications);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task ArtifactLease_SpansLaunchAndIsDisposedAfterEveryOutcome(string outcome)
    {
        var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());
        var lease = new TrackingLease();
        var launcher = new RecordingLauncher(async (_, _, cancellationToken) =>
        {
            Assert.True(lease.WasOpened);
            Assert.False(lease.IsDisposed);
            if (outcome == "failure")
            {
                throw new InvalidOperationException("launch failed");
            }

            if (outcome == "cancellation")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new StubProcess(generation);
        });
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(generation, leaseFactory: () => lease.Open())),
            launcher);
        using var cancellation = outcome == "cancellation"
            ? new CancellationTokenSource(TimeSpan.FromMilliseconds(50))
            : new CancellationTokenSource();

        _ = await Assert.ThrowsAnyAsync<Exception>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation), cancellation.Token));

        Assert.True(lease.WasOpened);
        Assert.True(lease.IsDisposed);
        Assert.Single(launcher.Specifications);
    }

    private static RoleLaunchPlan CreatePlan(
        ProcessGeneration generation,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyDictionary<string, SecretValue>? secretEnvironment = null,
        Func<IDisposable>? leaseFactory = null,
        IReadOnlyList<string>? arguments = null,
        string? applicationPath = null,
        string? workingDirectory = null,
        string? buildIdentity = null) =>
        new(
            generation.Role,
            generation,
            applicationPath ?? Path.GetFullPath(Environment.ProcessPath!),
            arguments ?? ["first", "second value"],
            workingDirectory ?? Path.GetFullPath(AppContext.BaseDirectory),
            environment ?? new Dictionary<string, string> { ["CUSTOM"] = "ordinary" },
            secretEnvironment ?? new Dictionary<string, SecretValue> { ["CUSTOM_SECRET"] = new("secret-value") },
            buildIdentity ?? "provider-build",
            RoleReadinessStrategy.IdentityBoundHttp,
            32123,
            TimeSpan.FromMilliseconds(250),
            leaseFactory ?? (() => new TrackingLease().Open()));

    private static LifecycleCommand StartCommand(ProcessGeneration generation) =>
        new(
            generation.Role == RuntimeRole.Server
                ? LifecycleCommandType.StartServer
                : LifecycleCommandType.StartWorker,
            generation,
            generation.OperationId,
            generation.Role == RuntimeRole.Server
                ? LifecycleDeadlineKey.ServerStart
                : LifecycleDeadlineKey.WorkerStart);

    private static ExecutorHarness CreateHarness(
        IRoleLaunchPlanProvider provider,
        IProcessLauncher launcher)
    {
        var profileRoot = Path.Combine(Path.GetTempPath(), $"role-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profileRoot);
        var postgresBin = Path.Combine(profileRoot, "unused-postgres-bin");
        Directory.CreateDirectory(postgresBin);
        var options = new AppHostOptions(
            profileRoot,
            postgresBin,
            Path.GetFullPath(Environment.ProcessPath!),
            15432,
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
            "unused-fixture-build");
        var secrets = new DiagnosticSecretRegistry();
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(Stream.Null),
            new SystemClock(),
            secrets);
        var executor = new LifecycleCommandExecutor(
            options,
            payload,
            identityStore: null,
            NoopRoleOperationBoundary.Instance,
            RoleOperationDeadlines.Production,
            new OwnershipGatedDiagnosticSink(sink, TimeSpan.FromSeconds(1)),
            secrets,
            diagnosticSecretObserver: null,
            provider,
            launcher);
        return new ExecutorHarness(profileRoot, executor);
    }

    private sealed class StubProvider : IRoleLaunchPlanProvider
    {
        private readonly RoleLaunchPlan? _plan;
        private readonly Exception? _error;

        internal StubProvider(RoleLaunchPlan plan) => _plan = plan;

        internal StubProvider(Exception error) => _error = error;

        internal RoleLaunchRequest? LastRequest { get; private set; }

        public RoleLaunchPlan Create(RoleLaunchRequest request)
        {
            LastRequest = request;
            if (_error is not null)
            {
                throw _error;
            }

            return _plan!;
        }
    }

    private sealed class RecordingLauncher(
        Func<LaunchSpecification, IProcessGroup, CancellationToken, ValueTask<ISupervisedProcess>> launch)
        : IProcessLauncher
    {
        internal List<LaunchSpecification> Specifications { get; } = [];

        public ValueTask<ISupervisedProcess> LaunchAsync(
            LaunchSpecification specification,
            IProcessGroup processGroup,
            CancellationToken cancellationToken)
        {
            Specifications.Add(specification);
            return launch(specification, processGroup, cancellationToken);
        }
    }

    private sealed class StubProcess(ProcessGeneration generation) : ISupervisedProcess
    {
        public SupervisedProcessIdentity Identity { get; } = new(
            generation.Role,
            generation,
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            Path.GetFullPath(Environment.ProcessPath!));

        public Task<int> Completion { get; } = Task.FromResult(0);

        public BoundedTextCollector StandardOutput { get; } = new(1024, 1024);

        public BoundedTextCollector StandardError { get; } = new(1024, 1024);

        internal bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingLease(Exception? disposeError = null) : IDisposable
    {
        internal bool WasOpened { get; private set; }

        internal bool IsDisposed { get; private set; }

        internal IDisposable Open()
        {
            WasOpened = true;
            return this;
        }

        public void Dispose()
        {
            IsDisposed = true;
            if (disposeError is not null)
            {
                throw disposeError;
            }
        }
    }

    private sealed class ExpectedLaunchException : Exception;

    private sealed class ExecutorHarness(string profileRoot, LifecycleCommandExecutor executor)
        : IAsyncDisposable
    {
        internal LifecycleCommandExecutor Executor { get; } = executor;

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            if (Directory.Exists(profileRoot))
            {
                Directory.Delete(profileRoot, recursive: true);
            }
        }
    }
}
