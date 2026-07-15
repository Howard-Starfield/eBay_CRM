using HowardLab.EbayCrm.AppHost.Composition;
using System.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Time;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Fixture;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Control;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

[Collection("Destructive containment")]
public sealed class AppHostStartupTests
{
    [Fact, Trait("Category", "AppHost")]
    public void Options_AcceptOnlyTheDocumentedStrictGrammar()
    {
        using var layout = TestLayout.Create();
        var arguments = layout.Arguments("run");

        var options = AppHostOptions.Parse(arguments);

        Assert.Equal(layout.ProfileRoot, options.ProfileRoot);
        Assert.Equal(layout.PostgresBin, options.PostgresBin);
        Assert.Equal(layout.FixturePath, options.FixturePath);
        Assert.Equal(15432, options.Port);
        Assert.Equal(AppHostMode.Run, options.Mode);
    }

    [Theory, Trait("Category", "AppHost")]
    [InlineData("--port=15432")]
    [InlineData("--unknown")]
    [InlineData("--port")]
    public void Options_RejectInlineUnknownAndMissingValues(string replacement)
    {
        using var layout = TestLayout.Create();
        var arguments = layout.Arguments("run").ToList();
        arguments.RemoveRange(arguments.IndexOf("--port"), 2);
        arguments.Add(replacement);

        var error = Assert.Throws<AppHostOptionsException>(() => AppHostOptions.Parse([.. arguments]));

        Assert.NotEmpty(error.ReasonCode);
    }

