using System.ComponentModel;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Control;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Tests.Processes;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Control;

public sealed class WindowsControlChannelTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CreateBeforeLaunch_UsesFreshRedactedEndpointSecrets()
    {
        await using var first = CreateChannel();
        await using var second = CreateChannel();

        Assert.NotEqual(first.EndpointIdentity.PipeName, second.EndpointIdentity.PipeName);
        Assert.NotEqual(first.EndpointIdentity.CapabilityNonce, second.EndpointIdentity.CapabilityNonce);
        Assert.DoesNotContain(first.EndpointIdentity.PipeName, first.EndpointIdentity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(first.EndpointIdentity.CapabilityNonce, first.EndpointIdentity.ToString(), StringComparison.Ordinal);
        Assert.Equal(43, first.EndpointIdentity.CapabilityNonce.Length);
    }

    [Fact]
    public async Task NativeFactory_RejectsSecondServerForSamePipeName()
    {
        await using var channel = CreateChannel();

        var error = Assert.Throws<Win32Exception>(() =>
            NativeNamedPipeServerFactory.Create(channel.EndpointIdentity.PipeName));
        Assert.Contains(error.NativeErrorCode, new[] { 5, 231 });
    }

    [Fact]
    public async Task AcceptAsync_AuthenticatesRetainedProcessAndHello()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, job, "valid");

        var accepted = await channel.AcceptAsync(process, job, CancellationToken.None)
            .WaitAsync(Deadline);

        Assert.Same(channel, accepted);
    }

    [Fact]
    public async Task ForceCloseAfterJobClose_SynchronouslyReleasesAuthenticatedPipeAndClientHandle()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, job, "valid");
        await channel.AcceptAsync(process, job, CancellationToken.None).WaitAsync(Deadline);

        job.Dispose();
        channel.ForceCloseAfterJobClose();

        Assert.True(channel.ResourcesClosedForTests);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.SendAsync(CreateEmptyEnvelope(channel, ControlMessageType.Shutdown, Guid.NewGuid())));
    }

    [Theory]
    [InlineData("wrong-nonce")]
    [InlineData("wrong-time")]
    [InlineData("wrong-build")]
    [InlineData("old-operation")]
    [InlineData("stale-generation")]
    [InlineData("oversize")]
    public async Task AcceptAsync_DestroysGenerationAfterInvalidHello(string mode)
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, job, mode);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await channel.AcceptAsync(process, job, CancellationToken.None).WaitAsync(Deadline));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await channel.AcceptAsync(process, job, CancellationToken.None).WaitAsync(Deadline));
    }

    [Fact]
    public async Task AcceptAsync_RejectsSameUserWrongProcessEvenWhenHelloSpoofsExpectedIdentity()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var expected = await LaunchAsync(
            ["hold"],
            job,
            generation: EndpointGeneration(channel));
        await using var attacker = await LaunchControlClientAsync(
            channel,
            job,
            "valid",
            new Dictionary<string, string>
            {
                ["TASK6_SPOOF_PROCESS_ID"] = expected.Identity.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["TASK6_SPOOF_CREATION_TICKS"] = expected.Identity.CreationTimeUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await channel.AcceptAsync(expected, job, CancellationToken.None).WaitAsync(Deadline));
    }

    [Fact]
    public async Task AcceptAsync_RejectsHelloWhenRetainedTask5GenerationDoesNotMatchEndpoint()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(
            channel,
            job,
            "valid",
            launchGeneration: new ProcessGeneration(
                RuntimeRole.Server,
                channel.EndpointIdentity.Generation + 1,
                channel.EndpointIdentity.StartupOperationId));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await channel.AcceptAsync(process, job, CancellationToken.None).WaitAsync(Deadline));
    }

    [Fact]
    public async Task SendAsync_FaultsAuthenticatedChannelAfterProtocolRejection()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, job, "valid");
        await channel.AcceptAsync(process, job, CancellationToken.None).WaitAsync(Deadline);
        var invalid = new ControlEnvelope(
            ControlProtocolConstants.CurrentVersion,
            Guid.NewGuid(),
            RuntimeRole.Server,
            channel.EndpointIdentity.Generation + 1,
            ControlMessageType.Shutdown,
            System.Text.Json.JsonSerializer.SerializeToElement(
                new { },
                ControlFrameCodec.SerializerOptions));

        await Assert.ThrowsAsync<InvalidDataException>(() => channel.SendAsync(invalid));
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendAsync(invalid));
    }

    [Fact]
    public async Task ReadAsync_RejectsCommandSentByAuthenticatedChild()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, job, "child-shutdown");
        await channel.AcceptAsync(process, job, CancellationToken.None).WaitAsync(Deadline);

        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ReadAsync());
    }

    [Fact]
    public async Task SendAsync_RejectsAcknowledgementSentByHost()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, job, "valid");
        await channel.AcceptAsync(process, job, CancellationToken.None).WaitAsync(Deadline);
        var operationId = Guid.NewGuid();
        await channel.SendAsync(CreateEmptyEnvelope(channel, ControlMessageType.Shutdown, operationId));

        await Assert.ThrowsAsync<InvalidDataException>(() => channel.SendAsync(
            CreateEmptyEnvelope(channel, ControlMessageType.ShutdownAccepted, operationId)));
    }

    [Fact]
    public async Task SendAsync_RejectsSecondIdentityChallengeAfterAuthentication()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, job, "valid");
        await channel.AcceptAsync(process, job, CancellationToken.None).WaitAsync(Deadline);
        var secondChallenge = new ControlEnvelope(
            ControlProtocolConstants.CurrentVersion,
            channel.EndpointIdentity.StartupOperationId,
            channel.EndpointIdentity.Role,
            channel.EndpointIdentity.Generation,
            ControlMessageType.IdentityChallenge,
            System.Text.Json.JsonSerializer.SerializeToElement(
                new IdentityChallengePayload(
                    process.Identity.ProcessId,
                    ProcessCreationTimeTicks.Format(process.Identity.CreationTimeUtc.UtcTicks),
                    "second-challenge"),
                ControlFrameCodec.SerializerOptions));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            channel.SendAsync(secondChallenge));

        Assert.Contains("second identity challenge", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadLoopAndSupervisorSend_CanUseDuplexChannelConcurrently()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, job, "shutdown-roundtrip");
        await channel.AcceptAsync(process, job, CancellationToken.None).WaitAsync(Deadline);
        var operationId = Guid.NewGuid();

        var acknowledgement = channel.ReadAsync();
        await channel.SendAsync(CreateEmptyEnvelope(channel, ControlMessageType.Shutdown, operationId))
            .WaitAsync(Deadline);
        var received = await acknowledgement.WaitAsync(Deadline);

        Assert.Equal(ControlMessageType.ShutdownAccepted, received.Type);
        Assert.Equal(operationId, received.OperationId);
    }

    [Fact]
    public async Task CombinedReadWriteFrameBudget_RejectsFrameAfterTotalOfOneThousandTwentyFour()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, job, "frame-budget");
        await channel.AcceptAsync(process, job, CancellationToken.None).WaitAsync(Deadline);
        var shutdown = CreateEmptyEnvelope(channel, ControlMessageType.Shutdown, Guid.NewGuid());
        await channel.SendAsync(shutdown);
        Assert.Equal(ControlMessageType.ShutdownAccepted, (await channel.ReadAsync()).Type);
        for (var index = 0; index < 1_020; index++)
        {
            await channel.SendAsync(shutdown);
        }

        var error = await Assert.ThrowsAsync<ControlProtocolException>(() =>
            channel.SendAsync(shutdown));

        Assert.Equal(ControlProtocolErrorCode.FrameLimitExceeded, error.Code);
    }

    [Fact]
    public async Task AcceptAsync_RejectsClientOutsideExpectedJob()
    {
        await using var channel = CreateChannel();
        using var actualJob = WindowsJobObject.CreateKillOnClose();
        using var wrongJob = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, actualJob, "valid");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await channel.AcceptAsync(process, wrongJob, CancellationToken.None).WaitAsync(Deadline));
    }

    [Fact]
    public async Task AcceptAsync_IsBoundedAndCancellableWithoutAClient()
    {
        await using var channel = CreateChannel(TimeSpan.FromMilliseconds(250));
        using var job = WindowsJobObject.CreateKillOnClose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await channel.AcceptAsync(
                new NeverUsedSupervisedProcess(),
                job,
                CancellationToken.None).WaitAsync(Deadline));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.AcceptAsync(new NeverUsedSupervisedProcess(), job, CancellationToken.None));
    }

    [Fact]
    public void CreateBeforeLaunch_RejectsTimeoutBeyondCancelAfterRangeBeforeCreatingPipe()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateChannel(TimeSpan.MaxValue));
    }

    [Fact]
    public void CreateBeforeLaunch_RejectsGenerationAboveSharedMaximumBeforeCreatingPipe()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsControlChannel.CreateBeforeLaunch(
                RuntimeRole.Server,
                ControlProtocolConstants.MaxGeneration + 1,
                Guid.NewGuid(),
                "task-6-build",
                TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DisposeAsync_WinsRaceBeforeAuthenticationPublicationAndAllCallersShareCompletion()
    {
        await using var channel = CreateChannel();
        using var job = WindowsJobObject.CreateKillOnClose();
        await using var process = await LaunchControlClientAsync(channel, job, "valid");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.AuthenticationPublishHook = async cancellationToken =>
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        var accept = channel.AcceptAsync(process, job, CancellationToken.None);
        await reached.Task.WaitAsync(Deadline);

        var firstDispose = channel.DisposeAsync().AsTask();
        var secondDispose = channel.DisposeAsync().AsTask();
        Assert.Same(firstDispose, secondDispose);
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(Deadline);
        release.TrySetResult();

        await Assert.ThrowsAnyAsync<Exception>(() => accept);
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendAsync(
            CreateEmptyEnvelope(channel, ControlMessageType.Shutdown, Guid.NewGuid())));
    }

    private static WindowsControlChannel CreateChannel(TimeSpan? timeout = null) =>
        WindowsControlChannel.CreateBeforeLaunch(
            RuntimeRole.Server,
            generation: 7,
            startupOperationId: Guid.NewGuid(),
            expectedBuildIdentity: "task-6-build",
            operationTimeout: timeout ?? TimeSpan.FromSeconds(5));

    private static async Task<WindowsSupervisedProcess> LaunchControlClientAsync(
        WindowsControlChannel channel,
        WindowsJobObject job,
        string mode,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        ProcessGeneration? launchGeneration = null)
    {
        var launchEnvironment = channel.CreateChildEnvironment();
        var environment = new Dictionary<string, string>(launchEnvironment.Environment, StringComparer.Ordinal);
        if (extraEnvironment is not null)
        {
            foreach (var pair in extraEnvironment)
            {
                environment.Add(pair.Key, pair.Value);
            }
        }

        return await LaunchAsync(
            ["control-connect", mode],
            job,
            environment,
            launchEnvironment.SecretEnvironment,
            launchGeneration ?? EndpointGeneration(channel));
    }

    private static async Task<WindowsSupervisedProcess> LaunchAsync(
        IReadOnlyList<string> arguments,
        WindowsJobObject job,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyDictionary<string, SecretValue>? secrets = null,
        ProcessGeneration? generation = null)
    {
        var specification = WindowsProcessLauncherTests.CreateSpecification(
            arguments,
            environment,
            secrets);
        if (generation is ProcessGeneration processGeneration)
        {
            specification = specification with
            {
                Role = processGeneration.Role,
                Generation = processGeneration,
            };
        }

        var launched = await WindowsProcessLauncherTests.CreateLauncher().LaunchAsync(
            specification,
            job,
            CancellationToken.None);
        return Assert.IsType<WindowsSupervisedProcess>(launched);
    }

    private static ProcessGeneration EndpointGeneration(WindowsControlChannel channel) =>
        new(
            channel.EndpointIdentity.Role,
            channel.EndpointIdentity.Generation,
            channel.EndpointIdentity.StartupOperationId);

    private static ControlEnvelope CreateEmptyEnvelope(
        WindowsControlChannel channel,
        ControlMessageType type,
        Guid operationId) =>
        new(
            ControlProtocolConstants.CurrentVersion,
            operationId,
            channel.EndpointIdentity.Role,
            channel.EndpointIdentity.Generation,
            type,
            System.Text.Json.JsonSerializer.SerializeToElement(
                new { },
                ControlFrameCodec.SerializerOptions));

    private sealed class NeverUsedSupervisedProcess : ISupervisedProcess
    {
        public SupervisedProcessIdentity Identity => throw new InvalidOperationException();
        public Task<int> Completion => throw new InvalidOperationException();
        public BoundedTextCollector StandardOutput => throw new InvalidOperationException();
        public BoundedTextCollector StandardError => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
