using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Native;

internal static unsafe partial class NativeMethods
{
    internal const uint SddlRevision1 = 1;
    internal const int TokenGroups = 2;
    internal const uint SeGroupLogonId = 0xC0000000;
    internal const uint MutexAllAccess = 0x001F0001;
    internal const uint WaitAbandoned0 = 0x00000080;
    internal const uint WaitFailed = 0xffffffff;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenGroupsHeader
    {
        internal uint GroupCount;
        internal SidAndAttributes Groups;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        uint* securityDescriptorSize);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        void* tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeWaitHandle CreateMutexEx(
        SecurityAttributes* mutexAttributes,
        string name,
        uint flags,
        uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseMutex(SafeWaitHandle mutex);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    internal static partial uint WaitForSingleObjectHandle(
        SafeWaitHandle handle,
        uint milliseconds);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr LocalFree(IntPtr memory);
}

internal sealed class SafeLocalAllocHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeLocalAllocHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeLocalAllocHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.LocalFree(handle) == IntPtr.Zero;
}
