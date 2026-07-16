using System.Reflection;
using System.Text.Json;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public sealed record ProductionReleaseCatalog(
    bool IsAvailable,
    string? ManifestDigest,
    string? CompatibilityIdentity)
{
    public const string ResourceName =
        "HowardLab.EbayCrm.AppHost.ProductionReleaseCatalog.v1.json";

    public static ProductionReleaseCatalog Unavailable { get; } =
        new(false, null, null);

    public static ProductionReleaseCatalog Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        try
        {
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null || stream.Length is <= 0 or > 64 * 1024)
            {
                return Unavailable;
            }
            using var memory = new MemoryStream(checked((int)stream.Length));
            stream.CopyTo(memory);
            return Parse(memory.ToArray());
        }
        catch (ProductionPayloadValidationException)
        {
            throw;
        }
        catch
        {
            throw new ProductionPayloadValidationException("production-release-catalog-invalid");
        }
    }

    internal static ProductionReleaseCatalog Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid();
            }
            var expected = new HashSet<string>(
                ["version", "available", "manifestDigest", "compatibilityIdentity"],
                StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!expected.Contains(property.Name) || !seen.Add(property.Name))
                {
                    throw Invalid();
                }
            }
            if (!seen.SetEquals(expected) ||
                !root.GetProperty("version").TryGetInt32(out var version) || version != 1 ||
                root.GetProperty("available").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Invalid();
            }

            var available = root.GetProperty("available").GetBoolean();
            var digest = NullableString(root.GetProperty("manifestDigest"));
            var compatibility = NullableString(root.GetProperty("compatibilityIdentity"));
            if (!available)
            {
                if (digest is not null || compatibility is not null)
                {
                    throw Invalid();
                }
                return Unavailable;
            }
            if (!ProductionPayloadManifestV2.IsHex(digest, lowerOnly: true) ||
                string.IsNullOrWhiteSpace(compatibility) || compatibility.Length > 256)
            {
                throw Invalid();
            }
            return new ProductionReleaseCatalog(true, digest, compatibility);
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

    private static string? NullableString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        _ => throw Invalid(),
    };

    private static ProductionPayloadValidationException Invalid() =>
        new("production-release-catalog-invalid");
}
