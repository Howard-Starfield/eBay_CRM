namespace HowardLab.EbayCrm.AppHost.Core.Lifecycle;

public sealed record LifecycleCommand(
    LifecycleCommandType Type,
    ProcessGeneration? Generation,
    Guid OperationId,
    LifecycleDeadlineKey DeadlineKey,
    string? ReasonCode = null);

public enum LifecycleCommandType
{
    AcquireInstance,
    ValidatePayload,
    PrepareRuntime,
    StartDatabase,
    WaitForDatabase,
    RunMigrations,
    StartServer,
    WaitForServer,
    StartWorker,
    WaitForWorker,
    DrainWorker,
    StopWorker,
    StopServer,
    StopDatabaseFast,
    ReconcileDatabaseStart,
    ReconcileDatabaseStop,
    ReleaseInstance,
    EscalateJob,
    EnterFault,
}

public enum LifecycleDeadlineKey
{
    None,
    InstanceAcquisition,
    PayloadValidation,
    RuntimePreparation,
    DatabaseStart,
    DatabaseReadiness,
    Migration,
    ServerStart,
    ServerReadiness,
    WorkerStart,
    WorkerReadiness,
    WorkerDrain,
    WorkerStop,
    ServerStop,
    DatabaseStop,
    DatabaseReconciliation,
}
