using System.Net;
using System.Net.Sockets;
using System.Text;
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
        PostgresRuntime runtime,
        FaultInjectingLauncher launcher)
    {
        Root = root;
        Layout = layout;
        Paths = paths;
        Port = port;
        Password = password;
        Job = job;
        Runtime = runtime;
        Launcher = launcher;
    }

    internal string Root { get; }
    internal PostgresBinaryLayout Layout { get; }
    internal PostgresClusterPaths Paths { get; }
    internal int Port { get; }
    internal SecretValue Password { get; }
    internal WindowsJobObject Job { get; private set; }
    internal PostgresRuntime Runtime { get; private set; }
    internal FaultInjectingLauncher Launcher { get; private set; }
    internal PostgresInstanceIdentity? Identity { get; set; }
    private bool RuntimeDisposed { get; set; }

    internal static async Task<PostgresTestCluster> CreateAsync(
        int? port = null,
        TimeSpan? startDeadline = null,
        TimeSpan? stopDeadline = null,
        TimeSpan? reconciliationDeadline = null) =>
        await CreateCoreAsync(initialize: true, port, startDeadline, stopDeadline, reconciliationDeadline);

    internal static async Task<PostgresTestCluster> CreateUninitializedAsync() =>
        await CreateCoreAsync(initialize: false, null, null, null, null);

    internal static async Task<PostgresTestCluster> OpenExistingAsync(
        string root,
        int port,
        SecretValue password)
    {
        var bin = Environment.GetEnvironmentVariable("EBAYCRM_POSTGRES_BIN")
            ?? throw new InvalidOperationException("EBAYCRM_POSTGRES_BIN is required.");
        var layout = PostgresBinaryLayout.Validate(Path.GetFullPath(bin));
        var paths = PostgresClusterPaths.Create(root);
        var job = WindowsJobObject.CreateKillOnClose();
        var launcher = new FaultInjectingLauncher(
            new WindowsProcessLauncher(NoopDiagnosticSink.Instance, maxOutputBytes: 256 * 1024));
        var runtime = new PostgresRuntime(
            layout,
            paths,
            new ProcessGeneration(RuntimeRole.Database, 2, Guid.NewGuid()),
            port,
            password,
            launcher,
            job,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
        try
        {
            var cluster = new PostgresTestCluster(root, layout, paths, port, password, job, runtime, launcher);
            Assert.Equal(PostgreSqlOperationOutcome.Completed, await runtime.InitializeAsync());
            return cluster;
        }
        catch
        {
            await runtime.DisposeAsync();
            job.Dispose();
            throw;
        }
    }

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
        var launcher = new FaultInjectingLauncher(
            new WindowsProcessLauncher(NoopDiagnosticSink.Instance, maxOutputBytes: 256 * 1024));
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
            var cluster = new PostgresTestCluster(root, layout, paths, port, password, job, runtime, launcher);
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
                try
                {
                    _ = await Runtime.StopFastAsync(identity);
                    _ = await Runtime.ReconcileStopAsync(identity);
                }
                catch (InvalidOperationException)
                {
                    // Fault tests can intentionally leave the runtime fenced; Job closure below is authoritative cleanup.
                }
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
        var launcher = new FaultInjectingLauncher(
            new WindowsProcessLauncher(NoopDiagnosticSink.Instance, maxOutputBytes: 256 * 1024));
        Launcher = launcher;
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

    internal async Task<PostgreSqlOperationResult<PostgresInstanceIdentity>> CrashJobAndRestartWithRetainedBootstrapLogAsync()
    {
        var previousIdentity = Identity ?? throw new InvalidOperationException("The cluster is not running.");
        var previousLog = Launcher.LastStartLogFile
            ?? throw new InvalidOperationException("The PostgreSQL start log was not captured.");
        Job.Dispose();
        await previousIdentity.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped,
            await Runtime.ReconcileStopAsync(previousIdentity));
        await Runtime.DisposeAsync();
        Identity = null;
        using var retainedLog = await OpenExclusiveWhenAvailableAsync(previousLog, TimeSpan.FromSeconds(30));

        Job = WindowsJobObject.CreateKillOnClose();
        var launcher = new FaultInjectingLauncher(
            new WindowsProcessLauncher(NoopDiagnosticSink.Instance, maxOutputBytes: 256 * 1024));
        Launcher = launcher;
        Runtime = new PostgresRuntime(
            Layout,
            Paths,
            new ProcessGeneration(RuntimeRole.Database, 2, Guid.NewGuid()),
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
        var launcher = new FaultInjectingLauncher(
            new WindowsProcessLauncher(NoopDiagnosticSink.Instance, maxOutputBytes: 256 * 1024));
        Launcher = launcher;
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

    internal async Task ExecuteSqlAsync(string sql)
    {
        _ = await ExecuteSqlCoreAsync(sql);
    }

    internal Task<string> ExecuteSqlScalarAsync(string sql) => ExecuteSqlCoreAsync(sql);

    internal async Task<SqlCommandLease> LaunchSqlCommandAsync(string sql)
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
        var specification = new LaunchSpecification(
            RuntimeRole.Database,
            new ProcessGeneration(RuntimeRole.Database, 98, Guid.NewGuid()),
            Layout.PsqlExe,
            ["-X", "-A", "-t", "-q", "-w", "-h", "127.0.0.1", "-p", Port.ToString(),
             "-U", "ebaycrm", "-d", "postgres", "-v", "ON_ERROR_STOP=1"],
            Paths.RuntimeDirectory,
            environment,
            new Dictionary<string, SecretValue>(StringComparer.Ordinal) { ["PGPASSWORD"] = Password },
            TimeSpan.FromSeconds(2),
            Encoding.UTF8.GetBytes(sql));
        var helperJob = WindowsJobObject.CreateKillOnClose();
        try
        {
            var process = await new WindowsProcessLauncher(
                NoopDiagnosticSink.Instance,
                maxOutputBytes: 256 * 1024).LaunchAsync(
                    specification,
                    helperJob,
                    CancellationToken.None);
            return new SqlCommandLease(process, helperJob);
        }
        catch
        {
            helperJob.Dispose();
            throw;
        }
    }

    internal void ConfigureAsDisconnectedStandby()
    {
        File.WriteAllBytes(Path.Combine(Paths.DataDirectory, "standby.signal"), []);
        File.AppendAllText(
            Path.Combine(Paths.DataDirectory, "postgresql.auto.conf"),
            $"{Environment.NewLine}hot_standby = on{Environment.NewLine}" +
            "primary_conninfo = 'host=127.0.0.1 port=1 user=ebaycrm connect_timeout=1'" + Environment.NewLine);
    }

    private async Task<string> ExecuteSqlCoreAsync(string sql)
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
        return command.StandardOutput.Snapshot().Trim();
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

    private static async Task<FileStream> OpenExclusiveWhenAvailableAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
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

    internal sealed class FaultInjectingLauncher(IProcessLauncher inner) : IProcessLauncher
    {
        internal bool FailNextStopLaunch { get; set; }
        internal int? CompleteNextStopWithExitCode { get; set; }
        internal TimeSpan DelayNextStopLaunch { get; set; }
        internal bool LeaveNextStopPending { get; set; }
        internal int? LastStopTimeoutSeconds { get; private set; }
        internal string? LastStartLogFile { get; private set; }
        internal bool BlockNextStartLogPath { get; set; }
        internal ISupervisedProcess? LastMigrationProcess { get; private set; }
        internal TaskCompletionSource MigrationLaunchObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StopLaunchObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ISupervisedProcess> LaunchAsync(
            LaunchSpecification specification,
            IProcessGroup processGroup,
            CancellationToken cancellationToken)
        {
            var isMigration = Path.GetFileName(specification.ApplicationPath)
                .Equals("psql.exe", StringComparison.OrdinalIgnoreCase) &&
                specification.StandardInput.Length > 0 &&
                specification.Arguments.Any(argument =>
                    argument.StartsWith("expected_cluster_id=", StringComparison.OrdinalIgnoreCase));
            var isStop = Path.GetFileName(specification.ApplicationPath)
                .Equals("pg_ctl.exe", StringComparison.OrdinalIgnoreCase) &&
                specification.Arguments.Count > 0 && specification.Arguments[0] == "stop";
            if (!isStop)
            {
                var isStart = Path.GetFileName(specification.ApplicationPath)
                    .Equals("pg_ctl.exe", StringComparison.OrdinalIgnoreCase) &&
                    specification.Arguments.Count > 0 && specification.Arguments[0] == "start";
                if (isStart)
                {
                    var logIndex = specification.Arguments.ToList().IndexOf("-l");
                    LastStartLogFile = specification.Arguments[logIndex + 1];
                    if (BlockNextStartLogPath)
                    {
                        BlockNextStartLogPath = false;
                        Directory.CreateDirectory(LastStartLogFile);
                    }
                }
                var launched = await inner.LaunchAsync(specification, processGroup, cancellationToken);
                if (isMigration)
                {
                    LastMigrationProcess = launched;
                    MigrationLaunchObserved.TrySetResult();
                }
                return launched;
            }

            StopLaunchObserved.TrySetResult();
            var timeoutIndex = specification.Arguments.ToList().IndexOf("-t");
            LastStopTimeoutSeconds = int.Parse(specification.Arguments[timeoutIndex + 1]);
            if (DelayNextStopLaunch > TimeSpan.Zero)
            {
                var delay = DelayNextStopLaunch;
                DelayNextStopLaunch = TimeSpan.Zero;
                await Task.Delay(delay, cancellationToken);
            }
            if (FailNextStopLaunch)
            {
                FailNextStopLaunch = false;
                throw new InvalidOperationException("injected-stop-launch-failure");
            }
            if (CompleteNextStopWithExitCode is { } exitCode)
            {
                CompleteNextStopWithExitCode = null;
                return InjectedProcess.Completed(exitCode);
            }
            if (LeaveNextStopPending)
            {
                LeaveNextStopPending = false;
                return InjectedProcess.Pending();
            }
            return await inner.LaunchAsync(specification, processGroup, cancellationToken);
        }
    }

    private sealed class InjectedProcess : ISupervisedProcess
    {
        private readonly TaskCompletionSource<int>? _pending;

        private InjectedProcess(Task<int> completion, TaskCompletionSource<int>? pending)
        {
            Completion = completion;
            _pending = pending;
            StandardOutput = new BoundedTextCollector(4096, 1024);
            StandardError = new BoundedTextCollector(4096, 1024);
            StandardOutput.Complete();
            StandardError.Complete();
            Identity = new SupervisedProcessIdentity(
                RuntimeRole.Database,
                new ProcessGeneration(RuntimeRole.Database, 99, Guid.NewGuid()),
                Environment.ProcessId,
                DateTimeOffset.UtcNow,
                Environment.ProcessPath!);
        }

        internal static InjectedProcess Completed(int exitCode) => new(Task.FromResult(exitCode), null);

        internal static InjectedProcess Pending()
        {
            var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            return new InjectedProcess(pending.Task, pending);
        }

        public SupervisedProcessIdentity Identity { get; }
        public Task<int> Completion { get; }
        public BoundedTextCollector StandardOutput { get; }
        public BoundedTextCollector StandardError { get; }

        public ValueTask DisposeAsync()
        {
            _pending?.TrySetResult(1);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class SqlCommandLease(
        ISupervisedProcess process,
        WindowsJobObject job) : IAsyncDisposable
    {
        internal ISupervisedProcess Process { get; } = process;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Process.DisposeAsync();
            }
            finally
            {
                job.Dispose();
            }
        }
    }
}
