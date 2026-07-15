namespace HowardLab.EbayCrm.AppHost.Core.Migrations;

public sealed record MigrationAttemptRecord
{
    public const int CurrentRecordVersion = 1;

    public MigrationAttemptRecord(
        Guid operationId,
        Version appVersion,
        int startingSchemaVersion,
        int targetSchemaVersion,
        MigrationAttemptState state,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? finishedAtUtc,
        string reasonCode)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Operation ID cannot be empty.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(appVersion);
        if (appVersion.Major < 0 || appVersion.Minor < 0 || appVersion.Build < 0)
            throw new ArgumentException("App version must include nonnegative major, minor, and build components.", nameof(appVersion));
        if (startingSchemaVersion < 0) throw new ArgumentOutOfRangeException(nameof(startingSchemaVersion));
        if (targetSchemaVersion <= startingSchemaVersion) throw new ArgumentOutOfRangeException(nameof(targetSchemaVersion));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (startedAtUtc == default || startedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Started timestamp must be a non-default UTC value.", nameof(startedAtUtc));
        if (state == MigrationAttemptState.Running && finishedAtUtc is not null)
            throw new ArgumentException("A running attempt cannot have a finished timestamp.", nameof(finishedAtUtc));
        if (state != MigrationAttemptState.Running && finishedAtUtc is null)
            throw new ArgumentException("A terminal attempt requires a finished timestamp.", nameof(finishedAtUtc));
        if (finishedAtUtc is { } finished && (finished.Offset != TimeSpan.Zero || finished < startedAtUtc))
            throw new ArgumentException("Finished timestamp must be UTC and no earlier than started.", nameof(finishedAtUtc));
        if (!IsReasonCode(reasonCode)) throw new ArgumentException("Reason code is malformed.", nameof(reasonCode));

        OperationId = operationId;
        AppVersion = appVersion;
        StartingSchemaVersion = startingSchemaVersion;
        TargetSchemaVersion = targetSchemaVersion;
        State = state;
        StartedAtUtc = startedAtUtc;
        FinishedAtUtc = finishedAtUtc;
        ReasonCode = reasonCode;
    }

    public int RecordVersion => CurrentRecordVersion;
    public Guid OperationId { get; }
    public Version AppVersion { get; }
    public int StartingSchemaVersion { get; }
    public int TargetSchemaVersion { get; }
    public MigrationAttemptState State { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset? FinishedAtUtc { get; }
    public string ReasonCode { get; }

    private static bool IsReasonCode(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 96 &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
