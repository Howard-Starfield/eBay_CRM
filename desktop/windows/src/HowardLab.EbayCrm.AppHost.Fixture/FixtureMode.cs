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
    HealthDrop,
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
            "health-drop" => FixtureMode.HealthDrop,
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
            "health-drop" or
            "hold-job-handle" or
            "migration-host";
    }
}
