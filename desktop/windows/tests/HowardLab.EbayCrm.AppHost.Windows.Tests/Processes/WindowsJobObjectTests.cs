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

        await process.StandardOutputLineAvailable.WaitAsync(Deadline);
        var announcement = JsonSerializer.Deserialize<GrandchildAnnouncement>(
            process.StandardOutput.Snapshot());
        Assert.NotNull(announcement);
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

    [Fact]
    public async Task DocumentedGrandchildAndHeldJobHandleModes_AreContainedByTheJob()
    {
        var job = WindowsJobObject.CreateKillOnClose();
        await using var launched = await WindowsProcessLauncherTests.CreateLauncher().LaunchAsync(
            WindowsProcessLauncherTests.CreateSpecification(["grandchild"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);

        await process.StandardOutputLineAvailable.WaitAsync(Deadline);
        var announcement = JsonSerializer.Deserialize<GrandchildAnnouncement>(
            process.StandardOutput.Snapshot());
        Assert.NotNull(announcement);
        using var grandchild = Process.GetProcessById(announcement.ProcessId);
        _ = grandchild.SafeHandle;
        Assert.True(job.Contains(grandchild.SafeHandle));

        job.Dispose();

        await process.Completion.WaitAsync(Deadline);
        await grandchild.WaitForExitAsync().WaitAsync(Deadline);

        using var heldJob = WindowsJobObject.CreateKillOnClose();
        await using var held = await WindowsProcessLauncherTests.CreateLauncher().LaunchAsync(
            WindowsProcessLauncherTests.CreateSpecification(["hold-job-handle"]),
            heldJob,
            CancellationToken.None);
        var heldProcess = Assert.IsType<WindowsSupervisedProcess>(held);
        heldJob.DuplicateIntoProcessForTests(heldProcess.ProcessHandle);
        heldJob.Dispose();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await held.Completion.WaitAsync(TimeSpan.FromMilliseconds(250)));
        Assert.False(heldProcess.HasExited);

        using var holder = Process.GetProcessById(heldProcess.Identity.ProcessId);
        using var forceDeadline = new CancellationTokenSource(Deadline);
        heldProcess.TerminateAndForceCloseAfterJobClose(forceDeadline.Token);
        Assert.True(holder.HasExited);
    }

    private sealed record GrandchildAnnouncement(int ProcessId);
}
