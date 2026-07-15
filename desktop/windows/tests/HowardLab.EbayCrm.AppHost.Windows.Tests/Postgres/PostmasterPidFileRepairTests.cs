using HowardLab.EbayCrm.AppHost.Windows.Postgres;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Postgres;

public sealed class PostmasterPidFileRepairTests
{
    [Fact]
    public void AccessDeniedOrOtherwiseAmbiguousProcess_PreservesPidFile()
    {
        using var fixture = RepairFixture.Create();

        var repaired = fixture.Repair(new SequenceProbe(PostmasterProcessState.PresentOrAmbiguous));

        Assert.False(repaired);
        Assert.True(File.Exists(fixture.PidPath));
    }

    [Fact]
    public void PidReuseBetweenProofs_PreservesPidFile()
    {
        using var fixture = RepairFixture.Create();

        var repaired = fixture.Repair(new SequenceProbe(
            PostmasterProcessState.Missing,
            PostmasterProcessState.PresentOrAmbiguous));

        Assert.False(repaired);
        Assert.True(File.Exists(fixture.PidPath));
    }

    [Fact]
    public void ReplacementRace_PreservesReplacementPidFile()
    {
        using var fixture = RepairFixture.Create();
        var probe = new ReplacingProbe(fixture);

        var repaired = fixture.Repair(probe);

        Assert.False(repaired);
        Assert.True(probe.ReplacementWasBlocked);
        Assert.True(File.Exists(fixture.PidPath));
        Assert.Contains("2147483647", File.ReadAllText(fixture.PidPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementAfterSecondProof_IsNeverDeletedThroughThePath()
    {
        using var fixture = RepairFixture.Create();
        var probe = new ReplacingAfterSecondProofProbe(fixture);

        var repaired = fixture.Repair(probe);

        Assert.False(repaired);
        Assert.True(File.Exists(fixture.PidPath));
        Assert.True(probe.ReplacementWasBlocked);
        Assert.Contains("2147483647", File.ReadAllText(fixture.PidPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoConclusiveMissingProofs_DeleteExactPidFile()
    {
        using var fixture = RepairFixture.Create();

        var repaired = fixture.Repair(new SequenceProbe(
            PostmasterProcessState.Missing,
            PostmasterProcessState.Missing));

        Assert.True(repaired);
        Assert.False(File.Exists(fixture.PidPath));
    }

    private sealed class SequenceProbe(params PostmasterProcessState[] states) : IPostmasterProcessProbe
    {
        private int _index;

        public PostmasterProcessState Probe(int processId) =>
            states[Math.Min(Interlocked.Increment(ref _index) - 1, states.Length - 1)];
    }

    private sealed class ReplacingProbe(RepairFixture fixture) : IPostmasterProcessProbe
    {
        internal bool ReplacementWasBlocked { get; private set; }

        public PostmasterProcessState Probe(int processId)
        {
            try
            {
                fixture.Write(processId: 2_147_483_646);
                return PostmasterProcessState.Missing;
            }
            catch (IOException)
            {
                ReplacementWasBlocked = true;
                return PostmasterProcessState.PresentOrAmbiguous;
            }
        }
    }

    private sealed class ReplacingAfterSecondProofProbe(RepairFixture fixture) : IPostmasterProcessProbe
    {
        private int _proof;

        internal bool ReplacementWasBlocked { get; private set; }

        public PostmasterProcessState Probe(int processId)
        {
            if (Interlocked.Increment(ref _proof) == 2)
            {
                try
                {
                    fixture.Write(processId: 2_147_483_646);
                }
                catch (IOException)
                {
                    ReplacementWasBlocked = true;
                    return PostmasterProcessState.PresentOrAmbiguous;
                }
            }

            return PostmasterProcessState.Missing;
        }
    }

    private sealed class RepairFixture : IDisposable
    {
        private RepairFixture(string root)
        {
            Root = root;
            DataDirectory = Path.Combine(root, "data");
            PidPath = Path.Combine(DataDirectory, "postmaster.pid");
            Directory.CreateDirectory(DataDirectory);
            Write(2_147_483_647);
        }

        internal string Root { get; }
        internal string DataDirectory { get; }
        internal string PidPath { get; }
        internal int Port => 15432;

        internal static RepairFixture Create() => new(Path.Combine(
            Path.GetTempPath(),
            $"ebaycrm-pid-repair-{Guid.NewGuid():N}"));

        internal bool Repair(IPostmasterProcessProbe probe) => PostmasterPidFileRepair.TryRepair(
            PidPath,
            DataDirectory,
            Port,
            probe,
            CancellationToken.None);

        internal void Write(int processId) => File.WriteAllText(
            PidPath,
            $"{processId}\n{DataDirectory}\n{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n{Port}\n\n127.0.0.1\n0 0\nready\n");

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
