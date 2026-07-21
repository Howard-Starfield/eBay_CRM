using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Windows.Payload;

return Run(args);

static int Run(string[] arguments)
{
    try
    {
        if (arguments.Length < 2)
        {
            return Fail("payload-tool-usage-invalid");
        }
        return (arguments[0], arguments[1]) switch
        {
            ("manifest", "create") => CreateManifest(ParseOptions(arguments[2..])),
            ("manifest", "verify") => VerifyManifest(ParseOptions(arguments[2..])),
            ("closure", "materialize") => MaterializeClosure(ParseOptions(arguments[2..])),
            ("build", "execute") => ExecuteBuild(ParseOptions(arguments[2..])),
            ("frontend", "place") => PlaceFrontend(ParseOptions(arguments[2..])),
            ("staging", "normalize-sdk") => NormalizeSdk(ParseOptions(arguments[2..])),
            ("installation", "secure") => SecureInstallation(ParseOptions(arguments[2..])),
            _ => Fail("payload-tool-command-invalid"),
        };
    }
    catch (ProductionPayloadBuildException error)
    {
        return Fail(error.ReasonCode, error.Diagnostic);
    }
    catch (ProductionPayloadValidationException error)
    {
        return Fail(error.ReasonCode);
    }
    catch
    {
        return Fail("payload-tool-failed");
    }
}

static int ExecuteBuild(IReadOnlyDictionary<string, string?> options)
{
    RequireExactOptions(
        options,
        "node",
        "git-root",
        "build-root",
        "compiled-desktop-node-root");
    var builder = new ProductionPayloadBuilder();
    var buildRoot = Required(options, "build-root");
    builder.RunBuildCommands(
        Required(options, "node"),
        Required(options, "git-root"),
        buildRoot,
        Required(options, "compiled-desktop-node-root"));
    builder.PlaceBuiltFrontend(buildRoot);
    Console.WriteLine("build-executed-and-frontend-placed");
    return 0;
}

static int PlaceFrontend(IReadOnlyDictionary<string, string?> options)
{
    RequireExactOptions(options, "build-root");
    new ProductionPayloadBuilder().PlaceBuiltFrontend(Required(options, "build-root"));
    Console.WriteLine("frontend-placed");
    return 0;
}

static int SecureInstallation(IReadOnlyDictionary<string, string?> options)
{
    RequireExactOptions(options, "root");
    new ProductionPayloadBuilder().ApplyAndVerifyInstalledReadExecuteAcl(
        Required(options, "root"));
    Console.WriteLine("installation-secured-and-verified");
    return 0;
}

static int NormalizeSdk(IReadOnlyDictionary<string, string?> options)
{
    RequireExactOptions(options, "build-root", "source-project");
    var result = new ProductionPayloadBuilder().NormalizeStagedTwentySdkProject(
        Required(options, "build-root"),
        Required(options, "source-project"));
    Console.WriteLine(
        $"windows-build-normalization preimage={result.PreimageSha256} output={result.OutputSha256} replacedCommands={result.ReplacedCommandCount} addedDependencies={result.AddedDependencyCount} helper={result.HelperSha256}");
    return 0;
}

static int CreateManifest(IReadOnlyDictionary<string, string?> options)
{
    RequireExactOptions(options, "root", "catalog-output");
    var root = PayloadToolPathPolicy.ValidateExistingDirectory(Required(options, "root"));
    var headerPath = PayloadToolPathPolicy.ValidateExistingFile(
        Path.Combine(root, "production-payload-header-v2.json"));
    var manifestPath = PayloadToolPathPolicy.ValidateAvailableFile(
        Path.Combine(root, ProductionPayloadValidator.ManifestFileName));
    var catalogOutput = PayloadToolPathPolicy.ValidateAvailableFile(
        Required(options, "catalog-output"));
    if (PayloadToolPathPolicy.IsSameOrUnder(catalogOutput, root) ||
        StringComparer.OrdinalIgnoreCase.Equals(catalogOutput, manifestPath))
    {
        throw new InvalidDataException("payload-tool-path-invalid");
    }
    var header = JsonSerializer.Deserialize<ProductionPayloadHeader>(
        PayloadToolPathPolicy.ReadBoundedUtf8File(headerPath, 1024 * 1024),
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) ?? throw new InvalidDataException();
    var builder = new ProductionPayloadBuilder();
    var manifest = builder.CreateManifest(root, header);
    PayloadToolPathPolicy.WriteAtomicPair(
        manifestPath,
        manifest.Serialize(),
        catalogOutput,
        SerializeCatalog(manifest.CanonicalDigest, header.BuildIdentity));
    Console.WriteLine($"manifest-created {manifest.Files.Count} {manifest.CanonicalDigest}");
    return 0;
}

