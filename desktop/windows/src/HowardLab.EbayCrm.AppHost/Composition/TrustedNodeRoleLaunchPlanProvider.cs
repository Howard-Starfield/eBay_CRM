using System.Collections.ObjectModel;
using System.Globalization;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Payload;

namespace HowardLab.EbayCrm.AppHost.Composition;

internal sealed record TrustedNodeRoleLaunchPlanProviderOptions(
    AppHostRuntimeBackend RuntimeBackend,
    string ProfileRoot,
    int ServerPort,
    int WorkerHealthPort,
    IReadOnlyDictionary<string, SecretValue> SecretEnvironment);

internal sealed class TrustedNodeRoleLaunchPlanProvider : IRoleLaunchPlanProvider
{
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromMilliseconds(100);

    private readonly TrustedNodePayload _payload;
    private readonly AppHostRuntimeBackend _runtimeBackend;
    private readonly string _profileRoot;
    private readonly string _storagePath;
    private readonly string _systemRoot;
    private readonly int _serverPort;
    private readonly int _workerHealthPort;
    private readonly IReadOnlyDictionary<string, SecretValue> _secretEnvironment;

    internal TrustedNodeRoleLaunchPlanProvider(
        TrustedNodePayload payload,
        TrustedNodeRoleLaunchPlanProviderOptions options)
    {
        try
        {
            _payload = payload ?? throw Failure();
            if (options is null ||
                !Enum.IsDefined(options.RuntimeBackend) ||
                !IsBoundedPort(options.ServerPort) ||
                !IsBoundedPort(options.WorkerHealthPort) ||
                options.ServerPort == options.WorkerHealthPort)
            {
                throw Failure();
            }

            ValidatePayloadShape(payload);
            payload.VerifyClosure();

            _runtimeBackend = options.RuntimeBackend;
            _profileRoot = CanonicalLocalPath(options.ProfileRoot);
            _storagePath = CanonicalLocalPath(Path.Combine(_profileRoot, "storage"));
            _systemRoot = CanonicalWindowsRoot();
            _serverPort = options.ServerPort;
            _workerHealthPort = options.WorkerHealthPort;
            _secretEnvironment = SnapshotSecrets(options.SecretEnvironment);

            _ = BuildEnvironment(RuntimeRole.Server);
            _ = BuildEnvironment(RuntimeRole.Worker);
        }
        catch (NodePayloadManifestException)
        {
            throw PayloadFailure();
        }
        catch (AppHostOptionsException)
        {
            throw Failure();
        }
        catch (Exception error) when (IsExpectedValidationFailure(error))
        {
            throw Failure();
        }
    }

    public RoleLaunchPlan Create(RoleLaunchRequest request)
    {
        try
        {
            if (request is null ||
                request.Role is not (RuntimeRole.Server or RuntimeRole.Worker) ||
                request.Generation.Role != request.Role ||
                request.Generation.Value < 0 ||
                request.Generation.OperationId == Guid.Empty)
            {
                throw Failure();
            }

            var healthPort = request.Role == RuntimeRole.Server
                ? _serverPort
                : _workerHealthPort;
            var entrypoint = request.Role == RuntimeRole.Server
                ? _payload.ServerEntrypoint
                : _payload.WorkerEntrypoint;
            var environment = BuildEnvironment(request.Role);

            return new RoleLaunchPlan(
                request.Role,
                request.Generation,
                _payload.NodeExecutable,
                [entrypoint, healthPort.ToString(CultureInfo.InvariantCulture)],
                _payload.CanonicalRoot,
                environment.Environment,
                environment.SecretEnvironment,
                _payload.Manifest.BuildIdentity,
                RoleReadinessStrategy.IdentityBoundHttp,
                healthPort,
                OutputDrainTimeout,
                _payload.OpenLifetimeLease,
                () => new TrustedNodePayloadArtifactLease(_payload),
                _payload.VerifyClosure);
        }
        catch (AppHostOptionsException)
        {
            throw Failure();
        }
        catch (Exception error) when (IsExpectedValidationFailure(error))
        {
            throw Failure();
        }
    }

