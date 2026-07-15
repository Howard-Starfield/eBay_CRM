using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Time;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;

namespace HowardLab.EbayCrm.AppHost.Composition;

public sealed record AppHostProbeResult(bool IsValid, string ReasonCode);

public static class AppHostComposition
{
    private const string TrustedFixtureManifestResource =
        "HowardLab.EbayCrm.AppHost.TrustedFixtureArtifacts";
    private static readonly IReadOnlyDictionary<string, string> TrustedFixtureArtifacts =
        ReadTrustedFixtureArtifactManifest();

    internal static IReadOnlyCollection<string> TrustedFixtureArtifactNames =>
        TrustedFixtureArtifacts.Keys.ToArray();

    public static RuntimeOrchestrator Create(AppHostOptions options)
    {
        var runtime = CreateRuntime(options, null);
        return runtime.Orchestrator;
    }

    internal static AppHostTestRuntime CreateForTests(
        AppHostOptions options,
        ShutdownBudget? shutdownBudget = null)
    {
        var canonical = Path.GetFullPath(options.ProfileRoot);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!canonical.StartsWith(temporary, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppHostOptionsException("test-profile-not-disposable");
        }

        return CreateRuntime(options, shutdownBudget);
    }

    private static AppHostTestRuntime CreateRuntime(
        AppHostOptions options,
        ShutdownBudget? shutdownBudget)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode != AppHostMode.Run)
        {
            throw new AppHostOptionsException("run-mode-required");
        }

        var validated = Validate(options, requireAvailablePort: true);
        var coordinator = new LifecycleCoordinator(
            new SystemClock(),
            new RestartBudget(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        var executor = new LifecycleCommandExecutor(options, validated);
        var orchestrator = new RuntimeOrchestrator(coordinator, executor, shutdownBudget);
        executor.EventSink = orchestrator.HandleEventAsync;
        return new AppHostTestRuntime(orchestrator, executor);
    }

    public static Task<AppHostProbeResult> ProbeAsync(
        AppHostOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Mode != AppHostMode.Probe)
        {
            throw new AppHostOptionsException("probe-mode-required");
        }

        _ = Validate(options, requireAvailablePort: true);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AppHostProbeResult(true, "probe-valid"));
    }

    private static ValidatedAppHostPayload Validate(AppHostOptions options, bool requireAvailablePort)
    {
        var profile = DataProfileIdentity.Create(options.ProfileRoot);
        var layout = PostgresBinaryLayout.Validate(options.PostgresBin);
        var postgresBinaries = new[]
        {
            layout.InitDbExe,
            layout.PgCtlExe,
            layout.PostgresExe,
            layout.PsqlExe,
            layout.PgIsReadyExe,
        };
        var postgresVersions = postgresBinaries
            .Select(path => FileVersionInfo.GetVersionInfo(path).ProductVersion)
            .ToArray();
        if (postgresVersions.Any(version => !StringComparer.Ordinal.Equals(version, "16.14")) ||
            postgresVersions.Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new AppHostOptionsException("postgres-version-mismatch");
        }

        if (!File.Exists(options.FixturePath) ||
            !Path.GetFileName(options.FixturePath).Equals(
                "HowardLab.EbayCrm.AppHost.Fixture.exe",
                StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(options.FixturePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new AppHostOptionsException("fixture-invalid");
        }

        _ = DataProfileIdentity.Create(Path.GetDirectoryName(options.FixturePath)!);
        var expectedFixture = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "HowardLab.EbayCrm.AppHost.Fixture.exe"));
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(options.FixturePath), expectedFixture))
        {
            throw new AppHostOptionsException("fixture-trust-mismatch");
        }

        ValidateTrustedFixtureExecutable(expectedFixture);

        const string fixtureBuildIdentity = ControlProtocolConstants.FixtureBuildIdentity;

        if (requireAvailablePort)
        {
            EnsurePortAvailable(options.Port);
        }

        var migrationPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "migrations",
            "0001_apphost_control.sql"));
        if (!File.Exists(migrationPath))
        {
            throw new AppHostOptionsException("migration-catalog-missing");
        }

        return new ValidatedAppHostPayload(
            profile,
            layout,
            PostgresClusterPaths.Create(profile.CanonicalPath),
            migrationPath,
            fixtureBuildIdentity);
    }

    internal static void ValidateTrustedFixtureExecutable(string path)
    {
        using var lease = OpenTrustedFixtureArtifacts(path);
    }

    internal static IDisposable OpenTrustedFixtureArtifacts(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var expectedExecutable = Path.Combine(directory, "HowardLab.EbayCrm.AppHost.Fixture.exe");
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), expectedExecutable))
        {
            throw new AppHostOptionsException("fixture-build-mismatch");
        }

        var leases = new List<FileStream>(TrustedFixtureArtifacts.Count);
        try
        {
            foreach (var artifact in TrustedFixtureArtifacts)
            {
                var handle = NativeMethods.CreateFile(
                    Path.Combine(directory, artifact.Key),
                    NativeMethods.GenericRead,
                    NativeMethods.FileShareRead,
                    IntPtr.Zero,
                    NativeMethods.OpenExisting,
                    NativeMethods.FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (handle.IsInvalid ||
                    !NativeMethods.GetFileInformationByHandleEx(
                        handle,
                        NativeMethods.FileAttributeTagInfo,
                        out var attributes,
                        checked((uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInformation>())) ||
                    ((FileAttributes)attributes.FileAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    handle.Dispose();
                    throw new AppHostOptionsException("fixture-build-mismatch");
                }

                var lease = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
                leases.Add(lease);
                var actual = Convert.ToHexString(SHA256.HashData(lease));
                if (!StringComparer.Ordinal.Equals(actual, artifact.Value))
                {
                    throw new AppHostOptionsException("fixture-build-mismatch");
                }
            }

            return new FixtureArtifactLease(leases);
        }
        catch (AppHostOptionsException)
        {
            DisposeLeases(leases);
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DisposeLeases(leases);
            throw new AppHostOptionsException("fixture-build-mismatch", error);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadTrustedFixtureArtifactManifest()
    {
        using var manifest = typeof(AppHostComposition).Assembly.GetManifestResourceStream(
            TrustedFixtureManifestResource) ??
            throw new InvalidOperationException("Trusted fixture artifact manifest is missing.");
        using var reader = new StreamReader(manifest);
        var artifacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf('|');
            var relativePath = separator > 0 ? line[..separator] : string.Empty;
            if (separator <= 0 || separator == line.Length - 1 ||
                Path.IsPathFullyQualified(relativePath) ||
                relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "" or "." or "..") ||
                !artifacts.TryAdd(relativePath, line[(separator + 1)..]))
            {
                throw new InvalidOperationException("Trusted fixture artifact manifest is invalid.");
            }
        }

        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException("Trusted fixture artifact manifest is empty.");
        }

        return artifacts;
    }

    private static void DisposeLeases(IEnumerable<FileStream> leases)
    {
        foreach (var lease in leases)
        {
            lease.Dispose();
        }
    }

    private sealed class FixtureArtifactLease(IReadOnlyList<FileStream> leases) : IDisposable
    {
        public void Dispose() => DisposeLeases(leases);
    }

    private static void EnsurePortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException error)
        {
            throw new AppHostOptionsException("port-unavailable", error);
        }
    }
}

internal sealed record AppHostTestRuntime(
    RuntimeOrchestrator Orchestrator,
    LifecycleCommandExecutor Executor);

public sealed record ValidatedAppHostPayload(
    DataProfileIdentity Profile,
    PostgresBinaryLayout PostgresLayout,
    PostgresClusterPaths PostgresPaths,
    string MigrationPath,
    string FixtureBuildIdentity);
