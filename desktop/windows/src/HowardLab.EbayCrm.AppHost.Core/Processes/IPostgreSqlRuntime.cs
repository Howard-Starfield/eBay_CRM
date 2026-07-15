namespace HowardLab.EbayCrm.AppHost.Core.Processes;

public enum PostgreSqlOperationOutcome
{
    Completed,
    TimedOutIndeterminate,
    ReconciledRunning,
    ReconciledStopped,
    Failed,
}

public sealed record PostgreSqlOperationResult<TIdentity>(
    PostgreSqlOperationOutcome Outcome,
    TIdentity? Identity,
    string? ReasonCode = null)
    where TIdentity : class;

public interface IPostgreSqlRuntime<TIdentity, TProbe>
    where TIdentity : class
    where TProbe : class
{
    Task<PostgreSqlOperationOutcome> InitializeAsync(CancellationToken cancellationToken = default);

    Task<PostgreSqlOperationResult<TIdentity>> StartAsync(CancellationToken cancellationToken = default);

    Task<PostgreSqlOperationResult<TIdentity>> ReconcileStartAsync(CancellationToken cancellationToken = default);

    Task<TProbe> ProbeAsync(TIdentity identity, CancellationToken cancellationToken = default);

    Task<PostgreSqlOperationOutcome> StopFastAsync(TIdentity identity, CancellationToken cancellationToken = default);

    Task<PostgreSqlOperationOutcome> ReconcileStopAsync(TIdentity identity, CancellationToken cancellationToken = default);
}
