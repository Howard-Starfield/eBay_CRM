using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Processes;

public sealed class WindowsProcessCleanupTests
{
    private static readonly TimeSpan TestCleanupDeadline = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task LaunchFailure_WhenProcessTerminationFails_EscalatesJobAndReleasesHandle()
    {
        var announcementPath = Path.Combine(Path.GetTempPath(), $"task5-{Guid.NewGuid():N}.json");
        var eventName = $"Local\\HowardLab.Task5.{Guid.NewGuid():N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var native = new EscalatingCleanupNative(terminateProcessSucceeds: false, forceFirstWaitTimeout: false);
        var cleanup = new BoundedProcessCleanup(native, TestCleanupDeadline);
        var verifier = new SignaledFailureVerifier(ready, announcementPath, nativeErrorCode: 123);
        using var job = WindowsJobObject.CreateKillOnClose();
        var launcher = CreateLauncher(cleanup, verifier);
        var specification = WindowsProcessLauncherTests.CreateSpecification(
            ["announce-tree-hold", announcementPath, eventName]);

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<ProcessLaunchException>(async () =>
                await launcher.LaunchAsync(specification, job, CancellationToken.None));
            stopwatch.Stop();

            using var parent = Assert.IsType<Process>(verifier.Parent);
            using var grandchild = Assert.IsType<Process>(verifier.Grandchild);
            await parent.WaitForExitAsync().WaitAsync(TestDeadline);
            await grandchild.WaitForExitAsync().WaitAsync(TestDeadline);

            Assert.Equal(123, error.Win32ErrorCode);
            Assert.True(stopwatch.Elapsed < TestDeadline);
            Assert.Equal(1, native.TerminateProcessCallCount);
            Assert.Equal(1, native.TerminateJobCallCount);
            Assert.NotNull(native.CapturedProcessHandle);
            Assert.True(native.CapturedProcessHandle.IsClosed);
            Assert.True(parent.HasExited);
            Assert.True(grandchild.HasExited);
        }
        finally
        {
            File.Delete(announcementPath);
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenProcessDoesNotSignal_EscalatesJobWithinDeadline()
    {
        var native = new EscalatingCleanupNative(terminateProcessSucceeds: true, forceFirstWaitTimeout: true);
        var cleanup = new BoundedProcessCleanup(native, TestCleanupDeadline);
        using var job = WindowsJobObject.CreateKillOnClose();
        var launcher = CreateLauncher(cleanup, WindowsProcessIdentityVerifier.Instance);
        var launched = await launcher.LaunchAsync(
            WindowsProcessLauncherTests.CreateSpecification(["immediate-grandchild"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);
        await process.StandardOutputLineAvailable.WaitAsync(TestDeadline);
        var announcement = JsonSerializer.Deserialize<GrandchildAnnouncement>(process.StandardOutput.Snapshot());
        Assert.NotNull(announcement);
        using var grandchild = Process.GetProcessById(announcement.ProcessId);
        _ = grandchild.SafeHandle;

        var stopwatch = Stopwatch.StartNew();
        await process.DisposeAsync().AsTask().WaitAsync(TestDeadline);
        stopwatch.Stop();
        await grandchild.WaitForExitAsync().WaitAsync(TestDeadline);

        Assert.True(stopwatch.Elapsed < TestDeadline);
        Assert.Equal(1, native.TerminateProcessCallCount);
        Assert.Equal(1, native.TerminateJobCallCount);
        Assert.Equal(2, native.WaitCallCount);
        Assert.True(process.ProcessHandle.IsClosed);
        Assert.True(grandchild.HasExited);
    }

    [Fact]
    public async Task ConcurrentDisposeAsync_CallersShareActiveCleanupTask()
    {
        var inner = new BoundedProcessCleanup(NativeProcessCleanup.Instance, TestCleanupDeadline);
        var gated = new GatedCleanupPolicy(inner);
        using var job = WindowsJobObject.CreateKillOnClose();
        var launcher = CreateLauncher(gated, WindowsProcessIdentityVerifier.Instance);
        var launched = await launcher.LaunchAsync(
            WindowsProcessLauncherTests.CreateSpecification(["hold"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);

        var first = process.DisposeAsync().AsTask();
        await gated.Entered.Task.WaitAsync(TestDeadline);
        var second = process.DisposeAsync().AsTask();

        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        gated.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TestDeadline);
        Assert.Equal(1, gated.CallCount);
    }

    [Fact]
    public async Task DisposeAsync_WhenEscalationCannotSignal_ReleasesHandleAndPreservesJobContainment()
    {
        var native = new NonTerminatingCleanupNative();
        var cleanup = new BoundedProcessCleanup(native, TimeSpan.FromMilliseconds(100));
        var job = WindowsJobObject.CreateKillOnClose();
        var launcher = CreateLauncher(cleanup, WindowsProcessIdentityVerifier.Instance);
        var launched = await launcher.LaunchAsync(
            WindowsProcessLauncherTests.CreateSpecification(["hold"]),
            job,
            CancellationToken.None);
        var process = Assert.IsType<WindowsSupervisedProcess>(launched);
        using var retained = Process.GetProcessById(process.Identity.ProcessId);
        _ = retained.SafeHandle;

        try
        {
            var error = await Assert.ThrowsAsync<ProcessCleanupException>(async () =>
                await process.DisposeAsync());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await process.Completion.WaitAsync(TestDeadline));

            Assert.Equal(5, error.ProcessTerminationErrorCode);
            Assert.Equal(6, error.JobTerminationErrorCode);
            Assert.True(error.TimedOut);
            Assert.Equal(1, native.TerminateProcessCallCount);
            Assert.Equal(1, native.TerminateJobCallCount);
            Assert.True(process.ProcessHandle.IsClosed);
            Assert.True(job.Contains(retained.SafeHandle));
            Assert.False(retained.HasExited);
        }
        finally
        {
            job.Dispose();
            await retained.WaitForExitAsync().WaitAsync(TestDeadline);
        }
    }

    private static WindowsProcessLauncher CreateLauncher(
        IProcessCleanupPolicy cleanup,
        IWindowsProcessIdentityVerifier verifier) =>
        new(
            NoopDiagnosticSink.Instance,
            maxOutputBytes: 64 * 1024,
            maxLineBytes: 4 * 1024,
            cleanup,
            verifier);

    private sealed record TreeAnnouncement(int ProcessId, int GrandchildProcessId);

    private sealed record GrandchildAnnouncement(int ProcessId);

    private sealed class SignaledFailureVerifier : IWindowsProcessIdentityVerifier
    {
        private readonly EventWaitHandle _ready;
        private readonly string _announcementPath;
        private readonly int _nativeErrorCode;

        internal SignaledFailureVerifier(
            EventWaitHandle ready,
            string announcementPath,
            int nativeErrorCode)
        {
            _ready = ready;
            _announcementPath = announcementPath;
            _nativeErrorCode = nativeErrorCode;
        }

        internal Process? Parent { get; private set; }

        internal Process? Grandchild { get; private set; }

        public SupervisedProcessIdentity Capture(
            RuntimeRole role,
            ProcessGeneration generation,
            SafeProcessHandle processHandle)
        {
            if (!_ready.WaitOne(TestDeadline))
            {
                throw new TimeoutException("The fixture tree did not become ready.");
            }

            var announcement = JsonSerializer.Deserialize<TreeAnnouncement>(
                File.ReadAllText(_announcementPath))
                ?? throw new InvalidOperationException("The fixture tree announcement is invalid.");
            Parent = Process.GetProcessById(announcement.ProcessId);
            Grandchild = Process.GetProcessById(announcement.GrandchildProcessId);
            _ = Parent.SafeHandle;
            _ = Grandchild.SafeHandle;

            throw new Win32Exception(_nativeErrorCode);
        }
    }

    private sealed class EscalatingCleanupNative : IProcessCleanupNative
    {
        private readonly bool _terminateProcessSucceeds;
        private readonly bool _forceFirstWaitTimeout;

        internal EscalatingCleanupNative(bool terminateProcessSucceeds, bool forceFirstWaitTimeout)
        {
            _terminateProcessSucceeds = terminateProcessSucceeds;
            _forceFirstWaitTimeout = forceFirstWaitTimeout;
        }

        internal int TerminateProcessCallCount { get; private set; }

        internal int TerminateJobCallCount { get; private set; }

        internal int WaitCallCount { get; private set; }

        internal SafeProcessHandle? CapturedProcessHandle { get; private set; }

        public NativeCallResult TerminateProcess(SafeProcessHandle processHandle, uint exitCode)
        {
            TerminateProcessCallCount++;
            CapturedProcessHandle = processHandle;
            return _terminateProcessSucceeds
                ? NativeCallResult.Success
                : NativeCallResult.Failure(5);
        }

        public ProcessWaitOutcome Wait(SafeProcessHandle processHandle, TimeSpan timeout)
        {
            WaitCallCount++;
            if (_forceFirstWaitTimeout && WaitCallCount == 1)
            {
                return ProcessWaitOutcome.TimedOut;
            }

            return NativeProcessCleanup.Instance.Wait(processHandle, timeout);
        }

        public NativeCallResult TerminateJob(IProcessTreeTerminator job, uint exitCode)
        {
            TerminateJobCallCount++;
            return job.TerminateTree(exitCode);
        }
    }

    private sealed class GatedCleanupPolicy : IProcessCleanupPolicy
    {
        private readonly IProcessCleanupPolicy _inner;

        internal GatedCleanupPolicy(IProcessCleanupPolicy inner)
        {
            _inner = inner;
        }

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int CallCount { get; private set; }

        public ProcessCleanupResult Cleanup(
            SafeProcessHandle processHandle,
            IProcessTreeTerminator job)
        {
            CallCount++;
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return _inner.Cleanup(processHandle, job);
        }
    }

    private sealed class NonTerminatingCleanupNative : IProcessCleanupNative
    {
        internal int TerminateProcessCallCount { get; private set; }

        internal int TerminateJobCallCount { get; private set; }

        public NativeCallResult TerminateProcess(SafeProcessHandle processHandle, uint exitCode)
        {
            TerminateProcessCallCount++;
            return NativeCallResult.Failure(5);
        }

        public ProcessWaitOutcome Wait(SafeProcessHandle processHandle, TimeSpan timeout) =>
            ProcessWaitOutcome.TimedOut;

        public NativeCallResult TerminateJob(IProcessTreeTerminator job, uint exitCode)
        {
            TerminateJobCallCount++;
            return NativeCallResult.Failure(6);
        }
    }

    private sealed class NoopDiagnosticSink : IDiagnosticSink
    {
        internal static readonly NoopDiagnosticSink Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            DiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
