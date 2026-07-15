using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Control;
using System.Collections.ObjectModel;

namespace HowardLab.EbayCrm.AppHost.Composition;

internal enum RoleReadinessStrategy
{
    IdentityBoundHttp,
}

internal sealed class RoleLaunchPlan
{
    internal static IReadOnlySet<string> ReservedEnvironmentKeys { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            WindowsControlChannel.PipeEnvironmentVariable,
            WindowsControlChannel.NonceEnvironmentVariable,
            WindowsControlChannel.RoleEnvironmentVariable,
            WindowsControlChannel.GenerationEnvironmentVariable,
            WindowsControlChannel.OperationEnvironmentVariable,
            WindowsControlChannel.BuildEnvironmentVariable,
        };

    internal RoleLaunchPlan(
        RuntimeRole role,
        ProcessGeneration generation,
        string applicationPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, SecretValue> secretEnvironment,
        string buildIdentity,
        RoleReadinessStrategy readinessStrategy,
        int? healthPort,
        TimeSpan outputDrainTimeout,
        Func<IDisposable> openBootstrapArtifactLease,
        Action verifyPayloadClosureAfterShutdown)
    {
        Role = role;
        Generation = generation;
        ApplicationPath = applicationPath;
        Arguments = Array.AsReadOnly(arguments?.ToArray() ??
            throw new ArgumentNullException(nameof(arguments)));
        WorkingDirectory = workingDirectory;
        Environment = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                environment ?? throw new ArgumentNullException(nameof(environment)),
                StringComparer.Ordinal));
        SecretEnvironment = new ReadOnlyDictionary<string, SecretValue>(
            new Dictionary<string, SecretValue>(
                secretEnvironment ?? throw new ArgumentNullException(nameof(secretEnvironment)),
                StringComparer.Ordinal));
        BuildIdentity = buildIdentity;
        ReadinessStrategy = readinessStrategy;
        HealthPort = healthPort;
        OutputDrainTimeout = outputDrainTimeout;
        OpenBootstrapArtifactLease = openBootstrapArtifactLease;
        VerifyPayloadClosureAfterShutdown = verifyPayloadClosureAfterShutdown;
    }

    internal RuntimeRole Role { get; }

    internal ProcessGeneration Generation { get; }

    internal string ApplicationPath { get; }

    internal IReadOnlyList<string> Arguments { get; }

    internal string WorkingDirectory { get; }

    internal IReadOnlyDictionary<string, string> Environment { get; }

    internal IReadOnlyDictionary<string, SecretValue> SecretEnvironment { get; }

    internal string BuildIdentity { get; }

    internal RoleReadinessStrategy ReadinessStrategy { get; }

    internal int? HealthPort { get; }

    internal TimeSpan OutputDrainTimeout { get; }

    internal Func<IDisposable> OpenBootstrapArtifactLease { get; }

    internal Action VerifyPayloadClosureAfterShutdown { get; }

    internal void ValidateFor(RoleLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Role is not (RuntimeRole.Server or RuntimeRole.Worker) ||
            Role != request.Role ||
            Generation != request.Generation ||
            Generation.Role != Role ||
            Generation.Value < 0 ||
            Generation.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(ApplicationPath) ||
            !Path.IsPathFullyQualified(ApplicationPath) ||
            !File.Exists(ApplicationPath) ||
            Arguments is null ||
            Arguments.Any(argument => argument is null || argument.Contains('\0')) ||
            string.IsNullOrWhiteSpace(WorkingDirectory) ||
            !Path.IsPathFullyQualified(WorkingDirectory) ||
            !Directory.Exists(WorkingDirectory) ||
            Environment is null ||
            SecretEnvironment is null ||
            string.IsNullOrWhiteSpace(BuildIdentity) ||
            BuildIdentity.Length > ControlProtocolConstants.MaxTextFieldChars ||
            BuildIdentity.Contains('\0') ||
            !Enum.IsDefined(ReadinessStrategy) ||
            ReadinessStrategy != RoleReadinessStrategy.IdentityBoundHttp ||
            HealthPort is not (> 0 and <= 65_535) ||
            OutputDrainTimeout <= TimeSpan.Zero ||
            OpenBootstrapArtifactLease is null ||
            VerifyPayloadClosureAfterShutdown is null)
        {
            throw new InvalidOperationException("The role launch plan is invalid.");
        }

        ValidateEnvironment(Environment.Keys);
        ValidateEnvironment(SecretEnvironment.Keys);
        if (Environment.Values.Any(value => value is null || value.Contains('\0')) ||
            SecretEnvironment.Values.Any(value =>
                value is null || value.RevealForChildEnvironment().Contains('\0')))
        {
            throw new InvalidOperationException("The role launch environment values are invalid.");
        }

        var ordinaryKeys = new HashSet<string>(Environment.Keys, StringComparer.OrdinalIgnoreCase);
        if (SecretEnvironment.Keys.Any(key => ordinaryKeys.Contains(key)))
        {
            throw new InvalidOperationException("The role launch environment maps collide.");
        }
    }

    private static void ValidateEnvironment(IEnumerable<string> keys)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (string.IsNullOrEmpty(key) ||
                key.Contains('=') ||
                key.Contains('\0') ||
                ReservedEnvironmentKeys.Contains(key) ||
                !unique.Add(key))
            {
                throw new InvalidOperationException("The role launch environment is invalid.");
            }
        }
    }
}
