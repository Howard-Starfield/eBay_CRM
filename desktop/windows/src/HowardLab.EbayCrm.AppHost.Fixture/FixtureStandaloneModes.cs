using System.Diagnostics;
using System.Text.Json;

namespace HowardLab.EbayCrm.AppHost.Fixture;

internal static class FixtureStandaloneModes
{
    internal static async Task<int> RunAsync(FixtureMode mode, string[] arguments)
    {
        switch (mode)
        {
            case FixtureMode.Grandchild:
                return await RunGrandchildAsync().ConfigureAwait(false);
            case FixtureMode.EchoLaunch:
                return RunEchoLaunch(arguments);
            case FixtureMode.FloodOutput:
                return await RunFloodOutputAsync().ConfigureAwait(false);
            case FixtureMode.HoldJobHandle:
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return 0;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static async Task<int> RunGrandchildAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath
                ?? throw new InvalidOperationException("The fixture executable path is unavailable."),
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("hold-job-handle");
        using var grandchild = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the fixture grandchild.");
        Console.WriteLine(JsonSerializer.Serialize(new { ProcessId = grandchild.Id }));
        Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 0;
    }

    private static int RunEchoLaunch(string[] arguments)
    {
        var environmentNames = Environment.GetEnvironmentVariables()
            .Keys
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Arguments = arguments,
            EnvironmentNames = environmentNames,
        }));
        return 0;
    }

    private static async Task<int> RunFloodOutputAsync()
    {
        Console.Write(new string('x', 128 * 1024));
        Console.Out.Flush();
        var received = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine(received.Length);
        return 0;
    }
}
