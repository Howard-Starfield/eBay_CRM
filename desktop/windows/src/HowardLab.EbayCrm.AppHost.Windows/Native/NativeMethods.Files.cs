using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Native;

internal static unsafe partial class NativeMethods
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint Delete = 0x00010000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint CreateNew = 1;
    internal const uint OpenExisting = 3;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const int FileDispositionInfo = 4;
    internal const int FileAttributeTagInfo = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInformation
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileDispositionInformation
    {
        internal byte DeleteFile;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFileWithSecurity(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        SecurityAttributes* securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateDirectory(
        string pathName,
        SecurityAttributes* securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileAttributeTagInformation information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        ref FileDispositionInformation information,
        uint bufferSize);
}
