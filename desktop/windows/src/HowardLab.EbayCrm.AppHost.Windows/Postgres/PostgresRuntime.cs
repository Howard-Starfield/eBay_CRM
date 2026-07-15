using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Postgres;

public sealed class PostgresRuntime :
    IPostgreSqlRuntime<PostgresInstanceIdentity, PostgresSqlProbe>, IAsyncDisposable
{
    private const int MaxLogBytes = 1024 * 1024;
    private static readonly TimeSpan InitializationDeadline = TimeSpan.FromSeconds(60);
    private readonly PostgresBinaryLayout _layout;
    private readonly PostgresClusterPaths _paths;
    private readonly ProcessGeneration _generation;
    private readonly int _port;
    private readonly SecretValue _password;
    private readonly IProcessLauncher _launcher;
    private readonly WindowsJobObject _job;
    private readonly TimeSpan _startDeadline;
    private readonly TimeSpan _stopDeadline;
    private readonly TimeSpan _reconciliationDeadline;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISupervisedProcess? _pendingStartCommand;
    private ISupervisedProcess? _pendingStopCommand;
    private PostgresInstanceIdentity? _identity;
    private bool _requiresStartQuietReconciliation;
    private RuntimeState _state;

    public PostgresRuntime(
        PostgresBinaryLayout layout,
        PostgresClusterPaths paths,
        ProcessGeneration generation,
        int loopbackPort,
        SecretValue password,
        IProcessLauncher launcher,
        WindowsJobObject job,
        TimeSpan startDeadline,
        TimeSpan stopDeadline,
        TimeSpan? reconciliationDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(job);
        if (generation.Role != RuntimeRole.Database) throw new ArgumentException("PostgreSQL requires a database generation.", nameof(generation));
        if (loopbackPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(loopbackPort));
        if (startDeadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(startDeadline));
        if (stopDeadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stopDeadline));
        if (reconciliationDeadline is { } invalid && invalid <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(reconciliationDeadline));
        _layout = layout;
        _paths = paths;
        _generation = generation;
        _port = loopbackPort;
        _password = password;
        _launcher = launcher;
        _job = job;
        _startDeadline = startDeadline;
        _stopDeadline = stopDeadline;
        _reconciliationDeadline = reconciliationDeadline ?? TimeSpan.FromMinutes(5);
    }

    public async Task<PostgreSqlOperationOutcome> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_paths.RuntimeDirectory);
            if (Directory.Exists(_paths.DataDirectory))
            {
                var entries = Directory.EnumerateFileSystemEntries(_paths.DataDirectory).Take(1).Any();
                if (entries)
                {
                    if (IsRecognizedCluster())
                    {
                        return PostgreSqlOperationOutcome.Completed;
                    }

                    throw new PostgresClusterRepairRequiredException("postgres-data-directory-unrecognized");
                }
            }
            else
            {
                Directory.CreateDirectory(_paths.DataDirectory);
            }

            var passwordPath = Path.Combine(_paths.RuntimeDirectory, $"initdb-{Guid.NewGuid():N}.pw");
            try
            {
                await WriteCurrentUserOnlySecretAsync(passwordPath, _password, cancellationToken).ConfigureAwait(false);
                await using var command = await LaunchCommandAsync(
                    _layout.InitDbExe,
                    ["--pgdata", _paths.DataDirectory, "--username", "ebaycrm", "--encoding", "UTF8",
                     "--auth-host", "scram-sha-256", "--auth-local", "scram-sha-256",
                     "--pwfile", passwordPath, "--no-instructions"],
                    secretEnvironment: null,
                    cancellationToken).ConfigureAwait(false);
                var exitCode = await command.Completion.WaitAsync(InitializationDeadline, cancellationToken).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    throw new PostgresCommandException(
                        "initdb-failed",
                        exitCode,
                        command.StandardOutput.Snapshot(),
                        command.StandardError.Snapshot());
                }
            }
            finally
            {
                ZeroAndDelete(passwordPath);
            }

            return PostgreSqlOperationOutcome.Completed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PostgreSqlOperationResult<PostgresInstanceIdentity>> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ISupervisedProcess? command = null;
        var launchCompleted = false;
        try
        {
            if (_state is not RuntimeState.Stopped)
            {
                throw new InvalidOperationException("A PostgreSQL start cannot begin until the prior generation is reconciled stopped.");
            }

            if (_pendingStartCommand is not null || _pendingStopCommand is not null)
                throw new InvalidOperationException("PostgreSQL control commands must be reconciled before another start.");

            _identity = null;
            _requiresStartQuietReconciliation = false;

            ValidateInitializedCluster();
            if (File.Exists(_paths.PostmasterPidFile))
            {
                return new(PostgreSqlOperationOutcome.Failed, null, ClassifyExistingPidFile());
            }
            PrepareBoundedLog();
            _state = RuntimeState.Starting;
            // PostgreSQL truncates this cyclic name on minute rotations. Size rotations can append
            // within a minute, so the hard count/size bounds are enforced only at stopped boundaries.
            var serverOptions =
                $"-p {_port.ToString(CultureInfo.InvariantCulture)} -h 127.0.0.1 " +
                "-c logging_collector=on " +
                "-c log_directory=../runtime/postgres-logs " +
                "-c log_filename=postgres-%M.log " +
                "-c log_rotation_age=1 " +
                "-c log_rotation_size=1024 " +
                "-c log_truncate_on_rotation=on";
            command = await LaunchCommandAsync(
                _layout.PgCtlExe,
                ["start", "-D", _paths.DataDirectory, "-l", _paths.LogFile, "-w", "-t", "60", "-o", serverOptions],
                secretEnvironment: null,
                cancellationToken,
                launchesPostmaster: true).ConfigureAwait(false);
            launchCompleted = true;
            var exitCode = await WaitBoundedAsync(command, _startDeadline, cancellationToken).ConfigureAwait(false);
            if (exitCode is null)
            {
                _pendingStartCommand = command;
                command = null;
                RefreshIdentity();
                _state = RuntimeState.StartIndeterminate;
                return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, _identity);
            }

            await command.DisposeAsync().ConfigureAwait(false);
            command = null;
            _identity = TryCaptureIdentity();
            if (exitCode != 0)
            {
                _requiresStartQuietReconciliation = _identity is null;
                _state = RuntimeState.StartIndeterminate;
                return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, _identity, "postgres-start-command-nonzero");
            }

            if (_identity is null)
            {
                _requiresStartQuietReconciliation = true;
                _state = RuntimeState.StartIndeterminate;
                return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, null, "postmaster-identity-not-yet-available");
            }

            try
            {
                _ = await ProbeCoreAsync(_identity, _reconciliationDeadline, cancellationToken).ConfigureAwait(false);
                _state = RuntimeState.Running;
            }
            catch (PostgresProbeException error)
            {
                _state = RuntimeState.StartIndeterminate;
                return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, _identity, error.ReasonCode);
            }
            return new(PostgreSqlOperationOutcome.Completed, _identity);
        }
        catch
        {
            if (launchCompleted)
            {
                if (command is not null)
                {
                    _pendingStartCommand = command;
                    command = null;
                }

                RefreshIdentity();
                _state = RuntimeState.StartIndeterminate;
            }
            else if (_state == RuntimeState.Starting)
            {
                _state = RuntimeState.Stopped;
            }
            throw;
        }
        finally
        {
            if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            _gate.Release();
        }
    }

    public async Task<PostgreSqlOperationResult<PostgresInstanceIdentity>> ReconcileStartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == RuntimeState.Running && _identity is { HasExited: false } runningIdentity)
            {
                try
                {
                    _ = await ProbeCoreAsync(runningIdentity, _reconciliationDeadline, cancellationToken).ConfigureAwait(false);
                    return new(PostgreSqlOperationOutcome.ReconciledRunning, runningIdentity);
                }
                catch (PostgresProbeException)
                {
                    _state = RuntimeState.StartIndeterminate;
                    return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, runningIdentity, "postgres-start-not-ready");
                }
            }
            if (_state == RuntimeState.Running && _identity is { } exitedRunningIdentity && exitedRunningIdentity.HasExited)
            {
                if (!await DrainPendingControlCommandsAsync(_reconciliationDeadline, cancellationToken).ConfigureAwait(false))
                    return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, exitedRunningIdentity, "postgres-control-command-running");
                DeleteReconciledPidFile(exitedRunningIdentity);
                TrimLogIfNeeded();
                _identity = null;
                _state = RuntimeState.Stopped;
                return new(PostgreSqlOperationOutcome.ReconciledStopped, null);
            }
            if (_state != RuntimeState.StartIndeterminate)
                return new(PostgreSqlOperationOutcome.ReconciledStopped, null);

            RefreshIdentity();
            if (_pendingStartCommand is { Completion.IsCompleted: false } && _identity is { HasExited: false })
                return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, _identity, "postgres-start-command-running");

            if (_pendingStartCommand is { } pending)
            {
                int exitCode;
                try
                {
                    exitCode = await pending.Completion.WaitAsync(_reconciliationDeadline, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    RefreshIdentity();
                    return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, _identity, "postgres-start-command-running");
                }

                await pending.DisposeAsync().ConfigureAwait(false);
                _pendingStartCommand = null;
                _requiresStartQuietReconciliation = true;
            }
            RefreshIdentity();
            if (_identity is not null && !_identity.HasExited)
            {
                try
                {
                    _ = await ProbeCoreAsync(_identity, _reconciliationDeadline, cancellationToken).ConfigureAwait(false);
                    _state = RuntimeState.Running;
                    return new(PostgreSqlOperationOutcome.ReconciledRunning, _identity);
                }
                catch (PostgresProbeException)
                {
                    return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, _identity, "postgres-start-not-ready");
                }
            }

            if (_identity is null && _requiresStartQuietReconciliation)
            {
                if (await WaitForExpectedPidFileAsync(_reconciliationDeadline, cancellationToken).ConfigureAwait(false))
                {
                    _identity = TryCaptureIdentity();
                    if (_identity is not null && !_identity.HasExited)
                    {
                        try
                        {
                            _ = await ProbeCoreAsync(_identity, _reconciliationDeadline, cancellationToken).ConfigureAwait(false);
                            _state = RuntimeState.Running;
                            _requiresStartQuietReconciliation = false;
                            return new(PostgreSqlOperationOutcome.ReconciledRunning, _identity);
                        }
                        catch (PostgresProbeException)
                        {
                            return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, _identity, "postgres-start-not-ready");
                        }
                    }

                    return new(PostgreSqlOperationOutcome.TimedOutIndeterminate, null, "postmaster-pid-not-yet-verifiable");
                }

                _requiresStartQuietReconciliation = false;
            }

            if (_identity is { } stoppedIdentity)
            {
                DeleteReconciledPidFile(stoppedIdentity);
                _identity = null;
            }
            _state = RuntimeState.Stopped;
            TrimLogIfNeeded();
            return new(PostgreSqlOperationOutcome.ReconciledStopped, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PostgresSqlProbe> ProbeAsync(PostgresInstanceIdentity identity, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ProbeCoreAsync(ValidateCurrentIdentity(identity), _reconciliationDeadline, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<PostgreSqlOperationOutcome> StopFastAsync(PostgresInstanceIdentity identity, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ISupervisedProcess? command = null;
        var launchCompleted = false;
        try
        {
            if (_state is RuntimeState.Stopping or RuntimeState.StopIndeterminate)
                throw new InvalidOperationException("A PostgreSQL fast stop is already pending reconciliation.");
            identity = ValidateCurrentIdentity(identity);
            if (_pendingStopCommand is not null)
                throw new InvalidOperationException("A PostgreSQL fast stop is already pending reconciliation.");
            if (identity.HasExited)
            {
                if (!await DrainPendingControlCommandsAsync(_reconciliationDeadline, cancellationToken).ConfigureAwait(false))
                    return PostgreSqlOperationOutcome.TimedOutIndeterminate;
                _state = RuntimeState.Stopped;
                DeleteReconciledPidFile(identity);
                TrimLogIfNeeded();
                _identity = null;
                return PostgreSqlOperationOutcome.ReconciledStopped;
            }
            _state = RuntimeState.Stopping;
            var seconds = Math.Max(1, (int)Math.Ceiling(_stopDeadline.TotalSeconds));
            command = await LaunchCommandAsync(
                _layout.PgCtlExe,
                ["stop", "-D", _paths.DataDirectory, "-m", "fast", "-w", "-t", seconds.ToString(CultureInfo.InvariantCulture)],
                secretEnvironment: null,
                cancellationToken).ConfigureAwait(false);
            launchCompleted = true;
            var exitCode = await WaitBoundedAsync(command, _stopDeadline, cancellationToken).ConfigureAwait(false);
            if (exitCode is null)
            {
                _pendingStopCommand = command;
                command = null;
                _state = RuntimeState.StopIndeterminate;
                return PostgreSqlOperationOutcome.TimedOutIndeterminate;
            }

            await command.DisposeAsync().ConfigureAwait(false);
            command = null;
            if (exitCode != 0 && !identity.HasExited)
            {
                _state = RuntimeState.StopIndeterminate;
                return PostgreSqlOperationOutcome.TimedOutIndeterminate;
            }

            _state = RuntimeState.StopIndeterminate;
            if (identity.HasExited)
            {
                TrimLogIfNeeded();
                return PostgreSqlOperationOutcome.Completed;
            }
            return PostgreSqlOperationOutcome.TimedOutIndeterminate;
        }
        catch
        {
            if (launchCompleted && command is not null)
            {
                _pendingStopCommand = command;
                command = null;
                _state = RuntimeState.StopIndeterminate;
            }
            throw;
        }
        finally
        {
            if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            _gate.Release();
        }
    }

    public async Task<PostgreSqlOperationOutcome> ReconcileStopAsync(PostgresInstanceIdentity identity, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            identity = ValidateCurrentIdentity(identity);
            if (!await DrainPendingControlCommandsAsync(_reconciliationDeadline, cancellationToken).ConfigureAwait(false))
                return PostgreSqlOperationOutcome.TimedOutIndeterminate;
            if (!identity.HasExited) return PostgreSqlOperationOutcome.TimedOutIndeterminate;
            _state = RuntimeState.Stopped;
            DeleteReconciledPidFile(identity);
            TrimLogIfNeeded();
            _identity = null;
            return PostgreSqlOperationOutcome.ReconciledStopped;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pendingStartCommand is { } pendingStart)
                await pendingStart.DisposeAsync().ConfigureAwait(false);
            _pendingStartCommand = null;
            if (_pendingStopCommand is { } pendingStop)
                await pendingStop.DisposeAsync().ConfigureAwait(false);
            _pendingStopCommand = null;
            if (_identity is null || _identity.HasExited) TrimLogIfNeeded();
        }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private async Task<PostgresSqlProbe> ProbeCoreAsync(PostgresInstanceIdentity identity, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (identity.HasExited || !_job.Contains(identity.PostmasterHandle)) throw new PostgresProbeException("postmaster-not-owned-live");
        await using var command = await LaunchCommandAsync(
            _layout.PsqlExe,
            ["-X", "-A", "-t", "-q", "-w", "-h", "127.0.0.1", "-p", identity.LoopbackPort.ToString(CultureInfo.InvariantCulture),
             "-U", "ebaycrm", "-d", "postgres", "-v", "ON_ERROR_STOP=1", "-c", "SELECT 1;", "-c", "SHOW data_directory;"],
            new Dictionary<string, SecretValue>(StringComparer.Ordinal) { ["PGPASSWORD"] = _password },
            cancellationToken).ConfigureAwait(false);
        int exit;
        try { exit = await command.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { throw new PostgresProbeException("postgres-sql-probe-timeout"); }
        if (exit != 0) throw new PostgresProbeException("postgres-sql-probe-failed");
        var lines = command.StandardOutput.Snapshot().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 2 || lines[0] != "1") throw new PostgresProbeException("postgres-sql-probe-malformed");
        var reported = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lines[1]));
        if (!StringComparer.OrdinalIgnoreCase.Equals(reported, identity.CanonicalDataDirectory)) throw new PostgresProbeException("postgres-wrong-data-directory");
        return new PostgresSqlProbe(lines[0], reported);
    }

    private PostgresInstanceIdentity CaptureIdentity()
    {
        var pidFile = PostmasterPidFile.Read(_paths.PostmasterPidFile, _paths.DataDirectory);
        if (pidFile.Port != _port) throw new PostmasterPidFileException("postmaster-pid-wrong-port");
        var handle = NativeMethods.OpenProcess(NativeMethods.Synchronize | NativeMethods.ProcessQueryLimitedInformation, false, checked((uint)pidFile.ProcessId));
        if (handle.IsInvalid) { var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            var captured = WindowsProcessIdentityVerifier.Instance.Capture(RuntimeRole.Database, _generation, handle);
            if (!StringComparer.OrdinalIgnoreCase.Equals(captured.VerifiedImagePath, _layout.PostgresExe)) throw new PostmasterPidFileException("postmaster-pid-unrelated-process");
            if (Math.Abs((captured.CreationTimeUtc - pidFile.StartTimeUtc).TotalSeconds) > 5) throw new PostmasterPidFileException("postmaster-pid-creation-time-mismatch");
            var inJob = _job.Contains(handle);
            if (!inJob) throw new PostmasterPidFileException("postmaster-not-in-apphost-job");
            return new PostgresInstanceIdentity(_generation, pidFile.CanonicalDataDirectory, captured.ProcessId, handle,
                captured.CreationTimeUtc, captured.VerifiedImagePath, inJob, pidFile.Port, null);
        }
        catch { handle.Dispose(); throw; }
    }

    private PostgresInstanceIdentity? TryCaptureIdentity()
    {
        try { return CaptureIdentity(); }
        catch (Exception error) when (error is IOException or PostmasterPidFileException or Win32Exception) { return null; }
    }

    private void RefreshIdentity()
    {
        var needsCapture = _identity is null;
        if (!needsCapture)
        {
            try { needsCapture = _identity!.HasExited; }
            catch (ObjectDisposedException) { needsCapture = true; }
        }

        if (needsCapture) _identity = TryCaptureIdentity();
    }

    private PostgresInstanceIdentity ValidateCurrentIdentity(PostgresInstanceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!ReferenceEquals(identity, _identity) || identity.Generation != _generation ||
            identity.PostmasterHandle.IsInvalid || identity.PostmasterHandle.IsClosed)
            throw new ArgumentException("Only the retained identity for this runtime generation is accepted.", nameof(identity));
        return identity;
    }

    private void ValidateInitializedCluster()
    {
        if (!IsRecognizedCluster())
            throw new PostgresClusterRepairRequiredException("postgres-cluster-not-initialized");
    }

    private bool IsRecognizedCluster()
    {
        var version = Path.Combine(_paths.DataDirectory, "PG_VERSION");
        return File.Exists(version) &&
            File.ReadAllText(version).Trim() == "16" &&
            File.Exists(Path.Combine(_paths.DataDirectory, "global", "pg_control")) &&
            Directory.Exists(Path.Combine(_paths.DataDirectory, "base")) &&
            File.Exists(Path.Combine(_paths.DataDirectory, "postgresql.conf")) &&
            File.Exists(Path.Combine(_paths.DataDirectory, "pg_hba.conf"));
    }

    private void DeleteReconciledPidFile(PostgresInstanceIdentity identity)
    {
        if (!File.Exists(_paths.PostmasterPidFile)) return;
        PostmasterPidFile parsed;
        try { parsed = PostmasterPidFile.Read(_paths.PostmasterPidFile, _paths.DataDirectory); }
        catch (PostmasterPidFileException) { return; }
        if (parsed.ProcessId != identity.ProcessId ||
            parsed.Port != identity.LoopbackPort ||
            Math.Abs((parsed.StartTimeUtc - identity.CreationTimeUtc).TotalSeconds) > 5)
            return;
        File.Delete(_paths.PostmasterPidFile);
    }

    private string ClassifyExistingPidFile()
    {
        PostmasterPidFile parsed;
        try
        {
            parsed = PostmasterPidFile.Read(_paths.PostmasterPidFile, _paths.DataDirectory);
        }
        catch (PostmasterPidFileException error)
        {
            return error.ReasonCode;
        }

        SafeProcessHandle? handle = null;
        try
        {
            handle = NativeMethods.OpenProcess(
                NativeMethods.Synchronize | NativeMethods.ProcessQueryLimitedInformation,
                inheritHandle: false,
                checked((uint)parsed.ProcessId));
            if (handle.IsInvalid) return "postmaster-pid-stale";
            if (parsed.Port != _port) return "postmaster-pid-wrong-port";
            var captured = WindowsProcessIdentityVerifier.Instance.Capture(RuntimeRole.Database, _generation, handle);
            if (!StringComparer.OrdinalIgnoreCase.Equals(captured.VerifiedImagePath, _layout.PostgresExe))
                return "postmaster-pid-unrelated-process";
            if (Math.Abs((captured.CreationTimeUtc - parsed.StartTimeUtc).TotalSeconds) > 5)
                return "postmaster-pid-creation-time-mismatch";
            return _job.Contains(handle) ? "postmaster-already-running" : "postmaster-not-in-apphost-job";
        }
        catch (Win32Exception)
        {
            return "postmaster-pid-unrelated-process";
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private async ValueTask<ISupervisedProcess> LaunchCommandAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, SecretValue>? secretEnvironment, CancellationToken cancellationToken,
        bool launchesPostmaster = false)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? throw new InvalidOperationException("SystemRoot is unavailable.");
        env["PATH"] = string.Join(Path.PathSeparator, _layout.CanonicalBinDirectory, Path.Combine(systemRoot, "System32"));
        foreach (var name in new[] { "SystemRoot", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) env[name] = value;
        var specification = new LaunchSpecification(RuntimeRole.Database, _generation, executable, arguments,
            _paths.RuntimeDirectory, env, secretEnvironment ?? new Dictionary<string, SecretValue>(), TimeSpan.FromSeconds(2));
        if (launchesPostmaster)
            return await _launcher.LaunchAsync(specification, _job, cancellationToken).ConfigureAwait(false);

        var helperJob = WindowsJobObject.CreateKillOnClose();
        try
        {
            var process = await _launcher.LaunchAsync(specification, helperJob, cancellationToken).ConfigureAwait(false);
            return new OwnedProcessGroupCommand(process, helperJob);
        }
        catch
        {
            helperJob.Dispose();
            throw;
        }
    }

    private static async Task<int?> WaitBoundedAsync(ISupervisedProcess command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { return await command.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    private async Task<bool> DrainPendingControlCommandsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pendingStart = _pendingStartCommand;
        var pendingStop = _pendingStopCommand;
        if (pendingStart is null && pendingStop is null) return true;

        var completions = new List<Task<int>>(2);
        if (pendingStart is not null) completions.Add(pendingStart.Completion);
        if (pendingStop is not null) completions.Add(pendingStop.Completion);
        try
        {
            await Task.WhenAll(completions).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }

        if (pendingStart is not null)
        {
            await pendingStart.DisposeAsync().ConfigureAwait(false);
            if (ReferenceEquals(_pendingStartCommand, pendingStart)) _pendingStartCommand = null;
        }
        if (pendingStop is not null)
        {
            await pendingStop.DisposeAsync().ConfigureAwait(false);
            if (ReferenceEquals(_pendingStopCommand, pendingStop)) _pendingStopCommand = null;
        }
        return true;
    }

    private async Task<bool> WaitForExpectedPidFileAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (File.Exists(_paths.PostmasterPidFile)) return true;
        using var watcher = new FileSystemWatcher(_paths.DataDirectory, Path.GetFileName(_paths.PostmasterPidFile))
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
        if (File.Exists(_paths.PostmasterPidFile)) return true;
        try
        {
            await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void PrepareBoundedLog()
    {
        Directory.CreateDirectory(_paths.RuntimeDirectory);
        Directory.CreateDirectory(_paths.ServerLogDirectory);
        PruneServerLogs(maxFiles: 7);
        if (File.Exists(_paths.LogFile)) File.WriteAllBytes(_paths.LogFile, []);
    }

    private void TrimLogIfNeeded()
    {
        PruneServerLogs(maxFiles: 8);
        if (!File.Exists(_paths.LogFile) || new FileInfo(_paths.LogFile).Length <= MaxLogBytes) return;
        using var stream = new FileStream(_paths.LogFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(-MaxLogBytes, SeekOrigin.End);
        var tail = new byte[MaxLogBytes];
        stream.ReadExactly(tail);
        stream.Position = 0;
        stream.Write(tail);
        stream.SetLength(MaxLogBytes);
    }

    private void PruneServerLogs(int maxFiles)
    {
        if (!Directory.Exists(_paths.ServerLogDirectory)) return;
        var files = new DirectoryInfo(_paths.ServerLogDirectory)
            .EnumerateFiles("postgres-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (var file in files)
        {
            if (file.Length <= MaxLogBytes) continue;
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(-MaxLogBytes, SeekOrigin.End);
                var tail = new byte[MaxLogBytes];
                stream.ReadExactly(tail);
                stream.Position = 0;
                stream.Write(tail);
                stream.SetLength(MaxLogBytes);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var file in files.Skip(maxFiles))
        {
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static unsafe Task WriteCurrentUserOnlySecretAsync(string path, SecretValue secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PostgresBootstrapPasswordFile.Write(path, secret);
        return Task.CompletedTask;
    }

    private static void ZeroAndDelete(string path) => PostgresBootstrapPasswordFile.ZeroAndDelete(path);

    private sealed class OwnedProcessGroupCommand : ISupervisedProcess
    {
        private readonly ISupervisedProcess _inner;
        private readonly WindowsJobObject _job;
        private int _disposed;

        internal OwnedProcessGroupCommand(ISupervisedProcess inner, WindowsJobObject job)
        {
            _inner = inner;
            _job = job;
        }

        public SupervisedProcessIdentity Identity => _inner.Identity;
        public Task<int> Completion => _inner.Completion;
        public BoundedTextCollector StandardOutput => _inner.StandardOutput;
        public BoundedTextCollector StandardError => _inner.StandardError;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await _inner.DisposeAsync().ConfigureAwait(false); }
            finally { _job.Dispose(); }
        }
    }

    private enum RuntimeState { Stopped, Starting, StartIndeterminate, Running, Stopping, StopIndeterminate }
}

internal static unsafe class PostgresBootstrapPasswordFile
{
    internal static void Write(string path, SecretValue secret)
    {
        using var descriptor = NativeSecurityDescriptor.CreateForCurrentUserOnly();
        var nativeAttributes = new PostgresNativeMethods.SecurityAttributes
        {
            Length = checked((uint)sizeof(PostgresNativeMethods.SecurityAttributes)),
            SecurityDescriptor = (void*)descriptor.DangerousGetHandle(),
            InheritHandle = 0,
        };
        using var handle = PostgresNativeMethods.CreateFile(
            path,
            PostgresNativeMethods.GenericWrite,
            shareMode: 0,
            &nativeAttributes,
            PostgresNativeMethods.CreateNew,
            PostgresNativeMethods.FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var bytes = Encoding.UTF8.GetBytes(secret.RevealForChildEnvironment());
        try
        {
            using var stream = new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: false);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static void ZeroAndDelete(string path, Action<string>? beforeDelete = null)
    {
        if (!File.Exists(path)) return;
        try
        {
            var length = new FileInfo(path).Length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[Math.Min(4096, checked((int)Math.Max(1, Math.Min(length, 4096))))];
                long written = 0;
                while (written < length)
                {
                    var count = (int)Math.Min(zeros.Length, length - written);
                    stream.Write(zeros, 0, count);
                    written += count;
                }
                stream.Flush(flushToDisk: true);
            }
            beforeDelete?.Invoke(path);
        }
        finally { File.Delete(path); }
    }
}

public sealed class PostgresClusterRepairRequiredException : Exception
{
    public PostgresClusterRepairRequiredException(string reasonCode) : base(reasonCode) => ReasonCode = reasonCode;
    public string ReasonCode { get; }
}

public sealed class PostgresCommandException : Exception
{
    public PostgresCommandException(string reasonCode, int exitCode, string standardOutput = "", string standardError = "")
        : base($"{reasonCode}: exit {exitCode}")
    {
        ReasonCode = reasonCode;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }
    public string ReasonCode { get; }
    public int ExitCode { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }
}

internal static unsafe partial class PostgresNativeMethods
{
    internal const uint GenericWrite = 0x40000000;
    internal const uint CreateNew = 1;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const uint OpenExisting = 3;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        internal uint Length;
        internal void* SecurityDescriptor;
        internal int InheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        SecurityAttributes* securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        char* filePath,
        uint filePathLength,
        uint flags);
}
