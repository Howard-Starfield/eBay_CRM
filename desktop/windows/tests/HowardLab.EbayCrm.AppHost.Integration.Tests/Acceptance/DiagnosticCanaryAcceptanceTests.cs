using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Core.Time;
using HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

[Collection("Diagnostic acceptance")]
public sealed class DiagnosticCanaryAcceptanceTests
{
    [Fact, Trait("Category", "Acceptance")]
    public async Task OwnershipGateDoesNotTouchSegmentsUntilActivated()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "logs", "apphost-0.jsonl");
            var sink = new JsonLinesDiagnosticSink(
                (_, _) =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    return ValueTask.FromResult<Stream>(new FileStream(
                        path,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read));
                },
                new SystemClock());
            var gated = new OwnershipGatedDiagnosticSink(sink, TimeSpan.FromSeconds(1));

            await gated.WriteAsync(DiagnosticEvent.Create("before-ownership"));
            Assert.False(Directory.Exists(Path.Combine(root, "logs")));

            gated.Activate();
            await gated.WriteAsync(DiagnosticEvent.Create("after-ownership"));
            await gated.DisposeAsync();

            Assert.Contains("after-ownership", await File.ReadAllTextAsync(path));
            Assert.DoesNotContain("before-ownership", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task OwnershipGateCompletionBudgetAbandonsBlockingStreamWithinOnePointFiveSeconds()
    {
        var stream = new NeverCompletingStream();
        var sink = new JsonLinesDiagnosticSink(
            (_, _) => ValueTask.FromResult<Stream>(stream),
            new SystemClock());
        var gated = new OwnershipGatedDiagnosticSink(sink, TimeSpan.FromSeconds(1));
        gated.Activate();
        await gated.WriteAsync(DiagnosticEvent.Create("blocking"));
        await stream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        await gated.DisposeAsync();
        stopwatch.Stop();

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(1_500));
        Assert.Equal(1, gated.CompletionTimeoutCount);
    }

    [PostgresFact, Trait("Category", "Acceptance")]
    public async Task ProductionCompositionRedactsGeneratedSecretsBoundsSegmentsAndLoserTouchesNothing()
    {
        using var layout = TestLayout.CreateReal("ebaycrm-task3-diagnostics");
        var observedSecrets = new List<SecretValue>();
        var canary = new SecretValue("CANARY-production-composition-static");
        var runtime = AppHostComposition.CreateForTests(
            AppHostOptions.Parse(layout.Arguments("run")),
            diagnosticSecrets: [canary],
            diagnosticSecretObserver: secret => observedSecrets.Add(secret));
        try
        {
            await runtime.Orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.True(observedSecrets.Count >= 5, $"Observed only {observedSecrets.Count} generated secrets.");

            var allCanaries = observedSecrets
                .Append(canary)
                .Select(secret => secret.RevealForChildEnvironment())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            await runtime.Executor.WriteDiagnosticForTestsAsync(
                DiagnosticEvent.Create("production.secrets")
                    .With("value", DiagnosticField.String(string.Join('|', allCanaries))));
            var logs = Path.Combine(layout.ProfileRoot, "logs");
            await WaitUntilAsync(
                () => Directory.Exists(logs) && Directory.EnumerateFiles(logs).Any(),
                TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            var beforeLoser = Directory.EnumerateFiles(logs)
                .ToDictionary(path => Path.GetFileName(path)!, ReadSharedBytes, StringComparer.Ordinal);
            var loserFactoryCalls = 0;
            var loser = AppHostComposition.CreateForTests(
                AppHostOptions.Parse(layout.Arguments("run")),
                diagnosticSegmentFactory: (_, _) =>
                {
                    Interlocked.Increment(ref loserFactoryCalls);
                    throw new IOException("loser must not open diagnostics");
                });
            try
            {
                var error = await Assert.ThrowsAsync<AppHostExecutionException>(() => loser.Orchestrator.StartAsync());
                Assert.Equal("profile-already-owned", error.ReasonCode);
            }
            finally
            {
                await loser.Orchestrator.DisposeAsync();
            }

            Assert.Equal(0, loserFactoryCalls);
            var afterLoser = Directory.EnumerateFiles(logs)
                .ToDictionary(path => Path.GetFileName(path)!, ReadSharedBytes, StringComparer.Ordinal);
            Assert.Equal(beforeLoser.Keys.Order(), afterLoser.Keys.Order());
            Assert.All(beforeLoser, pair => Assert.Equal(pair.Value, afterLoser[pair.Key]));

            var payload = new string('x', 3_900);
            var secretProbe = string.Join('|', allCanaries);
            for (var index = 0; index < 1_600; index++)
            {
                await runtime.Executor.WriteDiagnosticForTestsAsync(
                    DiagnosticEvent.Create("production.rotation")
                        .With("index", DiagnosticField.Integer(index))
                        .With("secrets", DiagnosticField.String(secretProbe))
                        .With("payload", DiagnosticField.String(payload)));
                if (index % 25 == 0)
                {
                    await Task.Delay(5);
                }
            }

            await runtime.Orchestrator.StopAsync().WaitAsync(TimeSpan.FromMinutes(1));

            var segments = Directory.EnumerateFiles(logs, "apphost-*.jsonl").ToArray();
            Assert.InRange(segments.Length, 2, 4);
            Assert.All(segments, path => Assert.InRange(new FileInfo(path).Length, 1, 1_048_576));
            Assert.InRange(segments.Sum(path => new FileInfo(path).Length), 1_048_577, 4L * 1_048_576);
            Assert.All(segments, path => Assert.Contains(
                Path.GetFileName(path),
                new[] { "apphost-0.jsonl", "apphost-1.jsonl", "apphost-2.jsonl", "apphost-3.jsonl" }));
            var retainedText = string.Concat(segments.Select(File.ReadAllText));
            Assert.Contains("production.rotation", retainedText, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", retainedText, StringComparison.Ordinal);
            Assert.All(allCanaries, value => Assert.DoesNotContain(value, retainedText, StringComparison.Ordinal));

            var scanRoot = CreateRoot();
            try
            {
                foreach (var segment in segments)
                {
                    File.Copy(segment, Path.Combine(scanRoot, Path.GetFileName(segment)));
                }

                await File.WriteAllTextAsync(Path.Combine(scanRoot, "manifest.json"), "{\"status\":\"safe\"}");
                using (var archive = ZipFile.Open(Path.Combine(scanRoot, "diagnostics.zip"), ZipArchiveMode.Create))
                {
                    archive.CreateEntryFromFile(Path.Combine(scanRoot, "manifest.json"), "manifest.json");
                }

                var scan = await AcceptanceArtifactScanner.ScanAsync(
                    scanRoot,
                    allCanaries,
                    1_048_576,
                    10);
                Assert.Empty(scan.Findings);
            }
            finally
            {
                Directory.Delete(scanRoot, recursive: true);
            }
        }
        finally
        {
            await runtime.Orchestrator.DisposeAsync();
        }
    }

    [PostgresFact, Trait("Category", "Acceptance")]
    public async Task ThrowingProductionSegmentFactoryDoesNotBlockShutdownOrProfileRelease()
    {
        using var layout = TestLayout.CreateReal("ebaycrm-task3-diagnostic-failure");
        var runtime = AppHostComposition.CreateForTests(
            AppHostOptions.Parse(layout.Arguments("run")),
            diagnosticSegmentFactory: (_, _) => throw new IOException("simulated unwritable diagnostics"));
        try
        {
            await runtime.Orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));
            await runtime.Executor.WriteDiagnosticForTestsAsync(DiagnosticEvent.Create("will-fail"));
            await WaitUntilAsync(() => runtime.Executor.DiagnosticSinkFailureCountForTests > 0, TimeSpan.FromSeconds(5));

            await runtime.Orchestrator.StopAsync().WaitAsync(TimeSpan.FromMinutes(1));

            Assert.True(runtime.Executor.DiagnosticSinkFailureCountForTests > 0);
            await using var reacquired = await UserProfileInstanceLock.TryAcquireAsync(
                DataProfileIdentity.Create(layout.ProfileRoot),
                CancellationToken.None);
            Assert.NotNull(reacquired);
        }
        finally
        {
            await runtime.Orchestrator.DisposeAsync();
        }
    }

    [PostgresFact, Trait("Category", "Acceptance")]
    public async Task BlockingProductionStreamShutdownAndProfileReacquisitionCompleteWithinOnePointFiveSeconds()
    {
        using var layout = TestLayout.CreateReal("ebaycrm-task3-blocking-diagnostics");
        var stream = new NeverCompletingStream();
        var runtime = AppHostComposition.CreateForTests(
            AppHostOptions.Parse(layout.Arguments("run")),
            diagnosticSegmentFactory: (_, _) => ValueTask.FromResult<Stream>(stream),
            diagnosticCompletionBudget: TimeSpan.FromSeconds(1));
        try
        {
            var operationId = Guid.NewGuid();
            _ = await runtime.Executor.ExecuteAsync(new LifecycleCommand(
                LifecycleCommandType.AcquireInstance,
                Generation: null,
                operationId,
                LifecycleDeadlineKey.InstanceAcquisition));
            await runtime.Executor.WriteDiagnosticForTestsAsync(DiagnosticEvent.Create("blocking-production"));
            await stream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(1));

            var stopwatch = Stopwatch.StartNew();
            await runtime.Executor.DisposeAsync();
            await using var reacquired = await UserProfileInstanceLock.TryAcquireAsync(
                DataProfileIdentity.Create(layout.ProfileRoot),
                CancellationToken.None);
            stopwatch.Stop();

            Assert.NotNull(reacquired);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(1_500));
            Assert.Equal(1, runtime.Executor.DiagnosticCompletionTimeoutCountForTests);
        }
        finally
        {
            await runtime.Orchestrator.DisposeAsync();
        }
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task FixtureFlood_StaysWithinRetainedByteMemoryLineAndFileBounds()
    {
        const int maxBytes = 64 * 1024;
        const int maxLineBytes = 4 * 1024;
        var root = CreateRoot();
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var beforeMemory = GC.GetTotalMemory(forceFullCollection: true);
            using var job = WindowsJobObject.CreateKillOnClose();
            var fixture = Path.Combine(TestLayout.FindPublishedDirectory(), "HowardLab.EbayCrm.AppHost.Fixture.exe");
            var specification = new LaunchSpecification(
                RuntimeRole.Server,
                new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid()),
                fixture,
                ["flood-output"],
                root,
                new Dictionary<string, string>(),
                new Dictionary<string, SecretValue>(),
                TimeSpan.FromSeconds(2),
                Enumerable.Repeat((byte)'a', 256 * 1024).ToArray());
            await using var process = await new WindowsProcessLauncher(
                NoopDiagnosticSink.Instance,
                maxOutputBytes: maxBytes,
                maxLineBytes: maxLineBytes).LaunchAsync(specification, job, CancellationToken.None);

            Assert.Equal(0, await process.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            var stdout = process.StandardOutput.Snapshot();
            var stderr = process.StandardError.Snapshot();
            var afterMemory = GC.GetTotalMemory(forceFullCollection: true);

            Assert.InRange(Encoding.UTF8.GetByteCount(stdout), 1, maxBytes);
            Assert.InRange(Encoding.UTF8.GetByteCount(stderr), 0, maxBytes);
            Assert.All(stdout.Split('\n'), line =>
                Assert.InRange(Encoding.UTF8.GetByteCount(line), 0, maxLineBytes));
            Assert.All(stderr.Split('\n'), line =>
                Assert.InRange(Encoding.UTF8.GetByteCount(line), 0, maxLineBytes));
            Assert.InRange(afterMemory - beforeMemory, long.MinValue, 32L * 1024 * 1024);
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact, Trait("Category", "Acceptance")]
    public void PublishedFolder_IsSelfContainedAndCarriesTheAttestedRuntimeClosure()
    {
        var publish = TestLayout.FindPublishedDirectory();
        var required = new[]
        {
            "HowardLab.EbayCrm.AppHost.exe",
            "HowardLab.EbayCrm.AppHost.dll",
            "HowardLab.EbayCrm.AppHost.Fixture.exe",
            "HowardLab.EbayCrm.AppHost.Fixture.dll",
            "HowardLab.EbayCrm.AppHost.Protocol.dll",
            "System.Security.Cryptography.ProtectedData.dll",
            "hostfxr.dll",
            "coreclr.dll",
            "System.Private.CoreLib.dll",
            Path.Combine("migrations", "0001_apphost_control.sql"),
        };

        Assert.All(required, relative => Assert.True(File.Exists(Path.Combine(publish, relative)), relative));
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task FixturePlainCanary_IsRedactedBeforeSubsequentArtifactScan()
    {
        const string canary = "task10-fixture-plain-secret";
        var root = CreateRoot();
        try
        {
            using var job = WindowsJobObject.CreateKillOnClose();
            var fixture = Path.Combine(TestLayout.FindPublishedDirectory(), "HowardLab.EbayCrm.AppHost.Fixture.exe");
            var specification = new LaunchSpecification(
                RuntimeRole.Server,
                new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid()),
                fixture,
                ["output-secret"],
                Path.GetDirectoryName(fixture)!,
                new Dictionary<string, string>(),
                new Dictionary<string, SecretValue> { ["TASK5_SECRET"] = new(canary) },
                TimeSpan.FromSeconds(2));
            await using var process = await new WindowsProcessLauncher(
                NoopDiagnosticSink.Instance,
                maxOutputBytes: 64 * 1024,
                maxLineBytes: 4 * 1024).LaunchAsync(specification, job, CancellationToken.None);
            Assert.Equal(0, await process.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await File.WriteAllTextAsync(
                Path.Combine(root, "captured-child-output.log"),
                process.StandardOutput.Snapshot() + process.StandardError.Snapshot());

            var result = await AcceptanceArtifactScanner.ScanAsync(root, [canary], 64 * 1024, 10);

            Assert.Empty(result.Findings);
            Assert.Contains("[REDACTED]", await File.ReadAllTextAsync(Path.Combine(root, "captured-child-output.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact, Trait("Category", "Acceptance")]
    public async Task Scanner_FindsCanarySplitAcrossReadBoundaries()
    {
        var root = CreateRoot();
        try
        {
            const string canary = "task10-plain-secret-value";
            await File.WriteAllTextAsync(
                Path.Combine(root, "lifecycle.jsonl"),
                new string('x', 4093) + canary + "-tail",
                Encoding.UTF8);

            var result = await AcceptanceArtifactScanner.ScanAsync(root, [canary], 1_000_000, 100);

            Assert.Contains(result.Findings, finding => finding.Kind == "canary-content");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task Scanner_ExaminesRelativeNamesZipEntriesAndManifestContent()
    {
        var root = CreateRoot();
        try
        {
            const string nameCanary = "task10-name-secret";
            const string contentCanary = "task10-manifest-secret";
            var archive = Path.Combine(root, "diagnostics.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry($"nested/{nameCanary}.json");
                await using (var stream = entry.Open())
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("safe"));
                }

                var manifest = zip.CreateEntry("manifest.json");
                await using (var manifestStream = manifest.Open())
                {
                    await manifestStream.WriteAsync(Encoding.UTF8.GetBytes(
                        new string('x', 5_000) + contentCanary));
                }
            }

            var result = await AcceptanceArtifactScanner.ScanAsync(
                root,
                [nameCanary, contentCanary],
                1_000_000,
                2);

            Assert.Contains(result.Findings, finding => finding.Kind == "canary-name");
            Assert.Contains(result.Findings, finding => finding.Kind == "canary-content");
            Assert.Contains(result.Findings, finding => finding.Kind == "file-count-bound");
            Assert.Equal(3, result.ScannedArtifacts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task Scanner_RejectsFileCountAndFileSizeBounds()
    {
        var root = CreateRoot();
        try
        {
            const string canary = "late-canary-after-bound";
            await File.WriteAllTextAsync(
                Path.Combine(root, "one.log"),
                new string('x', 5_000) + canary);
            await File.WriteAllTextAsync(Path.Combine(root, "two.log"), "safe");

            var result = await AcceptanceArtifactScanner.ScanAsync(root, [canary], 32, 1);

            Assert.Contains(result.Findings, finding => finding.Kind == "file-size-bound");
            Assert.Contains(result.Findings, finding => finding.Kind == "file-count-bound");
            Assert.Contains(result.Findings, finding => finding.Kind == "canary-content");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-task10-scanner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed >= timeout)
            {
                throw new TimeoutException("The diagnostic acceptance condition was not reached.");
            }

            await Task.Delay(20);
        }
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private sealed class NoopDiagnosticSink : IDiagnosticSink
    {
        internal static NoopDiagnosticSink Instance { get; } = new();
        public ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverCompletingStream : Stream
    {
        private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WriteStarted => _writeStarted.Task;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _writeStarted.TrySetResult();
            return new ValueTask(_never.Task);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => _never.Task;
        public override ValueTask DisposeAsync() => new(_never.Task);
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

[CollectionDefinition("Diagnostic acceptance", DisableParallelization = true)]
public sealed class DiagnosticAcceptanceCollection;
