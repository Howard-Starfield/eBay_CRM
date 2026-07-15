namespace HowardLab.EbayCrm.AppHost.Core.Migrations;

public interface IMigrationAttemptStore
{
    ValueTask<MigrationAttemptRecord?> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask WriteAsync(MigrationAttemptRecord record, CancellationToken cancellationToken = default);
}
