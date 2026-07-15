using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Composition;

internal sealed record AllowlistedRoleEnvironmentRequest(
    RuntimeRole Role,
    AppHostRuntimeBackend RuntimeBackend,
    string ProfileRoot,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, SecretValue> SecretEnvironment);

internal sealed class AllowlistedRoleEnvironment
{
    internal AllowlistedRoleEnvironment(
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, SecretValue> secretEnvironment)
    {
        Environment = environment;
        SecretEnvironment = secretEnvironment;
    }

    internal IReadOnlyDictionary<string, string> Environment { get; }

    internal IReadOnlyDictionary<string, SecretValue> SecretEnvironment { get; }
}

internal static class AllowlistedRoleEnvironmentBuilder
{
    private static readonly HashSet<string> CommonOrdinaryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemRoot",
        "NODE_ENV",
        "RUNTIME_BACKEND",
        "SERVER_URL",
        "STORAGE_TYPE",
        "STORAGE_LOCAL_PATH",
        "IS_CONFIG_VARIABLES_IN_DB_ENABLED",
    };

    private static readonly HashSet<string> CommonSecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PG_DATABASE_URL",
        "APP_SECRET",
        "ENCRYPTION_KEY",
        "FALLBACK_ENCRYPTION_KEY",
    };

    private static readonly HashSet<string> RequiredSecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PG_DATABASE_URL",
        "APP_SECRET",
    };

    internal static AllowlistedRoleEnvironment Build(AllowlistedRoleEnvironmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            ValidateRoleAndBackend(request.Role, request.RuntimeBackend);
            var profileRoot = CanonicalLocalPath(request.ProfileRoot);
            var ordinary = CopyOrdinary(request.Environment);
            var secrets = CopySecrets(request.SecretEnvironment);

            if (ordinary.Keys.Any(secrets.ContainsKey))
            {
                throw Failure();
            }

            ValidateOrdinaryShape(request.Role, ordinary);
            ValidateSecretShape(request.RuntimeBackend, secrets);
            ValidateOrdinaryValues(request.Role, request.RuntimeBackend, profileRoot, ordinary);
            ValidateSecretValues(secrets);

            return new AllowlistedRoleEnvironment(
                new ReadOnlyDictionary<string, string>(ordinary),
                new ReadOnlyDictionary<string, SecretValue>(secrets));
        }
        catch (AppHostOptionsException)
        {
            throw;
        }
        catch (Exception error) when (
            error is ArgumentException or FormatException or IOException or
                NotSupportedException or OverflowException)
        {
            throw Failure();
        }
    }

    private static Dictionary<string, string> CopyOrdinary(
        IReadOnlyDictionary<string, string>? source)
    {
        if (source is null)
        {
            throw Failure();
        }

        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            if (!IsValidEnvironmentKey(pair.Key) ||
                pair.Value is null ||
                pair.Value.Contains('\0') ||
                !copy.TryAdd(pair.Key, pair.Value))
            {
                throw Failure();
            }
        }

        return copy;
    }

    private static Dictionary<string, SecretValue> CopySecrets(
        IReadOnlyDictionary<string, SecretValue>? source)
    {
        if (source is null)
        {
            throw Failure();
        }

        var copy = new Dictionary<string, SecretValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            if (!IsValidEnvironmentKey(pair.Key) ||
                pair.Value is null ||
                !copy.TryAdd(pair.Key, pair.Value))
            {
                throw Failure();
            }
        }

        return copy;
    }

    private static void ValidateOrdinaryShape(
        RuntimeRole role,
        IReadOnlyDictionary<string, string> ordinary)
    {
        var allowed = new HashSet<string>(CommonOrdinaryKeys, StringComparer.OrdinalIgnoreCase)
        {
            role == RuntimeRole.Server ? "NODE_PORT" : "DISABLE_DB_MIGRATIONS",
        };
        if (ordinary.Count != allowed.Count || ordinary.Keys.Any(key => !allowed.Contains(key)))
        {
            throw Failure();
        }
    }

    private static void ValidateSecretShape(
        AppHostRuntimeBackend backend,
        IReadOnlyDictionary<string, SecretValue> secrets)
    {
        var allowed = new HashSet<string>(CommonSecretKeys, StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(RequiredSecretKeys, StringComparer.OrdinalIgnoreCase);
        if (backend == AppHostRuntimeBackend.RedisCompatibility)
        {
            allowed.Add("REDIS_URL");
            allowed.Add("REDIS_QUEUE_URL");
            required.Add("REDIS_URL");
        }

        if (secrets.Keys.Any(key => !allowed.Contains(key)) || required.Any(key => !secrets.ContainsKey(key)))
        {
            throw Failure();
        }
    }

    private static void ValidateOrdinaryValues(
        RuntimeRole role,
        AppHostRuntimeBackend backend,
        string profileRoot,
        IReadOnlyDictionary<string, string> ordinary)
    {
        var systemRoot = CanonicalLocalPath(
            ordinary["SystemRoot"],
            requireExistingDirectory: true);
        var windowsRoot = CanonicalWindowsRoot();
        if (!StringComparer.OrdinalIgnoreCase.Equals(systemRoot, windowsRoot))
        {
            throw Failure();
        }

        if (!StringComparer.Ordinal.Equals(ordinary["NODE_ENV"], "production") ||
            !StringComparer.Ordinal.Equals(
                ordinary["RUNTIME_BACKEND"],
                backend == AppHostRuntimeBackend.PostgresDesktop ? "postgres-desktop" : "redis") ||
            !StringComparer.Ordinal.Equals(ordinary["STORAGE_TYPE"], "local") ||
            !StringComparer.Ordinal.Equals(ordinary["IS_CONFIG_VARIABLES_IN_DB_ENABLED"], "false"))
        {
            throw Failure();
        }

        var serverUri = ParseLoopbackServerUri(ordinary["SERVER_URL"]);
        var storagePath = CanonicalLocalPath(ordinary["STORAGE_LOCAL_PATH"]);
        var profilePrefix = profileRoot.EndsWith(Path.DirectorySeparatorChar)
            ? profileRoot
            : profileRoot + Path.DirectorySeparatorChar;
        if (!storagePath.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Failure();
        }

        if (role == RuntimeRole.Server)
        {
            var nodePort = ParseCanonicalPort(ordinary["NODE_PORT"]);
            if (serverUri.Port != nodePort)
            {
                throw Failure();
            }
        }
        else if (!StringComparer.Ordinal.Equals(ordinary["DISABLE_DB_MIGRATIONS"], "true"))
        {
            throw Failure();
        }
    }

    private static void ValidateSecretValues(IReadOnlyDictionary<string, SecretValue> secrets)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in secrets)
        {
            var value = pair.Value.RevealForChildEnvironment();
            if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            {
                throw Failure();
            }

            values.Add(pair.Key, value);
        }

        ValidatePostgresUrl(values["PG_DATABASE_URL"]);
        if (values.TryGetValue("REDIS_URL", out var redisUrl))
        {
            ValidateRedisUrl(redisUrl);
        }

        if (values.TryGetValue("REDIS_QUEUE_URL", out var redisQueueUrl))
        {
            ValidateRedisUrl(redisQueueUrl);
        }
    }

    private static void ValidatePostgresUrl(string value)
    {
        var uri = ParseLocalServiceUri(value, "postgres", "postgresql");
        ValidateCredentials(uri, allowEmptyUsername: false);
        if (uri.AbsolutePath.Length <= 1)
        {
            throw Failure();
        }

        var databaseName = Uri.UnescapeDataString(uri.AbsolutePath[1..]);
        if (databaseName.Length == 0 || databaseName.Any(character => character is not (
                >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '_' or '-')))
        {
            throw Failure();
        }
    }

    private static void ValidateRedisUrl(string value)
    {
        var uri = ParseLocalServiceUri(value, "redis", "rediss");
        if (uri.UserInfo.Length > 0)
        {
            ValidateCredentials(uri, allowEmptyUsername: true);
        }

        if (uri.AbsolutePath.Length <= 1)
        {
            return;
        }

        if (uri.AbsolutePath[0] != '/')
        {
            throw Failure();
        }

        var database = Uri.UnescapeDataString(uri.AbsolutePath[1..]);
        if (!int.TryParse(
                database,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var databaseNumber) ||
            databaseNumber < 0)
        {
            throw Failure();
        }
    }

    private static void ValidateCredentials(Uri uri, bool allowEmptyUsername)
    {
        var credentialSeparator = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
        if (credentialSeparator < 0)
        {
            throw Failure();
        }

        var username = Uri.UnescapeDataString(uri.UserInfo[..credentialSeparator]);
        var password = Uri.UnescapeDataString(uri.UserInfo[(credentialSeparator + 1)..]);
        if (!allowEmptyUsername && username.Length == 0 ||
            password.Length == 0 ||
            username.Any(char.IsControl) ||
            password.Any(char.IsControl))
        {
            throw Failure();
        }
    }

    private static Uri ParseLocalServiceUri(string value, string scheme, string alternateScheme)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, scheme) &&
            !StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, alternateScheme) ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) ||
            !IPAddress.IsLoopback(address) ||
            uri.Port is < 1024 or > 65_535 ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw Failure();
        }

        return uri;
    }

    private static Uri ParseLoopbackServerUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, Uri.UriSchemeHttp) ||
            !uri.IsLoopback ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/" ||
            uri.Port is < 1024 or > 65_535)
        {
            throw Failure();
        }

        return uri;
    }

    private static int ParseCanonicalPort(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1024 or > 65_535 ||
            !StringComparer.Ordinal.Equals(value, port.ToString(CultureInfo.InvariantCulture)))
        {
            throw Failure();
        }

        return port;
    }

    private static string CanonicalLocalPath(
        string value,
        bool requireExistingDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('\0') ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw Failure();
        }

        var root = Path.GetPathRoot(value);
        var relative = root is null ? string.Empty : value[root.Length..];
        if (root is null ||
            value.IndexOf(':', root.Length) >= 0 ||
            relative.Length > 0 && relative
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                .Any(segment => segment.Length == 0 ||
                    segment is "." or ".." ||
                    segment[^1] is '.' or ' '))
        {
            throw Failure();
        }

        var requested = Path.TrimEndingDirectorySeparator(value);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!StringComparer.OrdinalIgnoreCase.Equals(requested, canonical) ||
            requireExistingDirectory && !Directory.Exists(canonical))
        {
            throw Failure();
        }

        return canonical;
    }

    private static string CanonicalWindowsRoot()
    {
        var systemDirectory = CanonicalLocalPath(
            Environment.SystemDirectory,
            requireExistingDirectory: true);
        var windowsRoot = Directory.GetParent(systemDirectory)?.FullName;
        return windowsRoot is null
            ? throw Failure()
            : CanonicalLocalPath(windowsRoot, requireExistingDirectory: true);
    }

    private static void ValidateRoleAndBackend(RuntimeRole role, AppHostRuntimeBackend backend)
    {
        if (role is not (RuntimeRole.Server or RuntimeRole.Worker) || !Enum.IsDefined(backend))
        {
            throw Failure();
        }
    }

    private static bool IsValidEnvironmentKey(string? key) =>
        !string.IsNullOrEmpty(key) &&
        !key.Contains('=') &&
        !key.Contains('\0');

    private static AppHostOptionsException Failure() => new("role-launch-plan-invalid");
}
