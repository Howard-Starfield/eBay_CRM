using System.Buffers;
using System.Text;

namespace HowardLab.EbayCrm.AppHost.Core.Diagnostics;

public sealed class BoundedTextCollector
{
    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly byte[] RedactedBytes = "[REDACTED]"u8.ToArray();
    private static readonly byte[] ReplacementRuneBytes = "\ufffd"u8.ToArray();
    private readonly object _gate = new();
    private readonly int _maxBytes;
    private readonly int _maxLineBytes;
    private readonly string[] _canaries;
    private readonly byte[][] _canaryBytes;
    private readonly MemoryStream _buffer;
    private long _droppedLineCount;
    private long _invalidEncodingCount;
    private long _lineCount;
    private long _truncatedByteCount;

    public BoundedTextCollector(
        int maxBytes,
        int maxLineBytes,
        IEnumerable<string>? registeredCanaries = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);

        _maxBytes = maxBytes;
        _maxLineBytes = maxLineBytes;
        _canaries = (registeredCanaries ?? [])
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
        _canaryBytes = _canaries.Select(Encoding.UTF8.GetBytes).ToArray();
        _buffer = new MemoryStream(Math.Min(maxBytes, 4_096));
    }

    public int BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return checked((int)_buffer.Length);
            }
        }
    }

    public long LineCount => Interlocked.Read(ref _lineCount);

    public long DroppedLineCount => Interlocked.Read(ref _droppedLineCount);

    public long InvalidEncodingCount => Interlocked.Read(ref _invalidEncodingCount);

    public long TruncatedByteCount => Interlocked.Read(ref _truncatedByteCount);

    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        AppendTextLines(text.AsSpan());
    }

    public void Append(ReadOnlySpan<byte> utf8Bytes) => AppendUtf8Lines(utf8Bytes);

    public string Snapshot()
    {
        lock (_gate)
        {
            return Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, checked((int)_buffer.Length));
        }
    }

    private void AppendTextLines(ReadOnlySpan<char> text)
    {
        var lineStart = 0;
        var foundDelimiter = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            foundDelimiter = true;
            AppendTextLine(text[lineStart..index]);
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            lineStart = index + 1;
        }

        if (lineStart < text.Length || !foundDelimiter)
        {
            AppendTextLine(text[lineStart..]);
        }
    }

    private void AppendUtf8Lines(ReadOnlySpan<byte> bytes)
    {
        var lineStart = 0;
        var foundDelimiter = false;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] is not ((byte)'\r' or (byte)'\n'))
            {
                continue;
            }

            foundDelimiter = true;
            AppendUtf8Line(bytes[lineStart..index]);
            if (bytes[index] == (byte)'\r' && index + 1 < bytes.Length && bytes[index + 1] == (byte)'\n')
            {
                index++;
            }

            lineStart = index + 1;
        }

        if (lineStart < bytes.Length || !foundDelimiter)
        {
            AppendUtf8Line(bytes[lineStart..]);
        }
    }

    private void AppendTextLine(ReadOnlySpan<char> line)
    {
        var boundedLine = new byte[Math.Min(_maxBytes, _maxLineBytes)];
        var keptBytes = 0;
        long totalBytes = 0;
        var acceptingOutput = true;
        var offset = 0;

        while (offset < line.Length && TryFindNextCanary(line[offset..], out var matchOffset, out var matchLength))
        {
            AppendText(line.Slice(offset, matchOffset), boundedLine, ref keptBytes, ref totalBytes, ref acceptingOutput);
            AppendBytes(RedactedBytes, boundedLine, ref keptBytes, ref totalBytes, ref acceptingOutput);
            offset += matchOffset + matchLength;
        }

        AppendText(line[offset..], boundedLine, ref keptBytes, ref totalBytes, ref acceptingOutput);
        CommitLine(boundedLine.AsSpan(0, keptBytes), totalBytes);
    }

    private void AppendUtf8Line(ReadOnlySpan<byte> line)
    {
        var boundedLine = new byte[Math.Min(_maxBytes, _maxLineBytes)];
        var keptBytes = 0;
        long totalBytes = 0;
        var acceptingOutput = true;
        var invalidEncodingCount = 0;
        var offset = 0;

        while (offset < line.Length && TryFindNextCanary(line[offset..], out var matchOffset, out var matchLength))
        {
            AppendUtf8(
                line.Slice(offset, matchOffset),
                boundedLine,
                ref keptBytes,
                ref totalBytes,
                ref acceptingOutput,
                ref invalidEncodingCount);
            AppendBytes(RedactedBytes, boundedLine, ref keptBytes, ref totalBytes, ref acceptingOutput);
            offset += matchOffset + matchLength;
        }

        AppendUtf8(
            line[offset..],
            boundedLine,
            ref keptBytes,
            ref totalBytes,
            ref acceptingOutput,
            ref invalidEncodingCount);
        if (invalidEncodingCount > 0)
        {
            Interlocked.Add(ref _invalidEncodingCount, invalidEncodingCount);
        }

        CommitLine(boundedLine.AsSpan(0, keptBytes), totalBytes);
    }

    private void CommitLine(ReadOnlySpan<byte> line, long totalBytes)
    {
        if (totalBytes > line.Length)
        {
            Interlocked.Add(ref _truncatedByteCount, totalBytes - line.Length);
        }

        lock (_gate)
        {
            if (_buffer.Length + line.Length + NewLine.Length > _maxBytes)
            {
                Interlocked.Increment(ref _droppedLineCount);
                return;
            }

            _buffer.Write(line);
            _buffer.Write(NewLine);
            Interlocked.Increment(ref _lineCount);
        }
    }

    private static void AppendText(
        ReadOnlySpan<char> text,
        Span<byte> destination,
        ref int keptBytes,
        ref long totalBytes,
        ref bool acceptingOutput)
    {
        Span<byte> encodedRune = stackalloc byte[4];
        while (!text.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(text, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                AppendBytes(ReplacementRuneBytes, destination, ref keptBytes, ref totalBytes, ref acceptingOutput);
                text = text[1..];
                continue;
            }

            var encodedLength = rune.EncodeToUtf8(encodedRune);
            AppendBytes(encodedRune[..encodedLength], destination, ref keptBytes, ref totalBytes, ref acceptingOutput);
            text = text[consumed..];
        }
    }

    private static void AppendUtf8(
        ReadOnlySpan<byte> bytes,
        Span<byte> destination,
        ref int keptBytes,
        ref long totalBytes,
        ref bool acceptingOutput,
        ref int invalidEncodingCount)
    {
        while (!bytes.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(bytes, out _, out var consumed);
            if (status != OperationStatus.Done)
            {
                AppendBytes(ReplacementRuneBytes, destination, ref keptBytes, ref totalBytes, ref acceptingOutput);
                invalidEncodingCount++;
                bytes = bytes[1..];
                continue;
            }

            AppendBytes(bytes[..consumed], destination, ref keptBytes, ref totalBytes, ref acceptingOutput);
            bytes = bytes[consumed..];
        }
    }

    private static void AppendBytes(
        ReadOnlySpan<byte> bytes,
        Span<byte> destination,
        ref int keptBytes,
        ref long totalBytes,
        ref bool acceptingOutput)
    {
        totalBytes += bytes.Length;
        if (!acceptingOutput)
        {
            return;
        }

        if (keptBytes + bytes.Length > destination.Length)
        {
            acceptingOutput = false;
            return;
        }

        bytes.CopyTo(destination[keptBytes..]);
        keptBytes += bytes.Length;
    }

    private bool TryFindNextCanary(
        ReadOnlySpan<char> text,
        out int matchOffset,
        out int matchLength)
    {
        matchOffset = int.MaxValue;
        matchLength = 0;
        foreach (var canary in _canaries)
        {
            var offset = text.IndexOf(canary.AsSpan(), StringComparison.Ordinal);
            if (offset >= 0 && offset < matchOffset)
            {
                matchOffset = offset;
                matchLength = canary.Length;
            }
        }

        return matchLength > 0;
    }

    private bool TryFindNextCanary(
        ReadOnlySpan<byte> bytes,
        out int matchOffset,
        out int matchLength)
    {
        matchOffset = int.MaxValue;
        matchLength = 0;
        foreach (var canary in _canaryBytes)
        {
            var offset = bytes.IndexOf(canary);
            if (offset >= 0 && offset < matchOffset)
            {
                matchOffset = offset;
                matchLength = canary.Length;
            }
        }

        return matchLength > 0;
    }
}
