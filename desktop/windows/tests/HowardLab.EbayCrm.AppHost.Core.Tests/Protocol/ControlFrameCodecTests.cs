using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Core.Tests.Protocol;

public sealed class ControlFrameCodecTests
{
    [Fact]
    public async Task RoundTripsEnvelopeThroughOneByteReads()
    {
        var codec = new ControlFrameCodec();
        var expected = CreateEnvelope(ControlMessageType.Health, new HealthPayload(1, "build-1", 7, "generation-7", "ready", 2));
        await using var written = new MemoryStream();

        await codec.WriteAsync(written, expected, CancellationToken.None);
        await using var partial = new OneByteReadStream(written.ToArray());
        var actual = await new ControlFrameCodec().ReadAsync(partial, CancellationToken.None);

        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.OperationId, actual.OperationId);
        Assert.Equal(expected.Role, actual.Role);
        Assert.Equal(expected.Generation, actual.Generation);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Payload.GetRawText(), actual.Payload.GetRawText());
    }

    [Theory]
    [InlineData(0u, ControlProtocolErrorCode.ZeroLength)]
    [InlineData(65_537u, ControlProtocolErrorCode.FrameTooLarge)]
    [InlineData(uint.MaxValue, ControlProtocolErrorCode.FrameTooLarge)]
    public async Task RejectsInvalidLengthBeforeRentingPayload(uint length, ControlProtocolErrorCode expectedCode)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, length);
        var pool = new TrackingArrayPool();
        var codec = new ControlFrameCodec(pool);

        var exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => codec.ReadAsync(new MemoryStream(prefix), CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(0, pool.RentCount);
    }

    [Fact]
    public async Task AcceptsFrameAtExactMaximumSize()
    {
        var json = CreateRawEnvelopeJson();
        var payload = new byte[ControlProtocolConstants.MaxFrameBytes];
        Encoding.UTF8.GetBytes(json).CopyTo(payload, 0);
        Array.Fill(payload, (byte)' ', Encoding.UTF8.GetByteCount(json), payload.Length - Encoding.UTF8.GetByteCount(json));

        var envelope = await new ControlFrameCodec().ReadAsync(
            new MemoryStream(CreateFrame(payload)),
            CancellationToken.None);

        Assert.Equal(ControlMessageType.Health, envelope.Type);
    }

    [Fact]
    public async Task RejectsTruncatedPrefix()
    {
        var exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => new ControlFrameCodec().ReadAsync(new MemoryStream([1, 0, 0]), CancellationToken.None));

        Assert.Equal(ControlProtocolErrorCode.TruncatedPrefix, exception.Code);
    }

    [Fact]
    public async Task RejectsTruncatedPayloadAndClearsRentedBuffer()
    {
        var pool = new TrackingArrayPool();
        var frame = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 10);
        frame[4] = (byte)'{';
        frame[5] = (byte)'}';

        var exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => new ControlFrameCodec(pool).ReadAsync(new MemoryStream(frame), CancellationToken.None));

        Assert.Equal(ControlProtocolErrorCode.TruncatedPayload, exception.Code);
        Assert.True(pool.ReturnedWithClearData);
    }

    [Fact]
    public async Task ClearsRentedBufferWhenPayloadReadIsCancelled()
    {
        var pool = new TrackingArrayPool();
        await using var stream = new CancelDuringPayloadStream(12);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ControlFrameCodec(pool).ReadAsync(stream, CancellationToken.None));

        Assert.True(pool.ReturnedWithClearData);
    }

    [Fact]
    public async Task RejectsInvalidUtf8()
    {
        var exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => new ControlFrameCodec().ReadAsync(new MemoryStream(CreateFrame([0xC3, 0x28])), CancellationToken.None));

        Assert.Equal(ControlProtocolErrorCode.InvalidUtf8, exception.Code);
    }

    [Fact]
    public async Task RejectsTruncatedJson()
    {
        var exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => new ControlFrameCodec().ReadAsync(
                new MemoryStream(CreateFrame(Encoding.UTF8.GetBytes("{\"version\":1"))),
                CancellationToken.None));

        Assert.Equal(ControlProtocolErrorCode.InvalidJson, exception.Code);
    }

    [Fact]
    public async Task RejectsUnknownProtocolVersion()
    {
        var json = CreateRawEnvelopeJson().Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal);

        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal(ControlProtocolErrorCode.UnknownVersion, exception.Code);
    }

    [Fact]
    public async Task RejectsUnknownMessageType()
    {
        var json = CreateRawEnvelopeJson().Replace("\"type\":\"health\"", "\"type\":999", StringComparison.Ordinal);

        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal(ControlProtocolErrorCode.UnknownMessageType, exception.Code);
    }

    [Fact]
    public async Task RejectsUnknownStringMessageTypeWithStableCode()
    {
        var json = CreateRawEnvelopeJson().Replace("\"type\":\"health\"", "\"type\":\"futureMessage\"", StringComparison.Ordinal);

        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal(ControlProtocolErrorCode.UnknownMessageType, exception.Code);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("999")]
    public async Task RejectsNumericMessageTypes(string numericType)
    {
        var json = CreateRawEnvelopeJson().Replace("\"type\":\"health\"", $"\"type\":{numericType}", StringComparison.Ordinal);

        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal(ControlProtocolErrorCode.UnknownMessageType, exception.Code);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("999")]
    public async Task RejectsNumericRoles(string numericRole)
    {
        var json = CreateRawEnvelopeJson().Replace("\"role\":\"worker\"", $"\"role\":{numericRole}", StringComparison.Ordinal);

        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal(ControlProtocolErrorCode.UnknownRole, exception.Code);
    }

    [Fact]
    public async Task RejectsUnmappedEnvelopeFields()
    {
        var json = CreateRawEnvelopeJson().Replace("{", "{\"unexpected\":true,", StringComparison.Ordinal);

        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal(ControlProtocolErrorCode.InvalidJson, exception.Code);
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":2,\"operationId\":\"57c0b8aa-a43a-431f-b63a-c89a1fb5adc8\",\"role\":\"worker\",\"generation\":7,\"type\":\"health\",\"payload\":{\"protocolVersion\":1,\"buildIdentity\":\"build-1\",\"generation\":7,\"generationNonce\":\"generation-7\",\"status\":\"ready\",\"activeWorkRemaining\":0}}")]
    [InlineData("{\"version\":1,\"operationId\":\"57c0b8aa-a43a-431f-b63a-c89a1fb5adc8\",\"role\":\"worker\",\"generation\":7,\"type\":\"health\",\"type\":\"drain\",\"payload\":{\"protocolVersion\":1,\"buildIdentity\":\"build-1\",\"generation\":7,\"generationNonce\":\"generation-7\",\"status\":\"ready\",\"activeWorkRemaining\":0}}")]
    [InlineData("{\"version\":1,\"operationId\":\"57c0b8aa-a43a-431f-b63a-c89a1fb5adc8\",\"role\":\"worker\",\"role\":\"server\",\"generation\":7,\"type\":\"health\",\"payload\":{\"protocolVersion\":1,\"buildIdentity\":\"build-1\",\"generation\":7,\"generationNonce\":\"generation-7\",\"status\":\"ready\",\"activeWorkRemaining\":0}}")]
    public async Task RejectsConflictingDuplicateTopLevelProperties(string json)
    {
        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal("DuplicateJsonProperty", exception.Code.ToString());
    }

    [Fact]
    public async Task RejectsDuplicateNestedHelloPropertyWithoutExposingSecretAndClearsBuffer()
    {
        const string firstSecret = "secret-first-value";
        const string secondSecret = "secret-second-value";
        var json = $"{{\"version\":1,\"operationId\":\"57c0b8aa-a43a-431f-b63a-c89a1fb5adc8\",\"role\":\"worker\",\"generation\":7,\"type\":\"hello\",\"payload\":{{\"processId\":42,\"processCreationTimeUtcTicks\":638880000000000000,\"capabilityNonce\":\"{firstSecret}\",\"capabilityNonce\":\"{secondSecret}\",\"buildIdentity\":\"build-1\",\"loopbackEndpoint\":null}}}}";
        var pool = new TrackingArrayPool();

        var exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => new ControlFrameCodec(pool).ReadAsync(
                new MemoryStream(CreateFrame(Encoding.UTF8.GetBytes(json))),
                CancellationToken.None));

        Assert.Equal("DuplicateJsonProperty", exception.Code.ToString());
        Assert.DoesNotContain(firstSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondSecret, exception.Message, StringComparison.Ordinal);
        Assert.True(pool.ReturnedWithClearData);
    }

    [Fact]
    public async Task RejectsDuplicateNestedHealthProperty()
    {
        var json = CreateRawEnvelopeJson().Replace(
            "\"status\":\"ready\"",
            "\"status\":\"ready\",\"status\":\"degraded\"",
            StringComparison.Ordinal);

        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal("DuplicateJsonProperty", exception.Code.ToString());
    }

    [Fact]
    public async Task RejectsUnmappedHelloPayloadFields()
    {
        var json = "{\"version\":1,\"operationId\":\"57c0b8aa-a43a-431f-b63a-c89a1fb5adc8\",\"role\":\"worker\",\"generation\":7,\"type\":\"hello\",\"payload\":{\"processId\":42,\"processCreationTimeUtcTicks\":638880000000000000,\"capabilityNonce\":\"secret\",\"buildIdentity\":\"build-1\",\"loopbackEndpoint\":null,\"unexpected\":true}}";

        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal(ControlProtocolErrorCode.InvalidPayload, exception.Code);
    }

    [Fact]
    public async Task RejectsFieldsOnPayloadlessMessages()
    {
        var json = CreateRawEnvelopeJson()
            .Replace("\"type\":\"health\"", "\"type\":\"drain\"", StringComparison.Ordinal)
            .Replace("{\"protocolVersion\":1,\"buildIdentity\":\"build-1\",\"generation\":7,\"generationNonce\":\"generation-7\",\"status\":\"ready\",\"activeWorkRemaining\":0}", "{\"unexpected\":true}", StringComparison.Ordinal);

        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal(ControlProtocolErrorCode.InvalidPayload, exception.Code);
    }

    [Fact]
    public async Task RejectsNegativeActiveWorkPayload()
    {
        var json = CreateRawEnvelopeJson()
            .Replace("\"type\":\"health\"", "\"type\":\"activeWorkRemaining\"", StringComparison.Ordinal)
            .Replace("{\"protocolVersion\":1,\"buildIdentity\":\"build-1\",\"generation\":7,\"generationNonce\":\"generation-7\",\"status\":\"ready\",\"activeWorkRemaining\":0}", "{\"count\":-1}", StringComparison.Ordinal);

        var exception = await ReadProtocolExceptionAsync(json);

        Assert.Equal(ControlProtocolErrorCode.InvalidPayload, exception.Code);
    }

    [Fact]
    public async Task RejectsMoreThanMaximumFramesPerSession()
    {
        var frame = CreateFrame(Encoding.UTF8.GetBytes(CreateRawEnvelopeJson()));
        await using var stream = new MemoryStream();
        for (var index = 0; index <= ControlProtocolConstants.MaxFramesPerSession; index++)
        {
            await stream.WriteAsync(frame, CancellationToken.None);
        }

        stream.Position = 0;
        var codec = new ControlFrameCodec();
        for (var index = 0; index < ControlProtocolConstants.MaxFramesPerSession; index++)
        {
            await codec.ReadAsync(stream, CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => codec.ReadAsync(stream, CancellationToken.None));

        Assert.Equal(ControlProtocolErrorCode.FrameLimitExceeded, exception.Code);
    }

    [Fact]
    public async Task RejectsOversizedSerializedEnvelope()
    {
        var hugeStatus = new string('x', ControlProtocolConstants.MaxFrameBytes);
        var envelope = CreateEnvelope(ControlMessageType.Drain, new { padding = hugeStatus });

        var exception = await Assert.ThrowsAsync<ControlProtocolException>(
            () => new ControlFrameCodec().WriteAsync(new MemoryStream(), envelope, CancellationToken.None));

        Assert.Equal(ControlProtocolErrorCode.FrameTooLarge, exception.Code);
    }

    [Fact]
    public async Task ClearsRentedWriteBufferWhenWriteIsCancelled()
    {
        var pool = new TrackingArrayPool();
        var envelope = CreateEnvelope(ControlMessageType.Health, new HealthPayload(1, "build-1", 7, "generation-7", "ready", 0));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ControlFrameCodec(pool).WriteAsync(new CancelOnWriteStream(), envelope, CancellationToken.None));

        Assert.True(pool.ReturnedWithClearData);
    }

    private static ControlEnvelope CreateEnvelope(ControlMessageType type, object payload) =>
        new(
            ControlProtocolConstants.CurrentVersion,
            Guid.Parse("57c0b8aa-a43a-431f-b63a-c89a1fb5adc8"),
            RuntimeRole.Worker,
            7,
            type,
            JsonSerializer.SerializeToElement(payload, ControlFrameCodec.SerializerOptions));

    private static string CreateRawEnvelopeJson() =>
        "{\"version\":1,\"operationId\":\"57c0b8aa-a43a-431f-b63a-c89a1fb5adc8\",\"role\":\"worker\",\"generation\":7,\"type\":\"health\",\"payload\":{\"protocolVersion\":1,\"buildIdentity\":\"build-1\",\"generation\":7,\"generationNonce\":\"generation-7\",\"status\":\"ready\",\"activeWorkRemaining\":0}}";

    private static byte[] CreateFrame(byte[] payload)
    {
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, checked((uint)payload.Length));
        payload.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<ControlProtocolException> ReadProtocolExceptionAsync(string json) =>
        await Assert.ThrowsAsync<ControlProtocolException>(
            () => new ControlFrameCodec().ReadAsync(
                new MemoryStream(CreateFrame(Encoding.UTF8.GetBytes(json))),
                CancellationToken.None));

    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class CancelDuringPayloadStream(int payloadLength) : Stream
    {
        private bool _prefixRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prefixRead)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.Span, checked((uint)payloadLength));
                _prefixRead = true;
                return ValueTask.FromResult(4);
            }

            return ValueTask.FromCanceled<int>(new CancellationToken(canceled: true));
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _rented = new(ReferenceEqualityComparer.Instance);
        private bool _allReturnedDataWasClear = true;
        private int _returnCount;

        public int RentCount { get; private set; }
        public bool ReturnedWithClearData => _returnCount > 0 && _allReturnedDataWasClear;

        public override byte[] Rent(int minimumLength)
        {
            RentCount++;
            var rented = new byte[minimumLength];
            Array.Fill(rented, (byte)0xA5);
            _rented.Add(rented);
            return rented;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(_rented.Remove(array));
            _returnCount++;
            _allReturnedDataWasClear &= clearArray && array.All(value => value == 0);
        }
    }

    private sealed class CancelOnWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromCanceled(new CancellationToken(canceled: true));

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
