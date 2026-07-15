using System.ComponentModel;
using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Processes;

internal interface IWindowsNativeExitObservationFactory
{
    Task Create(SafeProcessHandle processHandle);
}

internal sealed class WindowsNativeExitObservationFactory
    : IWindowsNativeExitObservationFactory
{
    internal static WindowsNativeExitObservationFactory Instance { get; } = new();

    private WindowsNativeExitObservationFactory()
    {
    }

    public Task Create(SafeProcessHandle processHandle) =>
        WindowsNativeExitObservation.Create(processHandle);
}

internal static class WindowsNativeExitObservation
{
    internal static Task Create(SafeProcessHandle processHandle)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        return WaitForDuplicatedProcessExitAsync(Duplicate(processHandle));
    }

    private static Task WaitForDuplicatedProcessExitAsync(SafeProcessHandle duplicatedHandle)
    {
        ProcessWaitHandle? waitHandle = null;
        RegisteredWaitHandle? registration = null;
        try
        {
            waitHandle = new ProcessWaitHandle(duplicatedHandle);
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            registration = ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
                completion,
                Timeout.Infinite,
                executeOnlyOnce: true);
            return AwaitAndDisposeAsync(
                completion.Task,
                registration,
                waitHandle,
                duplicatedHandle);
        }
        catch
        {
            registration?.Unregister(waitObject: null);
            waitHandle?.Dispose();
            duplicatedHandle.Dispose();
            throw;
        }
    }

    private static SafeProcessHandle Duplicate(SafeProcessHandle processHandle)
    {
        var currentProcess = NativeMethods.GetCurrentProcess();
        if (!NativeMethods.DuplicateProcessHandle(
            currentProcess,
            processHandle,
            currentProcess,
            out var duplicatedValue,
            desiredAccess: 0,
            inheritHandle: false,
            NativeMethods.DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new SafeProcessHandle(duplicatedValue, ownsHandle: true);
    }

    private static async Task AwaitAndDisposeAsync(
        Task completion,
        RegisteredWaitHandle registration,
        WaitHandle waitHandle,
        SafeProcessHandle duplicatedHandle)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(waitObject: null);
            waitHandle.Dispose();
            duplicatedHandle.Dispose();
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeProcessHandle processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(
                processHandle.DangerousGetHandle(),
                ownsHandle: false);
        }
    }
}
