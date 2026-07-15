namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

public enum ControlDirection
{
    AppHostToChild,
    ChildToAppHost,
}

public static class ControlDirectionPolicy
{
    public static bool IsAllowed(
        RuntimeRole role,
        ControlMessageType type,
        ControlDirection direction) =>
        (role, direction, type) switch
        {
            (RuntimeRole.Server, ControlDirection.AppHostToChild,
                ControlMessageType.IdentityChallenge or ControlMessageType.Shutdown) => true,
            (RuntimeRole.Server, ControlDirection.ChildToAppHost,
                ControlMessageType.Hello or
                ControlMessageType.Health or
                ControlMessageType.ShutdownAccepted or
                ControlMessageType.Stopped) => true,
            (RuntimeRole.Worker, ControlDirection.AppHostToChild,
                ControlMessageType.IdentityChallenge or
                ControlMessageType.Drain or
                ControlMessageType.Shutdown) => true,
            (RuntimeRole.Worker, ControlDirection.ChildToAppHost,
                ControlMessageType.Hello or
                ControlMessageType.Health or
                ControlMessageType.DrainAccepted or
                ControlMessageType.NoNewWorkAcquisition or
                ControlMessageType.ActiveWorkRemaining or
                ControlMessageType.Drained or
                ControlMessageType.ShutdownAccepted or
                ControlMessageType.Stopped) => true,
            _ => false,
        };
}
