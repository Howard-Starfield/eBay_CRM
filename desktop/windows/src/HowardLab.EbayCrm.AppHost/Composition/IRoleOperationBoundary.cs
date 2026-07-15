using HowardLab.EbayCrm.AppHost.Core.Lifecycle;

namespace HowardLab.EbayCrm.AppHost.Composition;

internal enum RoleOperationBoundaryPoint
{
    StartAcceptInFlight,
    StopAccepted,
}

internal interface IRoleOperationBoundary
{
    ValueTask PauseAsync(
        RoleOperationBoundaryPoint point,
        ProcessGeneration generation,
        Guid operationId,
        Task pendingOperation,
        CancellationToken roleLifetimeToken);
}

internal sealed class NoopRoleOperationBoundary : IRoleOperationBoundary
{
    internal static NoopRoleOperationBoundary Instance { get; } = new();

    public ValueTask PauseAsync(
        RoleOperationBoundaryPoint point,
        ProcessGeneration generation,
        Guid operationId,
        Task pendingOperation,
        CancellationToken roleLifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(pendingOperation);
        roleLifetimeToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed record RoleOperationDeadlines
{
    internal static RoleOperationDeadlines Production { get; } = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMilliseconds(100));

    internal RoleOperationDeadlines(
        TimeSpan startCommand,
        TimeSpan stopCommand,
        TimeSpan reconciliation,
        TimeSpan readiness,
        TimeSpan readinessPollInterval)
    {
        if (startCommand <= TimeSpan.Zero ||
            stopCommand <= TimeSpan.Zero ||
            reconciliation <= TimeSpan.Zero ||
            readiness <= TimeSpan.Zero ||
            readinessPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startCommand));
        }

        StartCommand = startCommand;
        StopCommand = stopCommand;
        Reconciliation = reconciliation;
        Readiness = readiness;
        ReadinessPollInterval = readinessPollInterval;
    }

    internal TimeSpan StartCommand { get; }

    internal TimeSpan StopCommand { get; }

    internal TimeSpan Reconciliation { get; }

    internal TimeSpan Readiness { get; }

    internal TimeSpan ReadinessPollInterval { get; }
}
