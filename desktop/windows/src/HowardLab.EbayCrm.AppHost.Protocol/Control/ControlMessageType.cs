namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

public enum ControlMessageType
{
    IdentityChallenge,
    Hello,
    Drain,
    DrainAccepted,
    NoNewWorkAcquisition,
    ActiveWorkRemaining,
    Drained,
    Shutdown,
    ShutdownAccepted,
    Stopped,
    Health,
}
