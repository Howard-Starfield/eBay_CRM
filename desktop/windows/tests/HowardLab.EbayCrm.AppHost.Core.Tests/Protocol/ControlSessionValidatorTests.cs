using System.IO.Pipes;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Tests.Protocol;

public sealed class ControlSessionValidatorTests
{
    private static readonly Guid StartupOperationId = Guid.Parse("046e33a9-8d67-4439-a69f-13debb7f5241");
    private static readonly Guid DrainOperationId = Guid.Parse("c7daa0b1-abfe-49c5-98de-f4030a838a91");
    private static readonly Guid ShutdownOperationId = Guid.Parse("29e70eb2-df9d-442f-9f27-10194202a803");
    private const string CapabilityNonce = "super-secret-capability";
    private const string ChallengeId = "challenge-01";

    [Fact]
    public void AcceptsHelloOnlyWhenEveryExpectedIdentityFieldMatches()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var envelope = CreateHello(expected);

        var result = ControlSessionValidator.ValidateHello(expected, envelope);

        Assert.Equal(ControlValidationStatus.Accepted, result.Status);
        Assert.Equal(ControlValidationReasonCode.None, result.ReasonCode);
    }

    [Fact]
    public void RejectsHelloWithStableCodeWithoutExposingCapabilityNonce()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var envelope = CreateHello(expected, capabilityNonce: "wrong-secret-value");

        var result = ControlSessionValidator.ValidateHello(expected, envelope);

        Assert.Equal(ControlValidationStatus.Rejected, result.Status);
        Assert.Equal(ControlValidationReasonCode.CapabilityNonceMismatch, result.ReasonCode);
        Assert.DoesNotContain("wrong-secret-value", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CapabilityNonce, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CapabilityNonce, envelope.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CapabilityNonce, envelope.Payload.Deserialize<HelloPayload>(ControlFrameCodec.SerializerOptions)!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsHelloWithWrongChallengeId()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var envelope = CreateHello(expected);
        var payload = envelope.Payload.Deserialize<HelloPayload>(ControlFrameCodec.SerializerOptions)!;
        envelope = envelope with
        {
            Payload = JsonSerializer.SerializeToElement(
                payload with { ChallengeId = "wrong-challenge" },
                ControlFrameCodec.SerializerOptions),
        };

        var result = ControlSessionValidator.ValidateHello(expected, envelope);

        Assert.Equal(ControlValidationStatus.Rejected, result.Status);
        Assert.Equal(ControlValidationReasonCode.ChallengeMismatch, result.ReasonCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsExactAndConflictingHelloAfterAuthentication(bool conflicting)
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var validator = new ControlSessionValidator(expected);
        var hello = CreateHello(expected);
        AssertAccepted(validator.Validate(hello));
        if (conflicting)
        {
            var payload = hello.Payload.Deserialize<HelloPayload>(ControlFrameCodec.SerializerOptions)!;
            hello = hello with
            {
                Payload = JsonSerializer.SerializeToElement(
                    payload with { BuildIdentity = "conflicting-build" },
                    ControlFrameCodec.SerializerOptions),
            };
        }

        var result = validator.Validate(hello);

        Assert.Equal(ControlValidationStatus.Rejected, result.Status);
        Assert.Equal(ControlValidationReasonCode.UnexpectedHello, result.ReasonCode);
    }

    [Fact]
    public void RejectsMalformedHelloPayloadWithStableCode()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var envelope = new ControlEnvelope(
            ControlProtocolConstants.CurrentVersion,
            expected.StartupOperationId,
            expected.Role,
            expected.Generation,
            ControlMessageType.Hello,
            default);

        var result = ControlSessionValidator.ValidateHello(expected, envelope);

        Assert.Equal(ControlValidationStatus.Rejected, result.Status);
        Assert.Equal(ControlValidationReasonCode.InvalidHelloPayload, result.ReasonCode);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("https://example.com")]
    [InlineData("http://192.0.2.10:3000")]
    [InlineData("")]
    public void RejectsNonLoopbackHelloEndpoint(string endpoint)
    {
        var expected = CreateExpected(RuntimeRole.Worker);

        var result = ControlSessionValidator.ValidateHello(expected, CreateHello(expected, loopbackEndpoint: endpoint));

        Assert.Equal(ControlValidationReasonCode.InvalidLoopbackEndpoint, result.ReasonCode);
    }

    [Fact]
    public void RequiresHelloAsFirstMessage()
    {
        var validator = new ControlSessionValidator(CreateExpected(RuntimeRole.Worker));

        var result = validator.Validate(CreateEmpty(ControlMessageType.Drain, DrainOperationId));

        Assert.Equal(ControlValidationStatus.Rejected, result.Status);
        Assert.Equal(ControlValidationReasonCode.HelloRequired, result.ReasonCode);
    }

    [Fact]
    public void AcceptsWorkerDrainAndShutdownOrdering()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var validator = new ControlSessionValidator(expected);

        AssertAccepted(validator.Validate(CreateHello(expected)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.Drain, DrainOperationId)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.DrainAccepted, DrainOperationId)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.NoNewWorkAcquisition, DrainOperationId)));
        AssertAccepted(validator.Validate(CreateActiveWorkRemaining(3, Guid.Parse("373ef51a-b4ea-494e-b912-229606eff9f6"))));
        AssertAccepted(validator.Validate(CreateActiveWorkRemaining(1, Guid.Parse("3cd191cc-218f-40ca-80ee-6b1310aa47cf"))));
        AssertAccepted(validator.Validate(CreateActiveWorkRemaining(0, Guid.Parse("a705ea47-c9e1-49cc-bf24-98e8f62ed636"))));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.Drained, DrainOperationId)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.Shutdown, ShutdownOperationId)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.ShutdownAccepted, ShutdownOperationId)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.Stopped, ShutdownOperationId)));
    }

    [Fact]
    public void AcceptsServerShutdownSubset()
    {
        var expected = CreateExpected(RuntimeRole.Server);
        var validator = new ControlSessionValidator(expected);

        AssertAccepted(validator.Validate(CreateHello(expected)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.Shutdown, ShutdownOperationId, RuntimeRole.Server)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.ShutdownAccepted, ShutdownOperationId, RuntimeRole.Server)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.Stopped, ShutdownOperationId, RuntimeRole.Server)));
    }

    [Fact]
    public void RejectsOutOfOrderWorkerMessage()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var validator = new ControlSessionValidator(expected);
        AssertAccepted(validator.Validate(CreateHello(expected)));

        var result = validator.Validate(CreateEmpty(ControlMessageType.Drained, DrainOperationId));

        Assert.Equal(ControlValidationReasonCode.OutOfOrder, result.ReasonCode);
    }

    [Fact]
    public void AcceptsExactDuplicateIdempotentlyWithoutAdvancingAgain()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var validator = new ControlSessionValidator(expected);
        AssertAccepted(validator.Validate(CreateHello(expected)));
        var drain = CreateEmpty(ControlMessageType.Drain, DrainOperationId);
        AssertAccepted(validator.Validate(drain));

        var duplicate = validator.Validate(drain);
        var next = validator.Validate(CreateEmpty(ControlMessageType.DrainAccepted, DrainOperationId));

        Assert.Equal(ControlValidationStatus.IdempotentDuplicate, duplicate.Status);
        AssertAccepted(next);
    }

    [Fact]
    public void RejectsConflictingDuplicateByGenerationOperationAndType()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var validator = new ControlSessionValidator(expected);
        AssertAccepted(validator.Validate(CreateHello(expected)));
        var health = CreateHealth(Guid.Parse("a7cc7524-c56a-45f8-b4fe-b991d4696aaf"), "ready", 2);
        AssertAccepted(validator.Validate(health));
        var conflict = health with
        {
            Payload = JsonSerializer.SerializeToElement(
                new HealthPayload(ControlProtocolConstants.CurrentVersion, "build-1", 7, "generation-7", "ready", 1),
                ControlFrameCodec.SerializerOptions),
        };

        var result = validator.Validate(conflict);

        Assert.Equal(ControlValidationReasonCode.ConflictingDuplicate, result.ReasonCode);
    }

    [Fact]
    public void HealthDoesNotAdvanceOrderingAndIsRejectedAfterStopped()
    {
        var expected = CreateExpected(RuntimeRole.Server);
        var validator = new ControlSessionValidator(expected);
        AssertAccepted(validator.Validate(CreateHello(expected)));
        var health = CreateHealth(Guid.NewGuid(), "ready", 0, RuntimeRole.Server);
        AssertAccepted(validator.Validate(health));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.Shutdown, ShutdownOperationId, RuntimeRole.Server)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.ShutdownAccepted, ShutdownOperationId, RuntimeRole.Server)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.Stopped, ShutdownOperationId, RuntimeRole.Server)));

        var result = validator.Validate(health);

        Assert.Equal(ControlValidationReasonCode.SessionStopped, result.ReasonCode);
    }

    [Fact]
    public void RejectsIncreasingActiveWorkAndDrainedBeforeZero()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var validator = CreateWorkerAtNoNewWork(expected);
        AssertAccepted(validator.Validate(CreateActiveWorkRemaining(2, Guid.NewGuid())));

        var increasing = validator.Validate(CreateActiveWorkRemaining(3, Guid.NewGuid()));
        var drained = validator.Validate(CreateEmpty(ControlMessageType.Drained, DrainOperationId));

        Assert.Equal(ControlValidationReasonCode.ActiveWorkIncreased, increasing.ReasonCode);
        Assert.Equal(ControlValidationReasonCode.ActiveWorkRemaining, drained.ReasonCode);
    }

    [Fact]
    public void RejectsWrongGenerationAndWrongOperation()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var validator = new ControlSessionValidator(expected);
        AssertAccepted(validator.Validate(CreateHello(expected)));

        var wrongGeneration = validator.Validate(CreateEmpty(ControlMessageType.Drain, DrainOperationId) with { Generation = 8 });
        Assert.Equal(ControlValidationReasonCode.GenerationMismatch, wrongGeneration.ReasonCode);

        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.Drain, DrainOperationId)));
        var wrongOperation = validator.Validate(CreateEmpty(ControlMessageType.DrainAccepted, Guid.NewGuid()));
        Assert.Equal(ControlValidationReasonCode.OperationIdMismatch, wrongOperation.ReasonCode);
    }

    [Fact]
    public void AcceptsDrainedDirectlyAfterNoNewWorkWhenNoActiveWorkWasReported()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var validator = CreateWorkerAtNoNewWork(expected);

        var result = validator.Validate(CreateEmpty(ControlMessageType.Drained, DrainOperationId));

        AssertAccepted(result);
    }

    [Fact]
    public async Task NamedPipeClientWaitsForChallengeAndEchoesExactIdentity()
    {
        var pipeName = $"ebaycrm-{Guid.NewGuid():N}";
        var expected = CreateExpected(RuntimeRole.Worker);
        var hello = CreateHello(expected);
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using var client = new NamedPipeControlClient(pipeName, hello, TimeSpan.FromSeconds(5));

        var exchange = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(CancellationToken.None);
            var codec = new ControlFrameCodec();
            var challenge = Create(
                ControlMessageType.IdentityChallenge,
                expected.StartupOperationId,
                expected.Role,
                new IdentityChallengePayload(
                    expected.ProcessId,
                    ProcessCreationTimeTicks.Format(expected.ProcessCreationTimeUtcTicks),
                    "issued-challenge"));
            await codec.WriteAsync(server, challenge, CancellationToken.None);
            await server.FlushAsync(CancellationToken.None);
            return await codec.ReadAsync(server, CancellationToken.None);
        });
        await client.ConnectAsync(CancellationToken.None);
        var received = await exchange;

        Assert.Equal(ControlMessageType.Hello, received.Type);
        Assert.Equal(StartupOperationId, received.OperationId);
        var payload = received.Payload.Deserialize<HelloPayload>(ControlFrameCodec.SerializerOptions);
        Assert.NotNull(payload);
        Assert.Equal("issued-challenge", payload.ChallengeId);
        Assert.Equal(ProcessCreationTimeTicks.Format(expected.ProcessCreationTimeUtcTicks), payload.ProcessCreationTimeUtcTicks);
    }

    [Fact]
    public async Task NamedPipeClientConnectionIsCancellable()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        await using var client = new NamedPipeControlClient($"missing-{Guid.NewGuid():N}", CreateHello(expected), TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(cancellation.Token));
    }

    [Fact]
    public async Task NamedPipeClientChallengeReadHonorsOperationTimeout()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var transport = new ControlledClientTransport(expected, blockChallenge: true);
        await using var client = new NamedPipeControlClient(
            transport,
            CreateHello(expected),
            TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NamedPipeClientChallengeReadHonorsCallerCancellation()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var transport = new ControlledClientTransport(expected, blockChallenge: true);
        await using var client = new NamedPipeControlClient(
            transport,
            CreateHello(expected),
            TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ConnectAsync(cancellation.Token));
    }

    [Fact]
    public async Task NamedPipeClientRejectsASecondChallengeAfterAuthentication()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var transport = new ControlledClientTransport(expected, duplicateChallenge: true);
        await using var client = new NamedPipeControlClient(transport, CreateHello(expected), TimeSpan.FromSeconds(5));

        await client.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadCommandAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(RuntimeRole.Worker, ControlMessageType.Drain)]
    [InlineData(RuntimeRole.Server, ControlMessageType.DrainAccepted)]
    public async Task NamedPipeClientRejectsOutboundMessagesOwnedByAppHostOrAnotherRole(
        RuntimeRole role,
        ControlMessageType messageType)
    {
        var expected = CreateExpected(role);
        var transport = new ControlledClientTransport(expected);
        await using var client = new NamedPipeControlClient(transport, CreateHello(expected), TimeSpan.FromSeconds(5));
        await client.ConnectAsync(CancellationToken.None);
        var writesBeforeRejection = transport.Stream.WriteCalls;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SendAsync(CreateEmpty(messageType, Guid.NewGuid(), role), CancellationToken.None));

        Assert.Equal(writesBeforeRejection, transport.Stream.WriteCalls);
        var operationsAfterRejection = transport.OperationCount;
        var sendError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendAsync(
                CreateHealth(Guid.NewGuid(), "ready", 0, role),
                CancellationToken.None));
        var readError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ReadAsync(CancellationToken.None));

        Assert.Equal("The control client is faulted and cannot be reused.", sendError.Message);
        Assert.Equal("The control client is faulted and cannot be reused.", readError.Message);
        Assert.Equal(operationsAfterRejection, transport.OperationCount);
    }

    [Fact]
    public async Task NamedPipeClientRejectsDatabaseControlChannelAtChallengeBoundary()
    {
        var expected = CreateExpected(RuntimeRole.Database);
        var transport = new ControlledClientTransport(expected);
        await using var client = new NamedPipeControlClient(transport, CreateHello(expected), TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.ConnectAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(ClientFailurePhase.Connect)]
    [InlineData(ClientFailurePhase.HelloWrite)]
    [InlineData(ClientFailurePhase.HelloFlush)]
    [InlineData(ClientFailurePhase.LaterWrite)]
    [InlineData(ClientFailurePhase.LaterFlush)]
    public async Task NamedPipeClientIsPermanentlyFaultedAfterTransportFailure(ClientFailurePhase failurePhase)
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var transport = new ControlledClientTransport(expected);
        await using var client = new NamedPipeControlClient(transport, CreateHello(expected), TimeSpan.FromSeconds(5));

        Func<Task> failingOperation;
        switch (failurePhase)
        {
            case ClientFailurePhase.Connect:
                transport.FailConnect = true;
                failingOperation = () => client.ConnectAsync(CancellationToken.None);
                break;
            case ClientFailurePhase.HelloWrite:
                transport.Stream.FailWriteCall = 2;
                failingOperation = () => client.ConnectAsync(CancellationToken.None);
                break;
            case ClientFailurePhase.HelloFlush:
                transport.FailFlushCall = 1;
                failingOperation = () => client.ConnectAsync(CancellationToken.None);
                break;
            case ClientFailurePhase.LaterWrite:
                await client.ConnectAsync(CancellationToken.None);
                transport.Stream.FailWriteCall = 4;
                failingOperation = () => client.SendAsync(CreateHealth(Guid.NewGuid(), "ready", 0), CancellationToken.None);
                break;
            case ClientFailurePhase.LaterFlush:
                await client.ConnectAsync(CancellationToken.None);
                transport.FailFlushCall = 2;
                failingOperation = () => client.SendAsync(CreateHealth(Guid.NewGuid(), "ready", 0), CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failurePhase));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(failingOperation);
        var callsAfterFailure = transport.OperationCount;

        var connectException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(CancellationToken.None));
        var sendException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync(CreateHealth(Guid.NewGuid(), "ready", 0), CancellationToken.None));

        Assert.Equal("The control client is faulted and cannot be reused.", connectException.Message);
        Assert.Equal("The control client is faulted and cannot be reused.", sendException.Message);
        Assert.Equal(callsAfterFailure, transport.OperationCount);
    }

    [Fact]
    public async Task NamedPipeClientDisposeIsIdempotent()
    {
        var expected = CreateExpected(RuntimeRole.Worker);
        var transport = new ControlledClientTransport(expected);
        var client = new NamedPipeControlClient(transport, CreateHello(expected), TimeSpan.FromSeconds(5));

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal(1, transport.DisposeCalls);
    }

    private static ControlSessionValidator CreateWorkerAtNoNewWork(ExpectedControlIdentity expected)
    {
        var validator = new ControlSessionValidator(expected);
        AssertAccepted(validator.Validate(CreateHello(expected)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.Drain, DrainOperationId)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.DrainAccepted, DrainOperationId)));
        AssertAccepted(validator.Validate(CreateEmpty(ControlMessageType.NoNewWorkAcquisition, DrainOperationId)));
        return validator;
    }

    private static async Task<ControlEnvelope> ReadFirstMessageAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync(CancellationToken.None);
        return await new ControlFrameCodec().ReadAsync(server, CancellationToken.None);
    }

    private static ExpectedControlIdentity CreateExpected(RuntimeRole role) =>
        new(role, 7, StartupOperationId, 4242, 638880000000000000, CapabilityNonce, "build-1", ChallengeId);

    private static ControlEnvelope CreateHello(
        ExpectedControlIdentity expected,
        string? capabilityNonce = null,
        string? loopbackEndpoint = "http://127.0.0.1:3000") =>
        Create(
            ControlMessageType.Hello,
            expected.StartupOperationId,
            expected.Role,
            new HelloPayload(
                expected.ProcessId,
                ProcessCreationTimeTicks.Format(expected.ProcessCreationTimeUtcTicks),
                capabilityNonce ?? expected.CapabilityNonce,
                expected.BuildIdentity,
                loopbackEndpoint,
                expected.ChallengeId));

    private static ControlEnvelope CreateEmpty(ControlMessageType type, Guid operationId, RuntimeRole role = RuntimeRole.Worker) =>
        Create(type, operationId, role, new { });

    private static ControlEnvelope CreateActiveWorkRemaining(int count, Guid operationId) =>
        Create(ControlMessageType.ActiveWorkRemaining, operationId, RuntimeRole.Worker, new { count });

    private static ControlEnvelope CreateHealth(Guid operationId, string status, int activeWorkRemaining, RuntimeRole role = RuntimeRole.Worker) =>
        Create(type: ControlMessageType.Health, operationId, role, new HealthPayload(ControlProtocolConstants.CurrentVersion, "build-1", 7, CapabilityNonce, status, activeWorkRemaining));

    private static ControlEnvelope Create(ControlMessageType type, Guid operationId, RuntimeRole role, object payload) =>
        new(
            ControlProtocolConstants.CurrentVersion,
            operationId,
            role,
            7,
            type,
            JsonSerializer.SerializeToElement(payload, ControlFrameCodec.SerializerOptions));

    private static void AssertAccepted(ControlValidationResult result) =>
        Assert.Equal(ControlValidationStatus.Accepted, result.Status);

    public enum ClientFailurePhase
    {
        Connect,
        HelloWrite,
        HelloFlush,
        LaterWrite,
        LaterFlush,
    }

    private sealed class ControlledClientTransport : IControlClientTransport
    {
        public ControlledClientTransport(
            ExpectedControlIdentity expected,
            bool duplicateChallenge = false,
            bool blockChallenge = false)
        {
            Stream = new ControlledDuplexStream(Create(
                ControlMessageType.IdentityChallenge,
                expected.StartupOperationId,
                expected.Role,
                new IdentityChallengePayload(
                    expected.ProcessId,
                    ProcessCreationTimeTicks.Format(expected.ProcessCreationTimeUtcTicks),
                    "issued-challenge")),
                duplicateChallenge,
                blockChallenge);
        }

        public ControlledDuplexStream Stream { get; }
        Stream IControlClientTransport.Stream => Stream;
        public bool IsConnected { get; private set; }
        public bool FailConnect { get; set; }
        public int FailFlushCall { get; set; }
        public int ConnectCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int OperationCount => ConnectCalls + Stream.ReadCalls + Stream.WriteCalls + FlushCalls;

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCalls++;
            if (FailConnect)
            {
                return Task.FromException(new OperationCanceledException());
            }

            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            return FlushCalls == FailFlushCall
                ? Task.FromException(new OperationCanceledException())
                : Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledDuplexStream : Stream
    {
        private readonly MemoryStream _challenge;
        private readonly bool _blockChallenge;

        public ControlledDuplexStream(
            ControlEnvelope challenge,
            bool duplicateChallenge,
            bool blockChallenge)
        {
            _blockChallenge = blockChallenge;
            var json = JsonSerializer.SerializeToUtf8Bytes(challenge, ControlFrameCodec.SerializerOptions);
            var singleFrameLength = sizeof(uint) + json.Length;
            var frame = new byte[singleFrameLength * (duplicateChallenge ? 2 : 1)];
            for (var offset = 0; offset < frame.Length; offset += singleFrameLength)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    frame.AsSpan(offset, sizeof(uint)),
                    checked((uint)json.Length));
                json.CopyTo(frame.AsSpan(offset + sizeof(uint)));
            }
            _challenge = new MemoryStream(frame, writable: false);
        }

        public int FailWriteCall { get; set; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return WriteCalls == FailWriteCall
                ? ValueTask.FromException(new OperationCanceledException())
                : ValueTask.CompletedTask;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return _blockChallenge
                ? WaitForCancellationAsync(cancellationToken)
                : _challenge.ReadAsync(buffer, cancellationToken);
        }

        private static async ValueTask<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