static int VerifyManifest(IReadOnlyDictionary<string, string?> options)
{
    RequireExactOptions(options, "root", "catalog-output");
    var root = Required(options, "root");
    var catalog = ReadCatalog(Required(options, "catalog-output"));
    new ProductionPayloadBuilder().VerifyManifestWithTemporaryProfile(root, catalog);
    Console.WriteLine($"manifest-verified {catalog.ManifestDigest}");
    return 0;
}

static int MaterializeClosure(IReadOnlyDictionary<string, string?> options)
{
    RequireExactOptions(
        options,
        "build-root",
        "payload-root",
        "node",
        "node-archive",
        "inventory",
        "compiled-desktop-node-root",
        "closure-only");
    if (options["closure-only"] is not null)
    {
        throw new InvalidDataException();
    }
    var builder = new ProductionPayloadBuilder();
    var result = builder.Materialize(new ProductionPayloadMaterializationRequest(
        Required(options, "build-root"),
        Required(options, "payload-root"),
        Required(options, "node"),
        Required(options, "inventory"),
        ClosureOnlyHeader(),
        ClosureOnly: true,
        Required(options, "compiled-desktop-node-root"),
        NodeArchivePath: Required(options, "node-archive")));
    Console.WriteLine(result.Classification);
    return result.Classification == "untrusted-build-closure" ? 0 : 1;
}

static ProductionPayloadHeader ClosureOnlyHeader() => new(
    2,
    new string('0', 40),
    "untrusted-build-closure",
    ProductionPayloadBuilder.NodeVersion,
    ProductionPayloadBuilder.YarnVersion,
    "win-x64",
    "pending",
    "pending",
    "node.exe",
    "packages/twenty-server/dist/main.js",
    "pending/worker.js",
    "pending/setup.js",
    "pending/migrate.js",
    "pending/acceptance.js",
    "pending/cleanup.js",
    "pending/preflight.js",
    "packages/twenty-server/dist/front/index.html",
    new string('0', 64),
    new string('0', 64),
    new());

static Dictionary<string, string?> ParseOptions(string[] arguments)
{
    var options = new Dictionary<string, string?>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index++)
    {
        var token = arguments[index];
        if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2)
        {
            throw new InvalidDataException();
        }
        var name = token[2..];
        string? value = null;
        if (index + 1 < arguments.Length &&
            !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = arguments[++index];
        }
        if (!options.TryAdd(name, value))
        {
            throw new InvalidDataException();
        }
    }
    return options;
}

static void RequireExactOptions(
    IReadOnlyDictionary<string, string?> options,
    params string[] names)
{
    if (options.Count != names.Length || names.Any(name => !options.ContainsKey(name)))
    {
        throw new InvalidDataException();
    }
}

static string Required(IReadOnlyDictionary<string, string?> options, string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidDataException();

static byte[] SerializeCatalog(string digest, string compatibilityIdentity)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", 1);
        writer.WriteBoolean("available", true);
        writer.WriteString("manifestDigest", digest);
        writer.WriteString("compatibilityIdentity", compatibilityIdentity);
        writer.WriteEndObject();
    }
    return stream.ToArray();
}

static ProductionReleaseCatalog ReadCatalog(string path)
{
    path = PayloadToolPathPolicy.ValidateExistingFile(path);
    using var document = JsonDocument.Parse(
        PayloadToolPathPolicy.ReadBoundedUtf8File(path, 64 * 1024));
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object ||
        root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "available", "compatibilityIdentity", "manifestDigest", "version" }) is false ||
        root.GetProperty("version").GetInt32() != 1 ||
        !root.GetProperty("available").GetBoolean())
    {
        throw new InvalidDataException();
    }
    return new ProductionReleaseCatalog(
        true,
        root.GetProperty("manifestDigest").GetString(),
        root.GetProperty("compatibilityIdentity").GetString());
}

static int Fail(string reason, string? diagnostic = null)
{
    PayloadToolFailureOutput.Write(Console.Error, reason, diagnostic);
    return 1;
}

public static class PayloadToolFailureOutput
{
    public const string BeginMarker = "production-build-diagnostic-begin";
    public const string EndMarker = "production-build-diagnostic-end";

    public static void Write(TextWriter error, string reason, string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(error);
        error.WriteLine(reason);
        if (string.IsNullOrEmpty(diagnostic))
        {
            return;
        }

        error.WriteLine(BeginMarker);
        var safeDiagnostic = diagnostic
            .Replace(BeginMarker, "[REDACTED]", StringComparison.Ordinal)
            .Replace(EndMarker, "[REDACTED]", StringComparison.Ordinal);
        WriteBoundedDiagnosticBody(error, safeDiagnostic);
        error.WriteLine(EndMarker);
    }

