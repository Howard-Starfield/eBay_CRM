namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

public enum ControlMessageType
{
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
