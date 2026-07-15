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
    private readonly Func<SafeFileHandle, RawSecurityDescriptor> _readSecurityDescriptor;

    public WindowsDiagnosticSegmentFactory(DataProfileIdentity profile)
        : this(
            profile,
            beforeDirectoryHandleOpenForTests: null,
            readSecurityDescriptor: ReadSecurityDescriptor)
    {
    }

    internal WindowsDiagnosticSegmentFactory(
        DataProfileIdentity profile,
        Action? beforeDirectoryHandleOpenForTests)
        : this(
            profile,
            beforeDirectoryHandleOpenForTests,
            readSecurityDescriptor: ReadSecurityDescriptor)
    {
    }

    internal WindowsDiagnosticSegmentFactory(
        DataProfileIdentity profile,
        Action? beforeDirectoryHandleOpenForTests,
        Func<SafeFileHandle, RawSecurityDescriptor> readSecurityDescriptor)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _beforeDirectoryHandleOpenForTests = beforeDirectoryHandleOpenForTests;
        _readSecurityDescriptor = readSecurityDescriptor ??
            throw new ArgumentNullException(nameof(readSecurityDescriptor));
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
            NativeMethods.FileReadAttributes | NativeMethods.ReadControl,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        EnsureValidHandle(directoryHandle);
        EnsureNotReparsePoint(directoryHandle, requireDirectory: true);
        ValidateExactCurrentUserOnlyDacl(directoryHandle);

        cancellationToken.ThrowIfCancellationRequested();
        var segmentPath = Path.Combine(logsPath, SegmentNames[slot]);
        var segmentHandle = OpenSegment(segmentPath);
        try
        {
            EnsureNotReparsePoint(segmentHandle, requireDirectory: false);
            ValidateExactCurrentUserOnlyDacl(segmentHandle);
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

    private void ValidateExactCurrentUserOnlyDacl(SafeFileHandle handle)
    {
        var security = _readSecurityDescriptor(handle);
        if ((security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
        {
            throw new UnauthorizedAccessException("The diagnostic path DACL permits inheritance.");
        }

        var dacl = security.DiscretionaryAcl;
        var currentSid = new SecurityIdentifier(NativeSecurityDescriptor.GetCurrentUserSid());
        if (dacl is null ||
            dacl.Count != 1 ||
            dacl[0] is not CommonAce rule ||
            rule.IsCallback ||
            rule.AceFlags != AceFlags.None ||
            rule.AceQualifier != AceQualifier.AccessAllowed ||
            rule.AccessMask != (int)FileSystemRights.FullControl ||
            !currentSid.Equals(rule.SecurityIdentifier))
        {
            throw new UnauthorizedAccessException("The diagnostic path DACL is not current-user-only.");
        }
    }

    private static RawSecurityDescriptor ReadSecurityDescriptor(SafeFileHandle handle)
    {
        _ = NativeMethods.GetFileObjectSecurity(
            handle,
            NativeMethods.DaclSecurityInformation,
            securityDescriptor: null,
            length: 0,
            out var needed);
        var error = Marshal.GetLastPInvokeError();
        if (error != NativeMethods.ErrorInsufficientBuffer || needed == 0)
        {
            throw SecurityDescriptorUnavailable(error);
        }

        var buffer = new byte[checked((int)needed)];
        fixed (byte* descriptor = buffer)
        {
            if (!NativeMethods.GetFileObjectSecurity(
                    handle,
                    NativeMethods.DaclSecurityInformation,
                    descriptor,
                    checked((uint)buffer.Length),
                    out _))
            {
                throw SecurityDescriptorUnavailable(Marshal.GetLastPInvokeError());
            }
        }

        return new RawSecurityDescriptor(buffer, 0);
    }

    private static UnauthorizedAccessException SecurityDescriptorUnavailable(int error) =>
        new(
            "The diagnostic path security descriptor could not be validated.",
            new Win32Exception(error));
}
