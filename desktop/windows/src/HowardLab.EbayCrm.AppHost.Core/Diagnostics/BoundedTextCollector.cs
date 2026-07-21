using System.Buffers;
using System.Text;

namespace HowardLab.EbayCrm.AppHost.Core.Diagnostics;

public sealed class BoundedTextCollector
{
    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly byte[] RedactedBytes = Encoding.UTF8.GetBytes(SecretCanary.RedactedText);
    private readonly object _gate = new();
    private readonly int _maxBytes;
    private readonly byte[][] _canaries;
    private readonly byte[] _matchBuffer;
    private readonly byte[] _decoderTail = new byte[4];
    private readonly byte[] _lineBuffer;
    private readonly MemoryStream _buffer;
    private int _matchCount;
    private int _decoderTailCount;
    private int _lineByteCount;
    private long _lineTotalBytes;
    private long _droppedLineCount;
    private long _invalidEncodingCount;
    private long _lineCount;
    private long _truncatedByteCount;
    private long _observedByteCount;
    private bool _lineAccepting = true;
    private bool _hasFinalFragment;
    private bool _previousWasCarriageReturn;
    private bool _completed;

    public BoundedTextCollector(
        int maxBytes,
        int maxLineBytes,
        IEnumerable<string>? registeredCanaries = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);

        _maxBytes = maxBytes;
        _canaries = (registeredCanaries ?? [])
            .Select(value => SecretCanary.Validate(value, nameof(registeredCanaries)))
            .Distinct(StringComparer.Ordinal)
            .Select(Encoding.UTF8.GetBytes)
            .OrderByDescending(value => value.Length)
            .ToArray();
        _matchBuffer = new byte[Math.Max(1, _canaries.FirstOrDefault()?.Length ?? 0)];
        _lineBuffer = new byte[Math.Min(maxBytes, maxLineBytes)];
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

    public long LineCount
    {
        get
        {
            lock (_gate)
            {
                return _lineCount;
            }
        }
    }

