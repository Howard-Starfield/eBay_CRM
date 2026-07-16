using System.Globalization;

namespace HowardLab.EbayCrm.AppHost.Composition;

public enum AppHostMode
{
    Run,
    Probe,
    AcceptanceRunOnce,
}

public enum AppHostRuntimeBackend
{
    PostgresDesktop,
    RedisCompatibility,
}

public enum AppHostRoleTarget
{
    ControlledFixture,
    ControlledNodeProbe,
}

public sealed record AppHostOptions(
    string ProfileRoot,
    string PostgresBin,
    string FixturePath,
    int Port,
    AppHostMode Mode,
    AppHostRuntimeBackend RuntimeBackend,
    AppHostRoleTarget RoleTarget,
    string? NodeProbeRoot = null)
{
    private static readonly string[] RequiredOptions =
    [
        "--profile-root",
        "--postgres-bin",
        "--fixture-path",
        "--port",
        "--mode",
        "--runtime-backend",
        "--role-target",
    ];
    private static readonly string[] KnownOptions =
    [
        .. RequiredOptions,
        "--node-probe-root",
    ];

    public static AppHostOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var option = arguments[index];
            if (option.Contains('=', StringComparison.Ordinal))
            {
                throw new AppHostOptionsException("inline-option-not-allowed");
            }

            if (!KnownOptions.Contains(option, StringComparer.Ordinal))
            {
                throw new AppHostOptionsException("unknown-option");
            }

            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new AppHostOptionsException("missing-option-value");
            }

            if (!values.TryAdd(option, arguments[index + 1]))
            {
                throw new AppHostOptionsException("duplicate-option");
            }
        }

        if (RequiredOptions.Any(option => !values.ContainsKey(option)))
        {
            throw new AppHostOptionsException("missing-required-option");
        }

        var profileRoot = ParseAbsoluteLocalPath(values["--profile-root"], "invalid-profile-root");
        var postgresBin = ParseAbsoluteLocalPath(values["--postgres-bin"], "invalid-postgres-bin");
        var fixturePath = ParseAbsoluteLocalPath(values["--fixture-path"], "invalid-fixture-path");
        if (!Path.GetFileName(fixturePath).Equals(
            "HowardLab.EbayCrm.AppHost.Fixture.exe",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new AppHostOptionsException("invalid-fixture-name");
        }

        if (!int.TryParse(values["--port"], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1024 or > 65535)
        {
            throw new AppHostOptionsException("invalid-port");
        }

        var mode = values["--mode"] switch
        {
            "run" => AppHostMode.Run,
            "probe" => AppHostMode.Probe,
            "acceptance-run-once" => AppHostMode.AcceptanceRunOnce,
            _ => throw new AppHostOptionsException("invalid-mode"),
        };

        var runtimeBackend = values["--runtime-backend"] switch
        {
            "postgres-desktop" => AppHostRuntimeBackend.PostgresDesktop,
            "redis" => AppHostRuntimeBackend.RedisCompatibility,
            _ => throw new AppHostOptionsException("invalid-runtime-backend"),
        };

        var roleTarget = values["--role-target"] switch
        {
            "controlled-fixture" => AppHostRoleTarget.ControlledFixture,
            "controlled-node-probe" => AppHostRoleTarget.ControlledNodeProbe,
            _ => throw new AppHostOptionsException("invalid-role-target"),
        };

        string? nodeProbeRoot;
        if (roleTarget == AppHostRoleTarget.ControlledNodeProbe)
        {
            if (mode != AppHostMode.AcceptanceRunOnce)
            {
                throw new AppHostOptionsException("role-target-mode-mismatch");
            }

            if (!StringComparer.Ordinal.Equals(
                    Environment.GetEnvironmentVariable("EBAYCRM_RELEASE_ACCEPTANCE"),
                    "1"))
            {
                throw new AppHostOptionsException("release-acceptance-required");
            }

            if (!values.TryGetValue("--node-probe-root", out var requestedNodeProbeRoot))
            {
                throw new AppHostOptionsException("node-probe-root-required");
            }

            nodeProbeRoot = ParseExistingAbsoluteLocalDirectory(
                requestedNodeProbeRoot,
                "invalid-node-probe-root");
        }
        else
        {
            if (values.ContainsKey("--node-probe-root"))
            {
                throw new AppHostOptionsException("node-probe-root-not-allowed");
            }

            if (mode == AppHostMode.AcceptanceRunOnce)
            {
                throw new AppHostOptionsException("role-target-mode-mismatch");
            }

            nodeProbeRoot = null;
        }

        return new AppHostOptions(
            profileRoot,
            postgresBin,
            fixturePath,
            port,
            mode,
            runtimeBackend,
            roleTarget,
            nodeProbeRoot);
    }

    private static string ParseAbsoluteLocalPath(string value, string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new AppHostOptionsException(reasonCode);
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AppHostOptionsException(reasonCode, error);
        }
    }

    private static string ParseExistingAbsoluteLocalDirectory(string value, string reasonCode)
    {
        var canonical = ParseAbsoluteLocalPath(value, reasonCode);
        try
        {
            if (!Directory.Exists(canonical))
            {
                throw new AppHostOptionsException(reasonCode);
            }

            for (var current = new DirectoryInfo(canonical);
                 current is not null;
                 current = current.Parent)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AppHostOptionsException(reasonCode);
                }
            }

            return canonical;
        }
        catch (AppHostOptionsException)
        {
            throw;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new AppHostOptionsException(reasonCode, error);
        }
    }
}

public sealed class AppHostOptionsException : Exception
{
    public AppHostOptionsException(string reasonCode, Exception? innerException = null)
        : base(reasonCode, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
