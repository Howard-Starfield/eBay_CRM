using System.Globalization;

namespace HowardLab.EbayCrm.AppHost.Composition;

public enum AppHostMode
{
    Run,
    Probe,
}

public enum AppHostRuntimeBackend
{
    PostgresDesktop,
    RedisCompatibility,
}

public enum AppHostRoleTarget
{
    ControlledFixture,
}

public sealed record AppHostOptions(
    string ProfileRoot,
    string PostgresBin,
    string FixturePath,
    int Port,
    AppHostMode Mode,
    AppHostRuntimeBackend RuntimeBackend,
    AppHostRoleTarget RoleTarget)
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

            if (!RequiredOptions.Contains(option, StringComparer.Ordinal))
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

        if (values.Count != RequiredOptions.Length || RequiredOptions.Any(option => !values.ContainsKey(option)))
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
            _ => throw new AppHostOptionsException("invalid-role-target"),
        };

        return new AppHostOptions(
            profileRoot,
            postgresBin,
            fixturePath,
            port,
            mode,
            runtimeBackend,
            roleTarget);
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
}

public sealed class AppHostOptionsException : Exception
{
    public AppHostOptionsException(string reasonCode, Exception? innerException = null)
        : base(reasonCode, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
