using System.ComponentModel;
using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Processes;

public sealed class WindowsJobObject : IProcessGroup, IProcessTreeTerminator
{
    private readonly SafeJobHandle _handle;

    private WindowsJobObject(SafeJobHandle handle)
    {
        _handle = handle;
    }

    public static unsafe WindowsJobObject CreateKillOnClose()
    {
        var rawHandle = NativeMethods.CreateJobObject(jobAttributes: null, name: null);
        if (rawHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error);
        }

        var handle = new SafeJobHandle(rawHandle, ownsHandle: true);

        var limits = new NativeMethods.JobObjectExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
        if (!NativeMethods.SetInformationJobObject(
            handle,
            NativeMethods.JobObjectExtendedLimitInformationClass,
            &limits,
            checked((uint)sizeof(NativeMethods.JobObjectExtendedLimitInformation))))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return new WindowsJobObject(handle);
    }

    public bool Contains(SafeProcessHandle processHandle)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        if (!NativeMethods.IsProcessInJob(processHandle, _handle, out var result))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return result != 0;
    }

    internal JobHandleLease AcquireHandle() => new(_handle);

    internal SafeJobHandle DuplicateForTests()
    {
        var currentProcess = NativeMethods.GetCurrentProcess();
        if (!NativeMethods.DuplicateHandle(
            currentProcess,
            _handle,
            currentProcess,
            out var duplicate,
            desiredAccess: 0,
            inheritHandle: false,
            NativeMethods.DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new SafeJobHandle(duplicate, ownsHandle: true);
    }

    internal void DuplicateIntoProcessForTests(SafeProcessHandle targetProcess)
    {
        ArgumentNullException.ThrowIfNull(targetProcess);
        var targetLease = false;
        try
        {
            targetProcess.DangerousAddRef(ref targetLease);
            if (!targetLease || !NativeMethods.DuplicateHandle(
                NativeMethods.GetCurrentProcess(),
                _handle,
                targetProcess.DangerousGetHandle(),
                out _,
                desiredAccess: 0,
                inheritHandle: false,
                NativeMethods.DuplicateSameAccess))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            if (targetLease)
            {
                targetProcess.DangerousRelease();
            }
        }
    }

    NativeCallResult IProcessTreeTerminator.TerminateTree(uint exitCode) =>
        TerminateTree(exitCode);

    internal NativeCallResult TerminateTree(uint exitCode)
    {
        try
        {
            return NativeMethods.TerminateJobObject(_handle, exitCode)
                ? NativeCallResult.Success
                : NativeCallResult.Failure(Marshal.GetLastPInvokeError());
        }
        catch (ObjectDisposedException)
        {
            return NativeCallResult.Failure(6);
        }
    }

    public void Dispose() => _handle.Dispose();

    internal sealed class JobHandleLease : IDisposable
    {
        private SafeJobHandle? _handle;

        internal JobHandleLease(SafeJobHandle handle)
        {
            var success = false;
            handle.DangerousAddRef(ref success);
            if (!success)
            {
                throw new ObjectDisposedException(nameof(WindowsJobObject));
            }

            _handle = handle;
            Value = handle.DangerousGetHandle();
        }

        internal IntPtr Value { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _handle, null)?.DangerousRelease();
        }
    }
}
