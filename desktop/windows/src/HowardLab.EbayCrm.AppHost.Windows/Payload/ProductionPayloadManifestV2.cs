using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public sealed record ProductionPayloadBounds(
    int MaxFiles = 500_000,
    int MaxRelativePathChars = 512,
    long MaxManifestBytes = 128L * 1024 * 1024,
    long MaxAggregateBytes = 8L * 1024 * 1024 * 1024)
{
    public void Validate()
    {
        if (MaxFiles is <= 0 or > 500_000 ||
            MaxRelativePathChars is <= 0 or > 512 ||
            MaxManifestBytes is <= 0 or > 128L * 1024 * 1024 ||
            MaxAggregateBytes is <= 0 or > 8L * 1024 * 1024 * 1024)
        {
            throw new ProductionPayloadValidationException("production-payload-bounds-invalid");
        }
    }
}

public sealed record ProductionPayloadFileRecord(
    int Ordinal,
    string RelativePath,
    long Length,
    string Sha256);

public sealed record ProductionPayloadHeader(
    int ManifestSchemaVersion,
    string SourceCommit,
    string BuildIdentity,
    string NodeVersion,
    string YarnVersion,
    string TargetRid,
    string ProtocolIdentity,
    string GenerationIdentity,
    string NodeExecutable,
    string ServerEntrypoint,
    string WorkerEntrypoint,
    string SetupEntrypoint,
    string InstanceCommandEntrypoint,
    string AcceptanceEntrypoint,
    string AcceptanceCleanupEntrypoint,
    string CompatibilityPreflightEntrypoint,
    string FrontendEntrypoint,
    string DatabaseManifestDigest,
    string FrontendConfigurationDigest,
    ProductionPayloadBounds Bounds);

