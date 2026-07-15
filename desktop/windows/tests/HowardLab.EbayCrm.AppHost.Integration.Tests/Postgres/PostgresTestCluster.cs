using System.Net;
using System.Net.Sockets;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;
using HowardLab.EbayCrm.AppHost.Windows.Processes;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;

internal sealed class PostgresTestCluster : IAsyncDisposable
{
    private PostgresTestCluster(
        string root,
        PostgresBinaryLayout layout,
        PostgresClusterPaths paths,
        int port,
        SecretValue password,
        WindowsJobObject job,
        PostgresRuntime runtime)
    {
        Root = root;
        Layout = layout;
        Paths = paths;
        Port = port;
        Password = password;
        Job = job;
        Runtime = runtime;
    }

    internal string Root { get; }
    internal PostgresBinaryLayout Layout { get; }
    internal PostgresClusterPaths Paths { get; }
    internal int Port { get; }
    internal SecretValue Password { get; }
    internal WindowsJobObject Job { get; private set; }
    internal PostgresRuntime Runtime { get; private set; }
    internal PostgresInstanceIdentity? Identity { get; set; }
    private DelayedStartLauncher? DelayedStart { get; set; }
    private bool RuntimeDisposed { get; set; }

    internal static async Task<PostgresTestCluster> CreateAsync(
        int? port = null,
        TimeSpan? startDeadline = null,
        TimeSpan? stopDeadline = null,
        TimeSpan? reconciliationDeadline = null) =>
        await CreateCoreAsync(initialize: true, port, startDeadline, stopDeadline, reconciliationDeadline);

    internal static async Task<PostgresTestCluster> CreateUninitializedAsync() =>
        await CreateCoreAsync(initialize: false, null, null, null, null);

