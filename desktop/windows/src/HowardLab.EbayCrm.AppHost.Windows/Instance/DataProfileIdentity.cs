using System.Security.Cryptography;
using System.Text;

namespace HowardLab.EbayCrm.AppHost.Windows.Instance;

public sealed record DataProfileIdentity
{
    private DataProfileIdentity(string canonicalPath, string profileHash)
    {
        CanonicalPath = canonicalPath;
        ProfileHash = profileHash;
    }

    public string CanonicalPath { get; }

    public string ProfileHash { get; }

    public static DataProfileIdentity Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalized = canonicalPath.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return new DataProfileIdentity(canonicalPath, hash);
    }
}
