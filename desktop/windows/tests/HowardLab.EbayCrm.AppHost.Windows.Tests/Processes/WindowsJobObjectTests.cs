using System.Diagnostics;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Windows.Processes;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Processes;

public sealed class WindowsJobObjectTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DisposingOnlyJobHandle_KillsImmediateGrandchild()
    {
        var job = WindowsJobObject.CreateKillOnClose();
        await using var launched = await WindowsProcessLauncherTests.CreateLauncher().LaunchAsync(
            WindowsProcessLauncherTests.CreateSpecification(["immediate-grandchild"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);

        var announcement = await WaitForJsonLineAsync<GrandchildAnnouncement>(
            process.StandardOutput.Snapshot,
            CancellationToken.None);
        using var grandchild = Process.GetProcessById(announcement.ProcessId);
        _ = grandchild.SafeHandle;
        Assert.True(job.Contains(process.ProcessHandle));
        Assert.True(job.Contains(grandchild.SafeHandle));

        job.Dispose();

        await process.Completion.WaitAsync(Deadline);
        await grandchild.WaitForExitAsync()
            .WaitAsync(Deadline);
    }

    [Fact]
    public async Task DuplicateJobHandle_PreventsKillUntilFinalHandleCloses()
    {
        var job = WindowsJobObject.CreateKillOnClose();
        using var duplicate = job.DuplicateForTests();
        await using var launched = await WindowsProcessLauncherTests.CreateLauncher().LaunchAsync(
            WindowsProcessLauncherTests.CreateSpecification(["hold"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);

        job.Dispose();

        Assert.False(process.ProcessHandle.IsClosed);
        Assert.False(process.HasExited);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await process.Completion.WaitAsync(TimeSpan.FromMilliseconds(250)));

        duplicate.Dispose();

        await process.Completion.WaitAsync(Deadline);
    }

    private static async Task<T> WaitForJsonLineAsync<T>(
        Func<string> snapshot,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = snapshot();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var value = JsonSerializer.Deserialize<T>(text);
                if (value is not null)
                {
                    return value;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        throw new TimeoutException("The fixture did not publish its grandchild identity before the deadline.");
    }

    private sealed record GrandchildAnnouncement(int ProcessId);
}
