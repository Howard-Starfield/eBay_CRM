using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

if (args.Length == 0)
{
    return 2;
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
                new HelloPayload(processId, creationTicks, nonce, build, LoopbackEndpoint: null),
                ControlFrameCodec.SerializerOptions);
            var hello = new ControlEnvelope(
                ControlProtocolConstants.CurrentVersion,
                operation,
                role,
                generation,
                ControlMessageType.Hello,
                payload);
            await using var client = new NamedPipeControlClient(
                pipeName,
                hello,
                TimeSpan.FromSeconds(5));
            await client.ConnectAsync();
            if (args[1] == "child-shutdown")
            {
                await client.SendAsync(CreateEmptyControlEnvelope(
                    ControlMessageType.Shutdown,
                    Guid.NewGuid(),
                    role,
                    generation));
            }
            else if (args[1] == "shutdown-roundtrip")
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
