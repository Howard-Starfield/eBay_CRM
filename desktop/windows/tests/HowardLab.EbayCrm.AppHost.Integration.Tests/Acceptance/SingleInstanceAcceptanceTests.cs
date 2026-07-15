using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Windows.Instance;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

[Collection("Destructive containment")]
public sealed class SingleInstanceAcceptanceTests
{
    [PostgresFact, Trait("Category", "Acceptance")]
    public async Task OwnedProfile_WinsBeforeSharedPortValidation()
    {
        using var layout = TestLayout.CreateReal();
        var profile = DataProfileIdentity.Create(layout.ProfileRoot);
        await using var owner = Assert.IsType<UserProfileInstanceLock>(
            await UserProfileInstanceLock.TryAcquireAsync(profile, CancellationToken.None));
        using var listener = new TcpListener(IPAddress.Loopback, layout.Port);
        listener.Start();
        var orchestrator = AppHostComposition.Create(
            AppHostOptions.Parse(layout.Arguments("run")));

        var error = await Assert.ThrowsAsync<AppHostExecutionException>(() => orchestrator.StartAsync());

        Assert.Equal("profile-already-owned", error.ReasonCode);
        await orchestrator.DisposeAsync();
    }

    [PostgresFact, Trait("Category", "DestructiveContainment")]
    public async Task TwentyPublishedContenders_ProduceOneOwnerAndStableLosers()
    {
        using var layout = TestLayout.CreatePublished("ebaycrm-task10-contenders");
        var executable = Path.Combine(TestLayout.FindPublishedDirectory(), "HowardLab.EbayCrm.AppHost.exe");
        var contenders = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in layout.Arguments("run")) info.ArgumentList.Add(argument);
            return Process.Start(info) ?? throw new InvalidOperationException("Contender did not start.");
        })));

        try
        {
            var observations = contenders.Select(ObserveAsync).ToList();
            ContenderObservation? owner = null;
            var earlyLosers = new List<ContenderObservation>();
            while (observations.Count > 0 && owner is null)
            {
                var completed = await Task.WhenAny(observations).WaitAsync(TimeSpan.FromMinutes(2));
                observations.Remove(completed);
                var observation = await completed;
                if (observation.Ready) owner = observation;
                else earlyLosers.Add(observation);
            }

            Assert.NotNull(owner);
            var losers = earlyLosers
                .Concat(await Task.WhenAll(observations).WaitAsync(TimeSpan.FromSeconds(30)))
                .ToArray();
            Assert.Equal(19, losers.Length);
            Assert.All(losers, loser =>
            {
                Assert.False(loser.Ready);
                Assert.Equal(2, loser.ExitCode);
                Assert.Equal("profile-already-owned", loser.StandardError.Trim());
            });

            owner.Process.Kill(entireProcessTree: false);
            await owner.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var contender in contenders)
            {
                if (!contender.HasExited) contender.Kill(entireProcessTree: false);
                await contender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                contender.Dispose();
            }
        }
    }

    private static async Task<ContenderObservation> ObserveAsync(Process process)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (StringComparer.Ordinal.Equals(line, RuntimeState.Ready.ToString()))
                return new ContenderObservation(process, true, null, string.Empty);
        }

        await process.WaitForExitAsync();
        return new ContenderObservation(
            process,
            false,
            process.ExitCode,
            await process.StandardError.ReadToEndAsync());
    }

    private sealed record ContenderObservation(
        Process Process,
        bool Ready,
        int? ExitCode,
        string StandardError);
}