    private AllowlistedRoleEnvironment BuildEnvironment(RuntimeRole role)
    {
        var serverUrl = $"http://127.0.0.1:{_serverPort.ToString(CultureInfo.InvariantCulture)}/";
        var ordinary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = _systemRoot,
            ["NODE_ENV"] = "production",
            ["RUNTIME_BACKEND"] = _runtimeBackend == AppHostRuntimeBackend.PostgresDesktop
                ? "postgres-desktop"
                : "redis",
            ["SERVER_URL"] = serverUrl,
            ["STORAGE_TYPE"] = "local",
            ["STORAGE_LOCAL_PATH"] = _storagePath,
            ["IS_CONFIG_VARIABLES_IN_DB_ENABLED"] = "false",
        };
        if (role == RuntimeRole.Server)
        {
            ordinary["NODE_PORT"] = _serverPort.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            ordinary["DISABLE_DB_MIGRATIONS"] = "true";
        }

        return AllowlistedRoleEnvironmentBuilder.Build(new AllowlistedRoleEnvironmentRequest(
            role,
            _runtimeBackend,
            _profileRoot,
            ordinary,
            _secretEnvironment));
    }

    private static IReadOnlyDictionary<string, SecretValue> SnapshotSecrets(
        IReadOnlyDictionary<string, SecretValue>? source)
    {
        if (source is null)
        {
            throw Failure();
        }

        var snapshot = new Dictionary<string, SecretValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            if (pair.Value is null || !snapshot.TryAdd(pair.Key, pair.Value))
            {
                throw Failure();
            }
        }

        return new ReadOnlyDictionary<string, SecretValue>(snapshot);
    }

    private static void ValidatePayloadShape(TrustedNodePayload payload)
    {
        var root = CanonicalLocalPath(payload.CanonicalRoot, requireExistingDirectory: true);
        var node = CanonicalLocalFile(payload.NodeExecutable);
        var server = CanonicalLocalFile(payload.ServerEntrypoint);
        var worker = CanonicalLocalFile(payload.WorkerEntrypoint);
        if (!StringComparer.OrdinalIgnoreCase.Equals(root, payload.CanonicalRoot) ||
            !StringComparer.OrdinalIgnoreCase.Equals(node, payload.NodeExecutable) ||
            !StringComparer.OrdinalIgnoreCase.Equals(server, payload.ServerEntrypoint) ||
            !StringComparer.OrdinalIgnoreCase.Equals(worker, payload.WorkerEntrypoint) ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(node), "node.exe") ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(server), ".js") ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(worker), ".js") ||
            !IsStrictlyUnder(node, root) ||
            !IsStrictlyUnder(server, root) ||
            !IsStrictlyUnder(worker, root) ||
            new[] { node, server, worker }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 ||
            string.IsNullOrWhiteSpace(payload.Manifest.BuildIdentity) ||
            payload.Manifest.BuildIdentity.Length > ControlProtocolConstants.MaxTextFieldChars ||
            payload.Manifest.BuildIdentity.Any(char.IsControl))
        {
            throw Failure();
        }
    }

    private static string CanonicalLocalFile(string? path)
    {
        var canonical = CanonicalLocalPath(path);
        if (!File.Exists(canonical))
        {
            throw Failure();
        }

        return canonical;
    }

    private static string CanonicalLocalPath(
        string? path,
        bool requireExistingDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.Contains('\0'))
        {
            throw Failure();
        }

        var root = Path.GetPathRoot(path);
        if (root is null || path.IndexOf(':', root.Length) >= 0)
        {
            throw Failure();
        }

        var requested = Path.TrimEndingDirectorySeparator(path);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
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

    private static bool IsStrictlyUnder(string candidate, string root) =>
        candidate.StartsWith(
            Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundedPort(int port) => port is >= 1024 and <= 65_535;

    private static bool IsExpectedValidationFailure(Exception error) =>
        error is ArgumentException or FormatException or IOException or
            InvalidOperationException or NotSupportedException or OverflowException or
            UnauthorizedAccessException;

    private static AppHostOptionsException Failure() => new("role-launch-plan-invalid");

    private static AppHostOptionsException PayloadFailure() =>
        new("role-payload-trust-failed");
}