    public long DroppedLineCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedLineCount;
            }
        }
    }

    public long InvalidEncodingCount
    {
        get
        {
            lock (_gate)
            {
                return _invalidEncodingCount;
            }
        }
    }

    public long TruncatedByteCount
    {
        get
        {
            lock (_gate)
            {
                return _truncatedByteCount;
            }
        }
    }

    public long ObservedByteCount
    {
        get
        {
            lock (_gate)
            {
                return _observedByteCount;
            }
        }
    }

    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            ThrowIfCompleted();
            var remaining = text.AsSpan();
            Span<byte> encodedRune = stackalloc byte[4];
            while (!remaining.IsEmpty)
            {
                var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
                if (status != OperationStatus.Done)
                {
                    rune = Rune.ReplacementChar;
                    consumed = 1;
                }

                var encodedLength = rune.EncodeToUtf8(encodedRune);
                AppendSourceBytes(encodedRune[..encodedLength]);
                remaining = remaining[consumed..];
            }
        }
    }

    public void Append(ReadOnlySpan<byte> utf8Bytes)
    {
        lock (_gate)
        {
            ThrowIfCompleted();
            AppendSourceBytes(utf8Bytes);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            ResolveMatches(final: true);
            if (_decoderTailCount > 0)
            {
                _invalidEncodingCount++;
                _decoderTailCount = 0;
            }

            if (_hasFinalFragment)
            {
                CommitLine();
            }

            _completed = true;
        }
    }

    public string Snapshot()
    {
        lock (_gate)
        {
            return Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, checked((int)_buffer.Length));
        }
    }

    private void AppendSourceBytes(ReadOnlySpan<byte> bytes)
    {
        _observedByteCount = checked(_observedByteCount + bytes.Length);
        foreach (var value in bytes)
        {
            _matchBuffer[_matchCount++] = value;
            ResolveMatches(final: false);
        }
    }

    private void ResolveMatches(bool final)
    {
        while (_matchCount > 0)
        {
            var pending = _matchBuffer.AsSpan(0, _matchCount);
            var exactLength = FindLongestCanaryPrefix(pending);
            var mayGrowIntoCanary = !final && HasLongerCanaryPrefix(pending);

            if (mayGrowIntoCanary || (!final && exactLength == 0 && IsCanaryPrefix(pending)))
            {
                return;
            }

            if (exactLength > 0)
            {
                EmitRedaction();
                RemoveMatchedBytes(exactLength);
                continue;
            }

            EmitDecodedByte(_matchBuffer[0]);
            RemoveMatchedBytes(1);
        }
    }

    private int FindLongestCanaryPrefix(ReadOnlySpan<byte> pending)
    {
        foreach (var canary in _canaries)
        {
            if (canary.Length <= pending.Length && pending.StartsWith(canary))
            {
                return canary.Length;
            }
        }

        return 0;
    }

    private bool IsCanaryPrefix(ReadOnlySpan<byte> pending)
    {
        foreach (var canary in _canaries)
        {
            if (canary.AsSpan().StartsWith(pending))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasLongerCanaryPrefix(ReadOnlySpan<byte> pending)
    {
        foreach (var canary in _canaries)
        {
            if (canary.Length > pending.Length && canary.AsSpan().StartsWith(pending))
            {
                return true;
            }
        }

        return false;
    }

    private void EmitRedaction()
    {
        foreach (var value in RedactedBytes)
        {
            EmitDecodedByte(value);
        }
    }

    private void RemoveMatchedBytes(int count)
    {
        _matchBuffer.AsSpan(count, _matchCount - count).CopyTo(_matchBuffer);
        _matchCount -= count;
    }

    private void EmitDecodedByte(byte value)
    {
        _decoderTail[_decoderTailCount++] = value;
        while (_decoderTailCount > 0)
        {
            var tail = _decoderTail.AsSpan(0, _decoderTailCount);
            var status = Rune.DecodeFromUtf8(tail, out var rune, out var consumed);
            if (status == OperationStatus.NeedMoreData)
            {
                return;
            }

            if (status == OperationStatus.InvalidData)
            {
                rune = Rune.ReplacementChar;
                consumed = 1;
                _invalidEncodingCount++;
            }

            ProcessRune(rune);
            _decoderTail.AsSpan(consumed, _decoderTailCount - consumed).CopyTo(_decoderTail);
            _decoderTailCount -= consumed;
        }
    }

    private void ProcessRune(Rune rune)
    {
        if (rune.Value == '\r')
        {
            CommitLine();
            _previousWasCarriageReturn = true;
            return;
        }

        if (rune.Value == '\n')
        {
            if (_previousWasCarriageReturn)
            {
                _previousWasCarriageReturn = false;
                return;
            }

            CommitLine();
            return;
        }

        _previousWasCarriageReturn = false;
        _hasFinalFragment = true;
        Span<byte> encodedRune = stackalloc byte[4];
        var encodedLength = rune.EncodeToUtf8(encodedRune);
        _lineTotalBytes += encodedLength;
        if (!_lineAccepting)
        {
            return;
        }

        if (_lineByteCount + encodedLength > _lineBuffer.Length)
        {
            _lineAccepting = false;
            return;
        }

        encodedRune[..encodedLength].CopyTo(_lineBuffer.AsSpan(_lineByteCount));
        _lineByteCount += encodedLength;
    }

    private void CommitLine()
    {
        if (_lineTotalBytes > _lineByteCount)
        {
            _truncatedByteCount += _lineTotalBytes - _lineByteCount;
        }

        if (_buffer.Length + _lineByteCount + NewLine.Length > _maxBytes)
        {
            _droppedLineCount++;
        }
        else
        {
            _buffer.Write(_lineBuffer, 0, _lineByteCount);
            _buffer.Write(NewLine);
            _lineCount++;
        }

        _lineByteCount = 0;
        _lineTotalBytes = 0;
        _lineAccepting = true;
        _hasFinalFragment = false;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Cannot append child output after the collector is complete.");
        }
    }
}
