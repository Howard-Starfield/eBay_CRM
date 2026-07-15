namespace HowardLab.EbayCrm.AppHost.Fixture;

public enum FixtureMode
{
    Server,
    Worker,
    Grandchild,
    EchoLaunch,
    FloodOutput,
    IgnoreShutdown,
    CrashAfterHello,
    CrashBeforeHello,
    HealthMismatch,
    PipeTimeout,
    ControlDisconnect,
    DrainDisconnectAfterAccepted,
    ShutdownDisconnectAfterAccepted,
    HealthDrop,
    HealthStaleBuild,
    HealthStaleProtocol,
    HealthStaleGeneration,
    HealthStaleNonce,
    HealthUnhealthy,
    HoldJobHandle,
    MigrationHost,
}

public static class FixtureModeParser
{
    public static bool TryParse(string value, out FixtureMode mode)
    {
        mode = value switch
        {
            "server" => FixtureMode.Server,
            "worker" => FixtureMode.Worker,
            "grandchild" => FixtureMode.Grandchild,
            "echo-launch" => FixtureMode.EchoLaunch,
            "flood-output" => FixtureMode.FloodOutput,
            "ignore-shutdown" => FixtureMode.IgnoreShutdown,
            "crash-after-hello" => FixtureMode.CrashAfterHello,
            "crash-before-hello" => FixtureMode.CrashBeforeHello,
            "health-mismatch" => FixtureMode.HealthMismatch,
            "pipe-timeout" => FixtureMode.PipeTimeout,
            "control-disconnect" => FixtureMode.ControlDisconnect,
            "drain-disconnect-after-accepted" => FixtureMode.DrainDisconnectAfterAccepted,
            "shutdown-disconnect-after-accepted" => FixtureMode.ShutdownDisconnectAfterAccepted,
            "health-drop" => FixtureMode.HealthDrop,
            "health-stale-build" => FixtureMode.HealthStaleBuild,
            "health-stale-protocol" => FixtureMode.HealthStaleProtocol,
            "health-stale-generation" => FixtureMode.HealthStaleGeneration,
            "health-stale-nonce" => FixtureMode.HealthStaleNonce,
            "health-unhealthy" => FixtureMode.HealthUnhealthy,
            "hold-job-handle" => FixtureMode.HoldJobHandle,
            "migration-host" => FixtureMode.MigrationHost,
            _ => default,
        };
        return value is
            "server" or
            "worker" or
            "grandchild" or
            "echo-launch" or
            "flood-output" or
            "ignore-shutdown" or
            "crash-after-hello" or
            "crash-before-hello" or
            "health-mismatch" or
            "pipe-timeout" or
            "control-disconnect" or
            "drain-disconnect-after-accepted" or
            "shutdown-disconnect-after-accepted" or
            "health-drop" or
            "health-stale-build" or
            "health-stale-protocol" or
            "health-stale-generation" or
            "health-stale-nonce" or
            "health-unhealthy" or
            "hold-job-handle" or
            "migration-host";
    }
}
