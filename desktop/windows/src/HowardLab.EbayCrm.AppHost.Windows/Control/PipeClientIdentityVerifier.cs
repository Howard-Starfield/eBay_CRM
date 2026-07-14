using System.ComponentModel;
using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using HowardLab.EbayCrm.AppHost.Windows.Processes;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Control;

internal sealed class PipeClientIdentityVerifier
{
    internal static PipeClientIdentityVerifier Instance { get; } = new();

    private PipeClientIdentityVerifier()
    {
    }

    internal SafeProcessHandle Verify(
        SafePipeHandle pipe,
        ISupervisedProcess expectedProcess,
        WindowsJobObject expectedJob)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(expectedProcess);
        ArgumentNullException.ThrowIfNull(expectedJob);
        if (expectedProcess is not WindowsSupervisedProcess windowsProcess)
        {
            throw new InvalidOperationException("The Windows control channel requires a retained Windows process.");
        }

        VerifyExpectedProcessStillMatches(windowsProcess, expectedJob);
        if (!NativeMethods.GetNamedPipeClientProcessId(pipe, out var clientProcessId))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var clientHandle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation | NativeMethods.Synchronize,
            inheritHandle: false,
            clientProcessId);
        if (clientHandle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            clientHandle.Dispose();
            throw new Win32Exception(error);
        }

        try
        {
            if (NativeMethods.WaitForSingleObject(clientHandle, milliseconds: 0) != NativeMethods.WaitTimeout)
            {
                throw new InvalidOperationException("The pipe client is not alive.");
            }

            var actual = WindowsProcessIdentityVerifier.Instance.Capture(
                windowsProcess.Identity.Role,
                windowsProcess.Identity.Generation,
                clientHandle);
            var expected = windowsProcess.Identity;
            if (actual.ProcessId != checked((int)clientProcessId) ||
                actual.ProcessId != expected.ProcessId ||
                actual.CreationTimeUtc.UtcTicks != expected.CreationTimeUtc.UtcTicks ||
                !string.Equals(
                    Path.GetFullPath(actual.VerifiedImagePath),
                    Path.GetFullPath(expected.VerifiedImagePath),
                    StringComparison.OrdinalIgnoreCase) ||
                !expectedJob.Contains(clientHandle))
            {
                throw new InvalidOperationException("The pipe client does not match the supervised process generation.");
            }

            return clientHandle;
        }
        catch
        {
            clientHandle.Dispose();
            throw;
        }
    }

    private static void VerifyExpectedProcessStillMatches(
        WindowsSupervisedProcess process,
        WindowsJobObject job)
    {
        if (NativeMethods.WaitForSingleObject(process.ProcessHandle, milliseconds: 0) != NativeMethods.WaitTimeout ||
            !job.Contains(process.ProcessHandle))
        {
            throw new InvalidOperationException("The supervised process is no longer live in the expected Job.");
        }

        var retained = WindowsProcessIdentityVerifier.Instance.Capture(
            process.Identity.Role,
            process.Identity.Generation,
            process.ProcessHandle);
        if (retained.ProcessId != process.Identity.ProcessId ||
            retained.CreationTimeUtc.UtcTicks != process.Identity.CreationTimeUtc.UtcTicks ||
            !string.Equals(
                Path.GetFullPath(retained.VerifiedImagePath),
                Path.GetFullPath(process.Identity.VerifiedImagePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The retained process identity changed.");
        }
    }
}
