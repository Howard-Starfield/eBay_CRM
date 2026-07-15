using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

[DebuggerDisplay("Role = {Role}, Generation = {Generation}, StartupOperationId = {StartupOperationId}, ProcessId = {ProcessId}, ProcessCreationTimeUtcTicks = {ProcessCreationTimeUtcTicks}, CapabilityNonce = <redacted>, BuildIdentity = {BuildIdentity}")]
public sealed record ExpectedControlIdentity(
    RuntimeRole Role,
    long Generation,
    Guid StartupOperationId,
    int ProcessId,
    long ProcessCreationTimeUtcTicks,
    string CapabilityNonce,
    string BuildIdentity)
{
    public override string ToString() =>
        $"ExpectedControlIdentity {{ Role = {Role}, Generation = {Generation}, StartupOperationId = {StartupOperationId}, ProcessId = {ProcessId}, ProcessCreationTimeUtcTicks = {ProcessCreationTimeUtcTicks}, CapabilityNonce = <redacted>, BuildIdentity = {BuildIdentity} }}";
}

public enum ControlValidationStatus
{
    Accepted,
    IdempotentDuplicate,
    Rejected,
}

public enum ControlValidationReasonCode
{
    None,
    HelloRequired,
    UnexpectedHello,
    UnknownVersion,
    UnknownMessageType,
    InvalidEnvelope,
    InvalidHelloPayload,
    InvalidHealthPayload,
    InvalidPayload,
    RoleMismatch,
    GenerationMismatch,
    OperationIdMismatch,
    ProcessIdMismatch,
    ProcessCreationTimeMismatch,
    CapabilityNonceMismatch,
    BuildIdentityMismatch,
    InvalidLoopbackEndpoint,
    ConflictingDuplicate,
    OutOfOrder,
    ActiveWorkNegative,
    ActiveWorkIncreased,
    ActiveWorkRemaining,
    SessionStopped,
}

public sealed record ControlValidationResult(ControlValidationStatus Status, ControlValidationReasonCode ReasonCode)
{
    public static ControlValidationResult Accepted { get; } = new(ControlValidationStatus.Accepted, ControlValidationReasonCode.None);
    public static ControlValidationResult IdempotentDuplicate { get; } = new(ControlValidationStatus.IdempotentDuplicate, ControlValidationReasonCode.None);

    public static ControlValidationResult Reject(ControlValidationReasonCode reasonCode) =>
        new(ControlValidationStatus.Rejected, reasonCode);
}

public sealed class ControlSessionValidator
{
    private readonly ExpectedControlIdentity _expected;
    private readonly Dictionary<OperationKey, ControlEnvelope> _acceptedOperations = [];
    private SessionState _state = SessionState.AwaitHello;
    private Guid? _drainOperationId;
    private Guid? _shutdownOperationId;
    private int? _lastActiveWorkRemaining;

    public ControlSessionValidator(ExpectedControlIdentity expected)
    {
        _expected = expected ?? throw new ArgumentNullException(nameof(expected));
    }