public sealed record ProductionPayloadManifestV2(
    ProductionPayloadHeader Header,
    IReadOnlyList<ProductionPayloadFileRecord> Files,
    string CanonicalDigest)
{
    public static ProductionPayloadManifestV2 Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (!MemoryMarshal.TryGetArray(utf8Json, out var segment) || segment.Array is null)
        {
            return Parse(new MemoryStream(utf8Json.ToArray(), writable: false));
        }
        using var stream = new MemoryStream(
            segment.Array,
            segment.Offset,
            segment.Count,
            writable: false,
            publiclyVisible: false);
        return Parse(stream);
    }

    public static ProductionPayloadManifestV2 Parse(Stream utf8Json, int chunkSize = 128 * 1024)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(utf8Json);
            if (!utf8Json.CanRead || !utf8Json.CanSeek ||
                utf8Json.Length is <= 0 ||
                utf8Json.Length > new ProductionPayloadBounds().MaxManifestBytes ||
                chunkSize is <= 0 or > 128 * 1024)
            {
                throw Failure("production-manifest-invalid");
            }
            utf8Json.Position = 0;
            var reader = new StreamingJsonTokenReader(utf8Json, chunkSize);
            ReadToken(reader, JsonTokenType.StartObject);
            ReadProperty(reader, "header");
            ReadToken(reader, JsonTokenType.StartObject);
            var schemaVersion = ReadInt(reader, "manifestSchemaVersion");
            var sourceCommit = ReadString(reader, "sourceCommit");
            var buildIdentity = ReadString(reader, "buildIdentity");
            var nodeVersion = ReadString(reader, "nodeVersion");
            var yarnVersion = ReadString(reader, "yarnVersion");
            var targetRid = ReadString(reader, "targetRid");
            var protocolIdentity = ReadString(reader, "protocolIdentity");
            var generationIdentity = ReadString(reader, "generationIdentity");
            var nodeExecutable = ReadString(reader, "nodeExecutable");
            var serverEntrypoint = ReadString(reader, "serverEntrypoint");
            var workerEntrypoint = ReadString(reader, "workerEntrypoint");
            var setupEntrypoint = ReadString(reader, "setupEntrypoint");
            var instanceCommandEntrypoint = ReadString(reader, "instanceCommandEntrypoint");
            var acceptanceEntrypoint = ReadString(reader, "acceptanceEntrypoint");
            var acceptanceCleanupEntrypoint = ReadString(reader, "acceptanceCleanupEntrypoint");
            var compatibilityPreflightEntrypoint = ReadString(reader, "compatibilityPreflightEntrypoint");
            var frontendEntrypoint = ReadString(reader, "frontendEntrypoint");
            var databaseManifestDigest = ReadString(reader, "databaseManifestDigest");
            var frontendConfigurationDigest = ReadString(reader, "frontendConfigurationDigest");
            ReadProperty(reader, "bounds");
            ReadToken(reader, JsonTokenType.StartObject);
            var bounds = new ProductionPayloadBounds(
                ReadInt(reader, "maxFiles"),
                ReadInt(reader, "maxRelativePathChars"),
                ReadLong(reader, "maxManifestBytes"),
                ReadLong(reader, "maxAggregateBytes"));
            ReadToken(reader, JsonTokenType.EndObject);
            ReadToken(reader, JsonTokenType.EndObject);
            bounds.Validate();
            reader.SetDeclaredMaximum(bounds.MaxManifestBytes);

            var header = new ProductionPayloadHeader(
                schemaVersion,
                sourceCommit,
                buildIdentity,
                nodeVersion,
                yarnVersion,
                targetRid,
                protocolIdentity,
                generationIdentity,
                nodeExecutable,
                serverEntrypoint,
                workerEntrypoint,
                setupEntrypoint,
                instanceCommandEntrypoint,
                acceptanceEntrypoint,
                acceptanceCleanupEntrypoint,
                compatibilityPreflightEntrypoint,
                frontendEntrypoint,
                databaseManifestDigest,
                frontendConfigurationDigest,
                bounds);
            ReadProperty(reader, "files");
            ReadToken(reader, JsonTokenType.StartArray);
            var files = new List<ProductionPayloadFileRecord>();
            string? previousPath = null;
            long aggregate = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject || files.Count >= bounds.MaxFiles)
                {
                    throw Failure("production-payload-bounds-invalid");
                }
                var ordinal = ReadInt(reader, "ordinal");
                var relativePath = ReadString(reader, "relativePath");
                var length = ReadLong(reader, "length");
                var sha256 = ReadString(reader, "sha256");
                ReadToken(reader, JsonTokenType.EndObject);
                if (ordinal != files.Count || length < 0 ||
                    relativePath.Length > bounds.MaxRelativePathChars ||
                    !NodePayloadPath.IsCanonicalRelative(relativePath) ||
                    previousPath is not null && StringComparer.Ordinal.Compare(previousPath, relativePath) >= 0 ||
                    previousPath is not null && StringComparer.OrdinalIgnoreCase.Equals(previousPath, relativePath) ||
                    !IsHex(sha256, lowerOnly: false) ||
                    !StringComparer.Ordinal.Equals(sha256, sha256.ToUpperInvariant()))
                {
                    throw Failure("production-manifest-record-invalid");
                }
                aggregate = checked(aggregate + length);
                if (aggregate > bounds.MaxAggregateBytes)
                {
                    throw Failure("production-payload-bounds-invalid");
                }
                files.Add(new ProductionPayloadFileRecord(ordinal, relativePath, length, sha256));
                previousPath = relativePath;
            }
            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw Failure("production-manifest-invalid");
            }
            var digest = ReadString(reader, "canonicalDigest");
            ReadToken(reader, JsonTokenType.EndObject);
            if (reader.Read())
            {
                throw Failure("production-manifest-invalid");
            }

            var manifest = new ProductionPayloadManifestV2(
                header,
                files.AsReadOnly(),
                digest);
            manifest.ValidateShape();
            utf8Json.Position = 0;
            var comparison = new CanonicalComparisonBufferWriter(utf8Json);
            using (var writer = new Utf8JsonWriter(
                comparison,
                new JsonWriterOptions { Indented = false }))
            {
                manifest.Write(writer, includeDigest: true);
            }
            if (!comparison.Complete())
            {
                throw Failure("production-manifest-noncanonical");
            }
            return manifest;
        }
        catch (ProductionPayloadValidationException)
        {
            throw;
        }
        catch
        {
            throw Failure("production-manifest-invalid");
        }
    }

    public byte[] Serialize()
    {
        ValidateShape();
        return SerializeCore();
    }

    private byte[] SerializeCore()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            Write(writer, includeDigest: true);
        }
        return stream.ToArray();
    }

    internal void Write(Utf8JsonWriter writer, bool includeDigest)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("header");
        ProductionPayloadCanonicalizer.WriteHeader(writer, Header);
        writer.WritePropertyName("files");
        writer.WriteStartArray();
        foreach (var file in Files)
        {
            ProductionPayloadCanonicalizer.WriteFile(writer, file);
        }
        writer.WriteEndArray();
        if (includeDigest)
        {
            writer.WriteString("canonicalDigest", CanonicalDigest);
        }
        writer.WriteEndObject();
    }

    internal void ValidateShape()
    {
        Header.Bounds.Validate();
        if (Header.ManifestSchemaVersion != 2 ||
            !IsBounded(Header.SourceCommit, 128) ||
            !IsBounded(Header.BuildIdentity, 256) ||
            !IsBounded(Header.NodeVersion, 64) ||
            !IsBounded(Header.YarnVersion, 64) ||
            !IsBounded(Header.TargetRid, 64) ||
            !IsBounded(Header.ProtocolIdentity, 128) ||
            !IsBounded(Header.GenerationIdentity, 128) ||
            !IsHex(Header.DatabaseManifestDigest, lowerOnly: false) ||
            !IsHex(Header.FrontendConfigurationDigest, lowerOnly: false) ||
            !IsHex(CanonicalDigest, lowerOnly: true))
        {
            throw Failure("production-manifest-invalid");
        }

        var entrypoints = new[]
        {
            Header.NodeExecutable, Header.ServerEntrypoint, Header.WorkerEntrypoint,
            Header.SetupEntrypoint, Header.InstanceCommandEntrypoint,
            Header.AcceptanceEntrypoint, Header.AcceptanceCleanupEntrypoint,
            Header.CompatibilityPreflightEntrypoint, Header.FrontendEntrypoint,
        };
        if (entrypoints.Any(path =>
                path.Length > Header.Bounds.MaxRelativePathChars ||
                !NodePayloadPath.IsCanonicalRelative(path)))
        {
            throw Failure("production-manifest-path-invalid");
        }

        if (Files.Count > Header.Bounds.MaxFiles)
        {
            throw Failure("production-payload-bounds-invalid");
        }

        long aggregate = 0;
        string? previousPath = null;
        for (var index = 0; index < Files.Count; index++)
        {
            var file = Files[index];
            if (file.Ordinal != index ||
                file.Length < 0 ||
                file.RelativePath.Length > Header.Bounds.MaxRelativePathChars ||
                !NodePayloadPath.IsCanonicalRelative(file.RelativePath) ||
                !IsHex(file.Sha256, lowerOnly: false) ||
                !StringComparer.Ordinal.Equals(file.Sha256, file.Sha256.ToUpperInvariant()) ||
                previousPath is not null && StringComparer.Ordinal.Compare(previousPath, file.RelativePath) >= 0 ||
                previousPath is not null && StringComparer.OrdinalIgnoreCase.Equals(previousPath, file.RelativePath))
            {
                throw Failure("production-manifest-record-invalid");
            }
            aggregate = checked(aggregate + file.Length);
            previousPath = file.RelativePath;
        }
        if (aggregate > Header.Bounds.MaxAggregateBytes)
        {
            throw Failure("production-payload-bounds-invalid");
        }
        ValidateNoCaseCollisions(Files);
    }

    internal static void ValidateCanonicalRecordOrder(
        IReadOnlyList<ProductionPayloadFileRecord> files)
    {
        string? previousPath = null;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (file.Ordinal != index ||
                previousPath is not null &&
                (StringComparer.Ordinal.Compare(previousPath, file.RelativePath) >= 0 ||
                 StringComparer.OrdinalIgnoreCase.Equals(previousPath, file.RelativePath)))
            {
                throw Failure("production-manifest-record-invalid");
            }
            previousPath = file.RelativePath;
        }
        ValidateNoCaseCollisions(files);
    }

    private static void ValidateNoCaseCollisions(
        IReadOnlyList<ProductionPayloadFileRecord> files)
    {
        if (files.Count < 2)
        {
            return;
        }
        var indices = ArrayPool<int>.Shared.Rent(files.Count);
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                indices[index] = index;
            }
            Array.Sort(
                indices,
                0,
                files.Count,
                Comparer<int>.Create((left, right) =>
                {
                    var comparison = StringComparer.OrdinalIgnoreCase.Compare(
                        files[left].RelativePath,
                        files[right].RelativePath);
                    return comparison != 0
                        ? comparison
                        : StringComparer.Ordinal.Compare(
                            files[left].RelativePath,
                            files[right].RelativePath);
                }));
            for (var index = 1; index < files.Count; index++)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(
                        files[indices[index - 1]].RelativePath,
                        files[indices[index]].RelativePath))
                {
                    throw Failure("production-manifest-record-invalid");
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indices);
        }
    }

    private static void ReadToken(StreamingJsonTokenReader reader, JsonTokenType expected)
    {
        if (!reader.Read() || reader.TokenType != expected)
        {
            throw Failure("production-manifest-invalid");
        }
    }

    private static void ReadProperty(StreamingJsonTokenReader reader, string name)
    {
        ReadToken(reader, JsonTokenType.PropertyName);
        if (!StringComparer.Ordinal.Equals(reader.StringValue, name))
        {
            throw Failure("production-manifest-invalid");
        }
    }

    private static string ReadString(StreamingJsonTokenReader reader, string property)
    {
        ReadProperty(reader, property);
        ReadToken(reader, JsonTokenType.String);
        if (reader.StringValue is not { } result)
        {
            throw Failure("production-manifest-invalid");
        }
        return result;
    }

    private static int ReadInt(StreamingJsonTokenReader reader, string property)
    {
        var value = ReadLong(reader, property);
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw Failure("production-manifest-invalid");
        }
        return checked((int)value);
    }

    private static long ReadLong(StreamingJsonTokenReader reader, string property)
    {
        ReadProperty(reader, property);
        ReadToken(reader, JsonTokenType.Number);
        if (reader.IntegerValue is not { } result)
        {
            throw Failure("production-manifest-invalid");
        }
        return result;
    }

    private static bool IsBounded(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Contains('\0');

    internal static bool IsHex(string? value, bool lowerOnly) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' ||
            character is >= 'a' and <= 'f' ||
            !lowerOnly && character is >= 'A' and <= 'F');

    private static ProductionPayloadValidationException Failure(string reason) => new(reason);

    private sealed class StreamingJsonTokenReader
    {
        private const int MaximumCarryBytes = 64 * 1024;
        private readonly Stream _stream;
        private readonly int _chunkSize;
        private byte[] _buffer;
        private int _buffered;
        private bool _endOfStream;
        private long _maximumBytes = new ProductionPayloadBounds().MaxManifestBytes;
        private long _totalBytesRead;
        private JsonReaderState _state = new(new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });

        internal StreamingJsonTokenReader(Stream stream, int chunkSize)
        {
            _stream = stream;
            _chunkSize = chunkSize;
            _buffer = new byte[Math.Min(Math.Max(chunkSize, 256), MaximumCarryBytes)];
        }

        internal JsonTokenType TokenType { get; private set; }

        internal string? StringValue { get; private set; }

        internal long? IntegerValue { get; private set; }

        internal void SetDeclaredMaximum(long maximumBytes)
        {
            _maximumBytes = maximumBytes;
            if (_totalBytesRead > maximumBytes)
            {
                throw Failure("production-payload-bounds-invalid");
            }
        }

        internal bool Read()
        {
            while (true)
            {
                var reader = new Utf8JsonReader(
                    _buffer.AsSpan(0, _buffered),
                    _endOfStream,
                    _state);
                try
                {
                    if (reader.Read())
                    {
                        TokenType = reader.TokenType;
                        StringValue = reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName
                            ? reader.GetString()
                            : null;
                        IntegerValue = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number)
                            ? number
                            : null;
                        Consume(checked((int)reader.BytesConsumed));
                        _state = reader.CurrentState;
                        return true;
                    }
                    Consume(checked((int)reader.BytesConsumed));
                    _state = reader.CurrentState;
                    if (_endOfStream)
                    {
                        return false;
                    }
                    Fill();
                }
                catch (JsonException)
                {
                    throw Failure("production-manifest-invalid");
                }
            }
        }

        private void Fill()
        {
            if (_buffered == _buffer.Length)
            {
                if (_buffer.Length == MaximumCarryBytes)
                {
                    throw Failure("production-manifest-invalid");
                }
                Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, MaximumCarryBytes));
            }
            var requested = Math.Min(_chunkSize, _buffer.Length - _buffered);
            var read = _stream.Read(_buffer, _buffered, requested);
            if (read == 0)
            {
                _endOfStream = true;
                return;
            }
            _buffered += read;
            _totalBytesRead = checked(_totalBytesRead + read);
            if (_totalBytesRead > _maximumBytes)
            {
                throw Failure("production-payload-bounds-invalid");
            }
        }

        private void Consume(int count)
        {
            if (count == 0)
            {
                return;
            }
            _buffered -= count;
            if (_buffered > 0)
            {
                Buffer.BlockCopy(_buffer, count, _buffer, 0, _buffered);
            }
        }
    }

    private sealed class CanonicalComparisonBufferWriter : IBufferWriter<byte>
    {
        private const int MaximumChunkBytes = 64 * 1024;
        private readonly Stream _expected;
        private byte[] _buffer = new byte[4096];
        private byte[] _expectedBuffer = new byte[4096];
        private bool _matches = true;

        internal CanonicalComparisonBufferWriter(Stream expected) =>
            _expected = expected;

        internal bool Complete() => _matches && _expected.ReadByte() == -1;

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            EnsureExpectedCapacity(count);
            var offset = 0;
            while (offset < count)
            {
                var read = _expected.Read(_expectedBuffer, offset, count - offset);
                if (read == 0)
                {
                    _matches = false;
                    break;
                }
                offset += read;
            }
            if (offset != count ||
                !_buffer.AsSpan(0, count).SequenceEqual(_expectedBuffer.AsSpan(0, count)))
            {
                _matches = false;
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer;
        }

        private void EnsureCapacity(int sizeHint)
        {
            var required = Math.Max(sizeHint, 1);
            if (required > MaximumChunkBytes)
            {
                throw Failure("production-manifest-invalid");
            }
            if (_buffer.Length < required)
            {
                _buffer = new byte[required];
            }
        }

        private void EnsureExpectedCapacity(int count)
        {
            if (_expectedBuffer.Length < count)
            {
                _expectedBuffer = new byte[count];
            }
        }
    }
}

