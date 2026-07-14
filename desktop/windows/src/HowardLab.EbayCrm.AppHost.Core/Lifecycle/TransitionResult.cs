namespace HowardLab.EbayCrm.AppHost.Core.Lifecycle;

public sealed record TransitionResult(
    RuntimeState Previous,
    RuntimeState Current,
    IReadOnlyList<LifecycleCommand> Commands,
    bool Ignored,
    string ReasonCode);
