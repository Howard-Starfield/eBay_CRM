using System.Globalization;
using System.Net;
using System.Net.Sockets;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Composition;

internal sealed class FixtureRoleLaunchPlanProvider : IRoleLaunchPlanProvider
{
    private readonly AppHostOptions _options;
    private readonly ValidatedAppHostPayload _payload;
    private string? _workerModeForTests;
    private Action _verifyPayloadClosureAfterShutdown = static () => { };

    internal FixtureRoleLaunchPlanProvider(
        AppHostOptions options,
        ValidatedAppHostPayload payload)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    internal string? WorkerModeForTests
    {
        set => Interlocked.Exchange(ref _workerModeForTests, value);
    }

    internal Action VerifyPayloadClosureAfterShutdownForTests
    {
        set => Interlocked.Exchange(
            ref _verifyPayloadClosureAfterShutdown,
            value ?? throw new ArgumentNullException(nameof(value)));
    }

    public RoleLaunchPlan Create(RoleLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var healthPort = ReserveLoopbackPort();
        var testMode = request.Role == RuntimeRole.Worker
            ? Interlocked.Exchange(ref _workerModeForTests, null)
            : null;
        var fixtureMode = testMode ?? request.Role.ToString().ToLowerInvariant();
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? throw new InvalidOperationException("SystemRoot is unavailable.");
        return new RoleLaunchPlan(
            request.Role,
            request.Generation,
            _options.FixturePath,
            [fixtureMode, healthPort.ToString(CultureInfo.InvariantCulture)],
            Path.GetDirectoryName(_options.FixturePath)!,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = systemRoot,
            },
            new Dictionary<string, SecretValue>(StringComparer.OrdinalIgnoreCase),
            _payload.FixtureBuildIdentity,
            RoleReadinessStrategy.IdentityBoundHttp,
            healthPort,
            TimeSpan.FromMilliseconds(100),
            () => AppHostComposition.OpenTrustedFixtureArtifacts(_options.FixturePath),
            Volatile.Read(ref _verifyPayloadClosureAfterShutdown));
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
