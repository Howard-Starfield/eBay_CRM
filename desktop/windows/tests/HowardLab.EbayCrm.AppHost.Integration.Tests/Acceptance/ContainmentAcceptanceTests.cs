using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using System.Text.Json;
using Xunit.Abstractions;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

[Collection("Destructive containment")]
public sealed class ContainmentAcceptanceTests(ITestOutputHelper output)
{
    private static readonly HashSet<string> ApprovedImages = new(StringComparer.OrdinalIgnoreCase)
    {
        "HowardLab.EbayCrm.AppHost.exe",
        "HowardLab.EbayCrm.AppHost.Fixture.exe",
        "postgres.exe",
        "pg_ctl.exe",
        "initdb.exe",
        "psql.exe",
    };

    [PostgresFact, Trait("Category", "DestructiveContainment")]
    public async Task PublishedHost_ExternalTerminationContainsEveryRetainedDescendantAndColdRestarts()
    {
        var publishInventory = CapturePublishedInventory();
        Assert.Equal(213, publishInventory.Length);
        using var layout = TestLayout.CreatePublished();
        var executable = Path.Combine(TestLayout.FindPublishedDirectory(), "HowardLab.EbayCrm.AppHost.exe");
        var retained = new ConcurrentDictionary<int, ProcessIdentitySnapshot>();
        var observedImages = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        using var host = Start(executable, layout.Arguments("run"));
        using var sampling = new CancellationTokenSource();
        var sampler = Task.CompletedTask;
        try
        {
            Assert.True(ProcessIdentitySnapshot.TryOpen(host.Id, 0, out var hostIdentity));
            retained.TryAdd(host.Id, hostIdentity!);
            sampler = SampleTreeAsync(host.Id, retained, observedImages, sampling.Token);
            await WaitForReadyAsync(host).WaitAsync(TimeSpan.FromMinutes(2));
            await Task.Delay(250);
            AssertExpectedClosure(retained, requireInitialization: true);
            var unexpected = retained.Values
                .Where(identity => !IsApproved(identity, retained))
                .Select(identity => $"{identity.ImageName}:{identity.ProcessId} parent={identity.ParentProcessId}/" +
                    (retained.TryGetValue(identity.ParentProcessId, out var parent) ? parent.ImageName : "not-retained") +
                    $" created={identity.CreationTimeUtc:O}")
                .ToArray();
            Assert.True(unexpected.Length == 0, "Unexpected descendants: " + string.Join("; ", unexpected));

            host.Kill(entireProcessTree: false);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(retained.Values.Select(identity => identity.WaitForExitAsync()))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(retained.Values, identity =>
            {
                Assert.True(identity.HasExited, $"Survivor: {identity.ImageName} {identity.ProcessId}");
                Assert.True(identity.SameIdentityIfReopened());
            });
            WriteIdentityEvidence(retained.Values);
        }
        finally
        {
            sampling.Cancel();
            try
            {
                await sampler;
            }
            finally
            {
                try
                {
                    try
                    {
                        await TerminateRootIfRunningAsync(host);
                    }
                    finally
                    {
                        await TerminateRetainedAsync(retained.Values);
                    }
                }
                finally
                {
                    foreach (var identity in retained.Values) identity.Dispose();
                }
            }
        }

        var restartedRetained = new ConcurrentDictionary<int, ProcessIdentitySnapshot>();
        var restartedImages = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        using var restarted = Start(executable, layout.Arguments("run"));
        using var restartedSampling = new CancellationTokenSource();
        var restartedSampler = Task.CompletedTask;
        try
        {
            Assert.True(ProcessIdentitySnapshot.TryOpen(restarted.Id, 0, out var restartedHostIdentity));
            restartedRetained.TryAdd(restarted.Id, restartedHostIdentity!);
            restartedSampler = SampleTreeAsync(
                restarted.Id,
                restartedRetained,
                restartedImages,
                restartedSampling.Token);
            await WaitForReadyAsync(restarted).WaitAsync(TimeSpan.FromMinutes(2));
            await Task.Delay(250);
            AssertExpectedClosure(restartedRetained, requireInitialization: false);
            restarted.Kill(entireProcessTree: false);
            await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(restartedRetained.Values.Select(identity => identity.WaitForExitAsync()))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(restartedRetained.Values, identity => Assert.True(identity.HasExited));
        }
        finally
        {
            restartedSampling.Cancel();
            try
            {
                await restartedSampler;
            }
            finally
            {
                try
                {
                    try
                    {
                        await TerminateRootIfRunningAsync(restarted);
                    }
                    finally
                    {
                        await TerminateRetainedAsync(restartedRetained.Values);
                    }
                }
                finally
                {
                    foreach (var identity in restartedRetained.Values) identity.Dispose();
                }
            }
        }

        Assert.Equal(publishInventory, CapturePublishedInventory());
    }

    private static Process Start(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Published AppHost did not start.");
    }

