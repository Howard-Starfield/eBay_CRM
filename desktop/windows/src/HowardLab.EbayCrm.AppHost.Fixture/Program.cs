using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using HowardLab.EbayCrm.AppHost.Fixture;

if (args.Length == 0)
{
    return 2;
}

if (FixtureModeParser.TryParse(args[0], out var fixtureMode) &&
    fixtureMode is FixtureMode.Server or FixtureMode.Worker or FixtureMode.IgnoreShutdown or
        FixtureMode.CrashAfterHello or FixtureMode.CrashBeforeHello or FixtureMode.HealthMismatch or
        FixtureMode.PipeTimeout or FixtureMode.ControlDisconnect or FixtureMode.DrainDisconnectAfterAccepted or
        FixtureMode.ShutdownDisconnectAfterAccepted or FixtureMode.HealthDrop or
        FixtureMode.HealthStaleBuild or FixtureMode.HealthStaleProtocol or FixtureMode.HealthStaleGeneration or
        FixtureMode.HealthStaleNonce or FixtureMode.HealthUnhealthy)
{
    return await FixtureControlLoop.RunAsync(fixtureMode, args[1..]);
}

if (FixtureModeParser.TryParse(args[0], out fixtureMode) &&
    fixtureMode is FixtureMode.Grandchild or FixtureMode.EchoLaunch or FixtureMode.FloodOutput or FixtureMode.HoldJobHandle)
{
    return await FixtureStandaloneModes.RunAsync(fixtureMode, args[1..]);
}

