using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("HowardLab.EbayCrm.AppHost.Windows.Tests")]
[assembly: InternalsVisibleTo("HowardLab.EbayCrm.AppHost")]

namespace HowardLab.EbayCrm.AppHost.Windows.Native;

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeJobHandle(IntPtr handle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

internal sealed class SafeKernelObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeKernelObjectHandle(IntPtr handle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