    private static bool IsApproved(
        ProcessIdentitySnapshot identity,
        IReadOnlyDictionary<int, ProcessIdentitySnapshot> retained)
    {
        if (ApprovedImages.Contains(identity.ImageName)) return true;
        if (!retained.TryGetValue(identity.ParentProcessId, out var parent)) return false;
        if (identity.ImageName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            return parent.ImageName is "initdb.exe" or "pg_ctl.exe" or "postgres.exe";
        }

        return identity.ImageName.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) &&
            parent.ImageName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) &&
            retained.TryGetValue(parent.ParentProcessId, out var grandparent) &&
            grandparent.ImageName is "initdb.exe" or "pg_ctl.exe" or "postgres.exe";
    }

    private static void AssertExpectedClosure(
        IReadOnlyDictionary<int, ProcessIdentitySnapshot> retained,
        bool requireInitialization)
    {
        var pgCtl = Assert.Single(retained.Values, identity =>
            identity.ImageName.Equals("pg_ctl.exe", StringComparison.OrdinalIgnoreCase));
        if (requireInitialization)
        {
            Assert.Contains(retained.Values, identity =>
                identity.ImageName.Equals("initdb.exe", StringComparison.OrdinalIgnoreCase));
        }

        var livePostgres = retained.Values
            .Where(identity =>
                !identity.HasExited &&
                identity.ImageName.Equals("postgres.exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var postmaster = Assert.Single(livePostgres, candidate =>
            HasAncestor(candidate, pgCtl.ProcessId, retained) &&
            livePostgres.Any(backend => HasAncestor(backend, candidate.ProcessId, retained)));
        Assert.Contains(livePostgres, backend =>
            backend.ProcessId != postmaster.ProcessId &&
            HasAncestor(backend, postmaster.ProcessId, retained));

        var liveFixtures = retained.Values.Where(identity =>
            !identity.HasExited &&
            identity.ImageName.Equals(
                "HowardLab.EbayCrm.AppHost.Fixture.exe",
                StringComparison.OrdinalIgnoreCase) &&
            retained.TryGetValue(identity.ParentProcessId, out var parent) &&
            parent.ImageName.Equals("HowardLab.EbayCrm.AppHost.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, liveFixtures.Count());
    }

    private static bool HasAncestor(
        ProcessIdentitySnapshot identity,
        int ancestorProcessId,
        IReadOnlyDictionary<int, ProcessIdentitySnapshot> retained)
    {
        var parentProcessId = identity.ParentProcessId;
        var visited = new HashSet<int>();
        while (parentProcessId != 0 && visited.Add(parentProcessId))
        {
            if (parentProcessId == ancestorProcessId) return true;
            if (!retained.TryGetValue(parentProcessId, out var parent)) return false;
            parentProcessId = parent.ParentProcessId;
        }

        return false;
    }

    private static Task TerminateRetainedAsync(IEnumerable<ProcessIdentitySnapshot> identities) =>
        Task.WhenAll(identities.Select(identity =>
            identity.TerminateIfRunningAsync(TimeSpan.FromSeconds(10))));

    private static async Task TerminateRootIfRunningAsync(Process root)
    {
        if (root.HasExited) return;
        root.Kill(entireProcessTree: false);
        await root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static PublishedArtifact[] CapturePublishedInventory()
    {
        var publish = TestLayout.FindPublishedDirectory();
        return Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                return new PublishedArtifact(
                    Path.GetRelativePath(publish, path).Replace('\\', '/'),
                    stream.Length,
                    Convert.ToHexString(SHA256.HashData(stream)));
            })
            .ToArray();
    }

    private void WriteIdentityEvidence(IEnumerable<ProcessIdentitySnapshot> identities)
    {
        var evidence = identities
            .OrderBy(identity => identity.CreationTimeUtc)
            .Select(identity => new
            {
                identity.ProcessId,
                identity.ParentProcessId,
                identity.CreationTimeUtc,
                identity.ImageName,
                Signaled = identity.HasExited,
            });
        output.WriteLine(JsonSerializer.Serialize(evidence, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    private static async Task WaitForReadyAsync(Process process)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
            if (StringComparer.Ordinal.Equals(line, RuntimeState.Ready.ToString())) return;
        throw new Xunit.Sdk.XunitException(
            $"AppHost exited before Ready ({process.ExitCode}): {await process.StandardError.ReadToEndAsync()}");
    }

    private static async Task SampleTreeAsync(
        int rootProcessId,
        ConcurrentDictionary<int, ProcessIdentitySnapshot> retained,
        ConcurrentDictionary<string, byte> observedImages,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var item in ProcessIdentitySnapshot.EnumerateTree(rootProcessId))
            {
                if (retained.ContainsKey(item.ProcessId)) continue;
                if (!ProcessIdentitySnapshot.TryOpen(item.ProcessId, item.ParentProcessId, out var identity)) continue;
                if (retained.TryAdd(item.ProcessId, identity!)) observedImages.TryAdd(identity!.ImageName, 0);
                else identity!.Dispose();
            }

            try { await Task.Delay(10, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

}

internal sealed record PublishedArtifact(string RelativePath, long Length, string Sha256);

[CollectionDefinition("Destructive containment", DisableParallelization = true)]
public sealed class DestructiveContainmentCollection;
