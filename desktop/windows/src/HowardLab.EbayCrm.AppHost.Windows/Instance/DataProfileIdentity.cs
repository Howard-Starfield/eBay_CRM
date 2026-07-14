using System.Security.Cryptography;
using System.Text;

namespace HowardLab.EbayCrm.AppHost.Windows.Instance;

internal interface IProfilePathInspector
{
    DriveType GetDriveType(string root);

    FileAttributes? TryGetAttributes(string path);
}

internal sealed class WindowsProfilePathInspector : IProfilePathInspector
{
    internal static WindowsProfilePathInspector Instance { get; } = new();

    private WindowsProfilePathInspector()
    {
    }

    public DriveType GetDriveType(string root) => new DriveInfo(root).DriveType;

    public FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

public enum ProfileOwnershipErrorCode
{
    ProfileMustBeLocalFixedStorage,
    ProfileMutexSecurityMismatch,
}

public sealed class ProfileOwnershipException : Exception
{
    public ProfileOwnershipException(ProfileOwnershipErrorCode code)
        : base($"Data profile ownership error: {code}.")
    {
        Code = code;
    }

    public ProfileOwnershipErrorCode Code { get; }
}

public sealed record DataProfileIdentity
{
    private DataProfileIdentity(string canonicalPath, string profileHash)
    {
        CanonicalPath = canonicalPath;
        ProfileHash = profileHash;
    }

    public string CanonicalPath { get; }

    public string ProfileHash { get; }

    public static DataProfileIdentity Create(string path) =>
        Create(path, WindowsProfilePathInspector.Instance);

    internal static DataProfileIdentity Create(string path, IProfilePathInspector pathInspector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pathInspector);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ProfileOwnershipException(ProfileOwnershipErrorCode.ProfileMustBeLocalFixedStorage);
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.GetPathRoot(canonicalPath);
            if (root is null ||
                root.Length != 3 ||
                !char.IsAsciiLetter(root[0]) ||
                root[1] != Path.VolumeSeparatorChar ||
                root[2] != Path.DirectorySeparatorChar ||
                pathInspector.GetDriveType(root) != DriveType.Fixed)
            {
                throw new ProfileOwnershipException(ProfileOwnershipErrorCode.ProfileMustBeLocalFixedStorage);
            }

            EnsureNoReparsePoints(canonicalPath, pathInspector);
        }
        catch (ProfileOwnershipException)
        {
            throw;
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            throw new ProfileOwnershipException(ProfileOwnershipErrorCode.ProfileMustBeLocalFixedStorage);
        }

        var normalized = canonicalPath.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return new DataProfileIdentity(canonicalPath, hash);
    }

    internal static void EnsureNoReparsePoints(string canonicalPath, IProfilePathInspector pathInspector)
    {
        var root = Path.GetPathRoot(canonicalPath)!;
        var current = root;
        var relative = canonicalPath[root.Length..];
        foreach (var component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileAttributes? attributes;
            try
            {
                attributes = pathInspector.TryGetAttributes(current);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new ProfileOwnershipException(ProfileOwnershipErrorCode.ProfileMustBeLocalFixedStorage);
            }

            if (attributes is null)
            {
                return;
            }

            if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                throw new ProfileOwnershipException(ProfileOwnershipErrorCode.ProfileMustBeLocalFixedStorage);
            }
        }
    }
}
