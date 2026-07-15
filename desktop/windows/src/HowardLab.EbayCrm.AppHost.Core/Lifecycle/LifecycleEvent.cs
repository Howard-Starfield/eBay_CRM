namespace HowardLab.EbayCrm.AppHost.Core.Lifecycle;

public abstract record LifecycleEvent(ProcessGeneration? Generation);

public sealed record StartRequested(Guid OperationId) : LifecycleEvent((ProcessGeneration?)null);

public sealed record StopRequested(Guid OperationId) : LifecycleEvent((ProcessGeneration?)null);

public sealed record InstanceAcquired(Guid OperationId) : LifecycleEvent((ProcessGeneration?)null);

public sealed record PayloadValidated(Guid OperationId) : LifecycleEvent((ProcessGeneration?)null);

public sealed record RuntimePrepared(Guid OperationId) : LifecycleEvent((ProcessGeneration?)null);

public sealed record RoleStarted(ProcessGeneration Value) : LifecycleEvent(Value);

public sealed record RoleReady(ProcessGeneration Value) : LifecycleEvent(Value);

public sealed record RoleExited(ProcessGeneration Value, int ExitCode) : LifecycleEvent(Value);

public sealed record HealthFailed(ProcessGeneration Value, string ReasonCode) : LifecycleEvent(Value);

public sealed record ControlDisconnected(ProcessGeneration Value) : LifecycleEvent(Value);

public sealed record OperationTimedOut(ProcessGeneration Value, Guid OperationId) : LifecycleEvent(Value);

public sealed record Reconciled(ProcessGeneration Value, ReconciledState State) : LifecycleEvent(Value);

public sealed record MigrationCompleted(Guid OperationId) : LifecycleEvent((ProcessGeneration?)null);

public sealed record ShutdownCompleted(Guid OperationId) : LifecycleEvent((ProcessGeneration?)null);

public sealed record ShutdownFailed(Guid OperationId, string ReasonCode) : LifecycleEvent((ProcessGeneration?)null);

public sealed record RecoveryFailed(string ReasonCode) : LifecycleEvent((ProcessGeneration?)null);

public enum ReconciledState
{
    Running,
    Stopped,
    Unknown,
}
