using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using HowardLab.EbayCrm.AppHost.Windows.Payload;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Composition;

internal sealed class PublishedNodeProbeRoleLaunchPlanProvider : IRoleLaunchPlanProvider
{
    private const string ManifestFileName = "node-payload-manifest-v1.json";
    private const string NodeRelativePath = "node.exe";
    private const string PackageRelativePath = "package.json";
    private const string ServerRelativePath = "app/probes/server-probe.js";
    private const string WorkerRelativePath = "app/probes/worker-probe.js";
    internal const string BuildIdentity = "published-node-probe/1";

    private readonly PublishedPayloadSnapshot _payload;
    private readonly string _systemRoot;
    private readonly Func<int> _healthPortAllocator;

    internal PublishedNodeProbeRoleLaunchPlanProvider(
        string nodeProbeRoot,
        Func<int> healthPortAllocator)
    {
        _payload = PublishedPayloadSnapshot.Load(nodeProbeRoot);
        _healthPortAllocator = healthPortAllocator ??
            throw new ArgumentNullException(nameof(healthPortAllocator));
        _systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ??
            throw new InvalidOperationException("SystemRoot is unavailable.");
    }

    public RoleLaunchPlan Create(RoleLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Role is not (RuntimeRole.Server or RuntimeRole.Worker) ||
            request.Generation.Role != request.Role)
        {
            throw new InvalidOperationException("The published Node probe role request is invalid.");
        }

        var healthPort = _healthPortAllocator();
        if (healthPort is < 1024 or > 65_535)
        {
            throw new InvalidOperationException("The published Node probe health port is invalid.");
        }