    private static void WriteBoundedDiagnosticBody(TextWriter error, string diagnostic)
    {
        const int maximumBytes = 32 * 1024;
        var reserveNewline = !diagnostic.EndsWith('\n');
        var byteLimit = reserveNewline ? maximumBytes - 1 : maximumBytes;
        var bytes = 0;
        var lines = 0;
        var endsWithNewline = false;
        foreach (var rune in diagnostic.EnumerateRunes())
        {
            var text = rune.ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(text);
            if (bytes + runeBytes > byteLimit || (rune.Value == '\n' && lines == 200))
            {
                break;
            }
            error.Write(text);
            bytes += runeBytes;
            endsWithNewline = rune.Value == '\n';
            if (endsWithNewline) lines++;
        }
        if (!endsWithNewline && lines < 200)
        {
            error.Write('\n');
        }
    }
}

public static class PayloadToolPathPolicy
{
    private const int ErrorHandleEof = 38;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static string ValidateExistingFile(string path) =>
        WalkExact(ValidateAbsoluteLocal(path), expectDirectory: false, allowMissingFinal: false);

    public static string ValidateExistingDirectory(string path) =>
        WalkExact(ValidateAbsoluteLocal(path), expectDirectory: true, allowMissingFinal: false);

    public static string ValidateAvailableFile(string path) =>
        WalkExact(ValidateAbsoluteLocal(path), expectDirectory: false, allowMissingFinal: true);

