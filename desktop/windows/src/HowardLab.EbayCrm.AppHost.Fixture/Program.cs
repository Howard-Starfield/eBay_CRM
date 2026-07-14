using System.Diagnostics;
using System.Text.Json;

if (args.Length == 0)
{
    return 2;
}

switch (args[0])
{
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
