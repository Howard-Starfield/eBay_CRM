using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

internal sealed record TrustedNodePayloadPathInspection(
    string FinalPath,
    bool IsDirectory,
    bool IsReparsePoint);

public sealed class TrustedNodePayloadValidator
{
    public const string ManifestFileName = "node-payload-manifest-v1.json";

    private readonly Func<SafeFileHandle, string, bool, TrustedNodePayloadPathInspection>
        _inspectHandle;
    private readonly Func<SafeFileHandle, string, RawSecurityDescriptor>
        _readSecurityDescriptor;

    public TrustedNodePayloadValidator()
        : this(inspectHandle: null, readSecurityDescriptor: null)
    {
    }

    internal TrustedNodePayloadValidator(
        Func<SafeFileHandle, string, bool, TrustedNodePayloadPathInspection>? inspectHandle,
        Func<SafeFileHandle, string, RawSecurityDescriptor>? readSecurityDescriptor)
    {
        _inspectHandle = inspectHandle ?? InspectHandle;
        _readSecurityDescriptor = readSecurityDescriptor ?? ReadSecurityDescriptor;
    }

    public TrustedNodePayload Validate(string payloadRoot, string profileRoot)
    {
        ValidationSnapshot? snapshot = null;
        try
        {
            var requestedRoot = CanonicalLocalAbsolute(payloadRoot);
            var canonicalProfileRoot = CanonicalLocalAbsolute(profileRoot);
            snapshot = ValidateSnapshot(requestedRoot, canonicalProfileRoot);
            var canonicalRoot = snapshot.CanonicalRoot;
            var pathLeaseHandles = snapshot.TakePathLeaseHandles();
            try
            {
                return new TrustedNodePayload(
                    canonicalRoot,
                    snapshot.ManifestPath,
                    snapshot.Manifest,
                    snapshot.ArtifactPaths,
                    pathLeaseHandles,
                    () =>
                    {
                        using var verification = ValidateSnapshot(
                            canonicalRoot,
                            canonicalProfileRoot);
                    });
            }
            catch
            {
                DisposeHandles(pathLeaseHandles);
                throw;
            }
        }
        catch (NodePayloadManifestException)
        {
            throw;
        }
        catch
        {
            throw Failure();
        }
        finally
        {
            snapshot?.Dispose();
        }
    }

