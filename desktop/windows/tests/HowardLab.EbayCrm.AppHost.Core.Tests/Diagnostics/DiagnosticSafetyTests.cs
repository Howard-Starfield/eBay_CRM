using System.Text;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;

namespace HowardLab.EbayCrm.AppHost.Core.Tests.Diagnostics;

public sealed class DiagnosticSafetyTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 14, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void SecretValueCanOnlyBeRevealedByTheExplicitChildEnvironmentMethod()
    {
        var secret = new SecretValue("CANARY-secret-123");

        Assert.Equal("CANARY-secret-123", secret.RevealForChildEnvironment());
        Assert.Equal("[REDACTED]", secret.ToString());
        Assert.Equal("[REDACTED]", $"{secret}");
    }

    [Fact]
    public async Task StandardDiagnosticsNeverContainRegisteredCanaries()
    {
        var secret = new SecretValue("CANARY-secret-123");
        var output = new MemoryStream();
        var sink = CreateSink(output, [secret]);

        await sink.WriteAsync(
            DiagnosticEvent.Create($"launch.{secret.RevealForChildEnvironment()}")
                .With($"key-{secret.RevealForChildEnvironment()}", DiagnosticField.String("worker"))
                .With("error", DiagnosticField.String($"child printed {secret.RevealForChildEnvironment()}"))
                .With("filename", DiagnosticField.String($"diagnostic-{secret.RevealForChildEnvironment()}.jsonl")));
        await sink.DisposeAsync();

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("CANARY-secret-123", text);
        Assert.Contains("[REDACTED]", text);
    }

    [Fact]
    public async Task ExactCanariesAreReplacedBeforeAnyFieldInputOrUtf8Truncation()
    {
        var rawSecret = "CANARY-" + new string('x', 70_000);
        var secret = new SecretValue(rawSecret);
        var output = new MemoryStream();
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(output),
            new TestClock(Epoch),
            [secret],
            channelCapacity: 1,
            maxFieldBytes: 32,
            maxSegmentBytes: 512,
            maxSegmentCount: 1);

        await sink.WriteAsync(
            DiagnosticEvent.Create("redaction.order")
                .With("value", DiagnosticField.String(rawSecret)));
        await sink.DisposeAsync();

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("CANARY-", text);
        Assert.Contains("[REDACTED]", text);
    }

    [Fact]
    public async Task OverlappingCanariesAreRedactedLongestFirst()
    {
        var shortSecret = new SecretValue("TOKEN");
        var longSecret = new SecretValue("TOKEN-secret-tail");
        var output = new MemoryStream();
        var sink = CreateSink(output, [shortSecret, longSecret]);

        await sink.WriteAsync(
            DiagnosticEvent.Create("redaction.overlap")
                .With("value", DiagnosticField.String(longSecret.RevealForChildEnvironment())));
        await sink.DisposeAsync();

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("TOKEN", text);
        Assert.DoesNotContain("secret-tail", text);
        Assert.Contains("[REDACTED]", text);
    }

    [Fact]
    public async Task DiagnosticFieldsSerializeOnlyTheirAllowlistedJsonShapes()
    {
        var output = new MemoryStream();
        var sink = CreateSink(output);
        var operationId = Guid.Parse("8cfd9445-c168-4bc6-8aa7-afcf0966f02a");

        await sink.WriteAsync(
            DiagnosticEvent.Create("typed.fields")
                .With("text", DiagnosticField.String("worker"))
                .With("count", DiagnosticField.Integer(42))
                .With("ready", DiagnosticField.Boolean(true))
                .With("operationId", DiagnosticField.Guid(operationId))
                .With("observedAt", DiagnosticField.Timestamp(Epoch))
                .With("reason", DiagnosticField.ReasonCode("child-exited")));
        await sink.DisposeAsync();

        using var document = JsonDocument.Parse(output.ToArray());
        var fields = document.RootElement.GetProperty("fields");
        Assert.Equal("worker", fields.GetProperty("text").GetString());
        Assert.Equal(42, fields.GetProperty("count").GetInt64());
        Assert.True(fields.GetProperty("ready").GetBoolean());
        Assert.Equal(operationId, fields.GetProperty("operationId").GetGuid());
        Assert.Equal(Epoch, fields.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal("child-exited", fields.GetProperty("reason").GetString());
    }

    [Fact]
    public void DiagnosticEventsAcceptDiagnosticFieldsRatherThanArbitraryObjects()
    {
        var withMethods = typeof(DiagnosticEvent).GetMethods()
            .Where(method => method.Name == nameof(DiagnosticEvent.With))
            .ToArray();

        var withMethod = Assert.Single(withMethods);
        Assert.Equal(
            [typeof(string), typeof(DiagnosticField)],
            withMethod.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            Enum.GetNames<DiagnosticFieldKind>(),
            name => string.Equals(name, "Object", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExceptionDiagnosticsContainOnlyTypeAndStableReasonCode()
    {
        var output = new MemoryStream();
        var sink = CreateSink(output);
        var exception = new InvalidOperationException("CANARY-message-that-must-not-be-serialized");

        await sink.WriteAsync(
            DiagnosticEvent.Create("launch.failed")
                .With("exceptionType", DiagnosticField.String(exception.GetType().FullName!))
                .With("reason", DiagnosticField.ReasonCode("launch-failed")));
        await sink.DisposeAsync();

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains(typeof(InvalidOperationException).FullName!, text);
        Assert.Contains("launch-failed", text);
        Assert.DoesNotContain(exception.Message, text);
        Assert.DoesNotContain(exception.StackTrace ?? "CANARY-no-stack", text);
    }

    [Fact]
    public void ReasonCodesRejectFreeFormExceptionText()
    {
        Assert.Equal(
            DiagnosticFieldKind.ReasonCode,
            DiagnosticField.ReasonCode("launch.child-exited").Kind);
        Assert.Throws<ArgumentException>(
            () => DiagnosticField.ReasonCode("database failed with password CANARY"));
    }

    [Fact]
    public async Task SlowAndFullSinkNeverBlocksLifecycleProducers()
    {
        var blockingStream = new BlockingWriteStream();
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(blockingStream),
            new TestClock(Epoch),
            channelCapacity: 1,
            maxFieldBytes: 128,
            maxSegmentBytes: 1_024,
            maxSegmentCount: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await sink.WriteAsync(DiagnosticEvent.Create("first"), deadline.Token);
            await blockingStream.WriteStarted.WaitAsync(deadline.Token);

            var producers = Enumerable.Range(0, 100)
                .Select(index => sink.WriteAsync(
                    DiagnosticEvent.Create("queued")
                        .With("index", DiagnosticField.Integer(index)),
                    deadline.Token).AsTask())
                .ToArray();
            await Task.WhenAll(producers).WaitAsync(deadline.Token);

            Assert.True(sink.DroppedEventCount > 0);
        }
        finally
        {
            blockingStream.ReleaseWrites();
            await sink.DisposeAsync();
        }

        Assert.True(sink.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CanceledCompletionAbortsABlockedWriterBeforeTheGateIsReleased()
    {
        var blockingStream = new BlockingWriteStream();
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(blockingStream),
            new TestClock(Epoch),
            channelCapacity: 1,
            maxFieldBytes: 128,
            maxSegmentBytes: 1_024,
            maxSegmentCount: 1);
        using var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var completionCancellation = new CancellationTokenSource();

        try
        {
            await sink.WriteAsync(DiagnosticEvent.Create("blocked"), testDeadline.Token);
            await blockingStream.WriteStarted.WaitAsync(testDeadline.Token);

            var completion = sink.CompleteAsync(completionCancellation.Token).AsTask();
            completionCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
            await sink.Completion.WaitAsync(testDeadline.Token);

            Assert.True(sink.Completion.IsCompletedSuccessfully);
            Assert.Equal(1, sink.DroppedEventCount);
            Assert.Equal(0, sink.SinkFailureCount);
        }
        finally
        {
            blockingStream.ReleaseWrites();
            await sink.DisposeAsync();
        }
    }

    [Fact]
    public async Task ThrowingSinkAccountsForFailedAndSubsequentEventsWithoutLeakingItsWriter()
    {
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(new ThrowingWriteStream()),
            new TestClock(Epoch),
            channelCapacity: 4,
            maxFieldBytes: 128,
            maxSegmentBytes: 1_024,
            maxSegmentCount: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await sink.WriteAsync(DiagnosticEvent.Create("will-fail"), deadline.Token);
        await sink.Completion.WaitAsync(deadline.Token);
        for (var index = 0; index < 10; index++)
        {
            await sink.WriteAsync(DiagnosticEvent.Create("after-failure"), deadline.Token);
        }

        await sink.DisposeAsync();

        Assert.Equal(1, sink.SinkFailureCount);
        Assert.Equal(11, sink.DroppedEventCount);
        Assert.True(sink.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StreamCancellationWithoutWriterCancellationIsAccountedAsSinkFailure()
    {
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(
                new ThrowingWriteStream(new OperationCanceledException("stream canceled itself"))),
            new TestClock(Epoch),
            channelCapacity: 1,
            maxFieldBytes: 128,
            maxSegmentBytes: 1_024,
            maxSegmentCount: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await sink.WriteAsync(DiagnosticEvent.Create("will-cancel"), deadline.Token);
        await sink.Completion.WaitAsync(deadline.Token);
        await sink.DisposeAsync();

        Assert.Equal(1, sink.SinkFailureCount);
        Assert.Equal(1, sink.DroppedEventCount);
        Assert.True(sink.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task LongSegmentLimitDoesNotOverflowTheBoundedEntrySerializer()
    {
        var output = new MemoryStream();
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(output),
            new TestClock(Epoch),
            channelCapacity: 1,
            maxFieldBytes: 128,
            maxSegmentBytes: (long)int.MaxValue + 1,
            maxSegmentCount: 1);

        await sink.WriteAsync(DiagnosticEvent.Create("large-segment-limit"));
        await sink.DisposeAsync();

        Assert.NotEmpty(output.ToArray());
        Assert.Equal(0, sink.DroppedEventCount);
        Assert.Equal(0, sink.SinkFailureCount);
    }

    [Fact]
    public async Task RotationUsesOnlyBoundedNumericSlotsAndRedactsEveryRetainedSegment()
    {
        var secret = new SecretValue("CANARY-rotation-secret");
        var retainedSegments = new Dictionary<int, MemoryStream>();
        var allSegments = new List<MemoryStream>();
        var requestedSlots = new List<int>();
        var generatedNames = new List<string>();
        var sink = new JsonLinesDiagnosticSink(
            (slot, _) =>
            {
                var stream = new MemoryStream();
                requestedSlots.Add(slot);
                generatedNames.Add($"diagnostics-{slot:D2}.jsonl");
                retainedSegments[slot] = stream;
                allSegments.Add(stream);
                return ValueTask.FromResult<Stream>(stream);
            },
            new TestClock(Epoch),
            [secret],
            channelCapacity: 16,
            maxFieldBytes: 96,
            maxSegmentBytes: 240,
            maxSegmentCount: 2);

        for (var index = 0; index < 8; index++)
        {
            await sink.WriteAsync(
                DiagnosticEvent.Create("rotation")
                    .With("index", DiagnosticField.Integer(index))
                    .With(
                        "filename",
                        DiagnosticField.String(
                            $"diagnostic-{secret.RevealForChildEnvironment()}-{index}.jsonl")));
        }

        await sink.DisposeAsync();

        Assert.True(requestedSlots.Count > 2);
        Assert.Equal(
            Enumerable.Range(0, requestedSlots.Count).Select(index => index % 2),
            requestedSlots);
        Assert.All(requestedSlots, slot => Assert.InRange(slot, 0, 1));
        Assert.Equal(2, retainedSegments.Count);
        Assert.All(retainedSegments.Values, stream => Assert.InRange(stream.ToArray().Length, 1, 240));
        Assert.DoesNotContain(generatedNames, name => name.Contains("CANARY", StringComparison.Ordinal));
        var retainedText = string.Concat(
            retainedSegments.OrderBy(pair => pair.Key)
                .Select(pair => Encoding.UTF8.GetString(pair.Value.ToArray())));
        var allText = string.Concat(allSegments.Select(stream => Encoding.UTF8.GetString(stream.ToArray())));
        Assert.DoesNotContain(secret.RevealForChildEnvironment(), retainedText);
        Assert.DoesNotContain(secret.RevealForChildEnvironment(), allText);
        Assert.Contains("[REDACTED]", allText);
    }

    [Fact]
    public async Task ConcurrentWritersProduceCompleteJsonLinesWithoutSilentLoss()
    {
        const int writerCount = 8;
        const int entriesPerWriter = 50;
        var output = new MemoryStream();
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(output),
            new TestClock(Epoch),
            channelCapacity: writerCount * entriesPerWriter,
            maxFieldBytes: 128,
            maxSegmentBytes: 65_536,
            maxSegmentCount: 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var writers = Enumerable.Range(0, writerCount)
            .Select(writer => Task.Run(async () =>
            {
                for (var entry = 0; entry < entriesPerWriter; entry++)
                {
                    await sink.WriteAsync(
                        DiagnosticEvent.Create("concurrent")
                            .With("id", DiagnosticField.Integer((writer * entriesPerWriter) + entry)),
                        deadline.Token);
                }
            }, deadline.Token))
            .ToArray();
        await Task.WhenAll(writers).WaitAsync(deadline.Token);
        await sink.CompleteAsync(deadline.Token);

        var lines = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var ids = lines.Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("fields").GetProperty("id").GetInt64();
        }).ToArray();
        Assert.Equal(writerCount * entriesPerWriter, lines.Length + sink.DroppedEventCount);
        Assert.Equal(lines.Length, ids.Distinct().Count());
        Assert.Equal(0, sink.SinkFailureCount);

        await sink.DisposeAsync();
        Assert.True(sink.Completion.IsCompletedSuccessfully);
    }

    private static JsonLinesDiagnosticSink CreateSink(
        MemoryStream output,
        IReadOnlyCollection<SecretValue>? secrets = null)
    {
        return new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(output),
            new TestClock(Epoch),
            secrets ?? [],
            channelCapacity: 8,
            maxFieldBytes: 256,
            maxSegmentBytes: 4_096,
            maxSegmentCount: 1);
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteStarted => _writeStarted.Task;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public void ReleaseWrites() => _release.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _writeStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        private readonly Exception _exception;

        public ThrowingWriteStream(Exception? exception = null)
        {
            _exception = exception ?? new IOException("simulated disk full");
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(_exception);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw _exception;
    }
}
