using System.Diagnostics;
using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Processes;

internal interface IProcessCleanupPolicy
{
    ProcessCleanupResult Cleanup(
        SafeProcessHandle processHandle,
        IProcessTreeTerminator job);
}

internal interface IProcessCleanupNative
{
    NativeCallResult TerminateProcess(SafeProcessHandle processHandle, uint exitCode);

    ProcessWaitOutcome Wait(SafeProcessHandle processHandle, TimeSpan timeout);

    NativeCallResult TerminateJob(IProcessTreeTerminator job, uint exitCode);
}

internal interface IProcessTreeTerminator
{
    NativeCallResult TerminateTree(uint exitCode);
}

internal readonly record struct NativeCallResult(bool Succeeded, int ErrorCode)
{
    internal static NativeCallResult Success { get; } = new(true, 0);

    internal static NativeCallResult Failure(int errorCode) => new(false, errorCode);
}

internal enum ProcessWaitStatus
{
    Signaled,
    TimedOut,
    Failed,
}

internal readonly record struct ProcessWaitOutcome(ProcessWaitStatus Status, int ErrorCode)
{
    internal static ProcessWaitOutcome Signaled { get; } = new(ProcessWaitStatus.Signaled, 0);

    internal static ProcessWaitOutcome TimedOut { get; } = new(ProcessWaitStatus.TimedOut, 0);

    internal static ProcessWaitOutcome Failure(int errorCode) => new(ProcessWaitStatus.Failed, errorCode);
}

internal readonly record struct ProcessCleanupResult(
    bool Signaled,
    bool EscalatedToJob,
    int? ProcessTerminationErrorCode,
    int? JobTerminationErrorCode,
    int? WaitErrorCode,
    bool TimedOut);

internal sealed class BoundedProcessCleanup : IProcessCleanupPolicy
{
    internal const uint CleanupExitCode = 1;
    internal static readonly TimeSpan ProductionDefaultTimeout = TimeSpan.FromSeconds(5);
    private readonly IProcessCleanupNative _native;
    private readonly TimeSpan _timeout;

    internal BoundedProcessCleanup(IProcessCleanupNative native, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(native);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _native = native;
        _timeout = timeout;
    }

    public ProcessCleanupResult Cleanup(
        SafeProcessHandle processHandle,
        IProcessTreeTerminator job)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        ArgumentNullException.ThrowIfNull(job);

        var started = Stopwatch.GetTimestamp();
        var terminateProcess = _native.TerminateProcess(processHandle, CleanupExitCode);
        int? processError = terminateProcess.Succeeded ? null : terminateProcess.ErrorCode;
        int? waitError = null;

        if (terminateProcess.Succeeded)
        {
            var firstWaitBudget = HalfOf(Remaining(started));
            if (firstWaitBudget > TimeSpan.Zero)
            {
                var firstWait = _native.Wait(processHandle, firstWaitBudget);
                if (firstWait.Status == ProcessWaitStatus.Signaled)
                {
                    return new ProcessCleanupResult(true, false, null, null, null, false);
                }

                if (firstWait.Status == ProcessWaitStatus.Failed)
                {
                    waitError = firstWait.ErrorCode;
                }
            }
        }

        var terminateJob = _native.TerminateJob(job, CleanupExitCode);
        int? jobError = terminateJob.Succeeded ? null : terminateJob.ErrorCode;
        var remaining = Remaining(started);
        if (remaining > TimeSpan.Zero)
        {
            var finalWait = _native.Wait(processHandle, remaining);
            if (finalWait.Status == ProcessWaitStatus.Signaled)
            {
                return new ProcessCleanupResult(
                    true,
                    true,
                    processError,
                    jobError,
                    waitError,
                    false);
            }

            if (finalWait.Status == ProcessWaitStatus.Failed)
            {
                waitError = finalWait.ErrorCode;
            }
        }

        return new ProcessCleanupResult(
            false,
            true,
            processError,
            jobError,
            waitError,
            TimedOut: waitError is null);
    }

    private TimeSpan Remaining(long started)
    {
        var remaining = _timeout - Stopwatch.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static TimeSpan HalfOf(TimeSpan value) =>
        value > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Max(1, value.Ticks / 2)) : TimeSpan.Zero;
}

internal sealed class NativeProcessCleanup : IProcessCleanupNative
{
    private const int ErrorInvalidHandle = 6;
    internal static NativeProcessCleanup Instance { get; } = new();

    private NativeProcessCleanup()
    {
    }

    public NativeCallResult TerminateProcess(SafeProcessHandle processHandle, uint exitCode)
    {
        try
        {
            return NativeMethods.TerminateProcess(processHandle, exitCode)
                ? NativeCallResult.Success
                : NativeCallResult.Failure(Marshal.GetLastPInvokeError());
        }
        catch (ObjectDisposedException)
        {
            return NativeCallResult.Failure(ErrorInvalidHandle);
        }
    }

    public ProcessWaitOutcome Wait(SafeProcessHandle processHandle, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return ProcessWaitOutcome.TimedOut;
        }

        var wholeMilliseconds = Math.Floor(timeout.TotalMilliseconds);
        if (wholeMilliseconds < 1)
        {
            return ProcessWaitOutcome.TimedOut;
        }

        var milliseconds = checked((uint)Math.Min(wholeMilliseconds, uint.MaxValue - 1d));
        try
        {
            var result = NativeMethods.WaitForSingleObject(processHandle, milliseconds);
            return result switch
            {
                NativeMethods.WaitObject0 => ProcessWaitOutcome.Signaled,
                NativeMethods.WaitTimeout => ProcessWaitOutcome.TimedOut,
                _ => ProcessWaitOutcome.Failure(Marshal.GetLastPInvokeError()),
            };
        }
        catch (ObjectDisposedException)
        {
            return ProcessWaitOutcome.Failure(ErrorInvalidHandle);
        }
    }

    public NativeCallResult TerminateJob(IProcessTreeTerminator job, uint exitCode) =>
        job.TerminateTree(exitCode);
}
