using System.Collections.ObjectModel;
using System.Text.Json;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public sealed class NodePayloadArtifactV1
{
    internal NodePayloadArtifactV1(string path, long length, string sha256)
    {
        Path = path;
        Length = length;
        Sha256 = sha256;
    }

    public string Path { get; }

    public long Length { get; }

    public string Sha256 { get; }
}

public sealed class NodePayloadManifestV1
{
    public const int CurrentVersion = 1;
    public const int MaxManifestBytes = 1_048_576;
    public const int MaxBuildIdentityChars = 1_024;
    public const int MaxPathChars = NodePayloadPath.MaxChars;
    public const int MaxArtifactCount = 4_096;

    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "version",
        "buildIdentity",
        "nodeExecutable",
        "serverEntrypoint",
        "workerEntrypoint",
        "artifacts",
    };

    private static readonly HashSet<string> ArtifactProperties = new(StringComparer.Ordinal)
    {
        "path",
        "length",
        "sha256",
    };

    private NodePayloadManifestV1(
        string buildIdentity,
        string nodeExecutable,
        string serverEntrypoint,
        string workerEntrypoint,
        IReadOnlyList<NodePayloadArtifactV1> artifacts)
    {
        BuildIdentity = buildIdentity;
        NodeExecutable = nodeExecutable;
        ServerEntrypoint = serverEntrypoint;
        WorkerEntrypoint = workerEntrypoint;
        Artifacts = new ReadOnlyCollection<NodePayloadArtifactV1>(artifacts.ToArray());
    }

    public int Version => CurrentVersion;

    public string BuildIdentity { get; }

    public string NodeExecutable { get; }

    public string ServerEntrypoint { get; }

    public string WorkerEntrypoint { get; }

    public IReadOnlyList<NodePayloadArtifactV1> Artifacts { get; }

    public static NodePayloadManifestV1 Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is 0 or > MaxManifestBytes)
        {
            throw Failure();
        }

        try
        {
            var reader = new Utf8JsonReader(
                utf8Json,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            var manifest = ReadManifest(ref reader);
            if (reader.Read())
            {
                throw Failure();
            }

            return manifest;
        }
        catch (NodePayloadManifestException)
        {
            throw;
        }
        catch (Exception error) when (
            error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw Failure();
        }
    }

    private static NodePayloadManifestV1 ReadManifest(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw Failure();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? version = null;
        string? buildIdentity = null;
        string? nodeExecutable = null;
        string? serverEntrypoint = null;
        string? workerEntrypoint = null;
        IReadOnlyList<NodePayloadArtifactV1>? artifacts = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw Failure();
            }

            var property = reader.GetString();
            if (property is null || !RootProperties.Contains(property) || !seen.Add(property) || !reader.Read())
            {
                throw Failure();
            }

            switch (property)
            {
                case "version":
                    version = ReadInt32(ref reader);
                    break;
                case "buildIdentity":
                    buildIdentity = ReadString(ref reader);
                    break;
                case "nodeExecutable":
                    nodeExecutable = ReadString(ref reader);
                    break;
                case "serverEntrypoint":
                    serverEntrypoint = ReadString(ref reader);
                    break;
                case "workerEntrypoint":
                    workerEntrypoint = ReadString(ref reader);
                    break;
                case "artifacts":
                    artifacts = ReadArtifacts(ref reader);
                    break;
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject ||
            seen.Count != RootProperties.Count ||
            version != CurrentVersion ||
            !IsValidBuildIdentity(buildIdentity) ||
            !NodePayloadPath.IsCanonicalRelative(nodeExecutable) ||
            !NodePayloadPath.IsCanonicalRelative(serverEntrypoint) ||
            !NodePayloadPath.IsCanonicalRelative(workerEntrypoint) ||
            artifacts is null)
        {
            throw Failure();
        }

        var bootstrapPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            nodeExecutable!,
            serverEntrypoint!,
            workerEntrypoint!,
        };
        if (bootstrapPaths.Count != 3)
        {
            throw Failure();
        }

        var declared = new HashSet<string>(
            artifacts.Select(artifact => artifact.Path),
            StringComparer.Ordinal);
        if (!declared.Contains(nodeExecutable!) ||
            !declared.Contains(serverEntrypoint!) ||
            !declared.Contains(workerEntrypoint!))
        {
            throw Failure();
        }

        return new NodePayloadManifestV1(
            buildIdentity!,
            nodeExecutable!,
            serverEntrypoint!,
            workerEntrypoint!,
            artifacts);
    }

    private static IReadOnlyList<NodePayloadArtifactV1> ReadArtifacts(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw Failure();
        }

        var artifacts = new List<NodePayloadArtifactV1>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (artifacts.Count >= MaxArtifactCount || reader.TokenType != JsonTokenType.StartObject)
            {
                throw Failure();
            }

            var artifact = ReadArtifact(ref reader);
            if (!paths.Add(artifact.Path))
            {
                throw Failure();
            }

            artifacts.Add(artifact);
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw Failure();
        }

        return artifacts;
    }

    private static NodePayloadArtifactV1 ReadArtifact(ref Utf8JsonReader reader)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? path = null;
        long? length = null;
        string? sha256 = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw Failure();
            }

            var property = reader.GetString();
            if (property is null || !ArtifactProperties.Contains(property) || !seen.Add(property) || !reader.Read())
            {
                throw Failure();
            }

            switch (property)
            {
                case "path":
                    path = ReadString(ref reader);
                    break;
                case "length":
                    length = ReadInt64(ref reader);
                    break;
                case "sha256":
                    sha256 = ReadString(ref reader);
                    break;
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject ||
            seen.Count != ArtifactProperties.Count ||
            !NodePayloadPath.IsCanonicalRelative(path) ||
            length is null or < 0 ||
            !IsCanonicalSha256(sha256))
        {
            throw Failure();
        }

        return new NodePayloadArtifactV1(path!, length.Value, sha256!);
    }

    private static string ReadString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw Failure();
        }

        return reader.GetString() ?? throw Failure();
    }

    private static int ReadInt32(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var value))
        {
            throw Failure();
        }

        return value;
    }

    private static long ReadInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
        {
            throw Failure();
        }

        return value;
    }

    private static bool IsValidBuildIdentity(string? buildIdentity) =>
        !string.IsNullOrWhiteSpace(buildIdentity) &&
        buildIdentity.Length <= MaxBuildIdentityChars &&
        !buildIdentity.Any(char.IsControl);

    private static bool IsCanonicalSha256(string? sha256) =>
        sha256 is { Length: 64 } &&
        sha256.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static NodePayloadManifestException Failure() => new();
}
