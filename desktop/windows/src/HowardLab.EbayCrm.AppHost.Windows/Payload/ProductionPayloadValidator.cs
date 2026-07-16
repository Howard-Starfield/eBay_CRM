using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public sealed class ProductionPayloadValidationException : Exception
{
    internal ProductionPayloadValidationException(string reasonCode)
        : base(reasonCode)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public sealed class ProductionPayloadValidator
{
    public const string ManifestFileName = "production-payload-manifest-v2.json";

    private readonly Func<string, bool> _isTrustedAcl;
    private readonly Func<string, bool> _isReparsePoint;
    private readonly Func<string, IDisposable> _openLease;
    private readonly bool _usesNativePathInspection;

    public ProductionPayloadValidator()
        : this(isTrustedAcl: null, isReparsePoint: null, openLease: null)
    {
    }

    internal ProductionPayloadValidator(
        Func<string, bool>? isTrustedAcl,
        Func<string, bool>? isReparsePoint,
        Func<string, IDisposable>? openLease)
    {
        _isTrustedAcl = isTrustedAcl ?? DefaultIsTrustedAcl;
        _isReparsePoint = isReparsePoint ?? DefaultIsReparsePoint;
        _openLease = openLease ?? OpenNativeLease;
        _usesNativePathInspection = isReparsePoint is null;
    }

    public ValidatedProductionPayload Validate(
        string payloadRoot,
        string profileRoot,
        ProductionReleaseCatalog releaseCatalog)
    {
        try
        {
            ValidateCatalog(releaseCatalog);
            var root = CanonicalLocalAbsolute(payloadRoot);
            var profile = CanonicalLocalAbsolute(profileRoot);
            if (IsSameOrUnder(root, profile) || IsSameOrUnder(profile, root))
            {
                throw Failure("production-payload-profile-overlap");
            }

            ValidateAncestorChain(profile, validateAclAtLeaf: false);
            var manifest = VerifySnapshot(root, releaseCatalog);
            var criticalPaths = CriticalPaths(root, manifest.Header).ToArray();
            var initialLease = OpenCompositeLease(criticalPaths);
            try
            {
                var verified = VerifySnapshot(root, releaseCatalog);
                if (!StringComparer.Ordinal.Equals(
                        manifest.CanonicalDigest,
                        verified.CanonicalDigest))
                {
                    throw Failure("production-manifest-digest-mismatch");
                }

                return new ValidatedProductionPayload(
                    root,
                    manifest.Header,
                    manifest.Files,
                    releaseCatalog.CompatibilityIdentity!,
                    criticalPaths,
                    initialLease,
                    _openLease,
                    () => _ = VerifySnapshot(root, releaseCatalog));
            }
            catch
            {
                initialLease.Dispose();
                throw;
            }
        }
        catch (ProductionPayloadValidationException)
        {
            throw;
        }
        catch
        {
            throw Failure("production-payload-trust-failed");
        }
    }

    private ProductionPayloadManifestV2 VerifySnapshot(
        string root,
        ProductionReleaseCatalog releaseCatalog)
    {
        ValidateAncestorChain(root, validateAclAtLeaf: true);
        var manifestPath = Path.Combine(root, ManifestFileName);
        EnsurePath(manifestPath, directory: false, root, validateAcl: true);
        ProductionPayloadManifestV2 manifest;
        using (var manifestStream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan))
        {
            manifest = ProductionPayloadManifestV2.Parse(manifestStream);
        }
        var computed = ProductionPayloadCanonicalizer.ComputeDigest(manifest.Header, manifest.Files);
        if (!StringComparer.Ordinal.Equals(computed, manifest.CanonicalDigest) ||
            !StringComparer.Ordinal.Equals(computed, releaseCatalog.ManifestDigest))
        {
            throw Failure("production-manifest-digest-mismatch");
        }

        ValidateEntrypointShape(manifest.Header, manifest.Files);
        ValidateFilesystemEquality(root, manifest);
        return manifest;
    }

    private void ValidateFilesystemEquality(
        string root,
        ProductionPayloadManifestV2 manifest)
    {
        using var actualFiles = EnumerateActualFilesOrdinal(
                root,
                manifest.Header.Bounds)
            .GetEnumerator();
        var hasActual = actualFiles.MoveNext();
        foreach (var expected in manifest.Files)
        {
            if (!hasActual)
            {
                throw Failure("production-payload-file-missing");
            }
            var comparison = StringComparer.Ordinal.Compare(
                expected.RelativePath,
                actualFiles.Current);
            if (comparison < 0)
            {
                throw Failure("production-payload-file-missing");
            }
            if (comparison > 0)
            {
                throw Failure("production-payload-file-extra");
            }
            ValidatePayloadFile(root, expected);
            hasActual = actualFiles.MoveNext();
        }
        if (hasActual)
        {
            throw Failure("production-payload-file-extra");
        }
    }

    private IEnumerable<string> EnumerateActualFilesOrdinal(
        string root,
        ProductionPayloadBounds bounds)
    {
        var state = new FilesystemEnumerationState(bounds);
        foreach (var relativePath in EnumerateDirectoryFilesOrdinal(
                     root,
                     root,
                     depth: 0,
                     state))
        {
            yield return relativePath;
        }
    }

    private IEnumerable<string> EnumerateDirectoryFilesOrdinal(
        string root,
        string directory,
        int depth,
        FilesystemEnumerationState state)
    {
        if (depth > 256)
        {
            throw Failure("production-payload-bounds-invalid");
        }
        EnsurePath(directory, directory: true, root, validateAcl: true);
        var entries = new List<FilesystemEntry>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            state.EntryCount++;
            if (state.EntryCount > state.MaximumEntries)
            {
                throw Failure("production-payload-bounds-invalid");
            }
            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            EnsurePath(entry, isDirectory, root, validateAcl: true);
            var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Length > state.Bounds.MaxRelativePathChars ||
                !NodePayloadPath.IsCanonicalRelative(relative))
            {
                throw Failure("production-payload-bounds-invalid");
            }
            if (isDirectory)
            {
                state.DirectoryCount++;
                if (state.DirectoryCount > state.Bounds.MaxFiles)
                {
                    throw Failure("production-payload-bounds-invalid");
                }
            }
            entries.Add(new FilesystemEntry(
                entry,
                relative,
                isDirectory,
                isDirectory ? relative + "/" : relative));
        }

        ValidateFilesystemBatchCaseCollisions(
            entries,
            static entry => entry.RelativePath);
        entries.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.SortKey, right.SortKey));
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                foreach (var descendant in EnumerateDirectoryFilesOrdinal(
                             root,
                             entry.FullPath,
                             depth + 1,
                             state))
                {
                    yield return descendant;
                }
                continue;
            }
            if (StringComparer.OrdinalIgnoreCase.Equals(entry.RelativePath, ManifestFileName))
            {
                if (!StringComparer.Ordinal.Equals(entry.RelativePath, ManifestFileName))
                {
                    throw Failure("production-manifest-path-invalid");
                }
                continue;
            }
            state.FileCount++;
            if (state.FileCount > state.Bounds.MaxFiles)
            {
                throw Failure("production-payload-bounds-invalid");
            }
            if (state.PreviousFilePath is not null &&
                (StringComparer.Ordinal.Compare(state.PreviousFilePath, entry.RelativePath) >= 0 ||
                 StringComparer.OrdinalIgnoreCase.Equals(state.PreviousFilePath, entry.RelativePath)))
            {
                throw Failure("production-manifest-record-invalid");
            }
            state.PreviousFilePath = entry.RelativePath;
            yield return entry.RelativePath;
        }
    }

    internal static void ValidateFilesystemBatchCaseCollisions<T>(
        List<T> entries,
        Func<T, string> relativePath)
    {
        entries.Sort((left, right) =>
        {
            var leftPath = relativePath(left);
            var rightPath = relativePath(right);
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(leftPath, rightPath);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(leftPath, rightPath);
        });
        for (var index = 1; index < entries.Count; index++)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(
                    relativePath(entries[index - 1]),
                    relativePath(entries[index])))
            {
                throw Failure("production-manifest-record-invalid");
            }
        }
    }

    private void ValidatePayloadFile(
        string root,
        ProductionPayloadFileRecord record)
    {
        var path = CombineRelative(root, record.RelativePath);
        EnsurePath(path, directory: false, root, validateAcl: true);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != record.Length)
        {
            throw Failure("production-payload-file-mismatch");
        }
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!StringComparer.Ordinal.Equals(hash, record.Sha256))
        {
            throw Failure("production-payload-file-mismatch");
        }
        if (record.RelativePath.EndsWith(".node", StringComparison.OrdinalIgnoreCase))
        {
            ValidateNativeAddon(stream);
        }
    }

    private sealed class FilesystemEnumerationState
    {
        internal FilesystemEnumerationState(ProductionPayloadBounds bounds)
        {
            Bounds = bounds;
            MaximumEntries = checked((long)Bounds.MaxFiles * 2 + 1);
        }

        internal ProductionPayloadBounds Bounds { get; }

        internal long MaximumEntries { get; }

        internal int DirectoryCount { get; set; } = 1;

        internal long EntryCount { get; set; }

        internal int FileCount { get; set; }

        internal string? PreviousFilePath { get; set; }
    }

    private sealed record FilesystemEntry(
        string FullPath,
        string RelativePath,
        bool IsDirectory,
        string SortKey);

    private void ValidateAncestorChain(string path, bool validateAclAtLeaf)
    {
        var ancestors = new List<string>();
        for (var current = path; current is not null; current = Directory.GetParent(current)?.FullName)
        {
            ancestors.Add(current);
        }
        ancestors.Reverse();
        foreach (var ancestor in ancestors)
        {
            EnsurePath(
                ancestor,
                directory: true,
                requiredRoot: null,
                validateAcl: validateAclAtLeaf && StringComparer.OrdinalIgnoreCase.Equals(ancestor, path));
        }
    }

    private void EnsurePath(string path, bool directory, string? requiredRoot, bool validateAcl)
    {
        var canonical = CanonicalLocalAbsolute(path);
        if (requiredRoot is not null && !IsSameOrUnder(canonical, requiredRoot))
        {
            throw Failure("production-manifest-path-invalid");
        }
        if (_isReparsePoint(canonical))
        {
            throw Failure("production-payload-reparse-point");
        }
        if (validateAcl || requiredRoot is not null)
        {
            EnsureNoAlternateDataStreams(canonical);
        }
        if (_usesNativePathInspection)
        {
            ValidateNativeIdentity(canonical, directory);
        }
        else
        {
            var attributes = File.GetAttributes(canonical);
            if (((attributes & FileAttributes.Directory) != 0) != directory)
            {
                throw Failure("production-payload-trust-failed");
            }
        }
        if (validateAcl && !_isTrustedAcl(canonical))
        {
            throw Failure("production-payload-untrusted-acl");
        }
    }

    private static void ValidateEntrypointShape(
        ProductionPayloadHeader header,
        IReadOnlyList<ProductionPayloadFileRecord> files)
    {
        var entrypoints = new[]
        {
            header.NodeExecutable, header.ServerEntrypoint, header.WorkerEntrypoint,
            header.SetupEntrypoint, header.InstanceCommandEntrypoint,
            header.AcceptanceEntrypoint, header.AcceptanceCleanupEntrypoint,
            header.CompatibilityPreflightEntrypoint, header.FrontendEntrypoint,
        };
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(header.NodeExecutable), "node.exe") ||
            entrypoints.Skip(1).Take(7).Any(path => !StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(path), ".js")) ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(header.FrontendEntrypoint), ".html") ||
            entrypoints.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entrypoints.Length ||
            entrypoints.Any(path => !ContainsDeclaredFile(files, path)))
        {
            throw Failure("production-manifest-entrypoint-invalid");
        }
    }

    private static bool ContainsDeclaredFile(
        IReadOnlyList<ProductionPayloadFileRecord> files,
        string relativePath)
    {
        var low = 0;
        var high = files.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = StringComparer.Ordinal.Compare(
                files[middle].RelativePath,
                relativePath);
            if (comparison == 0)
            {
                return true;
            }
            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return false;
    }

    private static IEnumerable<string> CriticalPaths(string root, ProductionPayloadHeader header)
    {
        var ancestors = new List<string>();
        for (var current = root; current is not null; current = Directory.GetParent(current)?.FullName)
        {
            ancestors.Add(current);
        }
        foreach (var ancestor in ancestors.AsEnumerable().Reverse())
        {
            yield return ancestor;
        }
        yield return Path.Combine(root, ManifestFileName);
        foreach (var relative in LaunchRelativePaths(header))
        {
            yield return CombineRelative(root, relative);
        }
    }

    private static IEnumerable<string> LaunchRelativePaths(ProductionPayloadHeader header)
    {
        yield return header.NodeExecutable;
        yield return header.ServerEntrypoint;
        yield return header.WorkerEntrypoint;
        yield return header.SetupEntrypoint;
        yield return header.InstanceCommandEntrypoint;
        yield return header.AcceptanceEntrypoint;
        yield return header.AcceptanceCleanupEntrypoint;
        yield return header.CompatibilityPreflightEntrypoint;
        yield return header.FrontendEntrypoint;
    }

    private IDisposable OpenCompositeLease(IEnumerable<string> paths)
    {
        var leases = new List<IDisposable>();
        try
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                leases.Add(_openLease(path));
            }
            return new CompositeLease(leases);
        }
        catch
        {
            DisposeAll(leases);
            throw;
        }
    }

    private static void ValidateCatalog(ProductionReleaseCatalog? catalog)
    {
        if (catalog is null || !catalog.IsAvailable)
        {
            throw Failure("production-release-catalog-unavailable");
        }
        if (!ProductionPayloadManifestV2.IsHex(catalog.ManifestDigest, lowerOnly: true) ||
            string.IsNullOrWhiteSpace(catalog.CompatibilityIdentity))
        {
            throw Failure("production-release-catalog-invalid");
        }
    }

    private static string CombineRelative(string root, string relative)
    {
        if (!NodePayloadPath.IsCanonicalRelative(relative))
        {
            throw Failure("production-manifest-path-invalid");
        }
        var combined = CanonicalLocalAbsolute(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrUnder(combined, root))
        {
            throw Failure("production-manifest-path-invalid");
        }
        return combined;
    }

    private static string CanonicalLocalAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Contains('\0'))
        {
            throw Failure("production-manifest-path-invalid");
        }
        var root = Path.GetPathRoot(path);
        if (root is null || path.IndexOf(':', root.Length) >= 0)
        {
            throw Failure("production-manifest-path-invalid");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsSameOrUnder(string candidate, string root) =>
        StringComparer.OrdinalIgnoreCase.Equals(candidate, root) ||
        candidate.StartsWith(
            Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool DefaultIsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void ValidateNativeAddon(FileStream stream)
    {
        const int dosHeaderLength = 64;
        const int maximumPeHeaderOffset = 1024 * 1024;
        Span<byte> dosHeader = stackalloc byte[dosHeaderLength];
        stream.Position = 0;
        if (!ReadExactly(stream, dosHeader) ||
            dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            throw Failure("production-payload-native-addon-invalid");
        }
        var peOffset = BitConverter.ToInt32(dosHeader[0x3c..]);
        if (peOffset < dosHeaderLength || peOffset > maximumPeHeaderOffset ||
            peOffset > stream.Length - 6)
        {
            throw Failure("production-payload-native-addon-invalid");
        }
        stream.Position = peOffset;
        Span<byte> signatureAndMachine = stackalloc byte[6];
        if (!ReadExactly(stream, signatureAndMachine) ||
            signatureAndMachine[0] != (byte)'P' ||
            signatureAndMachine[1] != (byte)'E' ||
            signatureAndMachine[2] != 0 || signatureAndMachine[3] != 0 ||
            BitConverter.ToUInt16(signatureAndMachine[4..]) != 0x8664)
        {
            throw Failure("production-payload-native-addon-invalid");
        }
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static void EnsureNoAlternateDataStreams(string path)
    {
        var handle = FindFirstStream(path, 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 38)
            {
                return;
            }
            throw Failure("production-payload-trust-failed");
        }
        try
        {
            while (true)
            {
                if (!StringComparer.Ordinal.Equals(data.StreamName, "::$DATA"))
                {
                    throw Failure("production-payload-alternate-data-stream");
                }
                if (!FindNextStream(handle, out data))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 38)
                    {
                        break;
                    }
                    throw Failure("production-payload-trust-failed");
                }
            }
        }
        finally
        {
            _ = FindClose(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        internal long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string StreamName;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr FindFirstStream(
        string fileName,
        int infoLevel,
        out Win32FindStreamData data,
        int flags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(IntPtr findHandle, out Win32FindStreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findHandle);

    private static bool DefaultIsTrustedAcl(string path)
    {
        try
        {
            using var handle = OpenNativeHandle(path, Directory.Exists(path), denyDelete: false);
            TrustedApplicationRootSecurity.Validate(ReadSecurityDescriptor(handle));
            return true;
        }
        catch
        {
            throw Failure("production-payload-untrusted-acl");
        }
    }

    private static IDisposable OpenNativeLease(string path) =>
        OpenNativeHandle(path, Directory.Exists(path), denyDelete: true);

    private static SafeFileHandle OpenNativeHandle(string path, bool directory, bool denyDelete)
    {
        var share = directory
            ? NativeMethods.FileShareRead | NativeMethods.FileShareWrite
            : NativeMethods.FileShareRead;
        if (!denyDelete)
        {
            share |= NativeMethods.FileShareDelete;
            if (!directory)
            {
                share |= NativeMethods.FileShareWrite;
            }
        }
        var handle = NativeMethods.CreateFile(
            path,
            NativeMethods.GenericRead | NativeMethods.FileReadAttributes | NativeMethods.ReadControl,
            share,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal |
                NativeMethods.FileFlagOpenReparsePoint |
                (directory ? NativeMethods.FileFlagBackupSemantics : 0),
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Failure("production-payload-trust-failed");
        }
        return handle;
    }

    private static unsafe void ValidateNativeIdentity(string expectedPath, bool directory)
    {
        using var handle = OpenNativeHandle(expectedPath, directory, denyDelete: false);
        if (!NativeMethods.GetFileInformationByHandleEx(
                handle,
                NativeMethods.FileAttributeTagInfo,
                out var information,
                checked((uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInformation>())))
        {
            throw Failure("production-payload-trust-failed");
        }
        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != directory)
        {
            throw Failure("production-payload-reparse-point");
        }
        var buffer = new char[32_768];
        fixed (char* pointer = buffer)
        {
            var length = NativeMethods.GetFinalPathNameByHandle(
                handle,
                pointer,
                checked((uint)buffer.Length),
                flags: 0);
            if (length == 0 || length >= buffer.Length)
            {
                throw Failure("production-payload-trust-failed");
            }
            var actual = CanonicalLocalAbsolute(NormalizeNativeFinalPath(
                new string(pointer, 0, checked((int)length))));
            if (!StringComparer.OrdinalIgnoreCase.Equals(actual, expectedPath))
            {
                throw Failure("production-payload-reparse-point");
            }
        }
    }

    private static unsafe RawSecurityDescriptor ReadSecurityDescriptor(SafeFileHandle handle)
    {
        const uint securityInformation =
            NativeMethods.OwnerSecurityInformation | NativeMethods.DaclSecurityInformation;
        _ = NativeMethods.GetFileObjectSecurity(
            handle,
            securityInformation,
            securityDescriptor: null,
            length: 0,
            out var needed);
        if (Marshal.GetLastPInvokeError() != NativeMethods.ErrorInsufficientBuffer || needed == 0)
        {
            throw Failure("production-payload-untrusted-acl");
        }
        var buffer = new byte[checked((int)needed)];
        fixed (byte* descriptor = buffer)
        {
            if (!NativeMethods.GetFileObjectSecurity(
                    handle,
                    securityInformation,
                    descriptor,
                    checked((uint)buffer.Length),
                    out _))
            {
                throw Failure("production-payload-untrusted-acl");
            }
        }
        return new RawSecurityDescriptor(buffer, 0);
    }

    private static string NormalizeNativeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }
        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path[4..] : path;
    }

    private static void DisposeAll(IReadOnlyList<IDisposable> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            leases[index].Dispose();
        }
    }

    private static ProductionPayloadValidationException Failure(string reason) => new(reason);

    private sealed class CompositeLease : IDisposable
    {
        private IReadOnlyList<IDisposable>? _leases;

        internal CompositeLease(IReadOnlyList<IDisposable> leases) => _leases = leases;

        public void Dispose()
        {
            var leases = Interlocked.Exchange(ref _leases, null);
            if (leases is not null)
            {
                DisposeAll(leases);
            }
        }
    }
}

