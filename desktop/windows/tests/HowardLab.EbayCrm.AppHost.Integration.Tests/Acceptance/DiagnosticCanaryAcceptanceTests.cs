using System.IO.Compression;
using System.Text;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

[Collection("Diagnostic acceptance")]
public sealed class DiagnosticCanaryAcceptanceTests
{
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

    private sealed class NoopDiagnosticSink : IDiagnosticSink
    {
        internal static NoopDiagnosticSink Instance { get; } = new();
        public ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

[CollectionDefinition("Diagnostic acceptance", DisableParallelization = true)]
public sealed class DiagnosticAcceptanceCollection;