    public static byte[] ReadBoundedUtf8File(string path, int maximumBytes)
    {
        path = ValidateExistingFile(path);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            if (maximumBytes <= 0 || stream.Length < 0 || stream.Length > maximumBytes)
            {
                throw new InvalidDataException("payload-tool-file-size-invalid");
            }
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (bytes.Length >= 3 &&
                bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                throw new InvalidDataException("payload-tool-file-encoding-invalid");
            }
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return bytes;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            DecoderFallbackException or OverflowException)
        {
            throw new InvalidDataException("payload-tool-file-read-invalid");
        }
    }

    public static void WriteAtomic(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var absolute = ValidateAvailableFile(path);
        CreateTrustedDirectoryTree(Path.GetDirectoryName(absolute)!);
        absolute = ValidateAvailableFile(absolute);
        var temporary = absolute + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _ = ValidateExistingFile(temporary);
            File.Move(temporary, absolute, overwrite: true);
            _ = ValidateExistingFile(absolute);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void WriteAtomicPair(
        string firstPath,
        byte[] firstBytes,
        string secondPath,
        byte[] secondBytes)
    {
        ArgumentNullException.ThrowIfNull(firstBytes);
        ArgumentNullException.ThrowIfNull(secondBytes);
        var targets = new[]
        {
            new AtomicTarget(ValidateAvailableFile(firstPath), firstBytes),
            new AtomicTarget(ValidateAvailableFile(secondPath), secondBytes),
        };
        if (StringComparer.OrdinalIgnoreCase.Equals(targets[0].Path, targets[1].Path))
        {
            throw new InvalidDataException("payload-tool-path-invalid");
        }
        foreach (var target in targets)
        {
            CreateTrustedDirectoryTree(Path.GetDirectoryName(target.Path)!);
            _ = ValidateAvailableFile(target.Path);
            WriteTemporary(target.TemporaryPath, target.Bytes);
        }
        var committed = false;
        try
        {
            foreach (var target in targets)
            {
                _ = ValidateAvailableFile(target.Path);
                if (File.Exists(target.Path))
                {
                    _ = ValidateExistingFile(target.Path);
                    File.Move(target.Path, target.BackupPath, overwrite: false);
                    target.BackupCreated = true;
                    _ = ValidateExistingFile(target.BackupPath);
                }
            }
            foreach (var target in targets)
            {
                _ = ValidateAvailableFile(target.Path);
                _ = ValidateExistingFile(target.TemporaryPath);
                File.Move(target.TemporaryPath, target.Path, overwrite: false);
                target.Installed = true;
                _ = ValidateExistingFile(target.Path);
            }
            committed = true;
            foreach (var target in targets)
            {
                if (target.BackupCreated)
                {
                    File.Delete(ValidateExistingFile(target.BackupPath));
                    target.BackupCreated = false;
                }
            }
        }
        catch
        {
            if (committed)
            {
                throw;
            }
            for (var index = targets.Length - 1; index >= 0; index--)
            {
                var target = targets[index];
                try
                {
                    if (target.Installed && File.Exists(target.Path))
                    {
                        File.Delete(ValidateExistingFile(target.Path));
                        target.Installed = false;
                    }
                    if (target.BackupCreated && File.Exists(target.BackupPath))
                    {
                        File.Move(
                            ValidateExistingFile(target.BackupPath),
                            ValidateAvailableFile(target.Path),
                            overwrite: false);
                        target.BackupCreated = false;
                    }
                }
                catch
                {
                    throw new InvalidDataException("payload-tool-atomic-rollback-failed");
                }
            }
            throw;
        }
        finally
        {
            foreach (var target in targets)
            {
                if (File.Exists(target.TemporaryPath))
                {
                    File.Delete(ValidateExistingFile(target.TemporaryPath));
                }
            }
        }
    }

    public static bool IsSameOrUnder(string candidate, string root) =>
        StringComparer.OrdinalIgnoreCase.Equals(candidate, root) ||
        candidate.StartsWith(
            Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void WriteTemporary(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        _ = ValidateExistingFile(path);
    }

    private sealed class AtomicTarget(string path, byte[] bytes)
    {
        internal string Path { get; } = path;
        internal byte[] Bytes { get; } = bytes;
        internal string TemporaryPath { get; } = path + ".tmp-" + Guid.NewGuid().ToString("N");
        internal string BackupPath { get; } = path + ".bak-" + Guid.NewGuid().ToString("N");
        internal bool BackupCreated { get; set; }
        internal bool Installed { get; set; }
    }

    private static void CreateTrustedDirectoryTree(string path)
    {
        var absolute = ValidateAbsoluteLocal(path);
        var root = Path.GetPathRoot(absolute)!;
        var current = root;
        EnsureNoAlternateDataStreams(current);
        foreach (var segment in absolute[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            if (!Directory.Exists(next) && !File.Exists(next))
            {
                Directory.CreateDirectory(next);
            }
            current = WalkExact(next, expectDirectory: true, allowMissingFinal: false);
        }
        if (!StringComparer.Ordinal.Equals(current, absolute))
        {
            throw new InvalidDataException("payload-tool-path-invalid");
        }
    }

    private static string WalkExact(string absolute, bool expectDirectory, bool allowMissingFinal)
    {
        var root = Path.GetPathRoot(absolute)!;
        var segments = absolute[root.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidDataException("payload-tool-path-invalid");
        }
        var current = root;
        try
        {
            EnsureNoAlternateDataStreams(current);
            for (var index = 0; index < segments.Length; index++)
            {
                var matches = new DirectoryInfo(current)
                    .EnumerateFileSystemInfos()
                    .Where(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.Name, segments[index]))
                    .ToArray();
                var final = index == segments.Length - 1;
                if (matches.Length == 0 && allowMissingFinal)
                {
                    return absolute;
                }
                if (matches.Length != 1 ||
                    !StringComparer.Ordinal.Equals(matches[0].Name, segments[index]) ||
                    matches[0].Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("payload-tool-path-invalid");
                }
                EnsureNoAlternateDataStreams(matches[0].FullName);
                if ((!final || expectDirectory) && matches[0] is not DirectoryInfo)
                {
                    throw new InvalidDataException("payload-tool-path-invalid");
                }
                if (final && !expectDirectory && matches[0] is not FileInfo)
                {
                    throw new InvalidDataException("payload-tool-path-invalid");
                }
                current = matches[0].FullName;
            }
            return Path.GetFullPath(current);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("payload-tool-path-invalid", error);
        }
    }

    private static string ValidateAbsoluteLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("payload-tool-path-invalid");
        }
        var absolute = Path.GetFullPath(path);
        var root = Path.GetPathRoot(absolute);
        if (string.IsNullOrEmpty(root) || absolute[root.Length..].Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("payload-tool-path-invalid");
        }
        return Path.TrimEndingDirectorySeparator(absolute);
    }

    private static void EnsureNoAlternateDataStreams(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var handle = FindFirstStreamW(ToExtendedLengthPath(path), 0, out var data, 0);
        if (handle == InvalidHandleValue)
        {
            if (Marshal.GetLastWin32Error() is 2 or ErrorHandleEof)
            {
                return;
            }
            throw new InvalidDataException("payload-tool-path-invalid");
        }
        try
        {
            do
            {
                if (!StringComparer.Ordinal.Equals(data.StreamName, "::$DATA"))
                {
                    throw new InvalidDataException("payload-tool-path-invalid");
                }
            }
            while (FindNextStreamW(handle, out data));
            if (Marshal.GetLastWin32Error() != ErrorHandleEof)
            {
                throw new InvalidDataException("payload-tool-path-invalid");
            }
        }
        finally
        {
            _ = FindClose(handle);
        }
    }

    private static string ToExtendedLengthPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        internal long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string StreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(
        string fileName,
        int infoLevel,
        out Win32FindStreamData data,
        int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr findHandle, out Win32FindStreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findHandle);
}