switch (args[0])
{
    case "control-connect":
        if (args.Length != 2)
        {
            return 5;
        }

        var pipeName = RequireEnvironment("HOWARDLAB_APPHOST_CONTROL_PIPE");
        var nonce = RequireEnvironment("HOWARDLAB_APPHOST_CONTROL_NONCE");
        var role = Enum.Parse<RuntimeRole>(RequireEnvironment("HOWARDLAB_APPHOST_CONTROL_ROLE"));
        var generation = long.Parse(RequireEnvironment("HOWARDLAB_APPHOST_CONTROL_GENERATION"), CultureInfo.InvariantCulture);
        var operation = Guid.Parse(RequireEnvironment("HOWARDLAB_APPHOST_CONTROL_OPERATION"));
        var build = RequireEnvironment("HOWARDLAB_APPHOST_CONTROL_BUILD");
        var processId = int.TryParse(
            Environment.GetEnvironmentVariable("TASK6_SPOOF_PROCESS_ID"),
            CultureInfo.InvariantCulture,
            out var spoofProcessId)
            ? spoofProcessId
            : Environment.ProcessId;
        using (var currentProcess = Process.GetCurrentProcess())
        {
            var creationTicks = long.TryParse(
                Environment.GetEnvironmentVariable("TASK6_SPOOF_CREATION_TICKS"),
                CultureInfo.InvariantCulture,
                out var spoofCreationTicks)
                ? spoofCreationTicks
                : currentProcess.StartTime.ToUniversalTime().Ticks;
            switch (args[1])
            {
                case "wrong-nonce":
                    nonce += "x";
                    break;
                case "wrong-time":
                    creationTicks--;
                    break;
                case "wrong-build":
                    build += "-wrong";
                    break;
                case "old-operation":
                    operation = Guid.NewGuid();
                    break;
                case "stale-generation":
                    generation--;
                    break;
                case "shutdown-roundtrip":
                case "child-shutdown":
                case "frame-budget":
                    break;
                case "oversize":
                    using (var rawClient = new NamedPipeClientStream(
                        ".",
                        pipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous))
                    {
                        await rawClient.ConnectAsync(5_000);
                        var prefix = new byte[sizeof(uint)];
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            prefix,
                            ControlProtocolConstants.MaxFrameBytes + 1u);
                        await rawClient.WriteAsync(prefix);
                        await rawClient.FlushAsync();
                        await Task.Delay(Timeout.InfiniteTimeSpan);
                    }

                    return 0;
                case not "valid":
                    return 6;
            }

            var payload = JsonSerializer.SerializeToElement(
                new HelloPayload(
                    processId,
                    ProcessCreationTimeTicks.Format(creationTicks),
                    nonce,
                    build,
                    LoopbackEndpoint: null,
                    ChallengeId: "pending-challenge"),
                ControlFrameCodec.SerializerOptions);
            var hello = new ControlEnvelope(
                ControlProtocolConstants.CurrentVersion,
                operation,
                role,
                generation,
                ControlMessageType.Hello,
                payload);
            if (args[1] == "child-shutdown")
            {
                await using var adversarialClient = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await adversarialClient.ConnectAsync(5_000);
                var codec = new ControlFrameCodec();
                var challengeEnvelope = await codec.ReadAsync(adversarialClient);
                var challenge = challengeEnvelope.Payload.Deserialize<IdentityChallengePayload>(
                    ControlFrameCodec.SerializerOptions)
                    ?? throw new InvalidDataException("Identity challenge payload is missing.");
                var helloIdentity = payload.Deserialize<HelloPayload>(ControlFrameCodec.SerializerOptions)
                    ?? throw new InvalidDataException("Hello payload is missing.");
                var challengeBoundHello = hello with
                {
                    Payload = JsonSerializer.SerializeToElement(
                        helloIdentity with { ChallengeId = challenge.ChallengeId },
                        ControlFrameCodec.SerializerOptions),
                };
                await codec.WriteAsync(adversarialClient, challengeBoundHello);
                await adversarialClient.FlushAsync();
                await codec.WriteAsync(
                    adversarialClient,
                    CreateEmptyControlEnvelope(
                        ControlMessageType.Shutdown,
                        Guid.NewGuid(),
                        role,
                        generation));
                await adversarialClient.FlushAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;
            }

            await using var client = new NamedPipeControlClient(
                pipeName,
                hello,
                TimeSpan.FromSeconds(5));
            await client.ConnectAsync();
            if (args[1] == "shutdown-roundtrip")
            {
                var shutdown = await client.ReadAsync();
                await client.SendAsync(CreateEmptyControlEnvelope(
                    ControlMessageType.ShutdownAccepted,
                    shutdown.OperationId,
                    role,
                    generation));
            }
            else if (args[1] == "frame-budget")
            {
                var shutdown = await client.ReadAsync();
                await client.SendAsync(CreateEmptyControlEnvelope(
                    ControlMessageType.ShutdownAccepted,
                    shutdown.OperationId,
                    role,
                    generation));
                for (var index = 0; index < 1_021; index++)
                {
                    _ = await client.ReadAsync();
                }
            }

            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        return 0;

    case "echo-launch":
        var environmentNames = Environment.GetEnvironmentVariables()
            .Keys
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Arguments = args[1..],
            EnvironmentNames = environmentNames,
        }));
        return 0;

    case "output":
        Console.Out.WriteLine("stdout-line");
        Console.Error.WriteLine("stderr-line");
        return 0;

    case "output-secret":
        var secret = Environment.GetEnvironmentVariable("TASK5_SECRET")
            ?? throw new InvalidOperationException("The fixture secret is unavailable.");
        Console.Out.WriteLine(secret);
        Console.Error.WriteLine(secret);
        return 0;

    case "stdin-echo":
        Console.WriteLine(await Console.In.ReadToEndAsync());
        return 0;

    case "exit-without-stdin":
        return 0;

    case "output-before-stdin":
        Console.Write(new string('x', 128 * 1024));
        Console.Out.Flush();
        var received = await Console.In.ReadToEndAsync();
        Console.WriteLine();
        Console.WriteLine(received.Length);
        return 0;

    case "migration-host":
        if (args.Length != 7)
        {
            return 7;
        }
        var migrationPasswordText = RequireEnvironment("TASK8_PG_PASSWORD");
        var migrationPassword = new SecretValue(migrationPasswordText);
        Environment.SetEnvironmentVariable("TASK8_PG_PASSWORD", null);
        var migrationLayout = PostgresBinaryLayout.Validate(Path.GetFullPath(args[1]));
        var migrationPaths = PostgresClusterPaths.Create(Path.GetFullPath(args[2]));
        var migrationPort = int.Parse(args[3], CultureInfo.InvariantCulture);
        var migrationClusterId = Guid.ParseExact(args[4], "D");
        var migrationScript = Path.GetFullPath(args[5]);
        var migrationGate = Path.GetFullPath(args[6]);
        using (var databaseJob = WindowsJobObject.CreateKillOnClose())
        {
            var migrationGeneration = new ProcessGeneration(RuntimeRole.Database, 1, Guid.NewGuid());
            var gatedLauncher = new MigrationGateLauncher(
                new WindowsProcessLauncher(FixtureDiagnosticSink.Instance),
                migrationGate);
            await using var migrationRuntime = new PostgresRuntime(
                migrationLayout,
                migrationPaths,
                migrationGeneration,
                migrationPort,
                migrationPassword,
                gatedLauncher,
                databaseJob,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));
            _ = await migrationRuntime.InitializeAsync();
            var migrationStarted = await migrationRuntime.StartAsync();
            var migrationIdentity = migrationStarted.Identity
                ?? throw new InvalidOperationException("The fixture postmaster did not start.");
            gatedLauncher.PostmasterProcessId = migrationIdentity.ProcessId;
            var migrationRunner = new PostgresMigrationRunner(
                migrationRuntime,
                migrationIdentity,
                new AtomicMigrationAttemptStore(args[2]),
                migrationClusterId,
                new Version(1, 0, 0),
                0,
                1,
                migrationScript,
                TimeSpan.FromMinutes(10));
            _ = await migrationRunner.RunAsync();
        }
        return 0;

    case "orphan-output-handles":
        var orphanStartInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath
                ?? throw new InvalidOperationException("The fixture executable path is unavailable."),
            UseShellExecute = false,
        };
        orphanStartInfo.ArgumentList.Add("hold");
        using (var orphan = Process.Start(orphanStartInfo)
            ?? throw new InvalidOperationException("Could not start the output-handle fixture."))
        {
            if (args.Length == 2)
            {
                File.WriteAllText(args[1], JsonSerializer.Serialize(new
                {
                    ProcessId = orphan.Id,
                    CreationTimeUtcTicks = orphan.StartTime.ToUniversalTime().Ticks,
                }));
            }
        }

        return 0;

    case "probe-inherited-handle":
        if (args.Length != 3 || !long.TryParse(args[1], out var inheritedHandleValue))
        {
            return 4;
        }
        if (FixtureHandleProbe.IsValid(new IntPtr(inheritedHandleValue)))
        {
            File.WriteAllText(args[2], "inherited");
        }
        return 0;

    case "announce-tree-hold":
        if (args.Length != 3)
        {
            return 4;
        }

        var treeStartInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath
                ?? throw new InvalidOperationException("The fixture executable path is unavailable."),
            UseShellExecute = false,
        };
        treeStartInfo.ArgumentList.Add("hold");
        using (var treeChild = Process.Start(treeStartInfo)
            ?? throw new InvalidOperationException("Could not start the fixture tree child."))
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(new
            {
                ProcessId = Environment.ProcessId,
                GrandchildProcessId = treeChild.Id,
            }));
        }

        using (var ready = EventWaitHandle.OpenExisting(args[2]))
        {
            ready.Set();
        }

        Thread.Sleep(Timeout.Infinite);
        return 0;

    case "immediate-grandchild":
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath
                ?? throw new InvalidOperationException("The fixture executable path is unavailable."),
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("hold");
        using (var grandchild = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the fixture grandchild."))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ProcessId = grandchild.Id }));
            Console.Out.Flush();
        }

        Thread.Sleep(Timeout.Infinite);
        return 0;

    case "hold":
        Thread.Sleep(Timeout.Infinite);
        return 0;

    default:
        return 3;
}

