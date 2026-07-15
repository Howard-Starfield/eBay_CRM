namespace HowardLab.EbayCrm.AppHost.Core.Migrations;

public enum MigrationOutcome
{
    Succeeded,
    ExplicitRetryAllowed,
    Failed,
    RepairRequired,
}

public sealed record MigrationClassificationInput(
    MigrationAttemptState MarkerState,
    int? ProcessExitCode,
    int StartingSchemaVersion,
    int TargetSchemaVersion,
    int ActualSchemaVersion);

public sealed record MigrationClassification(MigrationOutcome Outcome, string ReasonCode);

public static class MigrationOutcomeClassifier
{
    public static MigrationClassification Classify(MigrationClassificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Enum.IsDefined(input.MarkerState)) throw new ArgumentOutOfRangeException(nameof(input));
        if (input.StartingSchemaVersion < 0 || input.TargetSchemaVersion <= input.StartingSchemaVersion)
            throw new ArgumentException("The migration version range is invalid.", nameof(input));
        if (input.ActualSchemaVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(input));

        if (input.ActualSchemaVersion == input.TargetSchemaVersion)
        {
            return input.MarkerState == MigrationAttemptState.Running
                ? new(MigrationOutcome.Succeeded, "migration-commit-recovered")
                : new(MigrationOutcome.Succeeded, "migration-target-verified");
        }

        if (input.MarkerState == MigrationAttemptState.Running)
        {
            return input.ActualSchemaVersion == input.StartingSchemaVersion
                ? new(MigrationOutcome.ExplicitRetryAllowed, "migration-not-applied-retry-allowed")
                : new(MigrationOutcome.RepairRequired, "migration-schema-indeterminate");
        }

        if (input.ProcessExitCode == 0)
            return new(MigrationOutcome.Failed, "migration-exit-success-schema-mismatch");

        return new(MigrationOutcome.Failed, "migration-process-failed");
    }
}
