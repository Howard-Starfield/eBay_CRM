using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Core.Time;
using HowardLab.EbayCrm.AppHost.Fixture;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using HowardLab.EbayCrm.AppHost.Windows.Payload;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

public sealed class RoleLaunchPlanProviderTests
{
    [PostgresFact]
    public async Task CreateForTests_UsesTheExplicitFixtureTargetOrExactlyTheInjectedProvider()
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
            Assert.Equal(AppHostRoleTarget.ControlledFixture, options.RoleTarget);
            Assert.IsType<FixtureRoleLaunchPlanProvider>(defaultRuntime.ActiveRoleLaunchPlanProvider);
            Assert.Same(defaultRuntime.FixtureRoleLaunchPlanProvider, defaultRuntime.ActiveRoleLaunchPlanProvider);
            Assert.Same(injected, injectedRuntime.ActiveRoleLaunchPlanProvider);
            Assert.Null(injectedRuntime.FixtureRoleLaunchPlanProvider);
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

    [Fact]
    public async Task NullPayloadClosureVerifierFailsBeforeLeaseOrLaunch()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var lease = new TrackingLease();
        var launcher = new RecordingLauncher((_, _, _) =>
            throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                leaseFactory: () => lease.Open(),
                useNullPayloadClosureVerifier: true)),
            launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.False(lease.WasOpened);
        Assert.Empty(launcher.Specifications);
    }

    [Fact]
    public async Task NullPayloadLifetimeLeaseFactoryFailsBeforeAnyLeaseOrLaunch()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var bootstrapLease = new TrackingLease();
        var launcher = new RecordingLauncher((_, _, _) =>
            throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                leaseFactory: () => bootstrapLease.Open(),
                useNullPayloadLifetimeLeaseFactory: true)),
            launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.False(bootstrapLease.WasOpened);
        Assert.Empty(launcher.Specifications);
    }

    [Fact]
    public async Task PayloadLifetimeTrustFailureIsSanitizedBeforeBootstrapLeaseOrLaunch()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var bootstrapLease = new TrackingLease();
        var launcher = new RecordingLauncher((_, _, _) =>
            throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                payloadLifetimeLeaseFactory: () =>
                    throw new HowardLab.EbayCrm.AppHost.Windows.Payload.NodePayloadManifestException(),
                leaseFactory: () => bootstrapLease.Open())),
            launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Null(error.InnerException);
        Assert.False(bootstrapLease.WasOpened);
        Assert.Empty(launcher.Specifications);
    }

    [Fact]
    public async Task NullPayloadLifetimeLeaseResultIsInvalidBeforeBootstrapLeaseOrLaunch()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var bootstrapLease = new TrackingLease();
        var launcher = new RecordingLauncher((_, _, _) =>
            throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                payloadLifetimeLeaseFactory: () => null!,
                leaseFactory: () => bootstrapLease.Open())),
            launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.False(bootstrapLease.WasOpened);
        Assert.Empty(launcher.Specifications);
    }

    [Fact]
    public async Task NonWindowsLauncherContractBreachDoesNotVerifyPayloadClosure()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        var payloadLifetimeLease = new TrackingLease();
        var launcher = new RecordingLauncher((_, _, _) =>
            ValueTask.FromResult<ISupervisedProcess>(new StubProcess(generation)));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                payloadLifetimeLeaseFactory: () => payloadLifetimeLease.Open(),
                verifyPayloadClosureAfterShutdown: () => verificationCount++)),
            launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-process-type-invalid", error.ReasonCode);
        Assert.Equal(0, verificationCount);
        Assert.True(payloadLifetimeLease.IsDisposed);
    }

    [Fact]
    public async Task NonWindowsCleanupUncertaintyRetainsPayloadLifetimeLease()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var payloadLifetimeLease = new TrackingLease();
        var launcher = new RecordingLauncher((_, _, _) =>
            ValueTask.FromResult<ISupervisedProcess>(new StubProcess(
                generation,
                new InvalidOperationException("simulated uncertain cleanup"))));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                payloadLifetimeLeaseFactory: () => payloadLifetimeLease.Open())),
            launcher);

        _ = await Assert.ThrowsAnyAsync<Exception>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.False(payloadLifetimeLease.IsDisposed);
    }

    [Fact]
    public async Task LaunchFailureWithoutChildReleasesPayloadLifetimeLease()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var payloadLifetimeLease = new TrackingLease();
        var launcher = new RecordingLauncher((_, _, _) =>
            throw new InvalidOperationException("simulated launch failure"));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                payloadLifetimeLeaseFactory: () => payloadLifetimeLease.Open())),
            launcher);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.True(payloadLifetimeLease.IsDisposed);
    }

    [Fact]
    public async Task BootstrapTrustFailureAfterRuntimeLeaseReleasesRuntimeAndNeverLaunches()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var payloadLifetimeLease = new TrackingLease();
        var launcher = new RecordingLauncher((_, _, _) =>
            throw new InvalidOperationException("must not launch"));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                payloadLifetimeLeaseFactory: () => payloadLifetimeLease.Open(),
                leaseFactory: () =>
                {
                    Assert.True(payloadLifetimeLease.WasOpened);
                    Assert.False(payloadLifetimeLease.IsDisposed);
                    throw new HowardLab.EbayCrm.AppHost.Windows.Payload.NodePayloadManifestException();
                })),
            launcher);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.True(payloadLifetimeLease.IsDisposed);
        Assert.Empty(launcher.Specifications);
    }

    [Fact]
    public async Task RealWindowsChild_DisposeWaitsForExitAndVerifiesExactlyOnce()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        var payloadLifetimeLease = new TrackingLease();
        var healthPort = ReserveLoopbackPort();
        var fixturePath = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")!;
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                arguments: ["server", healthPort.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                applicationPath: fixturePath,
                workingDirectory: Path.GetDirectoryName(fixturePath),
                environment: new Dictionary<string, string> { ["SystemRoot"] = systemRoot },
                secretEnvironment: new Dictionary<string, SecretValue>(),
                buildIdentity: ControlProtocolConstants.FixtureBuildIdentity,
                healthPort: healthPort,
                payloadLifetimeLeaseFactory: () => payloadLifetimeLease.Open(),
                verifyPayloadClosureAfterShutdown: () =>
                {
                    Assert.False(payloadLifetimeLease.IsDisposed);
                    Interlocked.Increment(ref verificationCount);
                })),
            launcher: null);

        var started = await harness.Executor.ExecuteAsync(StartCommand(generation));

        Assert.IsType<RoleStarted>(started);
        Assert.True(payloadLifetimeLease.WasOpened);
        Assert.False(payloadLifetimeLease.IsDisposed);
        Assert.Equal(0, Volatile.Read(ref verificationCount));
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => harness.Executor.DisposeAsync().AsTask()));
        Assert.Equal(1, Volatile.Read(ref verificationCount));
        Assert.True(payloadLifetimeLease.IsDisposed);
        await harness.Executor.DisposeAsync();
        Assert.Equal(1, Volatile.Read(ref verificationCount));
    }

    [Fact]
    public async Task CleanupIndeterminateLaunch_RetainsTrustedPayloadUntilAuthoritativeExit()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var fixturePath = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");
        var payloadContainer = Path.Combine(
            Path.GetTempPath(),
            $"cleanup-indeterminate-payload-{Guid.NewGuid():N}");
        var payloadRoot = Path.Combine(payloadContainer, "payload");
        var profileRoot = Path.Combine(payloadContainer, "profile");
        Directory.CreateDirectory(profileRoot);
        var payload = CreateTrustedPayload(payloadRoot, profileRoot);
        var cleanup = new BlockingIndeterminateCleanupPolicy();
        var identityVerifier = new ThrowingIdentityVerifier();
        var launcher = new WindowsProcessLauncher(
            NoopDiagnosticSink.Instance,
            maxOutputBytes: 64 * 1024,
            maxLineBytes: 4 * 1024,
            cleanup,
            identityVerifier);
        var verificationCount = 0;
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                arguments: ["hold"],
                applicationPath: fixturePath,
                workingDirectory: Path.GetDirectoryName(fixturePath),
                environment: new Dictionary<string, string>
                {
                    ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
                },
                secretEnvironment: new Dictionary<string, SecretValue>(),
                payloadLifetimeLeaseFactory: payload.OpenLifetimeLease,
                verifyPayloadClosureAfterShutdown: () =>
                {
                    payload.VerifyClosure();
                    Interlocked.Increment(ref verificationCount);
                })),
            launcher);
        var renamedRoot = payloadRoot + "-renamed";

        var startTask = Task.Run(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));
        await cleanup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            payload.Dispose();
            Assert.ThrowsAny<IOException>(() => Directory.Move(payloadRoot, renamedRoot));
        }
        finally
        {
            cleanup.Release.TrySetResult();
        }

        var error = await Assert.ThrowsAsync<ProcessCleanupException>(() => startTask);
        Assert.NotNull(error.AuthoritativeNativeExitObservation);
        await error.AuthoritativeNativeExitObservation.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => Volatile.Read(ref verificationCount) == 1,
            TimeSpan.FromSeconds(5));

        Directory.Move(payloadRoot, renamedRoot);
        Assert.Equal(1, Volatile.Read(ref verificationCount));
        Directory.Delete(payloadContainer, recursive: true);
    }

    [Fact]
    public async Task CleanupIndeterminateResult_RetainsTrustedPayloadUntilCarriedExitProof()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var payloadContainer = Path.Combine(
            Path.GetTempPath(),
            $"cleanup-indeterminate-result-{Guid.NewGuid():N}");
        var payloadRoot = Path.Combine(payloadContainer, "payload");
        var profileRoot = Path.Combine(payloadContainer, "profile");
        Directory.CreateDirectory(profileRoot);
        var payload = CreateTrustedPayload(payloadRoot, profileRoot);
        var nativeExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var verificationCount = 0;
        var launcher = new RecordingLauncher((_, _, _) =>
            throw new ProcessCleanupException(
                RuntimeRole.Server,
                processTerminationErrorCode: null,
                jobTerminationErrorCode: null,
                waitErrorCode: null,
                timedOut: true,
                nativeExit.Task));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                payloadLifetimeLeaseFactory: payload.OpenLifetimeLease,
                verifyPayloadClosureAfterShutdown: () =>
                {
                    payload.VerifyClosure();
                    Interlocked.Increment(ref verificationCount);
                })),
            launcher);

        _ = await Assert.ThrowsAsync<ProcessCleanupException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));
        payload.Dispose();
        var renamedRoot = payloadRoot + "-renamed";

        Assert.ThrowsAny<IOException>(() => Directory.Move(payloadRoot, renamedRoot));

        nativeExit.SetResult();
        await WaitUntilAsync(
            () => Volatile.Read(ref verificationCount) == 1,
            TimeSpan.FromSeconds(5));
        Directory.Move(payloadRoot, renamedRoot);
        Assert.Equal(1, Volatile.Read(ref verificationCount));
        Directory.Delete(payloadContainer, recursive: true);
    }

    [Fact]
    public async Task CleanupIndeterminateProofUnavailable_RetainsPayloadLifetimeFailSafe()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var never = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var payloadLifetimeLease = new TrackingLease();
        var launcher = new RecordingLauncher((_, _, _) =>
            throw new ProcessCleanupException(
                RuntimeRole.Server,
                processTerminationErrorCode: null,
                jobTerminationErrorCode: null,
                waitErrorCode: null,
                timedOut: true,
                never,
                NativeExitObservationKind.ProofUnavailableContainment));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                payloadLifetimeLeaseFactory: payloadLifetimeLease.Open,
                verifyPayloadClosureAfterShutdown: () =>
                    throw new InvalidOperationException("must not verify without exit proof"))),
            launcher);

        var error = await Assert.ThrowsAsync<ProcessCleanupException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal(
            NativeExitObservationKind.ProofUnavailableContainment,
            error.NativeExitObservationKind);
        Assert.True(payloadLifetimeLease.WasOpened);
        Assert.False(payloadLifetimeLease.IsDisposed);
        await Task.Delay(100);
        Assert.False(payloadLifetimeLease.IsDisposed);
    }

    [Fact]
    public async Task RealWindowsChild_LeaseReleaseFailureVerifiesAfterCleanup()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        var healthPort = ReserveLoopbackPort();
        var fixturePath = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")!;
        var lease = new TrackingLease(new InvalidOperationException("untrusted-lease-detail"));
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                arguments: ["server", healthPort.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                applicationPath: fixturePath,
                workingDirectory: Path.GetDirectoryName(fixturePath),
                environment: new Dictionary<string, string> { ["SystemRoot"] = systemRoot },
                secretEnvironment: new Dictionary<string, SecretValue>(),
                leaseFactory: () => lease.Open(),
                buildIdentity: ControlProtocolConstants.FixtureBuildIdentity,
                healthPort: healthPort,
                verifyPayloadClosureAfterShutdown: () =>
                    Interlocked.Increment(ref verificationCount))),
            launcher: null);

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.DoesNotContain("untrusted-lease-detail", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref verificationCount));
        await harness.Executor.DisposeAsync();
        Assert.Equal(1, Volatile.Read(ref verificationCount));
    }

    [Fact]
    public async Task RealWindowsChild_ExitRunsPostExitBoundaryBeforeCleanupAndOnlyOnce()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        var payloadLifetimeLease = new TrackingLease();
        var healthPort = ReserveLoopbackPort();
        var fixturePath = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")!;
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                arguments: ["crash-after-hello", healthPort.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                applicationPath: fixturePath,
                workingDirectory: Path.GetDirectoryName(fixturePath),
                environment: new Dictionary<string, string> { ["SystemRoot"] = systemRoot },
                secretEnvironment: new Dictionary<string, SecretValue>(),
                buildIdentity: ControlProtocolConstants.FixtureBuildIdentity,
                healthPort: healthPort,
                payloadLifetimeLeaseFactory: () => payloadLifetimeLease.Open(),
                verifyPayloadClosureAfterShutdown: () =>
                {
                    Assert.False(payloadLifetimeLease.IsDisposed);
                    Interlocked.Increment(ref verificationCount);
                })),
            launcher: null);

        var result = await harness.Executor.ExecuteAsync(StartCommand(generation));

        Assert.IsType<RoleStarted>(result);
        await WaitUntilAsync(
            () => Volatile.Read(ref verificationCount) == 1,
            TimeSpan.FromSeconds(5));
        Assert.True(payloadLifetimeLease.IsDisposed);
        await harness.Executor.DisposeAsync();
        Assert.Equal(1, Volatile.Read(ref verificationCount));
    }

    [Fact]
    public async Task RealWindowsChild_EscalationWithCanceledCallerStillVerifiesAfterExit()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        var healthPort = ReserveLoopbackPort();
        var fixturePath = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")!;
        await using var harness = CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                arguments: ["server", healthPort.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                applicationPath: fixturePath,
                workingDirectory: Path.GetDirectoryName(fixturePath),
                environment: new Dictionary<string, string> { ["SystemRoot"] = systemRoot },
                secretEnvironment: new Dictionary<string, SecretValue>(),
                buildIdentity: ControlProtocolConstants.FixtureBuildIdentity,
                healthPort: healthPort,
                verifyPayloadClosureAfterShutdown: () =>
                    Interlocked.Increment(ref verificationCount))),
            launcher: null);
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));
        using var canceledCaller = new CancellationTokenSource();
        canceledCaller.Cancel();

        var escalationError = await Record.ExceptionAsync(() =>
            harness.Executor.ExecuteAsync(
                new LifecycleCommand(
                    LifecycleCommandType.EscalateJob,
                    Generation: null,
                    Guid.NewGuid(),
                    LifecycleDeadlineKey.None),
                canceledCaller.Token));

        Assert.DoesNotContain(
            "role-process-exit-unconfirmed",
            escalationError?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref verificationCount));
        _ = await harness.Executor.ExecuteAsync(new LifecycleCommand(
            LifecycleCommandType.EscalateJob,
            Generation: null,
            Guid.NewGuid(),
            LifecycleDeadlineKey.None));
        Assert.Equal(1, Volatile.Read(ref verificationCount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedNativeExitWaitFailureStillAllowsPostExitVerification(bool fault)
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        await using var harness = CreateRealWindowsHarness(
            generation,
            () => Interlocked.Increment(ref verificationCount));
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));
        harness.Executor.NativeExitWaitForTests = (_, _, _) => Task.FromException(
            fault
                ? new InvalidOperationException("simulated native-exit fault")
                : new TimeoutException("simulated native-exit timeout"));

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.DisposeRoleForTests(RuntimeRole.Server));

        Assert.Equal("role-process-exit-unconfirmed", error.ReasonCode);
        await WaitUntilAsync(
            () => Volatile.Read(ref verificationCount) == 1,
            TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref verificationCount));
    }

    [Fact]
    public async Task BoundedNativeExitTimeout_LateVerifierFailureReportsSanitizedReasonExactlyOnce()
    {
        const string secret = "late-verifier-secret";
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        using var releaseVerifier = new ManualResetEventSlim();
        var verifierEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = CreateRealWindowsHarness(
            generation,
            () =>
            {
                verifierEntered.TrySetResult();
                releaseVerifier.Wait();
                throw new InvalidOperationException(secret);
            });
        var reported = new List<string>();
        harness.Executor.PayloadPostExitFailureObservedForTests = reasonCode =>
        {
            lock (reported)
            {
                reported.Add(reasonCode);
            }

            return ValueTask.CompletedTask;
        };
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));
        harness.Executor.NativeExitWaitForTests = (_, _, _) =>
            Task.FromException(new TimeoutException("simulated bounded timeout"));

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() =>
            harness.Executor.DisposeRoleForTests(RuntimeRole.Server));

        Assert.Equal("role-process-exit-unconfirmed", error.ReasonCode);
        await verifierEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (reported)
        {
            Assert.Empty(reported);
        }

        releaseVerifier.Set();
        await WaitUntilAsync(
            () =>
            {
                lock (reported)
                {
                    return reported.Count == 1;
                }
            },
            TimeSpan.FromSeconds(5));
        lock (reported)
        {
            Assert.Equal(["role-payload-trust-failed"], reported);
            Assert.DoesNotContain(secret, string.Join('|', reported), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RealWindowsChild_MonitorFailureStillCleansAndVerifies()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        await using var harness = CreateRealWindowsHarness(
            generation,
            () => Interlocked.Increment(ref verificationCount));
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));
        harness.Executor.StopMonitoringCompletedForTests = static _ =>
            Task.FromException(new InvalidOperationException("simulated monitor failure"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Executor.DisposeRoleForTests(RuntimeRole.Server));

        Assert.Equal("simulated monitor failure", error.Message);
        Assert.Equal(1, Volatile.Read(ref verificationCount));
    }

    [Fact]
    public async Task RecoveryStop_NativeServerExitWinsWhileManagedCompletionIsPending()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        var nativeExitObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        WindowsJobObject? launchedJob = null;
        WindowsSupervisedProcess? launchedProcess = null;
        await using var harness = CreateRealWindowsHarness(
            generation,
            () => Interlocked.Increment(ref verificationCount));
        harness.Executor.RoleLaunchedForTests = (role, job, process) =>
        {
            Assert.Equal(RuntimeRole.Server, role);
            launchedJob = job;
            launchedProcess = process;
            process.NativeExitObservedBeforeCompletionForTests = async () =>
            {
                nativeExitObserved.TrySetResult();
                await releaseCompletion.Task;
            };
        };
        harness.Executor.RoleTeardownStartedForTests = role =>
        {
            Assert.Equal(RuntimeRole.Server, role);
            teardownStarted.TrySetResult();
            return Task.CompletedTask;
        };
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));

        Task<LifecycleEvent?>? stop = null;
        try
        {
            launchedJob!.Dispose();
            await nativeExitObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(launchedProcess!.HasExited);
            Assert.False(launchedProcess.Completion.IsCompleted);

            stop = harness.Executor.ExecuteAsync(StopCommand(generation));
            var winner = await Task.WhenAny(stop, teardownStarted.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(teardownStarted.Task, winner);
            Assert.False(launchedProcess.Completion.IsCompleted);
            Assert.Equal(
                launchedProcess.Identity.ProcessId,
                harness.Executor.SnapshotForTests().ServerProcessId);

            releaseCompletion.TrySetResult();
            Assert.Null(await stop);
            Assert.Null(harness.Executor.SnapshotForTests().ServerProcessId);
            Assert.Equal(1, Volatile.Read(ref verificationCount));
        }
        finally
        {
            releaseCompletion.TrySetResult();
            if (stop is not null)
            {
                _ = await Record.ExceptionAsync(() => stop);
            }
        }
    }

    [Fact]
    public async Task RecoveryStop_RechecksNativeWorkerExitAfterDrainTransportCloses()
    {
        var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());
        var verificationCount = 0;
        var stopMonitoringCount = 0;
        WindowsJobObject? launchedJob = null;
        WindowsSupervisedProcess? launchedProcess = null;
        await using var harness = CreateRealWindowsHarness(
            generation,
            () => Interlocked.Increment(ref verificationCount));
        harness.Executor.RoleLaunchedForTests = (role, job, process) =>
        {
            Assert.Equal(RuntimeRole.Worker, role);
            launchedJob = job;
            launchedProcess = process;
        };
        harness.Executor.StopMonitoringCompletedForTests = async role =>
        {
            Assert.Equal(RuntimeRole.Worker, role);
            if (Interlocked.Increment(ref stopMonitoringCount) != 2)
            {
                return;
            }

            launchedJob!.Dispose();
            await launchedProcess!.NativeExitObservation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(launchedProcess.HasExited);
        };
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));

        Assert.Null(await harness.Executor.ExecuteAsync(StopCommand(generation)));

        Assert.Null(harness.Executor.SnapshotForTests().WorkerProcessId);
        Assert.Equal(1, Volatile.Read(ref verificationCount));
    }

    [Fact]
    public async Task StopServer_ControlDisconnectWhileAlive_ReconcilesAuthoritativeNativeExit()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        var transportFailureObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransportFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        WindowsJobObject? launchedJob = null;
        WindowsSupervisedProcess? launchedProcess = null;
        await using var harness = CreateRealWindowsHarness(
            generation,
            () => Interlocked.Increment(ref verificationCount),
            fixtureMode: "control-disconnect");
        harness.Executor.RoleLaunchedForTests = (role, job, process) =>
        {
            Assert.Equal(RuntimeRole.Server, role);
            launchedJob = job;
            launchedProcess = process;
        };
        harness.Executor.RoleTransportFailureObservedForTests = async role =>
        {
            Assert.Equal(RuntimeRole.Server, role);
            transportFailureObserved.TrySetResult();
            await releaseTransportFailure.Task;
        };
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));
        Assert.IsType<RoleReady>(await harness.Executor.ExecuteAsync(WaitCommand(generation)));
        await harness.Executor.WaitForControlDisconnectForTestsAsync(RuntimeRole.Server)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var stop = harness.Executor.ExecuteAsync(StopCommand(generation));
        await transportFailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(launchedProcess!.HasExited);
        launchedJob!.Dispose();
        releaseTransportFailure.TrySetResult();

        Assert.Null(await stop.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(harness.Executor.SnapshotForTests().ServerProcessId);
        Assert.Equal(1, Volatile.Read(ref verificationCount));
    }

    [Fact]
    public async Task DrainWorker_ReplyPipeClosesWhileAlive_TimesOutThenReconcilesNativeExit()
    {
        var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());
        var operationId = Guid.NewGuid();
        var stopGeneration = generation with { OperationId = operationId };
        var verificationCount = 0;
        var transportFailureObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransportFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        WindowsJobObject? launchedJob = null;
        WindowsSupervisedProcess? launchedProcess = null;
        var deadlines = new RoleOperationDeadlines(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(100));
        await using var harness = CreateRealWindowsHarness(
            generation,
            () => Interlocked.Increment(ref verificationCount),
            fixtureMode: "drain-disconnect-after-accepted",
            roleOperationDeadlines: deadlines);
        harness.Executor.RoleLaunchedForTests = (role, job, process) =>
        {
            Assert.Equal(RuntimeRole.Worker, role);
            launchedJob = job;
            launchedProcess = process;
        };
        harness.Executor.RoleTransportFailureObservedForTests = async role =>
        {
            Assert.Equal(RuntimeRole.Worker, role);
            transportFailureObserved.TrySetResult();
            await releaseTransportFailure.Task;
        };
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));

        var drain = harness.Executor.ExecuteAsync(new LifecycleCommand(
            LifecycleCommandType.DrainWorker,
            stopGeneration,
            operationId,
            LifecycleDeadlineKey.WorkerStop));
        await transportFailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(launchedProcess!.HasExited);
        await Task.Delay(deadlines.StopCommand + TimeSpan.FromMilliseconds(100));
        releaseTransportFailure.TrySetResult();

        var timedOut = Assert.IsType<OperationTimedOut>(
            await drain.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(operationId, timedOut.OperationId);
        var reconcile = harness.Executor.ExecuteAsync(new LifecycleCommand(
            LifecycleCommandType.ReconcileRoleStop,
            stopGeneration,
            operationId,
            LifecycleDeadlineKey.RoleReconciliation));
        launchedJob!.Dispose();

        var reconciled = Assert.IsType<Reconciled>(
            await reconcile.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ReconciledState.Stopped, reconciled.State);
        Assert.Null(harness.Executor.SnapshotForTests().WorkerProcessId);
        Assert.Equal(1, Volatile.Read(ref verificationCount));
    }

    [Theory]
    [InlineData(ControlProtocolErrorCode.TruncatedPrefix)]
    [InlineData(ControlProtocolErrorCode.TruncatedPayload)]
    public void RoleTransportClassification_EofTruncationIsTransport(
        ControlProtocolErrorCode errorCode)
    {
        var error = new ControlProtocolException(errorCode, new EndOfStreamException());

        Assert.True(LifecycleCommandExecutor.IsRoleTransportFailure(error));
    }

    [Theory]
    [InlineData(ControlProtocolErrorCode.TruncatedPrefix)]
    [InlineData(ControlProtocolErrorCode.TruncatedPayload)]
    public void RoleTransportClassification_NonEofTruncationIsProtocolFailure(
        ControlProtocolErrorCode errorCode)
    {
        var error = new ControlProtocolException(errorCode, new InvalidDataException("malformed"));

        Assert.False(LifecycleCommandExecutor.IsRoleTransportFailure(error));
    }

    [Fact]
    public void RoleTransportClassification_InvalidDataIsProtocolFailure()
    {
        Assert.False(LifecycleCommandExecutor.IsRoleTransportFailure(
            new InvalidDataException("wrong-direction")));
    }

    [Fact]
    public async Task StopServer_ShutdownAcceptedThenPipeCloses_ReconcilesAuthoritativeNativeExit()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        var transportFailureObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransportFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        WindowsJobObject? launchedJob = null;
        WindowsSupervisedProcess? launchedProcess = null;
        await using var harness = CreateRealWindowsHarness(
            generation,
            () => Interlocked.Increment(ref verificationCount),
            fixtureMode: "shutdown-disconnect-after-accepted");
        harness.Executor.RoleLaunchedForTests = (role, job, process) =>
        {
            Assert.Equal(RuntimeRole.Server, role);
            launchedJob = job;
            launchedProcess = process;
        };
        harness.Executor.RoleTransportFailureObservedForTests = async role =>
        {
            Assert.Equal(RuntimeRole.Server, role);
            transportFailureObserved.TrySetResult();
            await releaseTransportFailure.Task;
        };
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));

        var stop = harness.Executor.ExecuteAsync(StopCommand(generation));
        await transportFailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(launchedProcess!.HasExited);
        launchedJob!.Dispose();
        releaseTransportFailure.TrySetResult();

        Assert.Null(await stop.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(harness.Executor.SnapshotForTests().ServerProcessId);
        Assert.Equal(1, Volatile.Read(ref verificationCount));
    }

    [Fact]
    public async Task ReconcileRoleStop_CallerCancellationPropagatesAndRetainsIndeterminateRole()
    {
        var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());
        var operationId = Guid.NewGuid();
        var stopGeneration = generation with { OperationId = operationId };
        var deadlines = new RoleOperationDeadlines(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(100));
        await using var harness = CreateRealWindowsHarness(
            generation,
            NoopPayloadClosureVerifier,
            fixtureMode: "drain-disconnect-after-accepted",
            roleOperationDeadlines: deadlines);
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));
        var retainedProcessId = harness.Executor.SnapshotForTests().WorkerProcessId;
        var drain = await harness.Executor.ExecuteAsync(new LifecycleCommand(
                LifecycleCommandType.DrainWorker,
                stopGeneration,
                operationId,
                LifecycleDeadlineKey.WorkerStop))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<OperationTimedOut>(drain);
        using var canceledCaller = new CancellationTokenSource();
        canceledCaller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Executor.ExecuteAsync(
                new LifecycleCommand(
                    LifecycleCommandType.ReconcileRoleStop,
                    stopGeneration,
                    operationId,
                    LifecycleDeadlineKey.RoleReconciliation),
                canceledCaller.Token));

        Assert.Equal(retainedProcessId, harness.Executor.SnapshotForTests().WorkerProcessId);
    }

    [Fact]
    public async Task RealWindowsChild_NormalAndEscalationRaceShareOneTeardownAndVerifier()
    {
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var verificationCount = 0;
        await using var harness = CreateRealWindowsHarness(
            generation,
            () => Interlocked.Increment(ref verificationCount));
        Assert.IsType<RoleStarted>(await harness.Executor.ExecuteAsync(StartCommand(generation)));
        var teardownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTeardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Executor.RoleTeardownStartedForTests = async _ =>
        {
            teardownEntered.TrySetResult();
            await releaseTeardown.Task;
        };

        var normalTeardown = Record.ExceptionAsync(() =>
            harness.Executor.DisposeRoleForTests(RuntimeRole.Server));
        await teardownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(harness.Executor.SnapshotForTests().ServerProcessId);
        var escalation = Record.ExceptionAsync(() => harness.Executor.ExecuteAsync(
            new LifecycleCommand(
                LifecycleCommandType.EscalateJob,
                Generation: null,
                Guid.NewGuid(),
                LifecycleDeadlineKey.None)));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (harness.Executor.SnapshotForTests().ServerProcessId is not null &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Null(harness.Executor.SnapshotForTests().ServerProcessId);
        await Task.Delay(50);
        Assert.False(escalation.IsCompleted);
        releaseTeardown.TrySetResult();
        var errors = await Task.WhenAll(normalTeardown, escalation);

        Assert.All(errors, error => Assert.Null(error));
        Assert.Equal(1, Volatile.Read(ref verificationCount));
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
        string? buildIdentity = null,
        int healthPort = 32123,
        Func<IDisposable>? payloadLifetimeLeaseFactory = null,
        Action? verifyPayloadClosureAfterShutdown = null,
        bool useNullPayloadClosureVerifier = false,
        bool useNullPayloadLifetimeLeaseFactory = false) =>
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
            healthPort,
            TimeSpan.FromMilliseconds(250),
            useNullPayloadLifetimeLeaseFactory
                ? null!
                : payloadLifetimeLeaseFactory ?? NoopPayloadLifetimeLease.Open,
            leaseFactory ?? (() => new TrackingLease().Open()),
            useNullPayloadClosureVerifier
                ? null!
                : verifyPayloadClosureAfterShutdown ?? NoopPayloadClosureVerifier);

    private static void NoopPayloadClosureVerifier()
    {
    }

    private static class NoopPayloadLifetimeLease
    {
        internal static IDisposable Open() => new TrackingLease().Open();
    }

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

    private static LifecycleCommand StopCommand(ProcessGeneration generation) =>
        new(
            generation.Role == RuntimeRole.Server
                ? LifecycleCommandType.StopServer
                : LifecycleCommandType.StopWorker,
            generation,
            Guid.NewGuid(),
            generation.Role == RuntimeRole.Server
                ? LifecycleDeadlineKey.ServerStop
                : LifecycleDeadlineKey.WorkerStop);

    private static LifecycleCommand WaitCommand(ProcessGeneration generation) =>
        new(
            generation.Role == RuntimeRole.Server
                ? LifecycleCommandType.WaitForServer
                : LifecycleCommandType.WaitForWorker,
            generation,
            generation.OperationId,
            generation.Role == RuntimeRole.Server
                ? LifecycleDeadlineKey.ServerStart
                : LifecycleDeadlineKey.WorkerStart);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("condition-not-reached");
            }

            await Task.Delay(10);
        }
    }

    private static ExecutorHarness CreateHarness(
        IRoleLaunchPlanProvider provider,
        IProcessLauncher? launcher,
        RoleOperationDeadlines? roleOperationDeadlines = null)
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
            AppHostMode.Run,
            AppHostRuntimeBackend.RedisCompatibility,
            AppHostRoleTarget.ControlledFixture);
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
            Path.Combine(profileRoot, "unused-migration.sql"));
        var secrets = new DiagnosticSecretRegistry();
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(Stream.Null),
            new SystemClock(),
            secrets);
        var gatedSink = new OwnershipGatedDiagnosticSink(sink, TimeSpan.FromSeconds(1));
        var executor = new LifecycleCommandExecutor(
            options,
            payload,
            identityStore: null,
            NoopRoleOperationBoundary.Instance,
            roleOperationDeadlines ?? RoleOperationDeadlines.Production,
            gatedSink,
            secrets,
            diagnosticSecretObserver: null,
            provider,
            launcher ?? new WindowsProcessLauncher(gatedSink));
        return new ExecutorHarness(profileRoot, executor);
    }

    private static ExecutorHarness CreateRealWindowsHarness(
        ProcessGeneration generation,
        Action verifyPayloadClosureAfterShutdown,
        string? fixtureMode = null,
        RoleOperationDeadlines? roleOperationDeadlines = null)
    {
        var healthPort = ReserveLoopbackPort();
        var fixturePath = Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe");
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")!;
        return CreateHarness(
            new StubProvider(CreatePlan(
                generation,
                arguments:
                [
                    fixtureMode ?? (generation.Role == RuntimeRole.Server ? "server" : "worker"),
                    healthPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ],
                applicationPath: fixturePath,
                workingDirectory: Path.GetDirectoryName(fixturePath),
                environment: new Dictionary<string, string> { ["SystemRoot"] = systemRoot },
                secretEnvironment: new Dictionary<string, SecretValue>(),
                buildIdentity: ControlProtocolConstants.FixtureBuildIdentity,
                healthPort: healthPort,
                verifyPayloadClosureAfterShutdown: verifyPayloadClosureAfterShutdown)),
            launcher: null,
            roleOperationDeadlines);
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

    private sealed class StubProcess(
        ProcessGeneration generation,
        Exception? disposeError = null) : ISupervisedProcess
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
            if (disposeError is not null)
            {
                throw disposeError;
            }

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

    private static TrustedNodePayload CreateTrustedPayload(
        string payloadRoot,
        string profileRoot)
    {
        Directory.CreateDirectory(Path.Combine(payloadRoot, "app"));
        var artifacts = new Dictionary<string, byte[]>
        {
            ["node.exe"] = "node"u8.ToArray(),
            ["app/server.js"] = "server"u8.ToArray(),
            ["app/worker.js"] = "worker"u8.ToArray(),
        };
        foreach (var artifact in artifacts)
        {
            File.WriteAllBytes(
                Path.Combine(
                    payloadRoot,
                    artifact.Key.Replace('/', Path.DirectorySeparatorChar)),
                artifact.Value);
        }

        var manifest = new
        {
            version = 1,
            buildIdentity = "cleanup-indeterminate-test/1",
            nodeExecutable = "node.exe",
            serverEntrypoint = "app/server.js",
            workerEntrypoint = "app/worker.js",
            artifacts = artifacts.Select(artifact => new
            {
                path = artifact.Key,
                length = artifact.Value.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(artifact.Value)),
            }).ToArray(),
        };
        File.WriteAllText(
            Path.Combine(payloadRoot, TrustedNodePayloadValidator.ManifestFileName),
            JsonSerializer.Serialize(manifest));
        return new TrustedNodePayloadValidator(
            inspectHandle: null,
            readSecurityDescriptor: (_, _) => TrustedDescriptor())
            .Validate(payloadRoot, profileRoot);
    }

    private static RawSecurityDescriptor TrustedDescriptor()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        return new RawSecurityDescriptor(
            ControlFlags.SelfRelative |
                ControlFlags.DiscretionaryAclPresent |
                ControlFlags.DiscretionaryAclProtected,
            administrators,
            group: null,
            systemAcl: null,
            discretionaryAcl: new RawAcl(GenericAcl.AclRevision, 0));
    }

    private sealed class ThrowingIdentityVerifier : IWindowsProcessIdentityVerifier
    {
        public SupervisedProcessIdentity Capture(
            RuntimeRole role,
            ProcessGeneration generation,
            SafeProcessHandle processHandle)
        {
            _ = NativeMethods.GetProcessId(processHandle);
            throw new Win32Exception(87);
        }
    }

    private sealed class BlockingIndeterminateCleanupPolicy : IProcessCleanupPolicy
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProcessCleanupResult Cleanup(
            SafeProcessHandle processHandle,
            IProcessTreeTerminator job)
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return new ProcessCleanupResult(
                Signaled: false,
                EscalatedToJob: true,
                ProcessTerminationErrorCode: null,
                JobTerminationErrorCode: null,
                WaitErrorCode: null,
                TimedOut: true);
        }
    }

    private sealed class NoopDiagnosticSink : IDiagnosticSink
    {
        internal static NoopDiagnosticSink Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            DiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

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