public sealed class ValidatedProductionPayload : IDisposable
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<string> _criticalPaths;
    private readonly Func<string, IDisposable> _openLease;
    private readonly Action _verifyClosure;
    private IDisposable? _initialLease;
    private bool _disposed;

    internal ValidatedProductionPayload(
        string root,
        ProductionPayloadHeader header,
        IReadOnlyList<ProductionPayloadFileRecord> files,
        string compatibilityIdentity,
        IReadOnlyList<string> criticalPaths,
        IDisposable initialLease,
        Func<string, IDisposable> openLease,
        Action verifyClosure)
    {
        Root = root;
        Header = header;
        Files = files;
        CompatibilityIdentity = compatibilityIdentity;
        _criticalPaths = criticalPaths;
        _initialLease = initialLease;
        _openLease = openLease;
        _verifyClosure = verifyClosure;
    }

    public string Root { get; }
    public ProductionPayloadHeader Header { get; }
    public IReadOnlyList<ProductionPayloadFileRecord> Files { get; }
    public string CompatibilityIdentity { get; }
    public string NodeExecutable => Absolute(Header.NodeExecutable);
    public string ServerEntrypoint => Absolute(Header.ServerEntrypoint);
    public string WorkerEntrypoint => Absolute(Header.WorkerEntrypoint);
    public string SetupEntrypoint => Absolute(Header.SetupEntrypoint);
    public string InstanceCommandEntrypoint => Absolute(Header.InstanceCommandEntrypoint);
    public string AcceptanceEntrypoint => Absolute(Header.AcceptanceEntrypoint);
    public string AcceptanceCleanupEntrypoint => Absolute(Header.AcceptanceCleanupEntrypoint);
    public string CompatibilityPreflightEntrypoint => Absolute(Header.CompatibilityPreflightEntrypoint);
    public string FrontendEntrypoint => Absolute(Header.FrontendEntrypoint);

    public IDisposable OpenLifetimeLease()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _verifyClosure();
            var lease = OpenMany(_criticalPaths);
            try
            {
                _verifyClosure();
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }

    public IDisposable OpenBootstrapLease(string entrypoint)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var absolute = Path.IsPathFullyQualified(entrypoint)
                ? Path.GetFullPath(entrypoint)
                : Absolute(entrypoint);
            var allowed = new[]
            {
                ServerEntrypoint, WorkerEntrypoint, SetupEntrypoint,
                InstanceCommandEntrypoint, AcceptanceEntrypoint,
                AcceptanceCleanupEntrypoint, CompatibilityPreflightEntrypoint,
            };
            if (!allowed.Contains(absolute, StringComparer.OrdinalIgnoreCase))
            {
                throw new ProductionPayloadValidationException("production-manifest-entrypoint-invalid");
            }
            _verifyClosure();
            var lease = _openLease(absolute);
            try
            {
                _verifyClosure();
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }

    public void VerifyClosure()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _verifyClosure();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _initialLease?.Dispose();
            _initialLease = null;
        }
        GC.SuppressFinalize(this);
    }

    private IDisposable OpenMany(IEnumerable<string> paths)
    {
        var leases = new List<IDisposable>();
        try
        {
            foreach (var path in paths)
            {
                leases.Add(_openLease(path));
            }
            return new LeaseSet(leases);
        }
        catch
        {
            for (var index = leases.Count - 1; index >= 0; index--)
            {
                leases[index].Dispose();
            }
            throw;
        }
    }

    private string Absolute(string relative) => Path.GetFullPath(Path.Combine(
        Root,
        relative.Replace('/', Path.DirectorySeparatorChar)));

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ProductionPayloadValidationException("production-payload-disposed");
        }
    }

    private sealed class LeaseSet : IDisposable
    {
        private IReadOnlyList<IDisposable>? _leases;
        internal LeaseSet(IReadOnlyList<IDisposable> leases) => _leases = leases;
        public void Dispose()
        {
            var leases = Interlocked.Exchange(ref _leases, null);
            if (leases is null) return;
            for (var index = leases.Count - 1; index >= 0; index--)
            {
                leases[index].Dispose();
            }
        }
    }
}
