using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Diagnostics;

public sealed unsafe class WindowsDiagnosticSegmentFactory
{
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private static readonly string[] SegmentNames =
    [
        "apphost-0.jsonl",
        "apphost-1.jsonl",
        "apphost-2.jsonl",
        "apphost-3.jsonl",
    ];

    private readonly DataProfileIdentity _profile;
    private readonly Action? _beforeDirectoryHandleOpenForTests;

    public WindowsDiagnosticSegmentFactory(DataProfileIdentity profile)
        : this(profile, beforeDirectoryHandleOpenForTests: null)
    {
    }

    internal WindowsDiagnosticSegmentFactory(
        DataProfileIdentity profile,
        Action? beforeDirectoryHandleOpenForTests)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _beforeDirectoryHandleOpenForTests = beforeDirectoryHandleOpenForTests;
    }

    public ValueTask<Stream> OpenAsync(int slot, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, SegmentNames.Length);
        cancellationToken.ThrowIfCancellationRequested();

        var logsPath = Path.Combine(_profile.CanonicalPath, "logs");
        DataProfileIdentity.EnsureNoReparsePoints(
            _profile.CanonicalPath,
            WindowsProfilePathInspector.Instance);
        DataProfileIdentity.EnsureNoReparsePoints(logsPath, WindowsProfilePathInspector.Instance);
        CreateDirectoryIfMissing(logsPath);
        DataProfileIdentity.EnsureNoReparsePoints(logsPath, WindowsProfilePathInspector.Instance);
        _beforeDirectoryHandleOpenForTests?.Invoke();

        using var directoryHandle = NativeMethods.CreateFile(
            logsPath,
            NativeMethods.FileReadAttributes,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        EnsureValidHandle(directoryHandle);
        EnsureNotReparsePoint(directoryHandle, requireDirectory: true);
        ValidateExactCurrentUserOnlyDacl(new DirectoryInfo(logsPath).GetAccessControl(
            AccessControlSections.Access));

        cancellationToken.ThrowIfCancellationRequested();
        var segmentPath = Path.Combine(logsPath, SegmentNames[slot]);
        var segmentHandle = OpenSegment(segmentPath);
        try
        {
            EnsureNotReparsePoint(segmentHandle, requireDirectory: false);
            ValidateExactCurrentUserOnlyDacl(new FileInfo(segmentPath).GetAccessControl(
                AccessControlSections.Access));
            var stream = new FileStream(segmentHandle, FileAccess.Write, bufferSize: 4_096, isAsync: false);
            segmentHandle = null;
            stream.SetLength(0);
            return ValueTask.FromResult<Stream>(stream);
        }
        finally
        {
            segmentHandle?.Dispose();
        }
    }

    private static void CreateDirectoryIfMissing(string path)
    {
        using var descriptor = NativeSecurityDescriptor.CreateForCurrentUserOnly();
        var attributes = new NativeMethods.SecurityAttributes
        {
            Length = checked((uint)sizeof(NativeMethods.SecurityAttributes)),
            SecurityDescriptor = descriptor.DangerousGetHandle().ToPointer(),
            InheritHandle = 0,
        };
        if (!NativeMethods.CreateDirectory(path, &attributes))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorAlreadyExists)
            {
                throw new Win32Exception(error);
            }
        }
    }

    private static SafeFileHandle OpenSegment(string path)
    {
        using var descriptor = NativeSecurityDescriptor.CreateForCurrentUserOnly();
        var attributes = new NativeMethods.SecurityAttributes
        {
            Length = checked((uint)sizeof(NativeMethods.SecurityAttributes)),
            SecurityDescriptor = descriptor.DangerousGetHandle().ToPointer(),
            InheritHandle = 0,
        };
        var created = NativeMethods.CreateFileWithSecurity(
            path,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead,
            &attributes,
            NativeMethods.CreateNew,
            NativeMethods.FileAttributeNormal | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!created.IsInvalid)
        {
            return created;
        }

        var error = Marshal.GetLastPInvokeError();
        created.Dispose();
        if (error is not (ErrorFileExists or ErrorAlreadyExists))
        {
            throw new Win32Exception(error);
        }

        var existing = NativeMethods.CreateFile(
            path,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        EnsureValidHandle(existing);
        return existing;
    }

    private static void EnsureValidHandle(SafeFileHandle handle)
    {
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
    }

    private static void EnsureNotReparsePoint(SafeFileHandle handle, bool requireDirectory)
    {
        if (!NativeMethods.GetFileInformationByHandleEx(
                handle,
                NativeMethods.FileAttributeTagInfo,
                out var attributes,
                checked((uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInformation>())))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var fileAttributes = (FileAttributes)attributes.FileAttributes;
        if ((fileAttributes & FileAttributes.ReparsePoint) != 0 ||
            requireDirectory != ((fileAttributes & FileAttributes.Directory) != 0))
        {
            throw new IOException("The diagnostic path is not a secure regular file-system object.");
        }
    }

    private static void ValidateExactCurrentUserOnlyDacl(FileSystemSecurity security)
    {
        if (!security.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException("The diagnostic path DACL permits inheritance.");
        }

        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var currentSid = new SecurityIdentifier(NativeSecurityDescriptor.GetCurrentUserSid());
        if (rules.Length != 1 ||
            rules[0].IsInherited ||
            rules[0].AccessControlType != AccessControlType.Allow ||
            rules[0].FileSystemRights != FileSystemRights.FullControl ||
            !currentSid.Equals(rules[0].IdentityReference))
        {
            throw new UnauthorizedAccessException("The diagnostic path DACL is not current-user-only.");
        }
    }
}
