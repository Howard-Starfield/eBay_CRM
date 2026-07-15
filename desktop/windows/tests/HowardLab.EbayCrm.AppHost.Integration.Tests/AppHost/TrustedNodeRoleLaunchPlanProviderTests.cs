using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Payload;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

public sealed class TrustedNodeRoleLaunchPlanProviderTests : IDisposable
{
    private const int ServerPort = 32123;
    private const int WorkerHealthPort = 32124;
    private const string PostgresSecret =
        "postgres://desktop:postgres-canary@127.0.0.1:55432/ebaycrm";
    private const string AppSecret = "app-secret-canary";
    private const string RedisSecret = "redis://:redis-canary@127.0.0.1:56379/0";
    private const string RedisQueueSecret = "redis://:queue-canary@127.0.0.1:56380/0";

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "trusted-node-role-provider-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Create_ServerRedisCompatibilityPlanHasExactProductionShape()
    {
        using var fixture = CreateFixture();
        var provider = Provider(fixture, AppHostRuntimeBackend.RedisCompatibility);
        var generation = Generation(RuntimeRole.Server);

        var plan = provider.Create(new RoleLaunchRequest(RuntimeRole.Server, generation));

        Assert.Equal(RuntimeRole.Server, plan.Role);
        Assert.Equal(generation, plan.Generation);
        Assert.Equal(fixture.Payload.NodeExecutable, plan.ApplicationPath);
        Assert.Equal([fixture.Payload.ServerEntrypoint, "32123"], plan.Arguments);
        Assert.Equal(fixture.Payload.CanonicalRoot, plan.WorkingDirectory);
        Assert.Equal(fixture.Payload.Manifest.BuildIdentity, plan.BuildIdentity);
        Assert.Equal(RoleReadinessStrategy.IdentityBoundHttp, plan.ReadinessStrategy);
        Assert.Equal(ServerPort, plan.HealthPort);
        Assert.InRange(plan.OutputDrainTimeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));

        Assert.Equal("production", plan.Environment["NODE_ENV"]);
        Assert.Equal("redis", plan.Environment["RUNTIME_BACKEND"]);
        Assert.Equal($"http://127.0.0.1:{ServerPort}/", plan.Environment["SERVER_URL"]);
        Assert.Equal(ServerPort.ToString(), plan.Environment["NODE_PORT"]);
        Assert.Equal("local", plan.Environment["STORAGE_TYPE"]);
        Assert.Equal(
            Path.Combine(fixture.ProfileRoot, "storage"),
            plan.Environment["STORAGE_LOCAL_PATH"]);
        Assert.Equal("false", plan.Environment["IS_CONFIG_VARIABLES_IN_DB_ENABLED"]);
        Assert.Equal(8, plan.Environment.Count);