public abstract record ProductionEntrypointRecordV1(
    string Kind,
    string SourcePath,
    string EmittedPath);

public sealed record ProductionLaunchExecutableEntrypointV1(
    string Role,
    string SourcePath,
    string EmittedPath,
    string Classification)
    : ProductionEntrypointRecordV1("launchExecutableJs", SourcePath, EmittedPath);

public sealed record ProductionImportRootEntrypointV1(
    string OwnerRole,
    string SourcePath,
    string EmittedPath,
    string Classification)
    : ProductionEntrypointRecordV1("importRootJs", SourcePath, EmittedPath);

public sealed record ProductionFrontendAssetEntrypointV1(
    string Role,
    string SourcePath,
    string EmittedPath,
    string BuildProvenance)
    : ProductionEntrypointRecordV1("frontendAsset", SourcePath, EmittedPath);

public sealed record ProductionEntrypointInventoryV1(
    int Version,
    IReadOnlyList<ProductionEntrypointRecordV1> Records)
{
    private static readonly HashSet<string> RootFields =
        new(["version", "records"], StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, HashSet<string>> KindFields =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["launchExecutableJs"] = new(
                ["kind", "role", "sourcePath", "emittedPath", "classification"],
                StringComparer.Ordinal),
            ["importRootJs"] = new(
                ["kind", "ownerRole", "sourcePath", "emittedPath", "classification"],
                StringComparer.Ordinal),
            ["frontendAsset"] = new(
                ["kind", "role", "sourcePath", "emittedPath", "buildProvenance"],
                StringComparer.Ordinal),
        };

    private static readonly HashSet<string> Classifications = new(
        ["immutableDesktopGuarded", "sideEffectFreeImport", "explicitlyUnreachable"],
        StringComparer.Ordinal);

    public static ProductionEntrypointInventoryV1 Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            if (utf8Json.IsEmpty || utf8Json.Length > 1024 * 1024 ||
                utf8Json.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                throw Invalid();
            }
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;
            RequireFields(root, RootFields);
            if (!root.GetProperty("version").TryGetInt32(out var version) || version != 1)
            {
                throw Invalid();
            }
            var recordsElement = root.GetProperty("records");
            if (recordsElement.ValueKind != JsonValueKind.Array ||
                recordsElement.GetArrayLength() is <= 0 or > 256)
            {
                throw Invalid();
            }

            var records = new List<ProductionEntrypointRecordV1>(recordsElement.GetArrayLength());
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var frontendCount = 0;
            foreach (var element in recordsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty("kind", out var kindElement) ||
                    kindElement.ValueKind != JsonValueKind.String ||
                    kindElement.GetString() is not { } kind ||
                    !KindFields.TryGetValue(kind, out var expectedFields))
                {
                    throw Invalid();
                }
                RequireFields(element, expectedFields);
                var source = RequiredString(element, "sourcePath");
                var destination = RequiredString(element, "emittedPath");
                if (!ValidPath(source) || !ValidPath(destination) ||
                    !sources.Add(source) || !emitted.Add(destination) ||
                    !allPaths.Add(source) || !allPaths.Add(destination))
                {
                    throw Invalid();
                }

                switch (kind)
                {
                    case "launchExecutableJs":
                    {
                        var role = RequiredString(element, "role");
                        var classification = RequiredString(element, "classification");
                        if (!ValidRole(role) || !roles.Add(role) ||
                            !Classifications.Contains(classification) ||
                            !source.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                            !destination.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        {
                            throw Invalid();
                        }
                        records.Add(new ProductionLaunchExecutableEntrypointV1(
                            role, source, destination, classification));
                        break;
                    }
                    case "importRootJs":
                    {
                        var owner = RequiredString(element, "ownerRole");
                        var classification = RequiredString(element, "classification");
                        if (!ValidRole(owner) || !Classifications.Contains(classification) ||
                            !source.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                            !destination.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        {
                            throw Invalid();
                        }
                        records.Add(new ProductionImportRootEntrypointV1(
                            owner, source, destination, classification));
                        break;
                    }
                    case "frontendAsset":
                    {
                        var role = RequiredString(element, "role");
                        var provenance = RequiredString(element, "buildProvenance");
                        if (role != "frontend" || !roles.Add(role) ||
                            !ValidToken(provenance) ||
                            !source.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                            !destination.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                        {
                            throw Invalid();
                        }
                        frontendCount++;
                        records.Add(new ProductionFrontendAssetEntrypointV1(
                            role, source, destination, provenance));
                        break;
                    }
                }
            }
            if (frontendCount != 1)
            {
                throw Invalid();
            }
            return new ProductionEntrypointInventoryV1(version, records);
        }
        catch (ProductionPayloadValidationException)
        {
            throw;
        }
        catch
        {
            throw Invalid();
        }
    }

    private static void RequireFields(JsonElement element, IReadOnlySet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid();
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw Invalid();
            }
        }
        if (!seen.SetEquals(expected))
        {
            throw Invalid();
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            throw Invalid();
        }
        return text;
    }

    private static bool ValidPath(string value) =>
        NodePayloadPath.IsCanonicalRelative(value) &&
        StringComparer.Ordinal.Equals(value, value.ToLowerInvariant());

    private static bool ValidRole(string value) =>
        value.Length is > 0 and <= 64 && ValidToken(value);

    private static bool ValidToken(string value) =>
        value.Length is > 0 and <= 128 && value.All(character =>
            character is >= 'a' and <= 'z' ||
            character is >= '0' and <= '9' ||
            character is '-' or '.' or '/' or '_');

    private static ProductionPayloadValidationException Invalid() =>
        new("production-entrypoint-inventory-invalid");
}