    private static async Task<PostgresTestCluster> CreateCoreAsync(
        bool initialize,
        int? requestedPort,
        TimeSpan? startDeadline,
        TimeSpan? stopDeadline,
        TimeSpan? reconciliationDeadline)
    {
        var bin = Environment.GetEnvironmentVariable("EBAYCRM_POSTGRES_BIN")
            ?? throw new InvalidOperationException("EBAYCRM_POSTGRES_BIN is required.");
        var layout = PostgresBinaryLayout.Validate(Path.GetFullPath(bin));
        var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-pg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = PostgresClusterPaths.Create(root);
        var port = requestedPort ?? AllocateLoopbackPort();
        var job = WindowsJobObject.CreateKillOnClose();
        var launcher = new WindowsProcessLauncher(NoopDiagnosticSink.Instance, maxOutputBytes: 256 * 1024);
        var generation = new ProcessGeneration(RuntimeRole.Database, 1, Guid.NewGuid());
        var password = new SecretValue($"task7-{Guid.NewGuid():N}-Aa1!");
        var runtime = new PostgresRuntime(
            layout,
            paths,
            generation,
            port,
            password,
            launcher,
            job,
            startDeadline ?? TimeSpan.FromSeconds(60),
            stopDeadline ?? TimeSpan.FromSeconds(30),
            reconciliationDeadline ?? TimeSpan.FromSeconds(30));
        try
        {
            var cluster = new PostgresTestCluster(root, layout, paths, port, password, job, runtime);
            if (initialize)
            {
                Assert.Equal(PostgreSqlOperationOutcome.Completed, await runtime.InitializeAsync());
            }

            return cluster;
        }
        catch (PostgresCommandException error)
        {
            await runtime.DisposeAsync();
            job.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw new InvalidOperationException(
                $"{error.Message}\nstdout:\n{error.StandardOutput}\nstderr:\n{error.StandardError}", error);
        }
        catch
        {
            await runtime.DisposeAsync();
            job.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Identity is { } identity && !identity.HasExited)
            {
                _ = await Runtime.StopFastAsync(identity);
                _ = await Runtime.ReconcileStopAsync(identity);
            }
        }
        finally
        {
            if (!RuntimeDisposed) await Runtime.DisposeAsync();
            if (Identity is { HasExited: false } liveIdentity)
            {
                Job.Dispose();
                await liveIdentity.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            else
            {
                Job.Dispose();
            }
            Identity?.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    internal async Task<PostgreSqlOperationResult<PostgresInstanceIdentity>> CrashJobAndRestartAsync()
    {
        var previousIdentity = Identity ?? throw new InvalidOperationException("The cluster is not running.");
        Job.Dispose();
        await previousIdentity.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped,
            await Runtime.ReconcileStopAsync(previousIdentity));
        await Runtime.DisposeAsync();
        Identity = null;

        Job = WindowsJobObject.CreateKillOnClose();
        var launcher = new WindowsProcessLauncher(NoopDiagnosticSink.Instance, maxOutputBytes: 256 * 1024);
        var generation = new ProcessGeneration(RuntimeRole.Database, 2, Guid.NewGuid());
        Runtime = new PostgresRuntime(
            Layout,
            Paths,
            generation,
            Port,
            Password,
            launcher,
            Job,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
        Assert.Equal(PostgreSqlOperationOutcome.Completed, await Runtime.InitializeAsync());
        return await Runtime.StartAsync();
    }

    internal async Task ReplaceRuntimePasswordAsync(SecretValue password, TimeSpan? reconciliationDeadline = null)
    {
        if (Identity is not null) throw new InvalidOperationException("The cluster is already running.");
        await Runtime.DisposeAsync();
        var launcher = new WindowsProcessLauncher(NoopDiagnosticSink.Instance, maxOutputBytes: 256 * 1024);
        Runtime = new PostgresRuntime(
            Layout,
            Paths,
            new ProcessGeneration(RuntimeRole.Database, 1, Guid.NewGuid()),
            Port,
            password,
            launcher,
            Job,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(30),
            reconciliationDeadline ?? TimeSpan.FromSeconds(2));
    }

    internal async Task<PostgreSqlOperationResult<PostgresInstanceIdentity>> CrashJobAndBeginIndeterminateRestartAsync()
    {
        var previousIdentity = Identity ?? throw new InvalidOperationException("The cluster is not running.");
        Job.Dispose();
        await previousIdentity.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped,
            await Runtime.ReconcileStopAsync(previousIdentity));
        await Runtime.DisposeAsync();
        Identity = null;

        Job = WindowsJobObject.CreateKillOnClose();
        var launcher = new DelayedStartLauncher(
            new WindowsProcessLauncher(NoopDiagnosticSink.Instance, maxOutputBytes: 256 * 1024));
        DelayedStart = launcher;
        Runtime = new PostgresRuntime(
            Layout,
            Paths,
            new ProcessGeneration(RuntimeRole.Database, 2, Guid.NewGuid()),
            Port,
            new SecretValue($"injected-recovery-gate-{Guid.NewGuid():N}-Aa1!"),
            launcher,
            Job,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2));
        Assert.Equal(PostgreSqlOperationOutcome.Completed, await Runtime.InitializeAsync());
        var start = await Runtime.StartAsync();
        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate, start.Outcome);
        await WaitForFileAsync(Paths.PostmasterPidFile, TimeSpan.FromSeconds(30));
        return await Runtime.ReconcileStartAsync();
    }

    internal void ReleaseDelayedStartCommand()
    {
        var delayed = DelayedStart ?? throw new InvalidOperationException("No delayed start command exists.");
        Assert.False(delayed.Released.Task.IsCompleted);
        delayed.Released.TrySetResult();
    }

    internal async Task ExecuteSqlAsync(string sql)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? throw new InvalidOperationException("SystemRoot is unavailable.");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = string.Join(Path.PathSeparator, Layout.CanonicalBinDirectory, Path.Combine(systemRoot, "System32")),
            ["SystemRoot"] = systemRoot,
        };
        foreach (var name in new[] { "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        var secrets = new Dictionary<string, SecretValue>(StringComparer.Ordinal) { ["PGPASSWORD"] = Password };
        var specification = new LaunchSpecification(
            RuntimeRole.Database,
            new ProcessGeneration(RuntimeRole.Database, 99, Guid.NewGuid()),
            Layout.PsqlExe,
            ["-X", "-A", "-t", "-q", "-w", "-h", "127.0.0.1", "-p", Port.ToString(),
             "-U", "ebaycrm", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", sql],
            Paths.RuntimeDirectory,
            environment,
            secrets,
            TimeSpan.FromSeconds(2));
        using var helperJob = WindowsJobObject.CreateKillOnClose();
        var launcher = new WindowsProcessLauncher(NoopDiagnosticSink.Instance, maxOutputBytes: 256 * 1024);
        await using var command = await launcher.LaunchAsync(specification, helperJob, CancellationToken.None);
        var exitCode = await command.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        if (exitCode != 0)
            throw new InvalidOperationException($"psql failed ({exitCode}): {command.StandardError.Snapshot()}");
    }

    internal async Task DisposeRuntimeAsync()
    {
        if (RuntimeDisposed) return;
        await Runtime.DisposeAsync();
        RuntimeDisposed = true;
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    internal static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        if (File.Exists(path)) return;
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FileSystemEventHandler signal = (_, _) => completion.TrySetResult();
        RenamedEventHandler rename = (_, _) => completion.TrySetResult();
        watcher.Created += signal;
        watcher.Changed += signal;
        watcher.Renamed += rename;
        if (File.Exists(path)) return;
        await completion.Task.WaitAsync(timeout);
    }

    private sealed class NoopDiagnosticSink : IDiagnosticSink
    {
        internal static readonly NoopDiagnosticSink Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class DelayedStartLauncher(IProcessLauncher inner) : IProcessLauncher
    {
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ISupervisedProcess> LaunchAsync(
            LaunchSpecification specification,
            IProcessGroup processGroup,
            CancellationToken cancellationToken)
        {
            var process = await inner.LaunchAsync(specification, processGroup, cancellationToken);
            return Path.GetFileName(specification.ApplicationPath).Equals("pg_ctl.exe", StringComparison.OrdinalIgnoreCase) &&
                specification.Arguments.Count > 0 && specification.Arguments[0] == "start"
                ? new DelayedCompletionProcess(process, Released)
                : process;
        }
    }

    private sealed class DelayedCompletionProcess : ISupervisedProcess
    {
        private readonly ISupervisedProcess _inner;
        private readonly TaskCompletionSource _release;

        internal DelayedCompletionProcess(ISupervisedProcess inner, TaskCompletionSource release)
        {
            _inner = inner;
            _release = release;
            Completion = CompleteAsync();
        }

        public SupervisedProcessIdentity Identity => _inner.Identity;
        public Task<int> Completion { get; }
        public BoundedTextCollector StandardOutput => _inner.StandardOutput;
        public BoundedTextCollector StandardError => _inner.StandardError;

        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            await _inner.DisposeAsync();
        }

        private async Task<int> CompleteAsync()
        {
            var exitCode = await _inner.Completion;
            await _release.Task;
            return exitCode;
        }
    }
}