        Assert.Equal(PostgresSecret, plan.SecretEnvironment["PG_DATABASE_URL"].RevealForChildEnvironment());
        Assert.Equal(AppSecret, plan.SecretEnvironment["APP_SECRET"].RevealForChildEnvironment());
        Assert.Equal(RedisSecret, plan.SecretEnvironment["REDIS_URL"].RevealForChildEnvironment());
        Assert.Equal(
            RedisQueueSecret,
            plan.SecretEnvironment["REDIS_QUEUE_URL"].RevealForChildEnvironment());
        Assert.Equal(4, plan.SecretEnvironment.Count);
        Assert.DoesNotContain(
            plan.Arguments,
            argument => plan.SecretEnvironment.Values.Any(secret =>
                argument.Contains(secret.RevealForChildEnvironment(), StringComparison.Ordinal)));
    }

    [Fact]
    public void Create_WorkerPlanUsesDistinctHealthPortAndServerEndpoint()
    {
        using var fixture = CreateFixture();
        var provider = Provider(fixture, AppHostRuntimeBackend.RedisCompatibility);
        var generation = Generation(RuntimeRole.Worker);

        var plan = provider.Create(new RoleLaunchRequest(RuntimeRole.Worker, generation));

        Assert.Equal([fixture.Payload.WorkerEntrypoint, "32124"], plan.Arguments);
        Assert.Equal(WorkerHealthPort, plan.HealthPort);
        Assert.Equal($"http://127.0.0.1:{ServerPort}/", plan.Environment["SERVER_URL"]);
        Assert.Equal("true", plan.Environment["DISABLE_DB_MIGRATIONS"]);
        Assert.False(plan.Environment.ContainsKey("NODE_PORT"));
        Assert.Equal(8, plan.Environment.Count);
    }

    [Fact]
    public void Create_PostgresDesktopPlanContainsNoRedisEnvironment()
    {
        using var fixture = CreateFixture();
        var provider = Provider(fixture, AppHostRuntimeBackend.PostgresDesktop);

        var plan = provider.Create(new RoleLaunchRequest(
            RuntimeRole.Server,
            Generation(RuntimeRole.Server)));

        Assert.Equal("postgres-desktop", plan.Environment["RUNTIME_BACKEND"]);
        Assert.DoesNotContain(plan.Environment.Keys, key => key.StartsWith("REDIS", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            plan.SecretEnvironment.Keys,
            key => key.StartsWith("REDIS", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, plan.SecretEnvironment.Count);
    }

    [Fact]
    public void Create_ReturnsIndependentOwnedCollectionsAndArtifactLeases()
    {
        using var fixture = CreateFixture();
        var provider = Provider(fixture, AppHostRuntimeBackend.RedisCompatibility);
        var request = new RoleLaunchRequest(RuntimeRole.Server, Generation(RuntimeRole.Server));

        var first = provider.Create(request);
        var second = provider.Create(request);

        Assert.NotSame(first.Arguments, second.Arguments);
        Assert.NotSame(first.Environment, second.Environment);
        Assert.NotSame(first.SecretEnvironment, second.SecretEnvironment);
        Assert.NotSame(first.OpenBootstrapArtifactLease, second.OpenBootstrapArtifactLease);
        using var firstLease = first.OpenBootstrapArtifactLease();
        using var secondLease = second.OpenBootstrapArtifactLease();
        Assert.IsType<TrustedNodePayloadArtifactLease>(firstLease);
        Assert.IsType<TrustedNodePayloadArtifactLease>(secondLease);
        Assert.NotSame(firstLease, secondLease);
        Assert.ThrowsAny<IOException>(() =>
            File.WriteAllText(fixture.Payload.NodeExecutable, "replacement"));
    }

    [Fact]
    public void Create_ProducesValidRepeatedPlansWithCoherentRolePorts()
    {
        using var fixture = CreateFixture();
        var provider = Provider(fixture, AppHostRuntimeBackend.RedisCompatibility);

        foreach (var role in new[] { RuntimeRole.Server, RuntimeRole.Worker })
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var request = new RoleLaunchRequest(role, Generation(role));
                var plan = provider.Create(request);

                plan.ValidateFor(request);
                var expectedPort = role == RuntimeRole.Server ? ServerPort : WorkerHealthPort;
                Assert.Equal(expectedPort, plan.HealthPort);
                Assert.Equal(expectedPort.ToString(), plan.Arguments[^1]);
                Assert.Equal(
                    $"http://127.0.0.1:{ServerPort}/",
                    plan.Environment["SERVER_URL"]);
                if (role == RuntimeRole.Server)
                {
                    Assert.Equal(ServerPort.ToString(), plan.Environment["NODE_PORT"]);
                }
            }
        }
    }

    [Fact]
    public void Constructor_SnapshotsSourceSecretDictionary()
    {
        using var fixture = CreateFixture();
        var secrets = new Dictionary<string, SecretValue>
        {
            ["PG_DATABASE_URL"] = new(PostgresSecret),
            ["APP_SECRET"] = new(AppSecret),
        };
        var provider = new TrustedNodeRoleLaunchPlanProvider(
            fixture.Payload,
            new TrustedNodeRoleLaunchPlanProviderOptions(
                AppHostRuntimeBackend.PostgresDesktop,
                fixture.ProfileRoot,
                ServerPort,
                WorkerHealthPort,
                secrets));

        secrets["PG_DATABASE_URL"] = new("postgres://changed:changed@127.0.0.1:55433/changed");
        secrets["UNEXPECTED_SECRET"] = new("unexpected-secret-canary");
        var plan = provider.Create(new RoleLaunchRequest(
            RuntimeRole.Server,
            Generation(RuntimeRole.Server)));

        Assert.Equal(
            PostgresSecret,
            plan.SecretEnvironment["PG_DATABASE_URL"].RevealForChildEnvironment());
        Assert.False(plan.SecretEnvironment.ContainsKey("UNEXPECTED_SECRET"));
    }

    [Fact]
    public void OwnerDisposeAfterProviderConstructionRejectsFuturePlanLifetimeAcquisition()
    {
        using var fixture = CreateFixture();
        var provider = Provider(fixture, AppHostRuntimeBackend.PostgresDesktop);
        fixture.Payload.Dispose();
        var request = new RoleLaunchRequest(RuntimeRole.Server, Generation(RuntimeRole.Server));

        var plan = provider.Create(request);

        plan.ValidateFor(request);
        Assert.Throws<NodePayloadManifestException>(() => plan.OpenPayloadLifetimeLease());
        Assert.Throws<NodePayloadManifestException>(() => plan.OpenBootstrapArtifactLease());
    }

    [Fact]
    public void OwnerDisposeBetweenRuntimeAndArtifactAcquisitionDefersHandlesAndFailsClosed()
    {
        using var fixture = CreateFixture();
        var provider = Provider(fixture, AppHostRuntimeBackend.PostgresDesktop);
        var plan = provider.Create(new RoleLaunchRequest(
            RuntimeRole.Server,
            Generation(RuntimeRole.Server)));
        var runtimeLifetimeLease = plan.OpenPayloadLifetimeLease();
        var replacement = fixture.Payload.CanonicalRoot + ".replacement";

        fixture.Payload.Dispose();

        fixture.Payload.VerifyClosure();
        Assert.Throws<NodePayloadManifestException>(() => plan.OpenBootstrapArtifactLease());
        Assert.ThrowsAny<IOException>(() =>
            Directory.Move(fixture.Payload.CanonicalRoot, replacement));

        runtimeLifetimeLease.Dispose();
        Directory.Move(fixture.Payload.CanonicalRoot, replacement);
    }

    [Fact]
    public void VerifyPayloadClosureAfterShutdown_UsesLivePayloadVerification()
    {
        using var fixture = CreateFixture();
        var provider = Provider(fixture, AppHostRuntimeBackend.PostgresDesktop);
        var plan = provider.Create(new RoleLaunchRequest(
            RuntimeRole.Worker,
            Generation(RuntimeRole.Worker)));
        File.WriteAllText(Path.Combine(fixture.Payload.CanonicalRoot, "unexpected.txt"), "unexpected");

        var error = Assert.Throws<NodePayloadManifestException>(
            plan.VerifyPayloadClosureAfterShutdown);

        Assert.Equal(NodePayloadManifestException.TrustFailureReason, error.ReasonCode);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(0, WorkerHealthPort)]
    [InlineData(1023, WorkerHealthPort)]
    [InlineData(65536, WorkerHealthPort)]
    [InlineData(ServerPort, 0)]
    [InlineData(ServerPort, 1023)]
    [InlineData(ServerPort, 65536)]
    [InlineData(ServerPort, ServerPort)]
    public void Constructor_InvalidOrCollidingPortsFailClosed(int serverPort, int workerPort)
    {
        using var fixture = CreateFixture();

        AssertInvalid(() => new TrustedNodeRoleLaunchPlanProvider(
            fixture.Payload,
            Options(
                fixture,
                AppHostRuntimeBackend.PostgresDesktop,
                serverPort,
                workerPort)));
    }

    [Fact]
    public void Constructor_InvalidPayloadProfileOrSecretStateFailsClosed()
    {
        using var fixture = CreateFixture();
        var valid = Options(fixture, AppHostRuntimeBackend.PostgresDesktop);

        AssertInvalid(() => new TrustedNodeRoleLaunchPlanProvider(null!, valid));
        AssertInvalid(() => new TrustedNodeRoleLaunchPlanProvider(
            fixture.Payload,
            valid with { ProfileRoot = "relative-profile-canary" }),
            "relative-profile-canary");
        AssertInvalid(() => new TrustedNodeRoleLaunchPlanProvider(
            fixture.Payload,
            valid with
            {
                SecretEnvironment = new Dictionary<string, SecretValue>
                {
                    ["PG_DATABASE_URL"] = new("postgres-secret-canary"),
                    ["APP_SECRET"] = new(AppSecret),
                },
            }),
            "postgres-secret-canary");

        fixture.Payload.Dispose();
        AssertProviderTrustFailure(() =>
            new TrustedNodeRoleLaunchPlanProvider(fixture.Payload, valid));
    }

    [Fact]
    public void Create_InvalidRequestFailsBeforeOpeningAnArtifactLease()
    {
        using var fixture = CreateFixture();
        var provider = Provider(fixture, AppHostRuntimeBackend.PostgresDesktop);
        var invalid = new RoleLaunchRequest(
            RuntimeRole.Server,
            Generation(RuntimeRole.Worker));

        AssertInvalid(() => provider.Create(null!));
        AssertInvalid(() => provider.Create(invalid));

        File.WriteAllText(fixture.Payload.NodeExecutable, "not-leased");
    }

    private TrustedNodeRoleLaunchPlanProvider Provider(
        PayloadFixture fixture,
        AppHostRuntimeBackend backend) =>
        new(fixture.Payload, Options(fixture, backend));

    private static TrustedNodeRoleLaunchPlanProviderOptions Options(
        PayloadFixture fixture,
        AppHostRuntimeBackend backend,
        int serverPort = ServerPort,
        int workerHealthPort = WorkerHealthPort) =>
        new(
            backend,
            fixture.ProfileRoot,
            serverPort,
            workerHealthPort,
            backend == AppHostRuntimeBackend.RedisCompatibility
                ? new Dictionary<string, SecretValue>
                {
                    ["PG_DATABASE_URL"] = new(PostgresSecret),
                    ["APP_SECRET"] = new(AppSecret),
                    ["REDIS_URL"] = new(RedisSecret),
                    ["REDIS_QUEUE_URL"] = new(RedisQueueSecret),
                }
                : new Dictionary<string, SecretValue>
                {
                    ["PG_DATABASE_URL"] = new(PostgresSecret),
                    ["APP_SECRET"] = new(AppSecret),
                });

    private PayloadFixture CreateFixture()
    {
        Directory.CreateDirectory(_testRoot);
        var profileRoot = Path.Combine(_testRoot, "profile");
        var payloadRoot = Path.Combine(_testRoot, "application", "payload");
        Directory.CreateDirectory(profileRoot);
        var fixture = new PayloadFixture(payloadRoot, profileRoot);
        fixture.Payload = new TrustedNodePayloadValidator(
            inspectHandle: null,
            readSecurityDescriptor: (_, _) => TrustedDescriptor())
            .Validate(payloadRoot, profileRoot);
        return fixture;
    }

    private static RawSecurityDescriptor TrustedDescriptor()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        return new RawSecurityDescriptor(
            ControlFlags.SelfRelative |
                ControlFlags.DiscretionaryAclPresent |
                ControlFlags.DiscretionaryAclProtected,
            administrators,
            group: null,
            systemAcl: null,
            discretionaryAcl: new RawAcl(GenericAcl.AclRevision, 0));
    }

    private static ProcessGeneration Generation(RuntimeRole role) =>
        new(role, 1, Guid.NewGuid());

    private static void AssertInvalid(Action action, string? canary = null)
    {
        var error = Assert.Throws<AppHostOptionsException>(action);
        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.Equal("role-launch-plan-invalid", error.Message);
        Assert.Null(error.InnerException);
        if (canary is not null)
        {
            Assert.DoesNotContain(canary, error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertProviderTrustFailure(Action action)
    {
        var error = Assert.Throws<AppHostOptionsException>(action);
        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Equal("role-payload-trust-failed", error.Message);
        Assert.Null(error.InnerException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class PayloadFixture : IDisposable
    {
        private readonly Dictionary<string, byte[]> _artifacts =
            new(StringComparer.OrdinalIgnoreCase);

        internal PayloadFixture(string payloadRoot, string profileRoot)
        {
            PayloadRoot = payloadRoot;
            ProfileRoot = profileRoot;
            Directory.CreateDirectory(PayloadRoot);
            AddArtifact("node.exe", "node");
            AddArtifact("app/server.js", "server");
            AddArtifact("app/worker.js", "worker");
            WriteManifest();
        }

        internal string PayloadRoot { get; }

        internal string ProfileRoot { get; }

        internal TrustedNodePayload Payload { get; set; } = null!;

        private string ArtifactPath(string relativePath) => Path.Combine(
            PayloadRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        private void AddArtifact(string relativePath, string content)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            _artifacts.Add(relativePath, bytes);
            var path = ArtifactPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        private void WriteManifest()
        {
            var manifest = new
            {
                version = 1,
                buildIdentity = "trusted-production-shape/1",
                nodeExecutable = "node.exe",
                serverEntrypoint = "app/server.js",
                workerEntrypoint = "app/worker.js",
                artifacts = _artifacts.Select(pair => new
                {
                    path = pair.Key,
                    length = pair.Value.LongLength,
                    sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)),
                }).ToArray(),
            };
            File.WriteAllText(
                Path.Combine(PayloadRoot, TrustedNodePayloadValidator.ManifestFileName),
                JsonSerializer.Serialize(manifest));
        }

        public void Dispose() => Payload.Dispose();
    }
}