    [Fact, Trait("Category", "AppHost")]
    public void Options_RejectDuplicatesBeforeAnyPreflightAction()
    {
        using var layout = TestLayout.Create();
        var arguments = layout.Arguments("run").Concat(["--mode", "probe"]).ToArray();

        var error = Assert.Throws<AppHostOptionsException>(() => AppHostOptions.Parse(arguments));

        Assert.Equal("duplicate-option", error.ReasonCode);
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task StartAndStop_UseTheCoordinatorAndEmitTheExactHappyPathOrder()
    {
        var executor = new RecordingExecutor();
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor);

        await orchestrator.StartAsync();

        Assert.Equal(RuntimeState.Ready, orchestrator.State);
        Assert.Equal(
            [
                RuntimeState.AcquiringInstance,
                RuntimeState.ValidatingPayload,
                RuntimeState.PreparingRuntime,
                RuntimeState.StartingDatabase,
                RuntimeState.WaitingForDatabase,
                RuntimeState.Migrating,
                RuntimeState.StartingServer,
                RuntimeState.WaitingForServer,
                RuntimeState.StartingWorker,
                RuntimeState.WaitingForWorker,
                RuntimeState.Ready,
            ],
            orchestrator.StateHistory);

        await orchestrator.StopAsync();

        Assert.Equal(RuntimeState.Stopped, orchestrator.State);
        Assert.Equal(
            [
                LifecycleCommandType.DrainWorker,
                LifecycleCommandType.StopWorker,
                LifecycleCommandType.StopServer,
                LifecycleCommandType.StopDatabaseFast,
                LifecycleCommandType.ReleaseInstance,
            ],
            executor.StopCommands);
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task ServerFixture_AuthenticatesControlAndReportsTheSameHealthIdentity()
    {
        var operationId = Guid.NewGuid();
        var generation = new ProcessGeneration(RuntimeRole.Server, 3, operationId);
        var healthPort = ReserveLoopbackPort();
        await using var channel = WindowsControlChannel.CreateBeforeLaunch(
            RuntimeRole.Server,
            generation.Value,
            operationId,
            ControlProtocolConstants.FixtureBuildIdentity,
            TimeSpan.FromSeconds(10));
        using var job = WindowsJobObject.CreateKillOnClose();
        var childEnvironment = channel.CreateChildEnvironment();
        var visibleEnvironment = new Dictionary<string, string>(childEnvironment.Environment, StringComparer.Ordinal)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")
                ?? throw new InvalidOperationException("SystemRoot is unavailable."),
        };
        var fixturePath = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");
        var specification = new LaunchSpecification(
            RuntimeRole.Server,
            generation,
            fixturePath,
            ["server", healthPort.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            Path.GetDirectoryName(fixturePath)!,
            visibleEnvironment,
            childEnvironment.SecretEnvironment,
            TimeSpan.FromSeconds(5));
        var launcher = new WindowsProcessLauncher(NoopDiagnosticSink.Instance);
        await using var process = await launcher.LaunchAsync(specification, job, CancellationToken.None);

        var accept = channel.AcceptAsync(process, job);
        var first = await Task.WhenAny(accept, process.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(
            first == accept,
            $"Fixture exited before control authentication. stdout={process.StandardOutput.Snapshot()} stderr={process.StandardError.Snapshot()}");
        await accept;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var rejected = await http.GetAsync($"http://127.0.0.1:{healthPort}/health");
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        var health = await GetFixtureHealthAsync(http, healthPort, channel);

        Assert.NotNull(health);
        Assert.Equal(ControlProtocolConstants.CurrentVersion, health.ProtocolVersion);
        Assert.Equal(ControlProtocolConstants.FixtureBuildIdentity, health.BuildIdentity);
        Assert.Equal(generation.Value, health.Generation);
        Assert.Equal(channel.EndpointIdentity.CapabilityNonce, health.GenerationNonce);
        Assert.Equal("ready", health.Status);

        var shutdownId = Guid.NewGuid();
        await channel.SendAsync(CreateEmptyEnvelope(
            ControlMessageType.Shutdown,
            shutdownId,
            generation));
        Assert.Equal(ControlMessageType.ShutdownAccepted, (await channel.ReadAsync()).Type);
        Assert.Equal(ControlMessageType.Stopped, (await channel.ReadAsync()).Type);
        Assert.Equal(0, await process.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task HealthServer_RejectsExcessClientsAndDisposesStalledHeadersWithinDeadline()
    {
        var port = ReserveLoopbackPort();
        await using var server = new FixtureHealthServer(
            port,
            new HealthPayload(
                ControlProtocolConstants.CurrentVersion,
                ControlProtocolConstants.FixtureBuildIdentity,
                1,
                "nonce",
                "ready",
                0));
        var clients = new List<TcpClient>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                await client.GetStream().WriteAsync("GET /health HTTP/1.1\r\n"u8.ToArray());
                clients.Add(client);
            }

            var excess = new TcpClient();
            clients.Add(excess);
            await excess.ConnectAsync(IPAddress.Loopback, port);
            var buffer = new byte[1];
            try
            {
                Assert.Equal(0, await excess.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            }
            catch (IOException)
            {
                // An immediate reset is also a bounded rejection.
            }

            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Theory, Trait("Category", "AppHost")]
    [InlineData("crash-before-hello", 90)]
    [InlineData("crash-after-hello", 91)]
    public async Task FixtureCrashBeforeOrAfterHello_NeverReachesReadiness(string mode, int exitCode)
    {
        var operationId = Guid.NewGuid();
        var generation = new ProcessGeneration(RuntimeRole.Server, 3, operationId);
        var healthPort = ReserveLoopbackPort();
        await using var channel = WindowsControlChannel.CreateBeforeLaunch(
            RuntimeRole.Server,
            generation.Value,
            operationId,
            ControlProtocolConstants.FixtureBuildIdentity,
            TimeSpan.FromSeconds(2));
        using var job = WindowsJobObject.CreateKillOnClose();
        var childEnvironment = channel.CreateChildEnvironment();
        var visibleEnvironment = new Dictionary<string, string>(childEnvironment.Environment, StringComparer.Ordinal)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
        };
        var fixturePath = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");
        var specification = new LaunchSpecification(
            RuntimeRole.Server,
            generation,
            fixturePath,
            [mode, healthPort.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            Path.GetDirectoryName(fixturePath)!,
            visibleEnvironment,
            childEnvironment.SecretEnvironment,
            TimeSpan.FromSeconds(5));
        await using var process = await new WindowsProcessLauncher(NoopDiagnosticSink.Instance)
            .LaunchAsync(specification, job, CancellationToken.None);

        try
        {
            await channel.AcceptAsync(process, job).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
        }

        Assert.Equal(exitCode, await process.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task BundledFixture_RejectsAHostExpectedBuildThatDiffersFromItsEmbeddedBuild()
    {
        var operationId = Guid.NewGuid();
        var generation = new ProcessGeneration(RuntimeRole.Server, 3, operationId);
        await using var channel = WindowsControlChannel.CreateBeforeLaunch(
            RuntimeRole.Server,
            generation.Value,
            operationId,
            "wrong-host-build",
            TimeSpan.FromSeconds(2));
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchFixtureAsync(
            "server",
            ReserveLoopbackPort(),
            generation,
            channel,
            job);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            channel.AcceptAsync(process, job).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(11, await process.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task FixturePipeTimeout_IsBoundedAndHealthMismatchIsRejected()
    {
        await AssertFixturePipeTimeoutAsync();

        var operationId = Guid.NewGuid();
        var generation = new ProcessGeneration(RuntimeRole.Server, 4, operationId);
        var healthPort = ReserveLoopbackPort();
        await using var channel = WindowsControlChannel.CreateBeforeLaunch(
            RuntimeRole.Server,
            generation.Value,
            operationId,
            ControlProtocolConstants.FixtureBuildIdentity,
            TimeSpan.FromSeconds(2));
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchFixtureAsync(
            "health-mismatch",
            healthPort,
            generation,
            channel,
            job);
        await channel.AcceptAsync(process, job).WaitAsync(TimeSpan.FromSeconds(5));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            GetFixtureHealthAsync(http, healthPort, channel));
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task WorkerFixture_DuplicateDrainOperationReturnsTheSameAcknowledgements()
    {
        var startupOperation = Guid.NewGuid();
        var generation = new ProcessGeneration(RuntimeRole.Worker, 6, startupOperation);
        await using var channel = WindowsControlChannel.CreateBeforeLaunch(
            RuntimeRole.Worker,
            generation.Value,
            startupOperation,
            ControlProtocolConstants.FixtureBuildIdentity,
            TimeSpan.FromSeconds(2));
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchFixtureAsync(
            "worker",
            ReserveLoopbackPort(),
            generation,
            channel,
            job);
        await channel.AcceptAsync(process, job).WaitAsync(TimeSpan.FromSeconds(5));
        var drainOperation = Guid.NewGuid();
        var first = new List<ControlEnvelope>();
        var duplicate = new List<ControlEnvelope>();

        foreach (var replies in new[] { first, duplicate })
        {
            await channel.SendAsync(CreateEmptyEnvelope(
                ControlMessageType.Drain,
                drainOperation,
                generation));
            for (var index = 0; index < 4; index++)
            {
                replies.Add(await channel.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        Assert.Equal(first.Select(reply => reply.Type), duplicate.Select(reply => reply.Type));
        Assert.Equal(
            first.Select(reply => reply.Payload.GetRawText()),
            duplicate.Select(reply => reply.Payload.GetRawText()));
        Assert.All(duplicate, reply => Assert.Equal(drainOperation, reply.OperationId));

        var shutdown = Guid.NewGuid();
        var shutdownEnvelope = CreateEmptyEnvelope(ControlMessageType.Shutdown, shutdown, generation);
        await channel.SendAsync(shutdownEnvelope);
        await channel.SendAsync(shutdownEnvelope);
        Assert.Equal(ControlMessageType.ShutdownAccepted, (await channel.ReadAsync()).Type);
        Assert.Equal(ControlMessageType.Stopped, (await channel.ReadAsync()).Type);
        Assert.Equal(ControlMessageType.ShutdownAccepted, (await channel.ReadAsync()).Type);
        Assert.Equal(ControlMessageType.Stopped, (await channel.ReadAsync()).Type);
        Assert.Equal(0, await process.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task AssertFixturePipeTimeoutAsync()
    {
        var operationId = Guid.NewGuid();
        var generation = new ProcessGeneration(RuntimeRole.Server, 5, operationId);
        await using var channel = WindowsControlChannel.CreateBeforeLaunch(
            RuntimeRole.Server,
            generation.Value,
            operationId,
            ControlProtocolConstants.FixtureBuildIdentity,
            TimeSpan.FromMilliseconds(250));
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchFixtureAsync(
            "pipe-timeout",
            ReserveLoopbackPort(),
            generation,
            channel,
            job);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            channel.AcceptAsync(process, job).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<ISupervisedProcess> LaunchFixtureAsync(
        string mode,
        int healthPort,
        ProcessGeneration generation,
        WindowsControlChannel channel,
        WindowsJobObject job)
    {
        var childEnvironment = channel.CreateChildEnvironment();
        var visibleEnvironment = new Dictionary<string, string>(childEnvironment.Environment, StringComparer.Ordinal)
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
        };
        var fixturePath = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");
        return await new WindowsProcessLauncher(NoopDiagnosticSink.Instance).LaunchAsync(
            new LaunchSpecification(
                generation.Role,
                generation,
                fixturePath,
                [mode, healthPort.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                Path.GetDirectoryName(fixturePath)!,
                visibleEnvironment,
                childEnvironment.SecretEnvironment,
                TimeSpan.FromSeconds(5)),
            job,
            CancellationToken.None);
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task StartupFailure_RollsBackOnlyTheFailingOperation()
    {
        var executor = new FailingExecutor(LifecycleCommandType.StartServer);
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor);

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.StartAsync());

        Assert.NotEqual(Guid.Empty, executor.RolledBackOperationId);
        Assert.All(executor.Commands, command =>
            Assert.Equal(executor.RolledBackOperationId, command.OperationId));
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task RealDisposableRuntime_StartsMigratesFixturesAndStopsWithoutLeaks()
    {
        var layout = TestLayout.CreateReal();
        var options = AppHostOptions.Parse(layout.Arguments("run"));
        var orchestrator = AppHostComposition.Create(options);

        try
        {
            await orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(RuntimeState.Ready, orchestrator.State);
            Assert.True(File.Exists(Path.Combine(layout.ProfileRoot, "postgres-data", "PG_VERSION")));
        }
        finally
        {
            await orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            await orchestrator.DisposeAsync();
        }

        Assert.False(File.Exists(Path.Combine(layout.ProfileRoot, "postgres-data", "postmaster.pid")));
        var profile = DataProfileIdentity.Create(layout.ProfileRoot);
        var reacquired = await UserProfileInstanceLock.TryAcquireAsync(profile, CancellationToken.None);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
        using (File.Open(
            Path.Combine(layout.ProfileRoot, "runtime", "profile.lock"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
        }
        layout.Dispose();
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task ExistingCluster_WithConclusiveStalePidFile_RepairsAndRestarts()
    {
        using var layout = TestLayout.CreateReal();
        var options = AppHostOptions.Parse(layout.Arguments("run"));
        var first = AppHostComposition.Create(options);
        await first.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
        await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
        await first.DisposeAsync();

        var dataDirectory = Path.Combine(layout.ProfileRoot, "postgres-data");
        var pidPath = Path.Combine(dataDirectory, "postmaster.pid");
        await File.WriteAllTextAsync(
            pidPath,
            $"2147483647\n{dataDirectory}\n{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n{layout.Port}\n\n127.0.0.1\n0 0\nready\n");

        var restarted = AppHostComposition.Create(options);
        try
        {
            await restarted.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(RuntimeState.Ready, restarted.State);
        }
        finally
        {
            await restarted.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            await restarted.DisposeAsync();
        }

        Assert.False(File.Exists(pidPath));
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task Probe_IsReadOnlyAndDoesNotCreateOrAcquireProfileState()
    {
        using var layout = TestLayout.CreateReal();
        Directory.Delete(layout.ProfileRoot);
        var options = AppHostOptions.Parse(layout.Arguments("probe"));

        var result = await AppHostComposition.ProbeAsync(options);

        Assert.True(result.IsValid);
        Assert.False(Directory.Exists(layout.ProfileRoot));
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task Startup_RejectsOccupiedPortAfterOwnershipAndBeforeLaunchingAnyChild()
    {
        using var layout = TestLayout.CreateReal();
        using var listener = new TcpListener(IPAddress.Loopback, layout.Port);
        listener.Start();

        var orchestrator = AppHostComposition.Create(AppHostOptions.Parse(layout.Arguments("run")));

        var error = await Assert.ThrowsAsync<AppHostOptionsException>(() => orchestrator.StartAsync());

        Assert.Equal("port-unavailable", error.ReasonCode);
        Assert.False(File.Exists(Path.Combine(layout.ProfileRoot, "postgres-data", "PG_VERSION")));
        await orchestrator.DisposeAsync();
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task Startup_RejectsAnAlreadyOwnedProfileBeforeLaunchingDatabase()
    {
        using var layout = TestLayout.CreateReal();
        var profile = DataProfileIdentity.Create(layout.ProfileRoot);
        await using var owner = Assert.IsType<UserProfileInstanceLock>(
            await UserProfileInstanceLock.TryAcquireAsync(profile, CancellationToken.None));
        var orchestrator = AppHostComposition.Create(
            AppHostOptions.Parse(layout.Arguments("run")));

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() => orchestrator.StartAsync());

        Assert.Equal("profile-already-owned", error.ReasonCode);
        Assert.False(File.Exists(Path.Combine(layout.ProfileRoot, "postgres-data", "PG_VERSION")));
        await orchestrator.DisposeAsync();
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task Startup_NonemptyClusterWithoutRuntimeIdentityFailsClosedBeforeGeneratingCredentials()
    {
        using var layout = TestLayout.CreateReal();
        var dataDirectory = Path.Combine(layout.ProfileRoot, "postgres-data");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "damaged-cluster-marker"), "present");
        var orchestrator = AppHostComposition.Create(AppHostOptions.Parse(layout.Arguments("run")));

        var error = await Assert.ThrowsAsync<ProfileRuntimeIdentityException>(() => orchestrator.StartAsync());

        Assert.Equal("profile-runtime-identity-missing", error.ReasonCode);
        Assert.False(File.Exists(Path.Combine(layout.ProfileRoot, "runtime", "profile-identity-v1.json")));
        Assert.False(File.Exists(Path.Combine(layout.ProfileRoot, "runtime", "postgres-credential-v1.dat")));
        await orchestrator.DisposeAsync();
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public void Composition_RejectsCopiedFixtureOutsideTheBundledFinalPath()
    {
        using var layout = TestLayout.CreateReal();
        var copiedDirectory = Path.Combine(layout.Root, "copied-fixture");
        Directory.CreateDirectory(copiedDirectory);
        var copiedFixture = Path.Combine(copiedDirectory, "HowardLab.EbayCrm.AppHost.Fixture.exe");
        File.Copy(layout.FixturePath, copiedFixture);
        var options = AppHostOptions.Parse(layout.Arguments("run")) with { FixturePath = copiedFixture };

        var error = Assert.Throws<AppHostOptionsException>(() => AppHostComposition.Create(options));

        Assert.Equal("fixture-trust-mismatch", error.ReasonCode);
    }

    [Fact, Trait("Category", "AppHost")]
    public void TrustedFixtureLease_PreventsWriteOrReplacementThroughProcessCreationWindow()
    {
        var fixture = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");

        using var lease = AppHostComposition.OpenTrustedFixtureArtifacts(fixture);

        Assert.Throws<IOException>(() => new FileStream(
            fixture,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete));

        var managedPayload = Path.ChangeExtension(fixture, ".dll");
        Assert.Throws<IOException>(() => new FileStream(
            managedPayload,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete));
    }

    public static TheoryData<string> TrustedRuntimeArtifactSubstitutionCases
    {
        get
        {
            var cases = new TheoryData<string>
            {
                "HowardLab.EbayCrm.AppHost.Fixture.exe",
                "HowardLab.EbayCrm.AppHost.Fixture.dll",
                "HowardLab.EbayCrm.AppHost.Protocol.dll",
                "System.Security.Cryptography.ProtectedData.dll",
            };
            if (AppHostComposition.TrustedFixtureArtifactNames.Contains(
                "hostfxr.dll",
                StringComparer.OrdinalIgnoreCase))
            {
                cases.Add("hostfxr.dll");
            }

            return cases;
        }
    }

    [Theory, Trait("Category", "AppHost")]
    [MemberData(nameof(TrustedRuntimeArtifactSubstitutionCases))]
    public void FixtureArtifactPreflight_RejectsReplacedRuntimeArtifact(string replacedArtifact)
    {
        var fixture = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");
        var sourceDirectory = Path.GetDirectoryName(fixture)!;
        var copiedDirectory = Path.Combine(Path.GetTempPath(), $"fixture-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copiedDirectory);
        try
        {
            foreach (var artifact in AppHostComposition.TrustedFixtureArtifactNames)
            {
                var destination = Path.Combine(copiedDirectory, artifact);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(sourceDirectory, artifact), destination);
            }

            File.WriteAllBytes(Path.Combine(copiedDirectory, replacedArtifact), "replaced-managed-artifact"u8.ToArray());

            var error = Assert.Throws<AppHostOptionsException>(() =>
                AppHostComposition.ValidateTrustedFixtureExecutable(Path.Combine(
                    copiedDirectory,
                    "HowardLab.EbayCrm.AppHost.Fixture.exe")));

            Assert.Equal("fixture-build-mismatch", error.ReasonCode);
        }
        finally
        {
            Directory.Delete(copiedDirectory, recursive: true);
        }
    }

    [Fact, Trait("Category", "AppHost")]
    public void TestComposition_RejectsProfilesOutsideTheDisposableTempBoundary()
    {
        var nonTemporaryProfile = Path.Combine(
            Path.GetPathRoot(Path.GetTempPath())!,
            $"ebaycrm-task9-nondisposable-{Guid.NewGuid():N}");
        var options = new AppHostOptions(
            nonTemporaryProfile,
            Path.Combine(nonTemporaryProfile, "postgres"),
            Path.Combine(nonTemporaryProfile, "HowardLab.EbayCrm.AppHost.Fixture.exe"),
            15432,
            AppHostMode.Run);

        var error = Assert.Throws<AppHostOptionsException>(() => AppHostComposition.CreateForTests(options));

        Assert.Equal("test-profile-not-disposable", error.ReasonCode);
        Assert.False(Directory.Exists(nonTemporaryProfile));
    }

    [PostgresFact, Trait("Category", "AppHost")]
    public async Task ExecutableProbe_UsesTheDocumentedCliAndLeavesProfileUntouched()
    {
        using var layout = TestLayout.CreateReal();
        Directory.Delete(layout.ProfileRoot);
        var executable = Path.ChangeExtension(typeof(AppHostComposition).Assembly.Location, ".exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in layout.Arguments("probe"))
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch the AppHost executable.");

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("probe-valid", output, StringComparison.Ordinal);
        Assert.Empty(error);
        Assert.False(Directory.Exists(layout.ProfileRoot));
    }

    [PostgresFact, Trait("Category", "AppHost"), Trait("Category", "DestructiveContainment")]
    public async Task ExecutableRun_ReachesReadyAndColdRestartsAfterHostDeath()
    {
        using var layout = TestLayout.CreateReal();
        var executable = Path.ChangeExtension(typeof(AppHostComposition).Assembly.Location, ".exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in layout.Arguments("run"))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch the AppHost executable.");
        string? line;
        do
        {
            line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(2));
            if (line is null)
            {
                var standardError = await process.StandardError.ReadToEndAsync();
                throw new Xunit.Sdk.XunitException(
                    $"AppHost exited before Ready with code {process.ExitCode}: {standardError}");
            }
        }
        while (!StringComparer.Ordinal.Equals(line, RuntimeState.Ready.ToString()));

        var pidPath = Path.Combine(layout.ProfileRoot, "postgres-data", "postmaster.pid");
        var pid = PostmasterPidFile.Read(pidPath, Path.GetDirectoryName(pidPath)!);
        using var postmaster = Process.GetProcessById(pid.ProcessId);
        _ = postmaster.SafeHandle;

        process.Kill(entireProcessTree: false);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await postmaster.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        postmaster.Dispose();

        var restarted = AppHostComposition.Create(
            AppHostOptions.Parse(layout.Arguments("run")));
        try
        {
            await restarted.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(RuntimeState.Ready, restarted.State);
        }
        finally
        {
            await restarted.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));
            await restarted.DisposeAsync();
        }

        Assert.False(File.Exists(pidPath));
    }

    [Theory, Trait("Category", "AppHost")]
    [InlineData(LifecycleCommandType.AcquireInstance)]
    [InlineData(LifecycleCommandType.ValidatePayload)]
    [InlineData(LifecycleCommandType.PrepareRuntime)]
    [InlineData(LifecycleCommandType.StartDatabase)]
    [InlineData(LifecycleCommandType.WaitForDatabase)]
    [InlineData(LifecycleCommandType.RunMigrations)]
    [InlineData(LifecycleCommandType.StartServer)]
    [InlineData(LifecycleCommandType.WaitForServer)]
    [InlineData(LifecycleCommandType.StartWorker)]
    [InlineData(LifecycleCommandType.WaitForWorker)]
    public async Task StartupCancellationAtEveryStage_RollsBackOnlyThatOperation(
        LifecycleCommandType canceledAt)
    {
        var executor = new CancelingExecutor(canceledAt);
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.StartAsync());

        Assert.NotEqual(Guid.Empty, executor.RolledBackOperationId);
        Assert.All(executor.Commands, command =>
            Assert.Equal(executor.RolledBackOperationId, command.OperationId));
        Assert.Equal(canceledAt, executor.Commands[^1].Type);
    }

    [Fact, Trait("Category", "AppHost")]
    public async Task StartupFailureAndRollbackFailure_AreBothPreserved()
    {
        var executor = new FailingRollbackExecutor();
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        await using var orchestrator = new RuntimeOrchestrator(coordinator, executor);

        var error = await Assert.ThrowsAsync<AggregateException>(() => orchestrator.StartAsync());

        Assert.Collection(
            error.InnerExceptions,
            startup => Assert.Equal("injected-startup-failure", startup.Message),
            rollback => Assert.Equal("injected-rollback-failure", rollback.Message));
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

    private static async Task<HealthPayload?> GetFixtureHealthAsync(
        HttpClient http,
        int port,
        WindowsControlChannel channel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/health");
        request.Headers.TryAddWithoutValidation("X-AppHost-Protocol", ControlProtocolConstants.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-AppHost-Build", channel.EndpointIdentity.ExpectedBuildIdentity);
        request.Headers.TryAddWithoutValidation("X-AppHost-Generation", channel.EndpointIdentity.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-AppHost-Nonce", channel.EndpointIdentity.CapabilityNonce);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HealthPayload>();
    }

    private static ControlEnvelope CreateEmptyEnvelope(
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

    private sealed class RecordingExecutor : ILifecycleCommandExecutor
    {
        internal List<LifecycleCommandType> StopCommands { get; } = [];

        public Task<LifecycleEvent?> ExecuteAsync(
            LifecycleCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LifecycleEvent? result = command.Type switch
            {
                LifecycleCommandType.AcquireInstance => new InstanceAcquired(command.OperationId),
                LifecycleCommandType.ValidatePayload => new PayloadValidated(command.OperationId),
                LifecycleCommandType.PrepareRuntime => new RuntimePrepared(command.OperationId),
                LifecycleCommandType.StartDatabase or
                LifecycleCommandType.StartServer or
                LifecycleCommandType.StartWorker => new RoleStarted(command.Generation!.Value),
                LifecycleCommandType.WaitForDatabase or
                LifecycleCommandType.WaitForServer or
                LifecycleCommandType.WaitForWorker => new RoleReady(command.Generation!.Value),
                LifecycleCommandType.RunMigrations => new MigrationCompleted(command.OperationId),
                _ => null,
            };
            if (command.Type is LifecycleCommandType.DrainWorker or
                LifecycleCommandType.StopWorker or
                LifecycleCommandType.StopServer or
                LifecycleCommandType.StopDatabaseFast or
                LifecycleCommandType.ReleaseInstance)
            {
                StopCommands.Add(command.Type);
            }

            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingExecutor(LifecycleCommandType failure) : ILifecycleCommandExecutor
    {
        internal List<LifecycleCommand> Commands { get; } = [];
        internal Guid RolledBackOperationId { get; private set; }

        public Task<LifecycleEvent?> ExecuteAsync(
            LifecycleCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (command.Type == failure)
            {
                throw new InvalidOperationException("injected-startup-failure");
            }

            LifecycleEvent? result = command.Type switch
            {
                LifecycleCommandType.AcquireInstance => new InstanceAcquired(command.OperationId),
                LifecycleCommandType.ValidatePayload => new PayloadValidated(command.OperationId),
                LifecycleCommandType.PrepareRuntime => new RuntimePrepared(command.OperationId),
                LifecycleCommandType.StartDatabase or LifecycleCommandType.StartServer =>
                    new RoleStarted(command.Generation!.Value),
                LifecycleCommandType.WaitForDatabase => new RoleReady(command.Generation!.Value),
                LifecycleCommandType.RunMigrations => new MigrationCompleted(command.OperationId),
                _ => null,
            };
            return Task.FromResult(result);
        }

        public Task RollbackAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            RolledBackOperationId = operationId;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancelingExecutor(LifecycleCommandType canceledAt) : ILifecycleCommandExecutor
    {
        internal List<LifecycleCommand> Commands { get; } = [];
        internal Guid RolledBackOperationId { get; private set; }

        public Task<LifecycleEvent?> ExecuteAsync(
            LifecycleCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (command.Type == canceledAt)
            {
                throw new OperationCanceledException("injected-startup-cancellation");
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
                _ => null,
            };
            return Task.FromResult(result);
        }

        public Task RollbackAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            RolledBackOperationId = operationId;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingRollbackExecutor : ILifecycleCommandExecutor
    {
        private int _executionCount;

        public Task<LifecycleEvent?> ExecuteAsync(
            LifecycleCommand command,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _executionCount) == 1)
            {
                throw new InvalidOperationException("injected-startup-failure");
            }

            return Task.FromResult<LifecycleEvent?>(null);
        }

        public Task RollbackAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("injected-rollback-failure");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopDiagnosticSink : IDiagnosticSink
    {
        internal static NoopDiagnosticSink Instance { get; } = new();

        public ValueTask WriteAsync(
            DiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