        var entrypoint = request.Role == RuntimeRole.Server
            ? _payload.ServerEntrypoint
            : _payload.WorkerEntrypoint;
        return new RoleLaunchPlan(
            request.Role,
            request.Generation,
            _payload.NodeExecutable,
            [entrypoint, healthPort.ToString(CultureInfo.InvariantCulture)],
            _payload.Root,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = _systemRoot,
            },
            new Dictionary<string, SecretValue>(StringComparer.OrdinalIgnoreCase),
            _payload.BuildIdentity,
            RoleReadinessStrategy.IdentityBoundHttp,
            healthPort,
            TimeSpan.FromMilliseconds(100),
            _payload.OpenLifetimeLease,
            _payload.OpenBootstrapLease,
            _payload.VerifyClosure);
    }

    private sealed class PublishedPayloadSnapshot
    {
        private readonly IReadOnlyDictionary<string, ExpectedFile> _artifacts;
        private readonly ExpectedFile _manifest;
        private readonly IReadOnlyList<string> _directories;

        private PublishedPayloadSnapshot(
            string root,
            NodePayloadManifestV1 manifest,
            ExpectedFile manifestFile,
            IReadOnlyDictionary<string, ExpectedFile> artifacts,
            IReadOnlyList<string> directories)
        {
            Root = root;
            BuildIdentity = manifest.BuildIdentity;
            NodeExecutable = artifacts[NodeRelativePath].FullPath;
            ServerEntrypoint = artifacts[ServerRelativePath].FullPath;
            WorkerEntrypoint = artifacts[WorkerRelativePath].FullPath;
            _manifest = manifestFile;
            _artifacts = artifacts;
            _directories = directories;
        }

        internal string Root { get; }

        internal string BuildIdentity { get; }

        internal string NodeExecutable { get; }

        internal string ServerEntrypoint { get; }

        internal string WorkerEntrypoint { get; }

        internal static PublishedPayloadSnapshot Load(string root)
        {
            try
            {
                var canonicalRoot = CanonicalExistingLocalDirectory(root);
                var manifestPath = Path.Combine(canonicalRoot, ManifestFileName);
                var manifestFile = SnapshotManifest(manifestPath);
                var manifest = NodePayloadManifestV1.Parse(manifestFile.Content);
                if (!StringComparer.Ordinal.Equals(
                        manifest.BuildIdentity,
                        PublishedNodeProbeRoleLaunchPlanProvider.BuildIdentity) ||
                    !StringComparer.Ordinal.Equals(manifest.NodeExecutable, NodeRelativePath) ||
                    !StringComparer.Ordinal.Equals(manifest.ServerEntrypoint, ServerRelativePath) ||
                    !StringComparer.Ordinal.Equals(manifest.WorkerEntrypoint, WorkerRelativePath))
                {
                    throw Failure();
                }

                var artifacts = new Dictionary<string, ExpectedFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var artifact in manifest.Artifacts)
                {
                    if (!IsGeneratedArtifact(artifact.Path))
                    {
                        throw Failure();
                    }

                    var fullPath = CombineUnderRoot(canonicalRoot, artifact.Path);
                    artifacts.Add(
                        artifact.Path,
                        new ExpectedFile(
                            artifact.Path,
                            fullPath,
                            artifact.Length,
                            artifact.Sha256,
                            Content: null));
                }

                if (!artifacts.ContainsKey(PackageRelativePath))
                {
                    throw Failure();
                }

                var directories = RequiredDirectories(canonicalRoot, artifacts.Keys);
                var snapshot = new PublishedPayloadSnapshot(
                    canonicalRoot,
                    manifest,
                    manifestFile,
                    artifacts,
                    directories);
                snapshot.VerifyClosure();
                ValidatePackage(snapshot._artifacts[PackageRelativePath]);
                return snapshot;
            }
            catch (AppHostOptionsException)
            {
                throw;
            }
            catch
            {
                throw Failure();
            }
        }

        internal IDisposable OpenLifetimeLease()
        {
            var handles = new List<SafeFileHandle>();
            try
            {
                VerifyClosure();
                handles.Add(OpenExactPath(Root, directory: true));
                foreach (var directory in _directories)
                {
                    handles.Add(OpenExactPath(directory, directory: true));
                }

                VerifyClosure();
                return new HandleLease(handles);
            }
            catch
            {
                DisposeHandles(handles);
                throw Failure();
            }
        }

        internal IDisposable OpenBootstrapLease()
        {
            var streams = new List<FileStream>();
            try
            {
                VerifyClosure();
                streams.Add(OpenAndVerify(_manifest));
                foreach (var artifact in _artifacts.Values)
                {
                    streams.Add(OpenAndVerify(artifact));
                }

                VerifyClosure();
                return new StreamLease(streams);
            }
            catch
            {
                DisposeStreams(streams);
                throw Failure();
            }
        }

        internal void VerifyClosure()
        {
            try
            {
                var expectedFiles = new HashSet<string>(
                    _artifacts.Keys,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ManifestFileName,
                };
                var expectedDirectories = new HashSet<string>(
                    _directories.Select(directory => RelativePath(Root, directory)),
                    StringComparer.OrdinalIgnoreCase);
                var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pending = new Queue<string>();
                pending.Enqueue(Root);
                while (pending.Count > 0)
                {
                    var directory = pending.Dequeue();
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            throw Failure();
                        }

                        var relative = RelativePath(Root, entry);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (!expectedDirectories.Contains(relative) ||
                                !seenDirectories.Add(relative))
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
                    !seenDirectories.SetEquals(expectedDirectories))
                {
                    throw Failure();
                }

                using (OpenAndVerify(_manifest))
                {
                }

                foreach (var artifact in _artifacts.Values)
                {
                    using (OpenAndVerify(artifact))
                    {
                    }
                }
            }
            catch (AppHostOptionsException)
            {
                throw;
            }
            catch
            {
                throw Failure();
            }
        }

        private static ExpectedFile SnapshotManifest(string manifestPath)
        {
            using var stream = OpenExactFile(manifestPath);
            if (stream.Length is <= 0 or > NodePayloadManifestV1.MaxManifestBytes)
            {
                throw Failure();
            }

            var content = new byte[checked((int)stream.Length)];
            stream.ReadExactly(content);
            return new ExpectedFile(
                ManifestFileName,
                manifestPath,
                content.LongLength,
                Convert.ToHexString(SHA256.HashData(content)),
                content);
        }

        private static IReadOnlyList<string> RequiredDirectories(
            string root,
            IEnumerable<string> artifacts)
        {
            var relatives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in artifacts)
            {
                var segments = artifact.Split('/');
                for (var count = 1; count < segments.Length; count++)
                {
                    relatives.Add(string.Join('/', segments.Take(count)));
                }
            }

            return relatives
                .OrderBy(relative => relative.Count(character => character == '/'))
                .ThenBy(relative => relative, StringComparer.OrdinalIgnoreCase)
                .Select(relative => CombineUnderRoot(root, relative))
                .ToArray();
        }

        private static bool IsGeneratedArtifact(string path)
        {
            if (StringComparer.Ordinal.Equals(path, NodeRelativePath) ||
                StringComparer.Ordinal.Equals(path, PackageRelativePath))
            {
                return true;
            }

            var segments = path.Split('/');
            return path.StartsWith("app/", StringComparison.Ordinal) &&
                path.EndsWith(".js", StringComparison.Ordinal) &&
                segments.All(segment =>
                    !segment.StartsWith(".env", StringComparison.OrdinalIgnoreCase));
        }

        private static void ValidatePackage(ExpectedFile package)
        {
            using var stream = OpenAndVerify(package);
            if (stream.Length is <= 0 or > 256)
            {
                throw Failure();
            }

            var content = new byte[checked((int)stream.Length)];
            stream.ReadExactly(content);
            if (content.Length >= 3 &&
                content[0] == 0xEF &&
                content[1] == 0xBB &&
                content[2] == 0xBF)
            {
                throw Failure();
            }

            try
            {
                using var document = JsonDocument.Parse(
                    content,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 2,
                    });
                var properties = document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.EnumerateObject().ToArray()
                    : [];
                if (properties.Length != 1 ||
                    !StringComparer.Ordinal.Equals(properties[0].Name, "type") ||
                    properties[0].Value.ValueKind != JsonValueKind.String ||
                    !StringComparer.Ordinal.Equals(properties[0].Value.GetString(), "module"))
                {
                    throw Failure();
                }
            }
            catch (AppHostOptionsException)
            {
                throw;
            }
            catch
            {
                throw Failure();
            }
        }

        private static FileStream OpenAndVerify(ExpectedFile expected)
        {
            var stream = OpenExactFile(expected.FullPath);
            try
            {
                if (stream.Length != expected.Length)
                {
                    throw Failure();
                }

                var actualHash = Convert.ToHexString(SHA256.HashData(stream));
                if (!StringComparer.Ordinal.Equals(actualHash, expected.Sha256))
                {
                    throw Failure();
                }

                stream.Position = 0;
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static string CanonicalExistingLocalDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathFullyQualified(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.Contains('\0'))
            {
                throw Failure();
            }

            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(canonical))
            {
                throw Failure();
            }

            for (var current = new DirectoryInfo(canonical);
                 current is not null;
                 current = current.Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw Failure();
                }
            }

            return canonical;
        }

        private static string CombineUnderRoot(string root, string relative)
        {
            var combined = Path.GetFullPath(Path.Combine(
                root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!combined.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Failure();
            }

            return combined;
        }

        private static string RelativePath(string root, string path) =>
            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

        private static FileStream OpenExactFile(string path) => new(
            OpenExactPath(path, directory: false),
            FileAccess.Read,
            bufferSize: 4096,
            isAsync: false);

        private static SafeFileHandle OpenExactPath(string path, bool directory)
        {
            var handle = NativeMethods.CreateFile(
                path,
                NativeMethods.GenericRead | NativeMethods.FileReadAttributes,
                directory
                    ? NativeMethods.FileShareRead | NativeMethods.FileShareWrite
                    : NativeMethods.FileShareRead,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagOpenReparsePoint |
                    (directory
                        ? NativeMethods.FileFlagBackupSemantics
                        : NativeMethods.FileAttributeNormal),
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw Failure();
            }

            try
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
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    ((attributes & FileAttributes.Directory) != 0) != directory)
                {
                    throw Failure();
                }

                var finalPathBuffer = new StringBuilder(32_768);
                var length = GetFinalPathNameByHandle(
                    handle,
                    finalPathBuffer,
                    checked((uint)finalPathBuffer.Capacity),
                    0);
                if (length == 0 || length >= finalPathBuffer.Capacity ||
                    !StringComparer.OrdinalIgnoreCase.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                            NormalizeNativeFinalPath(finalPathBuffer.ToString()))),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))))
                {
                    throw Failure();
                }

                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
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

        private static void DisposeHandles(IReadOnlyList<SafeFileHandle> handles)
        {
            for (var index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
        }

        private static void DisposeStreams(IReadOnlyList<FileStream> streams)
        {
            for (var index = streams.Count - 1; index >= 0; index--)
            {
                streams[index].Dispose();
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        private sealed class HandleLease(IReadOnlyList<SafeFileHandle> handles) : IDisposable
        {
            private IReadOnlyList<SafeFileHandle>? _handles = handles;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _handles, null) is { } active)
                {
                    DisposeHandles(active);
                }
            }
        }

        private sealed class StreamLease(IReadOnlyList<FileStream> streams) : IDisposable
        {
            private IReadOnlyList<FileStream>? _streams = streams;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _streams, null) is { } active)
                {
                    DisposeStreams(active);
                }
            }
        }

        private sealed record ExpectedFile(
            string RelativePath,
            string FullPath,
            long Length,
            string Sha256,
            byte[]? Content);
    }

    private static AppHostOptionsException Failure() =>
        new("node-probe-payload-invalid");
}
