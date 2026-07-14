using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using HowardLab.EbayCrm.AppHost.Core.Time;

namespace HowardLab.EbayCrm.AppHost.Core.Diagnostics;

/// <summary>
/// Opens an empty writable segment for the supplied bounded numeric slot.
/// Reused slots must replace or truncate their previous segment; diagnostic data never influences the slot.
/// </summary>
public delegate ValueTask<Stream> DiagnosticSegmentFactory(
    int slot,
    CancellationToken cancellationToken);

public sealed class JsonLinesDiagnosticSink : IDiagnosticSink
{
    private static readonly byte[] NewLine = [(byte)'\n'];
    private readonly DiagnosticSegmentFactory _segmentFactory;
    private readonly IClock _clock;
    private readonly string[] _canaries;
    private readonly int _maxFieldBytes;
    private readonly long _maxSegmentBytes;
    private readonly int _maxSegmentCount;
    private readonly Channel<DiagnosticEvent> _channel;
    private readonly CancellationTokenSource _writerCancellation = new();
    private readonly Task _writerTask;
    private int _completionRequested;
    private int _unavailable;
    private long _droppedEventCount;
    private long _sinkFailureCount;

    public JsonLinesDiagnosticSink(
        DiagnosticSegmentFactory segmentFactory,
        IClock clock,
        IEnumerable<SecretValue>? registeredSecrets = null,
        int channelCapacity = 256,
        int maxFieldBytes = 4_096,
        long maxSegmentBytes = 1_048_576,
        int maxSegmentCount = 4)
    {
        ArgumentNullException.ThrowIfNull(segmentFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFieldBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFieldBytes, 65_536);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSegmentBytes, 64);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegmentCount);

        _segmentFactory = segmentFactory;
        _clock = clock;
        _canaries = (registeredSecrets ?? [])
            .Select(secret => secret.RevealForChildEnvironment())
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
        _maxFieldBytes = maxFieldBytes;
        _maxSegmentBytes = maxSegmentBytes;
        _maxSegmentCount = maxSegmentCount;
        _channel = Channel.CreateBounded<DiagnosticEvent>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _writerTask = RunWriterAsync();
    }

    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    public long SinkFailureCount => Interlocked.Read(ref _sinkFailureCount);

    public Task Completion => _writerTask;

    public ValueTask WriteAsync(
        DiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var safeEvent = diagnosticEvent.SanitizeText(RedactAndTruncate);
        if (Volatile.Read(ref _unavailable) != 0 || !_channel.Writer.TryWrite(safeEvent))
        {
            Interlocked.Increment(ref _droppedEventCount);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        RequestCompletion();
        try
        {
            await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _writerCancellation.Cancel();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        RequestCompletion();
        await _writerTask.ConfigureAwait(false);
    }

    private void RequestCompletion()
    {
        if (Interlocked.Exchange(ref _completionRequested, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }
    }

    private async Task RunWriterAsync()
    {
        Stream? segment = null;
        long segmentBytes = 0;
        var slot = 0;
        var eventInFlight = false;
        var writerCancellationToken = _writerCancellation.Token;

        try
        {
            await foreach (var diagnosticEvent in _channel.Reader
                .ReadAllAsync(writerCancellationToken)
                .ConfigureAwait(false))
            {
                eventInFlight = true;
                var entry = Serialize(diagnosticEvent);
                if (segment is null || (segmentBytes > 0 && segmentBytes + entry.Length > _maxSegmentBytes))
                {
                    if (segment is not null)
                    {
                        await segment.FlushAsync(writerCancellationToken).ConfigureAwait(false);
                        await segment.DisposeAsync().ConfigureAwait(false);
                        slot = (slot + 1) % _maxSegmentCount;
                    }

                    segment = await _segmentFactory(slot, writerCancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("The diagnostic segment factory returned no stream.");
                    segmentBytes = 0;
                }

                await segment.WriteAsync(entry, writerCancellationToken).ConfigureAwait(false);
                segmentBytes += entry.Length;
                eventInFlight = false;
            }

            if (segment is not null)
            {
                await segment.FlushAsync(writerCancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (writerCancellationToken.IsCancellationRequested)
        {
            MarkUnavailable(eventInFlight, sinkFailure: false);
        }
        catch (Exception exception)
        {
            _ = exception.GetType();
            MarkUnavailable(eventInFlight, sinkFailure: true);
        }
        finally
        {
            if (segment is not null)
            {
                try
                {
                    await segment.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    Interlocked.Increment(ref _sinkFailureCount);
                }
            }
        }
    }

    private void MarkUnavailable(bool eventInFlight, bool sinkFailure)
    {
        if (sinkFailure)
        {
            Interlocked.Increment(ref _sinkFailureCount);
        }

        Volatile.Write(ref _unavailable, 1);
        _channel.Writer.TryComplete();
        if (eventInFlight)
        {
            Interlocked.Increment(ref _droppedEventCount);
        }

        while (_channel.Reader.TryRead(out _))
        {
            Interlocked.Increment(ref _droppedEventCount);
        }
    }

    private ReadOnlyMemory<byte> Serialize(DiagnosticEvent diagnosticEvent)
    {
        var timestamp = _clock.UtcNow;
        var eventName = diagnosticEvent.Name;
        var fields = diagnosticEvent.Fields;
        var fieldCount = fields.Count;
        var maximumEntryBytes = checked((int)Math.Min(_maxSegmentBytes, 65_536));

        while (fieldCount >= 0)
        {
            var candidate = SerializeCandidate(timestamp, eventName, fields, fieldCount);
            if (candidate.Length + NewLine.Length <= maximumEntryBytes)
            {
                var entry = new byte[candidate.Length + NewLine.Length];
                candidate.CopyTo(entry, 0);
                NewLine.CopyTo(entry, candidate.Length);
                return entry;
            }

            fieldCount--;
        }

        return "{\"event\":\"diagnostic.truncated\"}\n"u8.ToArray();
    }

    private byte[] SerializeCandidate(
        DateTimeOffset timestamp,
        string eventName,
        IReadOnlyList<KeyValuePair<string, DiagnosticField>> fields,
        int fieldCount)
    {
        var buffer = new ArrayBufferWriter<byte>(checked((int)Math.Min(1_024L, _maxSegmentBytes)));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", timestamp);
            writer.WriteString("event", eventName);
            writer.WriteStartObject("fields");
            for (var index = 0; index < fieldCount; index++)
            {
                WriteField(writer, fields[index].Key, fields[index].Value);
            }

            writer.WriteEndObject();
            if (fieldCount < fields.Count)
            {
                writer.WriteBoolean("fieldsTruncated", true);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private void WriteField(Utf8JsonWriter writer, string name, DiagnosticField field)
    {
        switch (field.Kind)
        {
            case DiagnosticFieldKind.String:
            case DiagnosticFieldKind.ReasonCode:
                writer.WriteString(name, field.Text!);
                break;
            case DiagnosticFieldKind.Integer:
                writer.WriteNumber(name, field.IntegerValue);
                break;
            case DiagnosticFieldKind.Boolean:
                writer.WriteBoolean(name, field.BooleanValue);
                break;
            case DiagnosticFieldKind.Guid:
                writer.WriteString(name, field.GuidValue);
                break;
            case DiagnosticFieldKind.Timestamp:
                writer.WriteString(name, field.TimestampValue);
                break;
            default:
                throw new InvalidOperationException("Unsupported diagnostic field kind.");
        }
    }

    private string RedactAndTruncate(string value)
    {
        foreach (var canary in _canaries)
        {
            value = value.Replace(canary, "[REDACTED]", StringComparison.Ordinal);
        }

        if (Encoding.UTF8.GetByteCount(value) <= _maxFieldBytes)
        {
            return value;
        }

        var prefixLength = GetCompleteUtf16PrefixLength(value, _maxFieldBytes);
        return value[..prefixLength];
    }

    private static int GetCompleteUtf16PrefixLength(ReadOnlySpan<char> value, int maximumBytes)
    {
        var characterOffset = 0;
        var byteCount = 0;
        while (characterOffset < value.Length)
        {
            var status = Rune.DecodeFromUtf16(value[characterOffset..], out var rune, out var consumed);
            var runeByteCount = status == OperationStatus.Done ? rune.Utf8SequenceLength : 3;
            consumed = status == OperationStatus.Done ? consumed : 1;
            if (byteCount + runeByteCount > maximumBytes)
            {
                break;
            }

            byteCount += runeByteCount;
            characterOffset += consumed;
        }

        return characterOffset;
    }
}
