namespace HowardLab.EbayCrm.AppHost.Core.Lifecycle;

public enum RuntimeState
{
    Stopped,
    AcquiringInstance,
    ValidatingPayload,
    PreparingRuntime,
    StartingDatabase,
    WaitingForDatabase,
    Migrating,
    StartingServer,
    WaitingForServer,
    StartingWorker,
    WaitingForWorker,
    Ready,
    ReconcilingDatabaseStart,
    ReconcilingDatabaseStop,
    Stopping,
    Faulted,
}
