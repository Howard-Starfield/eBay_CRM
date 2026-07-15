using System.Text;
using HowardLab.EbayCrm.AppHost.Core.Migrations;
using HowardLab.EbayCrm.AppHost.Core.Processes;

namespace HowardLab.EbayCrm.AppHost.Windows.Postgres;

public enum PostgresMigrationStage
{
    AfterChildExitBeforeVerification,
    AfterVerifiedTargetBeforeMarker,
}

public sealed record PostgresMigrationRunResult(
    MigrationOutcome Outcome,
    string ReasonCode,
    int? ProcessExitCode = null);

public sealed class PostgresMigrationRunner
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly PostgresRuntime _runtime;
    private readonly PostgresInstanceIdentity _identity;
    private readonly IMigrationAttemptStore _store;
    private readonly Guid _expectedClusterId;
    private readonly Version _appVersion;
    private readonly int _startingSchemaVersion;
    private readonly int _targetSchemaVersion;
    private readonly byte[] _migrationSql;
    private readonly TimeSpan _timeout;
    private readonly Action<PostgresMigrationStage>? _stageHook;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PostgresMigrationRunner(
        PostgresRuntime runtime,
        PostgresInstanceIdentity identity,
        IMigrationAttemptStore store,
        Guid expectedClusterId,
        Version appVersion,
        int startingSchemaVersion,
        int targetSchemaVersion,
        string migrationScriptPath,
        TimeSpan timeout,
        Action<PostgresMigrationStage>? stageHook = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(appVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationScriptPath);
        if (expectedClusterId == Guid.Empty) throw new ArgumentException("Expected cluster ID cannot be empty.", nameof(expectedClusterId));
        if (startingSchemaVersion < 0 || targetSchemaVersion <= startingSchemaVersion)
            throw new ArgumentException("The migration version transition is invalid.");
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!Path.IsPathFullyQualified(migrationScriptPath) || !File.Exists(migrationScriptPath))
            throw new ArgumentException("Migration script must be an existing absolute path.", nameof(migrationScriptPath));

        var scriptInfo = new FileInfo(migrationScriptPath);
        if (scriptInfo.Length is <= 0 or > Windows.Processes.WindowsProcessLauncher.MaximumStandardInputBytes)
            throw new ArgumentException("Migration script size is invalid.", nameof(migrationScriptPath));
        _migrationSql = File.ReadAllBytes(migrationScriptPath);
        _ = StrictUtf8.GetString(_migrationSql);

        _runtime = runtime;
        _identity = identity;
        _store = store;
        _expectedClusterId = expectedClusterId;
        _appVersion = appVersion;
        _startingSchemaVersion = startingSchemaVersion;
        _targetSchemaVersion = targetSchemaVersion;
        _timeout = timeout;
        _stageHook = stageHook;
    }

    public async Task<PostgresMigrationRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var probe = await _runtime.ProbeAsync(_identity, cancellationToken).ConfigureAwait(false);
            if (probe.ClusterId is { } actualClusterId && actualClusterId != _expectedClusterId)
                return new(MigrationOutcome.RepairRequired, "migration-cluster-id-mismatch");
            var actualVersion = probe.SchemaVersion ?? _startingSchemaVersion;
            var previous = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (previous is not null)
            {
                if (previous.StartingSchemaVersion != _startingSchemaVersion ||
                    previous.TargetSchemaVersion != _targetSchemaVersion)
                {
                    return new(MigrationOutcome.RepairRequired, "migration-marker-version-range-mismatch");
                }

                var previousExit = previous.State switch
                {
                    MigrationAttemptState.Succeeded => 0,
                    MigrationAttemptState.Failed => 1,
                    _ => (int?)null,
                };
                var classification = MigrationOutcomeClassifier.Classify(new(
                    previous.State,
                    previousExit,
                    _startingSchemaVersion,
                    _targetSchemaVersion,
                    actualVersion));
                if (classification.Outcome == MigrationOutcome.Succeeded)
                {
                    await PersistTerminalAsync(
                        previous,
                        MigrationAttemptState.Succeeded,
                        classification.ReasonCode,
                        cancellationToken).ConfigureAwait(false);
                }
                return new(classification.Outcome, classification.ReasonCode, previousExit);
            }

            if (actualVersion == _targetSchemaVersion)
            {
                var recovered = CreateRunningRecord();
                await PersistTerminalAsync(
                    recovered,
                    MigrationAttemptState.Succeeded,
                    "migration-target-verified",
                    cancellationToken).ConfigureAwait(false);
                return new(MigrationOutcome.Succeeded, "migration-target-verified");
            }
            if (actualVersion != _startingSchemaVersion)
                return new(MigrationOutcome.RepairRequired, "migration-schema-indeterminate");

            var running = CreateRunningRecord();
            await _store.WriteAsync(running, cancellationToken).ConfigureAwait(false);
            ISupervisedProcess? command = null;
            try
            {
                command = await _runtime.LaunchVerifiedMigrationAsync(
                    _identity,
                    _expectedClusterId,
                    _startingSchemaVersion,
                    _targetSchemaVersion,
                    _migrationSql,
                    cancellationToken).ConfigureAwait(false);

                int exitCode;
                try
                {
                    exitCode = await command.Completion.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await command.DisposeAsync().ConfigureAwait(false);
                    command = null;
                    var recovered = await RecoverCommittedTargetAsync(running).ConfigureAwait(false);
                    if (recovered is not null) return recovered;
                    throw new PostgresMigrationIndeterminateException("migration-process-timeout-indeterminate");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await command.DisposeAsync().ConfigureAwait(false);
                    command = null;
                    var recovered = await RecoverCommittedTargetAsync(running).ConfigureAwait(false);
                    if (recovered is not null) return recovered;
                    throw;
                }

                await command.DisposeAsync().ConfigureAwait(false);
                command = null;
                _stageHook?.Invoke(PostgresMigrationStage.AfterChildExitBeforeVerification);
                probe = await _runtime.ProbeAsync(_identity, CancellationToken.None).ConfigureAwait(false);
                if (probe.ClusterId is { } verifiedCluster && verifiedCluster != _expectedClusterId)
                    return new(MigrationOutcome.RepairRequired, "migration-cluster-id-mismatch", exitCode);
                actualVersion = probe.SchemaVersion ?? _startingSchemaVersion;
                var classification = MigrationOutcomeClassifier.Classify(new(
                    exitCode == 0 ? MigrationAttemptState.Succeeded : MigrationAttemptState.Failed,
                    exitCode,
                    _startingSchemaVersion,
                    _targetSchemaVersion,
                    actualVersion));

                if (classification.Outcome == MigrationOutcome.Succeeded)
                {
                    _stageHook?.Invoke(PostgresMigrationStage.AfterVerifiedTargetBeforeMarker);
                    await PersistTerminalAsync(
                        running,
                        MigrationAttemptState.Succeeded,
                        classification.ReasonCode,
                        CancellationToken.None).ConfigureAwait(false);
                }
                else if (classification.Outcome == MigrationOutcome.Failed)
                {
                    await PersistTerminalAsync(
                        running,
                        MigrationAttemptState.Failed,
                        classification.ReasonCode,
                        CancellationToken.None).ConfigureAwait(false);
                }
                return new(classification.Outcome, classification.ReasonCode, exitCode);
            }
            finally
            {
                if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private MigrationAttemptRecord CreateRunningRecord() => new(
        Guid.NewGuid(),
        _appVersion,
        _startingSchemaVersion,
        _targetSchemaVersion,
        MigrationAttemptState.Running,
        DateTimeOffset.UtcNow,
        finishedAtUtc: null,
        "migration-running");

    private async Task<PostgresMigrationRunResult?> RecoverCommittedTargetAsync(MigrationAttemptRecord running)
    {
        var probe = await _runtime.ProbeAsync(_identity, CancellationToken.None).ConfigureAwait(false);
        if (probe.ClusterId != _expectedClusterId || probe.SchemaVersion != _targetSchemaVersion)
            return null;
        _stageHook?.Invoke(PostgresMigrationStage.AfterVerifiedTargetBeforeMarker);
        await PersistTerminalAsync(
            running,
            MigrationAttemptState.Succeeded,
            "migration-target-verified",
            CancellationToken.None).ConfigureAwait(false);
        return new(MigrationOutcome.Succeeded, "migration-target-verified");
    }

    private ValueTask PersistTerminalAsync(
        MigrationAttemptRecord attempt,
        MigrationAttemptState state,
        string reasonCode,
        CancellationToken cancellationToken) => _store.WriteAsync(
            new MigrationAttemptRecord(
                attempt.OperationId,
                attempt.AppVersion,
                attempt.StartingSchemaVersion,
                attempt.TargetSchemaVersion,
                state,
                attempt.StartedAtUtc,
                DateTimeOffset.UtcNow,
                reasonCode),
            cancellationToken);
}

public sealed class PostgresMigrationIndeterminateException : Exception
{
    public PostgresMigrationIndeterminateException(string reasonCode)
        : base(reasonCode) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
