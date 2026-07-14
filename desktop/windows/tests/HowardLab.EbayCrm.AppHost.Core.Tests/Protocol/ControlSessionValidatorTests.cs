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
                new HealthPayload(1, "build-1", 7, "generation-7", "ready", 1),
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
    public async Task NamedPipeClientConnectsToSuppliedNameAndSendsHelloFirst()
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

        var receiving = ReadFirstMessageAsync(server);
        await client.ConnectAsync(CancellationToken.None);
        var received = await receiving;

        Assert.Equal(ControlMessageType.Hello, received.Type);
        Assert.Equal(StartupOperationId, received.OperationId);
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
        new(role, 7, StartupOperationId, 4242, 638880000000000000, CapabilityNonce, "build-1");

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
                expected.ProcessCreationTimeUtcTicks,
                capabilityNonce ?? expected.CapabilityNonce,
                expected.BuildIdentity,
                loopbackEndpoint));

    private static ControlEnvelope CreateEmpty(ControlMessageType type, Guid operationId, RuntimeRole role = RuntimeRole.Worker) =>
        Create(type, operationId, role, new { });

    private static ControlEnvelope CreateActiveWorkRemaining(int count, Guid operationId) =>
        Create(ControlMessageType.ActiveWorkRemaining, operationId, RuntimeRole.Worker, new { count });

    private static ControlEnvelope CreateHealth(Guid operationId, string status, int activeWorkRemaining, RuntimeRole role = RuntimeRole.Worker) =>
        Create(type: ControlMessageType.Health, operationId, role, new HealthPayload(1, "build-1", 7, "generation-7", status, activeWorkRemaining));

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
}
