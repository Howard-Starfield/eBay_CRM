using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

public enum ControlProtocolErrorCode
{
    ZeroLength,
    FrameTooLarge,
    FrameLimitExceeded,
    TruncatedPrefix,
    TruncatedPayload,
    InvalidUtf8,
    InvalidJson,
    UnknownVersion,
    UnknownMessageType,
    UnknownRole,
    InvalidEnvelope,
    InvalidPayload,
    DuplicateJsonProperty,
}

public sealed class ControlProtocolException(ControlProtocolErrorCode code, Exception? innerException = null)
    : Exception($"Control protocol error: {code}.", innerException)
{
    public ControlProtocolErrorCode Code { get; } = code;
}

public sealed class ControlFrameCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly ArrayPool<byte> _payloadPool;
    private int _frameCount;

    public ControlFrameCodec()
        : this(ArrayPool<byte>.Shared)
    {
    }

    public ControlFrameCodec(ArrayPool<byte> payloadPool)
    {
        _payloadPool = payloadPool ?? throw new ArgumentNullException(nameof(payloadPool));
    }

    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    public async Task<ControlEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        CountFrame();

        var prefix = new byte[sizeof(uint)];
        try
        {
            await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.TruncatedPrefix, exception);
        }

        var unsignedLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (unsignedLength == 0)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.ZeroLength);
        }

        if (unsignedLength > ControlProtocolConstants.MaxFrameBytes)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.FrameTooLarge);
        }

        var length = checked((int)unsignedLength);
        var payload = _payloadPool.Rent(length);
        try
        {
            try
            {
                await stream.ReadExactlyAsync(payload.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException exception)
            {
                throw new ControlProtocolException(ControlProtocolErrorCode.TruncatedPayload, exception);
            }

            try
            {
                _ = StrictUtf8.GetCharCount(payload, 0, length);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ControlProtocolException(ControlProtocolErrorCode.InvalidUtf8, exception);
            }

            ControlEnvelope envelope;
            try
            {
                PrevalidateJson(payload.AsSpan(0, length));
                envelope = JsonSerializer.Deserialize<ControlEnvelope>(payload.AsSpan(0, length), SerializerOptions)
                    ?? throw new ControlProtocolException(ControlProtocolErrorCode.InvalidJson);
            }
            catch (JsonException exception)
            {
                throw new ControlProtocolException(ControlProtocolErrorCode.InvalidJson, exception);
            }

            ValidateEnvelope(envelope);
            return envelope;
        }
        finally
        {
            Array.Clear(payload);
            _payloadPool.Return(payload, clearArray: true);
        }
    }

    public async Task WriteAsync(Stream stream, ControlEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);
        CountFrame();
        ValidateEnvelope(envelope, validatePayload: false);

        using var payload = new PooledPayloadWriter(_payloadPool);
        try
        {
            using var jsonWriter = new Utf8JsonWriter(payload);
            JsonSerializer.Serialize(jsonWriter, envelope, SerializerOptions);
            jsonWriter.Flush();
        }
        catch (ControlProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.InvalidJson, exception);
        }

        if (payload.WrittenCount == 0)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.ZeroLength);
        }

        if (payload.WrittenCount > ControlProtocolConstants.MaxFrameBytes)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.FrameTooLarge);
        }

        ValidatePayload(envelope);

        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.WrittenCount));
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateEnvelope(ControlEnvelope envelope, bool validatePayload = true)
    {
        if (envelope.Version != ControlProtocolConstants.CurrentVersion)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.UnknownVersion);
        }

        if (!Enum.IsDefined(envelope.Type))
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.UnknownMessageType);
        }

        if (!Enum.IsDefined(envelope.Role))
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.UnknownRole);
        }

        if (envelope.OperationId == Guid.Empty ||
            envelope.Generation is < 0 or > ControlProtocolConstants.MaxGeneration ||
            envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.InvalidEnvelope);
        }

        if (validatePayload)
        {
            ValidatePayload(envelope);
        }
    }

    private static void ValidatePayload(ControlEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case ControlMessageType.IdentityChallenge:
                var challenge = DeserializePayload<IdentityChallengePayload>(envelope.Payload);
                if (challenge.ProcessId <= 0 ||
                    !ProcessCreationTimeTicks.TryParseCanonical(challenge.ProcessCreationTimeUtcTicks, out _) ||
                    !IsBoundedNonEmpty(challenge.ChallengeId))
                {
                    throw new ControlProtocolException(ControlProtocolErrorCode.InvalidPayload);
                }

                break;

            case ControlMessageType.Hello:
                var hello = DeserializePayload<HelloPayload>(envelope.Payload);
                if (hello.ProcessId <= 0 ||
                    !ProcessCreationTimeTicks.TryParseCanonical(hello.ProcessCreationTimeUtcTicks, out _) ||
                    !IsBoundedNonEmpty(hello.CapabilityNonce) ||
                    !IsBoundedNonEmpty(hello.BuildIdentity) ||
                    !IsBoundedNonEmpty(hello.ChallengeId) ||
                    hello.LoopbackEndpoint is not null && !IsBoundedNonEmpty(hello.LoopbackEndpoint))
                {
                    throw new ControlProtocolException(ControlProtocolErrorCode.InvalidPayload);
                }

                break;

            case ControlMessageType.Health:
                var health = DeserializePayload<HealthPayload>(envelope.Payload);
                if (health.ProtocolVersion != envelope.Version ||
                    health.Generation != envelope.Generation ||
                    !IsBoundedNonEmpty(health.BuildIdentity) ||
                    !IsBoundedNonEmpty(health.GenerationNonce) ||
                    !IsBoundedNonEmpty(health.Status) ||
                    health.ActiveWorkRemaining is < 0 or > ControlProtocolConstants.MaxActiveWorkRemaining)
                {
                    throw new ControlProtocolException(ControlProtocolErrorCode.InvalidPayload);
                }

                break;

            case ControlMessageType.ActiveWorkRemaining:
                var activeWork = DeserializePayload<ActiveWorkRemainingPayload>(envelope.Payload);
                if (activeWork.Count is < 0 or > ControlProtocolConstants.MaxActiveWorkRemaining)
                {
                    throw new ControlProtocolException(ControlProtocolErrorCode.InvalidPayload);
                }

                break;

            default:
                _ = DeserializePayload<EmptyControlPayload>(envelope.Payload);
                break;
        }
    }

    internal static TPayload DeserializePayload<TPayload>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<TPayload>(SerializerOptions)
                ?? throw new ControlProtocolException(ControlProtocolErrorCode.InvalidPayload);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.InvalidPayload, exception);
        }
    }

    internal static bool IsBoundedNonEmpty(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= ControlProtocolConstants.MaxTextFieldChars;

    private static void PrevalidateJson(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
        var objectProperties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                continue;
            }

            if (reader.TokenType == JsonTokenType.EndObject)
            {
                objectProperties.Pop();
                continue;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var propertyName = reader.GetString()!;
            if (!objectProperties.Peek().Add(propertyName))
            {
                throw new ControlProtocolException(ControlProtocolErrorCode.DuplicateJsonProperty);
            }

            if (objectProperties.Count != 1 ||
                propertyName is not ("type" or "role"))
            {
                continue;
            }

            if (!reader.Read())
            {
                return;
            }

            var known = reader.TokenType == JsonTokenType.String && propertyName switch
            {
                "type" => reader.GetString() is
                    "identityChallenge" or
                    "hello" or
                    "drain" or
                    "drainAccepted" or
                    "noNewWorkAcquisition" or
                    "activeWorkRemaining" or
                    "drained" or
                    "shutdown" or
                    "shutdownAccepted" or
                    "stopped" or
                    "health",
                "role" => reader.GetString() is "database" or "server" or "worker",
                _ => false,
            };

            if (!known)
            {
                throw new ControlProtocolException(
                    propertyName == "type"
                        ? ControlProtocolErrorCode.UnknownMessageType
                        : ControlProtocolErrorCode.UnknownRole);
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private void CountFrame()
    {
        if (_frameCount >= ControlProtocolConstants.MaxFramesPerSession)
        {
            throw new ControlProtocolException(ControlProtocolErrorCode.FrameLimitExceeded);
        }

        _frameCount++;
    }

    private sealed class PooledPayloadWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly ArrayPool<byte> _pool;
        private byte[]? _buffer;
        private int _written;

        public PooledPayloadWriter(ArrayPool<byte> pool)
        {
            _pool = pool;
            _buffer = pool.Rent(256);
        }

        public int WrittenCount => _written;
        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || _written > ControlProtocolConstants.MaxFrameBytes + 1 - count)
            {
                throw new ControlProtocolException(ControlProtocolErrorCode.FrameTooLarge);
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public void Dispose()
        {
            if (_buffer is null)
            {
                return;
            }

            Array.Clear(_buffer);
            _pool.Return(_buffer, clearArray: true);
            _buffer = null;
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            sizeHint = Math.Max(1, sizeHint);
            if (sizeHint > ControlProtocolConstants.MaxFrameBytes + 1 - _written)
            {
                throw new ControlProtocolException(ControlProtocolErrorCode.FrameTooLarge);
            }

            if (_buffer!.Length - _written >= sizeHint)
            {
                return;
            }

            var required = _written + sizeHint;
            var newLength = Math.Min(
                ControlProtocolConstants.MaxFrameBytes + 1,
                Math.Max(required, Math.Min(_buffer.Length * 2, ControlProtocolConstants.MaxFrameBytes + 1)));
            var replacement = _pool.Rent(newLength);
            _buffer.AsSpan(0, _written).CopyTo(replacement);
            Array.Clear(_buffer);
            _pool.Return(_buffer, clearArray: true);
            _buffer = replacement;
        }
    }
}

internal sealed record EmptyControlPayload;