static string RequireEnvironment(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Required fixture environment variable '{name}' is unavailable.");

static ControlEnvelope CreateEmptyControlEnvelope(
    ControlMessageType type,
    Guid operationId,
    RuntimeRole role,
    long generation) =>
    new(
        ControlProtocolConstants.CurrentVersion,
        operationId,
        role,
        generation,
        type,
    JsonSerializer.SerializeToElement(new { }, ControlFrameCodec.SerializerOptions));

static class FixtureHandleProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);

    internal static bool IsValid(IntPtr handle) => GetHandleInformation(handle, out _);
}

sealed class MigrationGateLauncher(IProcessLauncher inner, string gatePath) : IProcessLauncher
{
    internal int PostmasterProcessId { get; set; }

    public async ValueTask<ISupervisedProcess> LaunchAsync(
        LaunchSpecification specification,
        IProcessGroup processGroup,
        CancellationToken cancellationToken)
    {
        var process = await inner.LaunchAsync(specification, processGroup, cancellationToken);
        if (!specification.Arguments.Any(argument =>
            argument.StartsWith("expected_cluster_id=", StringComparison.OrdinalIgnoreCase)))
        {
            return process;
        }

        var temporaryGate = gatePath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryGate,
            $"{PostmasterProcessId.ToString(CultureInfo.InvariantCulture)}|" +
            process.Identity.ProcessId.ToString(CultureInfo.InvariantCulture),
            CancellationToken.None);
        File.Move(temporaryGate, gatePath, overwrite: true);
        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        return process;
    }
}

sealed class FixtureDiagnosticSink : IDiagnosticSink
{
    internal static FixtureDiagnosticSink Instance { get; } = new();

    public ValueTask WriteAsync(
        DiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
