using System.Globalization;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Composition;

internal sealed class NodeProbeRoleLaunchPlanProvider : IRoleLaunchPlanProvider
{
    internal const string BuildIdentity = "node-probe/1";

    private readonly string _nodeExecutablePath;
    private readonly string _workingDirectory;
    private readonly string _serverEntrypointPath;
    private readonly string _workerEntrypointPath;
    private readonly string _systemRoot;
    private readonly Func<IDisposable> _openBootstrapArtifactLease;
    private readonly Func<int> _healthPortAllocator;

    internal NodeProbeRoleLaunchPlanProvider(
        string nodeExecutablePath,
        string workingDirectory,
        string serverEntrypointPath,
        string workerEntrypointPath,
        Func<IDisposable> openBootstrapArtifactLease,
        Func<int> healthPortAllocator)
    {
        _nodeExecutablePath = ValidateFile(nodeExecutablePath, "node.exe", nameof(nodeExecutablePath));
        _workingDirectory = ValidateDirectory(workingDirectory, nameof(workingDirectory));
        _serverEntrypointPath = ValidateEntrypoint(
            serverEntrypointPath,
            "server-probe.ts",
            _workingDirectory,
            nameof(serverEntrypointPath));
        _workerEntrypointPath = ValidateEntrypoint(
            workerEntrypointPath,
            "worker-probe.ts",
            _workingDirectory,
            nameof(workerEntrypointPath));
        _openBootstrapArtifactLease = openBootstrapArtifactLease ??
            throw new ArgumentNullException(nameof(openBootstrapArtifactLease));
        _healthPortAllocator = healthPortAllocator ??
            throw new ArgumentNullException(nameof(healthPortAllocator));
        _systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ??
            throw new InvalidOperationException("SystemRoot is unavailable.");
    }

    public RoleLaunchPlan Create(RoleLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Role is not (RuntimeRole.Server or RuntimeRole.Worker) ||
            request.Generation.Role != request.Role)
        {
            throw new InvalidOperationException("The Node probe role request is invalid.");
        }

        var healthPort = _healthPortAllocator();
        if (healthPort is < 1024 or > 65_535)
        {
            throw new InvalidOperationException("The Node probe health port is invalid.");
        }

        var entrypoint = request.Role == RuntimeRole.Server
            ? _serverEntrypointPath
            : _workerEntrypointPath;
        return new RoleLaunchPlan(
            request.Role,
            request.Generation,
            _nodeExecutablePath,
            ["--import", "tsx", entrypoint, healthPort.ToString(CultureInfo.InvariantCulture)],
            _workingDirectory,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = _systemRoot,
            },
            new Dictionary<string, SecretValue>(StringComparer.OrdinalIgnoreCase),
            BuildIdentity,
            RoleReadinessStrategy.IdentityBoundHttp,
            healthPort,
            TimeSpan.FromMilliseconds(100),
            _openBootstrapArtifactLease,
            static () => { });
    }

    private static string ValidateFile(string path, string expectedName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A fully qualified file path is required.", parameterName);
        }

        var canonical = Path.GetFullPath(path);
        if (!File.Exists(canonical) ||
            !Path.GetFileName(canonical).Equals(expectedName, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("The launch file is invalid.", parameterName);
        }

        return canonical;
    }

    private static string ValidateDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A fully qualified directory path is required.", parameterName);
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(canonical) ||
            (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("The working directory is invalid.", parameterName);
        }

        return canonical;
    }

    private static string ValidateEntrypoint(
        string path,
        string expectedName,
        string workingDirectory,
        string parameterName)
    {
        var canonical = ValidateFile(path, expectedName, parameterName);
        var relative = Path.GetRelativePath(workingDirectory, canonical);
        if (Path.IsPathFullyQualified(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The probe entrypoint is outside the working directory.", parameterName);
        }

        return canonical;
    }
}
