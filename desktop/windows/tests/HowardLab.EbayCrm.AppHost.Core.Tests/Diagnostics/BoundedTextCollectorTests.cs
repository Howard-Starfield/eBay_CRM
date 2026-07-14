using System.Text;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;

namespace HowardLab.EbayCrm.AppHost.Core.Tests.Diagnostics;

public sealed class BoundedTextCollectorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("[REDACTED]")]
    [InlineData("REDACTED")]
    [InlineData("ACT")]
    public void RegisteredCanariesRejectReplacementTokenCollisions(string invalidCanary)
    {
        Assert.Throws<ArgumentException>(
            () => new BoundedTextCollector(maxBytes: 64, maxLineBytes: 32, [invalidCanary]));
    }

    [Fact]
    public void CollectorTruncatesWithoutGrowingPastItsUtf8ByteBudget()
    {
        var collector = new BoundedTextCollector(maxBytes: 64, maxLineBytes: 16, ["CANARY"]);

        for (var i = 0; i < 1_000; i++)
        {
            collector.Append("CANARY-éééééééé-abcdefghijklmnopqrstuvwxyz\n");
        }

        collector.Complete();

        Assert.InRange(collector.BufferedBytes, 0, 64);
        Assert.Equal(collector.BufferedBytes, Encoding.UTF8.GetByteCount(collector.Snapshot()));
        Assert.True(collector.DroppedLineCount > 0);
        Assert.True(collector.TruncatedByteCount > 0);
        Assert.DoesNotContain("CANARY", collector.Snapshot());
    }

    [Fact]
    public void PerLineLimitDoesNotSplitAMultiByteUtf8Scalar()
    {
        var collector = new BoundedTextCollector(maxBytes: 32, maxLineBytes: 5);

        collector.Append("ééé");
        collector.Complete();

        Assert.Equal("éé\n", collector.Snapshot());
        Assert.Equal(5, collector.BufferedBytes);
        Assert.Equal(2, collector.TruncatedByteCount);
    }

    [Fact]
    public void InvalidUtf8IsReplacedAndCountedWithoutExceedingTheBudget()
    {
        var collector = new BoundedTextCollector(maxBytes: 16, maxLineBytes: 8);

        collector.Append(new byte[] { (byte)'a', 0xff, (byte)'b' });
        collector.Complete();

        Assert.Equal("a\ufffdb\n", collector.Snapshot());
        Assert.Equal(1, collector.InvalidEncodingCount);
        Assert.Equal(6, collector.BufferedBytes);
        Assert.Equal(collector.BufferedBytes, Encoding.UTF8.GetByteCount(collector.Snapshot()));
    }

    [Fact]
    public void ExactCanariesAreRedactedBeforePerLineTruncation()
    {
        var collector = new BoundedTextCollector(maxBytes: 64, maxLineBytes: 24, ["very-secret-canary"]);

        collector.Append("prefix-very-secret-canary-suffix");
        collector.Complete();

        Assert.Equal("prefix-[REDACTED]-suffix\n", collector.Snapshot());
        Assert.DoesNotContain("very-secret-canary", collector.Snapshot());
    }

    [Fact]
    public void OverlappingCanariesAreRedactedLongestFirst()
    {
        var collector = new BoundedTextCollector(
            maxBytes: 64,
            maxLineBytes: 32,
            ["TOKEN", "TOKEN-secret-tail"]);

        collector.Append("TOKEN-secret-tail");
        collector.Complete();

        Assert.Equal("[REDACTED]\n", collector.Snapshot());
        Assert.DoesNotContain("secret-tail", collector.Snapshot());
    }

    [Fact]
    public void NewlineDelimitedInputTracksAcceptedAndDroppedLines()
    {
        var collector = new BoundedTextCollector(maxBytes: 8, maxLineBytes: 8);

        collector.Append("one\ntwo\nthree");
        collector.Complete();

        Assert.Equal("one\ntwo\n", collector.Snapshot());
        Assert.Equal(2, collector.LineCount);
        Assert.Equal(1, collector.DroppedLineCount);
    }

    [Fact]
    public void BufferedStateStopsGrowingAfterTheGlobalBudgetIsReached()
    {
        var collector = new BoundedTextCollector(maxBytes: 12, maxLineBytes: 8);
        collector.Append("first\n");
        collector.Append("second\n");
        var fullSnapshot = collector.Snapshot();
        var fullByteCount = collector.BufferedBytes;

        for (var index = 0; index < 10_000; index++)
        {
            collector.Append("more-output\n");
        }

        collector.Complete();

        Assert.Equal(fullSnapshot, collector.Snapshot());
        Assert.Equal(fullByteCount, collector.BufferedBytes);
        Assert.InRange(collector.BufferedBytes, 0, 12);
    }

    [Fact]
    public void ProcessingMemoryIsBoundedByConfiguredBudgetsRatherThanInputLength()
    {
        var collector = new BoundedTextCollector(maxBytes: 64, maxLineBytes: 32);
        var untrustedLine = new string('x', 2_000_000);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        collector.Append(untrustedLine);

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Empty(collector.Snapshot());
        collector.Complete();
        Assert.InRange(allocatedBytes, 0, 262_144);
        Assert.InRange(collector.BufferedBytes, 0, 64);
        Assert.Equal(2_000_000 - 32, collector.TruncatedByteCount);
    }

    [Fact]
    public void InvalidUtf8AndRegisteredCanariesCannotEscapeTogether()
    {
        var collector = new BoundedTextCollector(maxBytes: 64, maxLineBytes: 32, ["CANARY"]);
        var bytes = new byte[]
        {
            (byte)'C', (byte)'A', (byte)'N', (byte)'A', (byte)'R', (byte)'Y',
            (byte)'-', 0xff, (byte)'x',
        };

        collector.Append(bytes);
        collector.Complete();

        Assert.Equal("[REDACTED]-\ufffdx\n", collector.Snapshot());
        Assert.Equal(1, collector.InvalidEncodingCount);
        Assert.DoesNotContain("CANARY", collector.Snapshot());
        Assert.Equal(collector.BufferedBytes, Encoding.UTF8.GetByteCount(collector.Snapshot()));
    }

    [Fact]
    public void FinalUnterminatedFragmentAppearsOnlyAfterCompletion()
    {
        var collector = new BoundedTextCollector(maxBytes: 64, maxLineBytes: 32);

        collector.Append("partial");

        Assert.Empty(collector.Snapshot());
        Assert.Equal(0, collector.LineCount);
        collector.Complete();
        Assert.Equal("partial\n", collector.Snapshot());
        Assert.Equal(1, collector.LineCount);
    }

    [Fact]
    public void NormalLinesAndFragmentsAreMaintainedAcrossAppendBoundaries()
    {
        var collector = new BoundedTextCollector(maxBytes: 64, maxLineBytes: 32);

        collector.Append("one");
        Assert.Empty(collector.Snapshot());
        collector.Append("\ntwo");
        Assert.Equal("one\n", collector.Snapshot());
        collector.Complete();

        Assert.Equal("one\ntwo\n", collector.Snapshot());
    }

    [Fact]
    public void CanaryIsRedactedAcrossEveryTwoChunkAndPerByteBoundary()
    {
        const string canary = "sëcret-CANARY-value";
        var input = Encoding.UTF8.GetBytes($"prefix-{canary}-suffix");

        for (var boundary = 1; boundary < input.Length; boundary++)
        {
            var collector = new BoundedTextCollector(maxBytes: 64, maxLineBytes: 48, [canary]);
            collector.Append(input.AsSpan(0, boundary));
            collector.Append(input.AsSpan(boundary));
            collector.Complete();

            Assert.Equal("prefix-[REDACTED]-suffix\n", collector.Snapshot());
        }

        var bytewiseCollector = new BoundedTextCollector(maxBytes: 64, maxLineBytes: 48, [canary]);
        foreach (var value in input)
        {
            bytewiseCollector.Append([value]);
        }

        bytewiseCollector.Complete();
        Assert.Equal("prefix-[REDACTED]-suffix\n", bytewiseCollector.Snapshot());
    }

    [Fact]
    public void Utf8ScalarSplitAtEveryByteBoundaryIsDecodedOnceWithoutInvalidData()
    {
        var input = Encoding.UTF8.GetBytes("A😀B");

        for (var boundary = 1; boundary < input.Length; boundary++)
        {
            var collector = new BoundedTextCollector(maxBytes: 32, maxLineBytes: 24);
            collector.Append(input.AsSpan(0, boundary));
            collector.Append(input.AsSpan(boundary));
            collector.Complete();

            Assert.Equal("A😀B\n", collector.Snapshot());
            Assert.Equal(0, collector.InvalidEncodingCount);
        }
    }

    [Fact]
    public void CanaryContainingNewlineIsRedactedBeforeLineSplittingAcrossEveryBoundary()
    {
        const string canary = "TOKEN\r\nsecret";
        var input = Encoding.UTF8.GetBytes($"prefix-{canary}-suffix\n");

        for (var boundary = 1; boundary < input.Length; boundary++)
        {
            var collector = new BoundedTextCollector(maxBytes: 64, maxLineBytes: 48, [canary]);
            collector.Append(input.AsSpan(0, boundary));
            collector.Append(input.AsSpan(boundary));
            collector.Complete();

            Assert.Equal("prefix-[REDACTED]-suffix\n", collector.Snapshot());
            Assert.Equal(1, collector.LineCount);
        }
    }

    [Fact]
    public void IncompleteTrailingUtf8ScalarIsCountedAndDiscardedOnCompletion()
    {
        var collector = new BoundedTextCollector(maxBytes: 16, maxLineBytes: 8);

        collector.Append(new byte[] { 0xf0, 0x9f });
        Assert.Empty(collector.Snapshot());
        Assert.Equal(0, collector.InvalidEncodingCount);
        collector.Complete();

        Assert.Empty(collector.Snapshot());
        Assert.Equal(1, collector.InvalidEncodingCount);
    }
}
