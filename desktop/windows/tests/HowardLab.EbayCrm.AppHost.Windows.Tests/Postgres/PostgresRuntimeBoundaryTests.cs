using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;
using HowardLab.EbayCrm.AppHost.Windows.Processes;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Postgres;

public sealed class PostgresRuntimeBoundaryTests
{
    [Fact]
    public async Task NonzeroPgCtlStart_RemainsIndeterminateUntilReconciled()
    {
        await using var fixture = RuntimeFixture.Create(FakeProcess.Exited(1));

        var result = await fixture.Runtime.StartAsync();

        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate, result.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Runtime.StartAsync());
    }

    [Fact]
    public async Task CancellationAfterPgCtlLaunch_RetainsCommandAndStartFenceUntilDisposeWaitsForCleanup()
    {
        var process = FakeProcess.Pending();
        await using var fixture = RuntimeFixture.Create(process);
        using var cancellation = new CancellationTokenSource();

        var start = fixture.Runtime.StartAsync(cancellation.Token);
        await fixture.Launcher.Launched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await start);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Runtime.StartAsync());

        await fixture.Runtime.DisposeAsync();
        Assert.True(process.DisposeCompleted.Task.IsCompletedSuccessfully);
        fixture.RuntimeDisposed = true;
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(string root, WindowsJobObject job, FakeLauncher launcher, PostgresRuntime runtime)
        {
            Root = root;
            Job = job;
            Launcher = launcher;
            Runtime = runtime;
        }

        internal string Root { get; }
        internal WindowsJobObject Job { get; }
        internal FakeLauncher Launcher { get; }
        internal PostgresRuntime Runtime { get; }
        internal bool RuntimeDisposed { get; set; }

        internal static RuntimeFixture Create(FakeProcess process)
        {
            var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-boundary-{Guid.NewGuid():N}");
            var bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            foreach (var name in new[] { "initdb.exe", "pg_ctl.exe", "postgres.exe", "psql.exe", "pg_isready.exe" })
                File.WriteAllBytes(Path.Combine(bin, name), [0]);
            var paths = PostgresClusterPaths.Create(Path.Combine(root, "profile"));
            Directory.CreateDirectory(paths.RuntimeDirectory);
            Directory.CreateDirectory(paths.DataDirectory);
            Directory.CreateDirectory(Path.Combine(paths.DataDirectory, "global"));
            Directory.CreateDirectory(Path.Combine(paths.DataDirectory, "base"));
            File.WriteAllText(Path.Combine(paths.DataDirectory, "PG_VERSION"), "16");
            File.WriteAllBytes(Path.Combine(paths.DataDirectory, "global", "pg_control"), [0]);
            File.WriteAllText(Path.Combine(paths.DataDirectory, "postgresql.conf"), "# fixture");
            File.WriteAllText(Path.Combine(paths.DataDirectory, "pg_hba.conf"), "# fixture");
            var job = WindowsJobObject.CreateKillOnClose();
            var launcher = new FakeLauncher(process);
            var runtime = new PostgresRuntime(
                PostgresBinaryLayout.Validate(bin),
                paths,
                new ProcessGeneration(RuntimeRole.Database, 1, Guid.NewGuid()),
                54321,
                new SecretValue("boundary-secret-Aa1!"),
                launcher,
                job,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20));
            return new RuntimeFixture(root, job, launcher, runtime);
        }

        public async ValueTask DisposeAsync()
        {
            if (!RuntimeDisposed) await Runtime.DisposeAsync();
            Job.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeLauncher(FakeProcess process) : IProcessLauncher
    {
        internal TaskCompletionSource Launched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ISupervisedProcess> LaunchAsync(
            LaunchSpecification specification,
            IProcessGroup processGroup,
            CancellationToken cancellationToken)
        {
            Launched.TrySetResult();
            return ValueTask.FromResult<ISupervisedProcess>(process);
        }
    }

    private sealed class FakeProcess : ISupervisedProcess
    {
        private readonly TaskCompletionSource<int>? _pending;

        private FakeProcess(Task<int> completion, TaskCompletionSource<int>? pending)
        {
            Completion = completion;
            _pending = pending;
            StandardOutput = new BoundedTextCollector(4096, 1024);
            StandardError = new BoundedTextCollector(4096, 1024);
            StandardOutput.Complete();
            StandardError.Complete();
            Identity = new SupervisedProcessIdentity(
                RuntimeRole.Database,
                new ProcessGeneration(RuntimeRole.Database, 1, Guid.NewGuid()),
                Environment.ProcessId,
                DateTimeOffset.UtcNow,
                Environment.ProcessPath!);
        }

        internal static FakeProcess Exited(int exitCode) => new(Task.FromResult(exitCode), null);

        internal static FakeProcess Pending()
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            return new FakeProcess(completion.Task, completion);
        }

        public SupervisedProcessIdentity Identity { get; }
        public Task<int> Completion { get; }
        public BoundedTextCollector StandardOutput { get; }
        public BoundedTextCollector StandardError { get; }
        internal TaskCompletionSource DisposeCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync()
        {
            _pending?.TrySetResult(1);
            DisposeCompleted.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
