using System.ComponentModel;
using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Processes;

internal static unsafe class WindowsProcessIdentityVerifier
{
    internal static SupervisedProcessIdentity Capture(
        RuntimeRole role,
        ProcessGeneration generation,
        SafeProcessHandle processHandle)
    {
        if (!NativeMethods.GetProcessTimes(
            processHandle,
            out var creation,
            out _,
            out _,
            out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var processId = NativeMethods.GetProcessId(processHandle);
        if (processId == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var imagePath = QueryImagePath(processHandle);
        var creationTime = new DateTimeOffset(DateTime.FromFileTimeUtc(creation.ToInt64()), TimeSpan.Zero);
        return new SupervisedProcessIdentity(
            role,
            generation,
            checked((int)processId),
            creationTime,
            Path.GetFullPath(imagePath));
    }

    private static string QueryImagePath(SafeProcessHandle processHandle)
    {
        var buffer = new char[1024];
        while (true)
        {
            var length = checked((uint)buffer.Length);
            fixed (char* bufferPointer = buffer)
            {
                if (NativeMethods.QueryFullProcessImageName(
                    processHandle,
                    flags: 0,
                    bufferPointer,
                    ref length))
                {
                    return new string(bufferPointer, 0, checked((int)length));
                }
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != 122 || buffer.Length >= 32768)
            {
                throw new Win32Exception(error);
            }

            Array.Resize(ref buffer, Math.Min(buffer.Length * 2, 32768));
        }
    }
}