    public static ControlValidationResult ValidateHello(ExpectedControlIdentity expected, ControlEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Version != ControlProtocolConstants.CurrentVersion)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.UnknownVersion);
        }

        if (envelope.Type != ControlMessageType.Hello)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.HelloRequired);
        }

        if (envelope.OperationId != expected.StartupOperationId)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.OperationIdMismatch);
        }

        if (envelope.Role != expected.Role)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.RoleMismatch);
        }

        if (envelope.Generation != expected.Generation)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.GenerationMismatch);
        }

        HelloPayload hello;
        try
        {
            hello = ControlFrameCodec.DeserializePayload<HelloPayload>(envelope.Payload);
        }
        catch (ControlProtocolException)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidHelloPayload);
        }

        if (hello.ProcessId <= 0 ||
            hello.ProcessCreationTimeUtcTicks <= 0 ||
            !ControlFrameCodec.IsBoundedNonEmpty(hello.CapabilityNonce) ||
            !ControlFrameCodec.IsBoundedNonEmpty(hello.BuildIdentity))
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidHelloPayload);
        }

        if (hello.ProcessId != expected.ProcessId)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.ProcessIdMismatch);
        }

        if (hello.ProcessCreationTimeUtcTicks != expected.ProcessCreationTimeUtcTicks)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.ProcessCreationTimeMismatch);
        }

        if (!SecretEquals(hello.CapabilityNonce, expected.CapabilityNonce))
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.CapabilityNonceMismatch);
        }

        if (!string.Equals(hello.BuildIdentity, expected.BuildIdentity, StringComparison.Ordinal))
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.BuildIdentityMismatch);
        }

        if (hello.LoopbackEndpoint is not null && !IsValidLoopbackEndpoint(hello.LoopbackEndpoint))
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidLoopbackEndpoint);
        }

        return ControlValidationResult.Accepted;
    }

    public ControlValidationResult Validate(ControlEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Version != ControlProtocolConstants.CurrentVersion)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.UnknownVersion);
        }

        if (!Enum.IsDefined(envelope.Type))
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.UnknownMessageType);
        }

        if (envelope.OperationId == Guid.Empty || envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidEnvelope);
        }

        if (_state == SessionState.Stopped && envelope.Type == ControlMessageType.Health)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.SessionStopped);
        }

        var key = new OperationKey(envelope.Generation, envelope.OperationId, envelope.Type);
        if (_acceptedOperations.TryGetValue(key, out var accepted))
        {
            return EnvelopesMatch(accepted, envelope)
                ? ControlValidationResult.IdempotentDuplicate
                : ControlValidationResult.Reject(ControlValidationReasonCode.ConflictingDuplicate);
        }

        if (_state == SessionState.AwaitHello)
        {
            if (envelope.Type != ControlMessageType.Hello)
            {
                return ControlValidationResult.Reject(ControlValidationReasonCode.HelloRequired);
            }

            var helloResult = ValidateHello(_expected, envelope);
            if (helloResult.Status == ControlValidationStatus.Accepted)
            {
                Accept(key, envelope);
                _state = _expected.Role == RuntimeRole.Worker
                    ? SessionState.WorkerAwaitDrain
                    : SessionState.AwaitShutdown;
            }

            return helloResult;
        }

        if (envelope.Generation != _expected.Generation)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.GenerationMismatch);
        }

        if (envelope.Role != _expected.Role)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.RoleMismatch);
        }

        if (envelope.Type == ControlMessageType.Hello)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.UnexpectedHello);
        }

        if (_state == SessionState.Stopped)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.SessionStopped);
        }

        if (envelope.Type == ControlMessageType.Health)
        {
            var healthResult = ValidateHealth(envelope);
            if (healthResult.Status == ControlValidationStatus.Accepted)
            {
                Accept(key, envelope);
            }

            return healthResult;
        }

        var result = _expected.Role == RuntimeRole.Worker
            ? ValidateWorker(envelope)
            : ValidateShutdownSequence(envelope);
        if (result.Status == ControlValidationStatus.Accepted)
        {
            Accept(key, envelope);
        }

        return result;
    }

    private ControlValidationResult ValidateHealth(ControlEnvelope envelope)
    {
        HealthPayload health;
        try
        {
            health = ControlFrameCodec.DeserializePayload<HealthPayload>(envelope.Payload);
        }
        catch (ControlProtocolException)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidHealthPayload);
        }

        if (health.ProtocolVersion != ControlProtocolConstants.CurrentVersion ||
            health.Generation != _expected.Generation ||
            !ControlFrameCodec.IsBoundedNonEmpty(health.BuildIdentity) ||
            !ControlFrameCodec.IsBoundedNonEmpty(health.GenerationNonce) ||
            !ControlFrameCodec.IsBoundedNonEmpty(health.Status) ||
            health.ActiveWorkRemaining < 0)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidHealthPayload);
        }

        if (!string.Equals(health.BuildIdentity, _expected.BuildIdentity, StringComparison.Ordinal))
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.BuildIdentityMismatch);
        }

        if (!string.Equals(health.GenerationNonce, _expected.CapabilityNonce, StringComparison.Ordinal))
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.CapabilityNonceMismatch);
        }

        return ControlValidationResult.Accepted;
    }

    private ControlValidationResult ValidateWorker(ControlEnvelope envelope)
    {
        switch (_state)
        {
            case SessionState.WorkerAwaitDrain when envelope.Type == ControlMessageType.Drain:
                if (!HasEmptyPayload(envelope))
                {
                    return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidPayload);
                }

                _drainOperationId = envelope.OperationId;
                _state = SessionState.WorkerAwaitDrainAccepted;
                return ControlValidationResult.Accepted;

            case SessionState.WorkerAwaitDrainAccepted when envelope.Type == ControlMessageType.DrainAccepted:
                return AdvanceDrain(envelope, SessionState.WorkerAwaitNoNewWork);

            case SessionState.WorkerAwaitNoNewWork when envelope.Type == ControlMessageType.NoNewWorkAcquisition:
                return AdvanceDrain(envelope, SessionState.WorkerAwaitActiveOrDrained);

            case SessionState.WorkerAwaitActiveOrDrained:
            case SessionState.WorkerActive:
                if (envelope.Type == ControlMessageType.ActiveWorkRemaining)
                {
                    return ValidateActiveWork(envelope);
                }

                if (envelope.Type == ControlMessageType.Drained)
                {
                    if (_state == SessionState.WorkerActive && _lastActiveWorkRemaining != 0)
                    {
                        return ControlValidationResult.Reject(ControlValidationReasonCode.ActiveWorkRemaining);
                    }

                    return AdvanceDrain(envelope, SessionState.AwaitShutdown);
                }

                return ControlValidationResult.Reject(ControlValidationReasonCode.OutOfOrder);

            case SessionState.AwaitShutdown:
            case SessionState.AwaitShutdownAccepted:
            case SessionState.AwaitStopped:
                return ValidateShutdownSequence(envelope);

            default:
                return ControlValidationResult.Reject(ControlValidationReasonCode.OutOfOrder);
        }
    }

    private ControlValidationResult ValidateShutdownSequence(ControlEnvelope envelope)
    {
        if (_state == SessionState.AwaitShutdown && envelope.Type == ControlMessageType.Shutdown)
        {
            if (!HasEmptyPayload(envelope))
            {
                return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidPayload);
            }

            _shutdownOperationId = envelope.OperationId;
            _state = SessionState.AwaitShutdownAccepted;
            return ControlValidationResult.Accepted;
        }

        if (_state == SessionState.AwaitShutdownAccepted && envelope.Type == ControlMessageType.ShutdownAccepted)
        {
            return AdvanceShutdown(envelope, SessionState.AwaitStopped);
        }

        if (_state == SessionState.AwaitStopped && envelope.Type == ControlMessageType.Stopped)
        {
            return AdvanceShutdown(envelope, SessionState.Stopped);
        }

        return ControlValidationResult.Reject(ControlValidationReasonCode.OutOfOrder);
    }

    private ControlValidationResult ValidateActiveWork(ControlEnvelope envelope)
    {
        ActiveWorkRemainingPayload payload;
        try
        {
            payload = ControlFrameCodec.DeserializePayload<ActiveWorkRemainingPayload>(envelope.Payload);
        }
        catch (ControlProtocolException)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidPayload);
        }

        if (payload.Count < 0)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.ActiveWorkNegative);
        }

        if (_lastActiveWorkRemaining is int previous && payload.Count > previous)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.ActiveWorkIncreased);
        }

        _lastActiveWorkRemaining = payload.Count;
        _state = SessionState.WorkerActive;
        return ControlValidationResult.Accepted;
    }

    private ControlValidationResult AdvanceDrain(ControlEnvelope envelope, SessionState nextState)
    {
        if (envelope.OperationId != _drainOperationId)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.OperationIdMismatch);
        }

        if (!HasEmptyPayload(envelope))
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidPayload);
        }

        _state = nextState;
        return ControlValidationResult.Accepted;
    }

    private ControlValidationResult AdvanceShutdown(ControlEnvelope envelope, SessionState nextState)
    {
        if (envelope.OperationId != _shutdownOperationId)
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.OperationIdMismatch);
        }

        if (!HasEmptyPayload(envelope))
        {
            return ControlValidationResult.Reject(ControlValidationReasonCode.InvalidPayload);
        }

        _state = nextState;
        return ControlValidationResult.Accepted;
    }

    private void Accept(OperationKey key, ControlEnvelope envelope) =>
        _acceptedOperations.Add(key, envelope);

    private static bool HasEmptyPayload(ControlEnvelope envelope) =>
        !envelope.Payload.EnumerateObject().Any();

    private static bool EnvelopesMatch(ControlEnvelope left, ControlEnvelope right) =>
        left.Version == right.Version &&
        left.OperationId == right.OperationId &&
        left.Role == right.Role &&
        left.Generation == right.Generation &&
        left.Type == right.Type &&
        JsonElement.DeepEquals(left.Payload, right.Payload);

    private static bool SecretEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(left.AsSpan()),
            MemoryMarshal.AsBytes(right.AsSpan()));

    private static bool IsValidLoopbackEndpoint(string endpoint) =>
        !string.IsNullOrWhiteSpace(endpoint) &&
        endpoint.Length <= ControlProtocolConstants.MaxTextFieldChars &&
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        uri.IsLoopback;

    private readonly record struct OperationKey(long Generation, Guid OperationId, ControlMessageType Type);

    private enum SessionState
    {
        AwaitHello,
        WorkerAwaitDrain,
        WorkerAwaitDrainAccepted,
        WorkerAwaitNoNewWork,
        WorkerAwaitActiveOrDrained,
        WorkerActive,
        AwaitShutdown,
        AwaitShutdownAccepted,
        AwaitStopped,
        Stopped,
    }
}

internal sealed record ActiveWorkRemainingPayload(int Count);
