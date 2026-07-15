using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;

namespace HowardLab.EbayCrm.AppHost.Windows.Instance;

public sealed record ProfileRuntimeIdentity(Guid ClusterId, SecretValue Password);

public interface IProfileRuntimeIdentityStore
{
    Task<ProfileRuntimeIdentity> OpenOrCreateAsync(
        DataProfileIdentity profile,
        bool existingCluster,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileRuntimeIdentityStore : IProfileRuntimeIdentityStore
{
    private const int CurrentVersion = 1;
    private const int MaximumRecordBytes = 16 * 1024;
    private const string IdentityName = "profile-identity-v1.json";
    private const string CredentialName = "postgres-credential-v1.dat";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProfileRuntimeIdentity> OpenOrCreateAsync(
        DataProfileIdentity profile,
        bool existingCluster,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtime = Path.Combine(profile.CanonicalPath, "runtime");
            var identityPath = Path.Combine(runtime, IdentityName);
            var credentialPath = Path.Combine(runtime, CredentialName);
            var identityExists = File.Exists(identityPath);
            var credentialExists = File.Exists(credentialPath);
            if (!identityExists && !credentialExists)
            {
                if (existingCluster)
                {
                    throw new ProfileRuntimeIdentityException("profile-runtime-identity-missing");
                }

                return Create(profile, runtime, identityPath, credentialPath, cancellationToken);
            }

            if (!identityExists || !credentialExists)
            {
                throw new ProfileRuntimeIdentityException("profile-runtime-identity-incomplete");
            }

            return Open(profile, identityPath, credentialPath, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ProfileRuntimeIdentity Create(
        DataProfileIdentity profile,
        string runtime,
        string identityPath,
        string credentialPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(runtime);
        DataProfileIdentity.EnsureNoReparsePoints(runtime, WindowsProfilePathInspector.Instance);
        var passwordBytes = RandomNumberGenerator.GetBytes(32);
        var password = Convert.ToBase64String(passwordBytes);
        byte[]? encrypted = null;
        byte[]? entropy = null;
        try
        {
            entropy = CreateEntropy(profile);
            encrypted = ProtectedData.Protect(
                passwordBytes,
                entropy,
                DataProtectionScope.CurrentUser);
            var clusterId = CreateClusterId();
            WriteAtomicCurrentUserOnly(credentialPath, Convert.ToBase64String(encrypted));
            var record = JsonSerializer.Serialize(new IdentityRecord(
                CurrentVersion,
                profile.ProfileHash,
                clusterId));
            WriteAtomicCurrentUserOnly(identityPath, record);
            return new ProfileRuntimeIdentity(clusterId, new SecretValue(password));
        }
        catch (ProfileRuntimeIdentityException)
        {
            throw;
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ProfileRuntimeIdentityException("profile-runtime-identity-create-failed", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            if (entropy is not null)
            {
                CryptographicOperations.ZeroMemory(entropy);
            }
        }
    }

    private static ProfileRuntimeIdentity Open(
        DataProfileIdentity profile,
        string identityPath,
        string credentialPath,
        CancellationToken cancellationToken)
    {
        byte[]? encrypted = null;
        byte[]? decrypted = null;
        byte[]? entropy = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DataProfileIdentity.EnsureNoReparsePoints(identityPath, WindowsProfilePathInspector.Instance);
            DataProfileIdentity.EnsureNoReparsePoints(credentialPath, WindowsProfilePathInspector.Instance);
            var record = JsonSerializer.Deserialize<IdentityRecord>(ReadBoundedText(identityPath))
                ?? throw new ProfileRuntimeIdentityException("profile-runtime-identity-corrupt");
            if (record.Version != CurrentVersion ||
                record.ClusterId == Guid.Empty ||
                !StringComparer.Ordinal.Equals(record.ProfileHash, profile.ProfileHash))
            {
                throw new ProfileRuntimeIdentityException("profile-runtime-identity-mismatch");
            }

            encrypted = Convert.FromBase64String(ReadBoundedText(credentialPath));
            entropy = CreateEntropy(profile);
            decrypted = ProtectedData.Unprotect(
                encrypted,
                entropy,
                DataProtectionScope.CurrentUser);
            var password = Convert.ToBase64String(decrypted);
            return new ProfileRuntimeIdentity(record.ClusterId, new SecretValue(password));
        }
        catch (ProfileRuntimeIdentityException)
        {
            throw;
        }
        catch (Exception error) when (error is
            CryptographicException or
            IOException or
            UnauthorizedAccessException or
            JsonException or
            FormatException or
            DecoderFallbackException)
        {
            throw new ProfileRuntimeIdentityException("profile-runtime-identity-corrupt", error);
        }
        finally
        {
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            if (decrypted is not null)
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }

            if (entropy is not null)
            {
                CryptographicOperations.ZeroMemory(entropy);
            }
        }
    }

    private static string ReadBoundedText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumRecordBytes)
        {
            throw new ProfileRuntimeIdentityException("profile-runtime-identity-corrupt");
        }

        return File.ReadAllText(path, new UTF8Encoding(false, true));
    }

    private static void WriteAtomicCurrentUserOnly(string path, string value)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            PostgresBootstrapPasswordFile.Write(temporary, new SecretValue(value));
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                PostgresBootstrapPasswordFile.ZeroAndDelete(temporary);
            }
        }
    }

    private static byte[] CreateEntropy(DataProfileIdentity profile) => SHA256.HashData(
        Encoding.UTF8.GetBytes(
            $"HowardLab.EbayCrm.AppHost.ProfileRuntimeIdentity.v1|{profile.ProfileHash}"));

    private static Guid CreateClusterId()
    {
        Guid result;
        do
        {
            result = new Guid(RandomNumberGenerator.GetBytes(16));
        }
        while (result == Guid.Empty);

        return result;
    }

    private sealed record IdentityRecord(int Version, string ProfileHash, Guid ClusterId);
}

public sealed class ProfileRuntimeIdentityException : Exception
{
    public ProfileRuntimeIdentityException(string reasonCode, Exception? innerException = null)
        : base(reasonCode, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
