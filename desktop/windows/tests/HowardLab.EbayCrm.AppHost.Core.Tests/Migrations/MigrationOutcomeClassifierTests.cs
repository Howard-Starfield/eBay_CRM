using HowardLab.EbayCrm.AppHost.Core.Migrations;

namespace HowardLab.EbayCrm.AppHost.Core.Tests.Migrations;

public sealed class MigrationOutcomeClassifierTests
{
    public static TheoryData<MigrationClassificationInput, MigrationOutcome, string> Cases => new()
    {
        { Input(MigrationAttemptState.Succeeded, 0, 2), MigrationOutcome.Succeeded, "migration-target-verified" },
        { Input(MigrationAttemptState.Failed, 7, 1), MigrationOutcome.Failed, "migration-process-failed" },
        { Input(MigrationAttemptState.Running, null, 2), MigrationOutcome.Succeeded, "migration-commit-recovered" },
        { Input(MigrationAttemptState.Running, null, 1), MigrationOutcome.ExplicitRetryAllowed, "migration-not-applied-retry-allowed" },
        { Input(MigrationAttemptState.Running, null, 7), MigrationOutcome.RepairRequired, "migration-schema-indeterminate" },
        { Input(MigrationAttemptState.Succeeded, 0, 1), MigrationOutcome.Failed, "migration-exit-success-schema-mismatch" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify_UsesMarkerProcessAndVerifiedSchema(
        MigrationClassificationInput input,
        MigrationOutcome expectedOutcome,
        string expectedReasonCode)
    {
        var result = MigrationOutcomeClassifier.Classify(input);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedReasonCode, result.ReasonCode);
    }

    [Fact]
    public void Classify_RejectsInvalidVersionRange()
    {
        var input = new MigrationClassificationInput(
            MigrationAttemptState.Running,
            ProcessExitCode: null,
            StartingSchemaVersion: 2,
            TargetSchemaVersion: 2,
            ActualSchemaVersion: 2);

        Assert.Throws<ArgumentException>(() => MigrationOutcomeClassifier.Classify(input));
    }

    private static MigrationClassificationInput Input(
        MigrationAttemptState state,
        int? exitCode,
        int actualVersion) => new(
            state,
            exitCode,
            StartingSchemaVersion: 1,
            TargetSchemaVersion: 2,
            ActualSchemaVersion: actualVersion);
}
