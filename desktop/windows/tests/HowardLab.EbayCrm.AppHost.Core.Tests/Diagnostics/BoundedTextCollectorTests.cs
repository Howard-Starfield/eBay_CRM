using System.Text;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;

namespace HowardLab.EbayCrm.AppHost.Core.Tests.Diagnostics;

public sealed class BoundedTextCollectorTests
{
    [Fact]
    public void CollectorTruncatesWithoutGrowingPastItsUtf8ByteBudget()
    {
        var collector = new BoundedTextCollector(maxBytes: 64, maxLineBytes: 16, ["CANARY"]);

        for (var i = 0; i < 1_000; i++)
        {
            collector.Append("CANARY-éééééééé-abcdefghijklmnopqrstuvwxyz");
        }

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

        Assert.Equal("éé\n", collector.Snapshot());
        Assert.Equal(5, collector.BufferedBytes);
        Assert.Equal(2, collector.TruncatedByteCount);
    }

    [Fact]
    public void InvalidUtf8IsReplacedAndCountedWithoutExceedingTheBudget()
    {
        var collector = new BoundedTextCollector(maxBytes: 16, maxLineBytes: 8);

        collector.Append(new byte[] { (byte)'a', 0xff, (byte)'b' });

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

        Assert.Equal("[REDACTED]\n", collector.Snapshot());
        Assert.DoesNotContain("secret-tail", collector.Snapshot());
    }

    [Fact]
    public void NewlineDelimitedInputTracksAcceptedAndDroppedLines()
    {
        var collector = new BoundedTextCollector(maxBytes: 8, maxLineBytes: 8);

        collector.Append("one\ntwo\nthree");

        Assert.Equal("one\ntwo\n", collector.Snapshot());
        Assert.Equal(2, collector.LineCount);
        Assert.Equal(1, collector.DroppedLineCount);
    }

    [Fact]
    public void BufferedStateStopsGrowingAfterTheGlobalBudgetIsReached()
    {
        var collector = new BoundedTextCollector(maxBytes: 12, maxLineBytes: 8);
        collector.Append("first");
        collector.Append("second");
        var fullSnapshot = collector.Snapshot();
        var fullByteCount = collector.BufferedBytes;

        for (var index = 0; index < 10_000; index++)
        {
            collector.Append("more-output");
        }

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

        Assert.Equal("[REDACTED]-\ufffdx\n", collector.Snapshot());
        Assert.Equal(1, collector.InvalidEncodingCount);
        Assert.DoesNotContain("CANARY", collector.Snapshot());
        Assert.Equal(collector.BufferedBytes, Encoding.UTF8.GetByteCount(collector.Snapshot()));
    }
}