    private ValidationSnapshot ValidateSnapshot(string requestedRoot, string profileRoot)
    {
        using var profileChain = ValidateDirectoryChain(
            profileRoot,
            retainPathLease: false,
            validateRootSecurity: false);
        var canonicalProfileRoot = profileChain.CanonicalRoot;
        if (IsSameOrUnder(requestedRoot, canonicalProfileRoot))
        {
            throw Failure();
        }

        using var payloadChain = ValidateDirectoryChain(
            requestedRoot,
            retainPathLease: true,
            validateRootSecurity: true);
        var canonicalRoot = payloadChain.CanonicalRoot;
        if (IsSameOrUnder(canonicalRoot, canonicalProfileRoot))
        {
            throw Failure();
        }

        var pathLeaseHandles = payloadChain.TakePathLeaseHandles();
        try
        {
            var manifestPath = Path.Combine(canonicalRoot, ManifestFileName);
            NodePayloadManifestV1 manifest;
            string canonicalManifestPath;
            using (var manifestHandle = OpenHandle(manifestPath, directory: false, readContent: true))
            {
                canonicalManifestPath = ValidateHandle(
                    manifestHandle,
                    manifestPath,
                    directory: false,
                    requiredRoot: canonicalRoot,
                    validateSecurity: true);
                manifest = NodePayloadManifestV1.Parse(ReadBoundedManifest(manifestHandle));
            }

            ValidateBootstrapShape(manifest);
            var declaredFiles = new HashSet<string>(
                manifest.Artifacts.Select(artifact => artifact.Path),
                StringComparer.OrdinalIgnoreCase);
            if (declaredFiles.Contains(ManifestFileName) ||
                declaredFiles.Any(ContainsEnvironmentLeaf))
            {
                throw Failure();
            }

            var requiredDirectories = RequiredDirectories(declaredFiles);
            foreach (var relativeDirectory in requiredDirectories)
            {
                var path = CombineRelative(canonicalRoot, relativeDirectory);
                using var handle = OpenHandle(path, directory: true, readContent: false);
                _ = ValidateHandle(
                    handle,
                    path,
                    directory: true,
                    requiredRoot: canonicalRoot,
                    validateSecurity: true);
            }

            ValidateClosure(canonicalRoot, declaredFiles, requiredDirectories);

            var canonicalArtifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in manifest.Artifacts)
            {
                var path = CombineRelative(canonicalRoot, artifact.Path);
                using var handle = OpenHandle(path, directory: false, readContent: true);
                var canonicalPath = ValidateHandle(
                    handle,
                    path,
                    directory: false,
                    requiredRoot: canonicalRoot,
                    validateSecurity: true);
                VerifyArtifact(handle, artifact);
                canonicalArtifacts.Add(artifact.Path, canonicalPath);
            }

            return new ValidationSnapshot(
                canonicalRoot,
                canonicalManifestPath,
                manifest,
                canonicalArtifacts,
                pathLeaseHandles);
        }
        catch
        {
            DisposeHandles(pathLeaseHandles);
            throw;
        }
    }

    private DirectoryChainValidation ValidateDirectoryChain(
        string requestedRoot,
        bool retainPathLease,
        bool validateRootSecurity)
    {
        var ancestors = new List<string>();
        for (var current = requestedRoot; current is not null; current = Directory.GetParent(current)?.FullName)
        {
            ancestors.Add(current);
        }

        ancestors.Reverse();
        string? canonicalRoot = null;
        var pathLeaseHandles = new List<SafeFileHandle>();
        try
        {
            foreach (var ancestor in ancestors)
            {
                var isRoot = StringComparer.OrdinalIgnoreCase.Equals(ancestor, requestedRoot);
                SafeFileHandle? handle = OpenHandle(
                    ancestor,
                    directory: true,
                    readContent: false,
                    denyDeleteSharing: retainPathLease);
                try
                {
                    var finalPath = ValidateHandle(
                        handle,
                        ancestor,
                        directory: true,
                        requiredRoot: null,
                        validateSecurity: isRoot && validateRootSecurity);
                    if (isRoot)
                    {
                        canonicalRoot = finalPath;
                    }

                    if (retainPathLease)
                    {
                        pathLeaseHandles.Add(handle);
                        handle = null;
                    }
                }
                finally
                {
                    handle?.Dispose();
                }
            }

            return new DirectoryChainValidation(
                canonicalRoot ?? throw Failure(),
                pathLeaseHandles);
        }
        catch
        {
            DisposeHandles(pathLeaseHandles);
            throw;
        }
    }

    private string ValidateHandle(
        SafeFileHandle handle,
        string requestedPath,
        bool directory,
        string? requiredRoot,
        bool validateSecurity)
    {
        var inspection = _inspectHandle(handle, requestedPath, directory);
        if (inspection.IsReparsePoint || inspection.IsDirectory != directory)
        {
            throw Failure();
        }

        var finalPath = CanonicalLocalAbsolute(inspection.FinalPath);
        if (requiredRoot is not null && !IsSameOrUnder(finalPath, requiredRoot))
        {
            throw Failure();
        }

        if (validateSecurity)
        {
            TrustedApplicationRootSecurity.Validate(_readSecurityDescriptor(handle, requestedPath));
        }

        return finalPath;
    }

    private static void ValidateBootstrapShape(NodePayloadManifestV1 manifest)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFileName(manifest.NodeExecutable),
                "node.exe") ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetExtension(manifest.ServerEntrypoint),
                ".js") ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetExtension(manifest.WorkerEntrypoint),
                ".js"))
        {
            throw Failure();
        }
    }

    private static HashSet<string> RequiredDirectories(IEnumerable<string> artifacts)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            var segments = artifact.Split('/');
            for (var count = 1; count < segments.Length; count++)
            {
                directories.Add(string.Join('/', segments.Take(count)));
            }
        }

        return directories;
    }

    private static void ValidateClosure(
        string canonicalRoot,
        IReadOnlySet<string> declaredFiles,
        IReadOnlySet<string> requiredDirectories)
    {
        var expectedFiles = new HashSet<string>(declaredFiles, StringComparer.OrdinalIgnoreCase)
        {
            ManifestFileName,
        };
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();
        pending.Enqueue(canonicalRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(canonicalRoot, entry)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!NodePayloadPath.IsCanonicalRelative(relative) ||
                    ContainsEnvironmentLeaf(relative))
                {
                    throw Failure();
                }

                var isDirectory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
                if (isDirectory)
                {
                    if (!requiredDirectories.Contains(relative) || !seenDirectories.Add(relative))
                    {
                        throw Failure();
                    }

                    pending.Enqueue(entry);
                }
                else if (!expectedFiles.Contains(relative) || !seenFiles.Add(relative))
                {
                    throw Failure();
                }
            }
        }

        if (!seenFiles.SetEquals(expectedFiles) ||
            !seenDirectories.SetEquals(requiredDirectories))
        {
            throw Failure();
        }
    }

    private static byte[] ReadBoundedManifest(SafeFileHandle handle)
    {
        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length is <= 0 or > NodePayloadManifestV1.MaxManifestBytes)
        {
            throw Failure();
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void VerifyArtifact(SafeFileHandle handle, NodePayloadArtifactV1 artifact)
    {
        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length != artifact.Length)
        {
            throw Failure();
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!StringComparer.Ordinal.Equals(actualHash, artifact.Sha256))
        {
            throw Failure();
        }
    }

    private static SafeFileHandle OpenHandle(
        string path,
        bool directory,
        bool readContent,
        bool denyDeleteSharing = false)
    {
        var desiredAccess = NativeMethods.FileReadAttributes | NativeMethods.ReadControl;
        if (readContent || denyDeleteSharing)
        {
            desiredAccess |= NativeMethods.GenericRead;
        }

        var directoryShare = NativeMethods.FileShareRead | NativeMethods.FileShareWrite;
        if (!denyDeleteSharing)
        {
            directoryShare |= NativeMethods.FileShareDelete;
        }

        var handle = NativeMethods.CreateFile(
            path,
            desiredAccess,
            directory ? directoryShare : NativeMethods.FileShareRead,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagOpenReparsePoint |
                (directory ? NativeMethods.FileFlagBackupSemantics : NativeMethods.FileAttributeNormal),
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Failure();
        }

        return handle;
    }

    private static unsafe TrustedNodePayloadPathInspection InspectHandle(
        SafeFileHandle handle,
        string _,
        bool __)
    {
        if (!NativeMethods.GetFileInformationByHandleEx(
                handle,
                NativeMethods.FileAttributeTagInfo,
                out var information,
                checked((uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInformation>())))
        {
            throw Failure();
        }

        var attributes = (FileAttributes)information.FileAttributes;
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
                throw Failure();
            }

            var finalPath = NormalizeNativeFinalPath(new string(pointer, 0, checked((int)length)));
            return new TrustedNodePayloadPathInspection(
                finalPath,
                (attributes & FileAttributes.Directory) != 0,
                (attributes & FileAttributes.ReparsePoint) != 0);
        }
    }

    private static unsafe RawSecurityDescriptor ReadSecurityDescriptor(
        SafeFileHandle handle,
        string requestedPath)
    {
        _ = requestedPath;
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
            throw Failure();
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
                throw Failure();
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

        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    private static string CanonicalLocalAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Contains('\0'))
        {
            throw Failure();
        }

        var root = Path.GetPathRoot(path);
        if (root is null || path.IndexOf(':', root.Length) >= 0)
        {
            throw Failure();
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return StringComparer.OrdinalIgnoreCase.Equals(normalizedCandidate, normalizedRoot) ||
            normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineRelative(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static bool ContainsEnvironmentLeaf(string relativePath) =>
        relativePath.Split('/').Any(segment =>
            segment.StartsWith(".env", StringComparison.OrdinalIgnoreCase));

    private static void DisposeHandles(IReadOnlyList<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }

    private static NodePayloadManifestException Failure() => new();

    private sealed class DirectoryChainValidation : IDisposable
    {
        private IReadOnlyList<SafeFileHandle>? _pathLeaseHandles;

        internal DirectoryChainValidation(
            string canonicalRoot,
            IReadOnlyList<SafeFileHandle> pathLeaseHandles)
        {
            CanonicalRoot = canonicalRoot;
            _pathLeaseHandles = pathLeaseHandles;
        }

        internal string CanonicalRoot { get; }

        internal IReadOnlyList<SafeFileHandle> TakePathLeaseHandles()
        {
            var handles = _pathLeaseHandles ?? throw Failure();
            if (handles.Count == 0)
            {
                throw Failure();
            }

            _pathLeaseHandles = null;
            return handles;
        }

        public void Dispose()
        {
            if (_pathLeaseHandles is { } handles)
            {
                DisposeHandles(handles);
            }

            _pathLeaseHandles = null;
        }
    }

    private sealed class ValidationSnapshot : IDisposable
    {
        private IReadOnlyList<SafeFileHandle>? _pathLeaseHandles;

        internal ValidationSnapshot(
            string canonicalRoot,
            string manifestPath,
            NodePayloadManifestV1 manifest,
            IReadOnlyDictionary<string, string> artifactPaths,
            IReadOnlyList<SafeFileHandle> pathLeaseHandles)
        {
            CanonicalRoot = canonicalRoot;
            ManifestPath = manifestPath;
            Manifest = manifest;
            ArtifactPaths = artifactPaths;
            _pathLeaseHandles = pathLeaseHandles;
        }

        internal string CanonicalRoot { get; }

        internal string ManifestPath { get; }

        internal NodePayloadManifestV1 Manifest { get; }

        internal IReadOnlyDictionary<string, string> ArtifactPaths { get; }

        internal IReadOnlyList<SafeFileHandle> TakePathLeaseHandles()
        {
            var handles = _pathLeaseHandles ?? throw Failure();
            _pathLeaseHandles = null;
            return handles;
        }

        public void Dispose()
        {
            if (_pathLeaseHandles is { } handles)
            {
                DisposeHandles(handles);
            }

            _pathLeaseHandles = null;
        }
    }
}
