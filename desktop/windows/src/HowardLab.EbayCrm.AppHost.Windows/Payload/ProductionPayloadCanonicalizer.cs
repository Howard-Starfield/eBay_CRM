using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public static class ProductionPayloadCanonicalizer
{
    public static string ComputeDigest(
        ProductionPayloadHeader header,
        IReadOnlyList<ProductionPayloadFileRecord> files)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(files);
        header.Bounds.Validate();
        if (files.Count > header.Bounds.MaxFiles)
        {
            throw new ProductionPayloadValidationException("production-payload-bounds-invalid");
        }
        ProductionPayloadManifestV2.ValidateCanonicalRecordOrder(files);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new HashingBufferWriter(hash);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("header");
            WriteHeader(writer, header);
            writer.WritePropertyName("files");
            writer.WriteStartArray();
            foreach (var file in files)
            {
                WriteFile(writer, file);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    internal static void WriteHeader(Utf8JsonWriter writer, ProductionPayloadHeader header)
    {
        writer.WriteStartObject();
        writer.WriteNumber("manifestSchemaVersion", header.ManifestSchemaVersion);
        writer.WriteString("sourceCommit", header.SourceCommit);
        writer.WriteString("buildIdentity", header.BuildIdentity);
        writer.WriteString("nodeVersion", header.NodeVersion);
        writer.WriteString("yarnVersion", header.YarnVersion);
        writer.WriteString("targetRid", header.TargetRid);
        writer.WriteString("protocolIdentity", header.ProtocolIdentity);
        writer.WriteString("generationIdentity", header.GenerationIdentity);
        writer.WriteString("nodeExecutable", header.NodeExecutable);
        writer.WriteString("serverEntrypoint", header.ServerEntrypoint);
        writer.WriteString("workerEntrypoint", header.WorkerEntrypoint);
        writer.WriteString("setupEntrypoint", header.SetupEntrypoint);
        writer.WriteString("instanceCommandEntrypoint", header.InstanceCommandEntrypoint);
        writer.WriteString("acceptanceEntrypoint", header.AcceptanceEntrypoint);
        writer.WriteString("acceptanceCleanupEntrypoint", header.AcceptanceCleanupEntrypoint);
        writer.WriteString("compatibilityPreflightEntrypoint", header.CompatibilityPreflightEntrypoint);
        writer.WriteString("frontendEntrypoint", header.FrontendEntrypoint);
        writer.WriteString("databaseManifestDigest", header.DatabaseManifestDigest);
        writer.WriteString("frontendConfigurationDigest", header.FrontendConfigurationDigest);
        writer.WritePropertyName("bounds");
        writer.WriteStartObject();
        writer.WriteNumber("maxFiles", header.Bounds.MaxFiles);
        writer.WriteNumber("maxRelativePathChars", header.Bounds.MaxRelativePathChars);
        writer.WriteNumber("maxManifestBytes", header.Bounds.MaxManifestBytes);
        writer.WriteNumber("maxAggregateBytes", header.Bounds.MaxAggregateBytes);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    internal static void WriteFile(Utf8JsonWriter writer, ProductionPayloadFileRecord file)
    {
        writer.WriteStartObject();
        writer.WriteNumber("ordinal", file.Ordinal);
        writer.WriteString("relativePath", file.RelativePath);
        writer.WriteNumber("length", file.Length);
        writer.WriteString("sha256", file.Sha256);
        writer.WriteEndObject();
    }

    private sealed class HashingBufferWriter : IBufferWriter<byte>
    {
        private const int MaximumChunkBytes = 64 * 1024;
        private readonly IncrementalHash _hash;
        private byte[] _buffer = new byte[4096];

        internal HashingBufferWriter(IncrementalHash hash) => _hash = hash;

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            _hash.AppendData(_buffer, 0, count);
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
                throw new ProductionPayloadValidationException("production-manifest-invalid");
            }
            if (_buffer.Length < required)
            {
                _buffer = new byte[required];
            }
        }
    }
}
