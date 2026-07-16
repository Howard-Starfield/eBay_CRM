using System.Security.Cryptography;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Payload;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Node;

public sealed class PublishedNodeProbeRoleLaunchPlanProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ebaycrm-published-node-provider-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(RuntimeRole.Server, "server-probe.js")]
    [InlineData(RuntimeRole.Worker, "worker-probe.js")]
    public void Create_UsesExactManifestBoundDirectJavaScriptLaunch(
        RuntimeRole role,
        string entrypointName)
    {
        var payload = CreatePayload();
        var provider = new PublishedNodeProbeRoleLaunchPlanProvider(payload.Root, () => 43_123);
        var generation = new ProcessGeneration(role, 3, Guid.NewGuid());

        var plan = provider.Create(new RoleLaunchRequest(role, generation));

        var expectedEntrypoint = Path.Combine(payload.Root, "app", "probes", entrypointName);
        Assert.Equal(Path.Combine(payload.Root, "node.exe"), plan.ApplicationPath);
        Assert.Equal([expectedEntrypoint, "43123"], plan.Arguments);
        Assert.DoesNotContain("tsx", plan.Arguments);
        Assert.DoesNotContain(plan.Arguments, argument =>
            argument.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(payload.Root, plan.WorkingDirectory);
        Assert.Equal("published-node-probe/1", plan.BuildIdentity);
        Assert.Equal(43_123, plan.HealthPort);
        Assert.Empty(plan.SecretEnvironment);
        Assert.Equal("SystemRoot", Assert.Single(plan.Environment).Key);
        plan.ValidateFor(new RoleLaunchRequest(role, generation));
    }

    [Fact]
    public void LeasesProtectBootstrapFilesAndPostExitVerifierRechecksDeclaredClosure()
    {
        var payload = CreatePayload();
        var provider = new PublishedNodeProbeRoleLaunchPlanProvider(payload.Root, () => 43_123);
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());
        var plan = provider.Create(new RoleLaunchRequest(RuntimeRole.Server, generation));

        using (plan.OpenPayloadLifetimeLease())
        using (plan.OpenBootstrapArtifactLease())
        {
            Assert.Throws<IOException>(() => new FileStream(
                Path.Combine(payload.Root, "node.exe"),
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete));
            Assert.Throws<IOException>(() => new FileStream(
                Path.Combine(payload.Root, TrustedNodePayloadValidator.ManifestFileName),
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete));
            Assert.Throws<IOException>(() => new FileStream(
                payload.SharedModule,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete));
        }

        File.AppendAllText(payload.SharedModule, "tampered");

        Assert.Throws<AppHostOptionsException>(plan.VerifyPayloadClosureAfterShutdown);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("undeclared")]
    [InlineData("hash")]
    [InlineData("swapped")]
    [InlineData("typescript")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("build-identity")]
    [InlineData("extra-typescript")]
    [InlineData("invalid-length")]
    [InlineData("outside-root")]
    public void Constructor_RejectsPayloadOutsideTheExactManifestClosure(string mutation)
    {
        var payload = CreatePayload();
        switch (mutation)
        {
            case "missing":
                File.Delete(payload.WorkerEntrypoint);
                break;
            case "undeclared":
                File.WriteAllText(Path.Combine(payload.Root, "undeclared.js"), "undeclared");
                break;
            case "hash":
                File.AppendAllText(payload.WorkerEntrypoint, "tampered");
                break;
            case "swapped":
                WriteManifest(
                    payload,
                    serverEntrypoint: "app/probes/worker-probe.js",
                    workerEntrypoint: "app/probes/server-probe.js");
                break;
            case "typescript":
                var typescript = Path.Combine(payload.Root, "app", "probes", "server-probe.ts");
                File.Move(payload.ServerEntrypoint, typescript);
                payload.Artifacts.Remove("app/probes/server-probe.js");
                payload.Artifacts.Add("app/probes/server-probe.ts", typescript);
                WriteManifest(payload, serverEntrypoint: "app/probes/server-probe.ts");
                break;
            case "duplicate":
                WriteManifest(payload, duplicateFirstArtifact: true);
                break;
            case "malformed":
                File.WriteAllText(payload.Manifest, "{");
                break;
            case "build-identity":
                WriteManifest(payload, buildIdentity: "other-build/1");
                break;
            case "extra-typescript":
                var extraTypeScript = Path.Combine(payload.Root, "app", "probes", "extra.ts");
                File.WriteAllText(extraTypeScript, "typescript");
                payload.Artifacts.Add("app/probes/extra.ts", extraTypeScript);
                WriteManifest(payload);
                break;
            case "invalid-length":
                WriteManifest(payload, lengthAdjustment: 1);
                break;
            case "outside-root":
                WriteOutsideRootManifest(payload);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var error = Assert.Throws<AppHostOptionsException>(() =>
            new PublishedNodeProbeRoleLaunchPlanProvider(payload.Root, () => 43_123));

        Assert.Equal("node-probe-payload-invalid", error.ReasonCode);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("app/probes/extra.ts")]
    [InlineData("app/probes/extra.tsx")]
    [InlineData("app/probes/extra.js.map")]
    [InlineData("app/.env.js")]
    [InlineData("extra.js")]
    [InlineData("app/data.json")]
    public void Constructor_RejectsDeclaredArtifactOutsideGeneratedAllowlist(string relativePath)
    {
        var payload = CreatePayload();
        var fullPath = Path.Combine(
            payload.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "undeclared-shape");
        payload.Artifacts.Add(relativePath, fullPath);
        WriteManifest(payload);

        var error = Assert.Throws<AppHostOptionsException>(() =>
            new PublishedNodeProbeRoleLaunchPlanProvider(payload.Root, () => 43_123));

        Assert.Equal("node-probe-payload-invalid", error.ReasonCode);
    }

    [Fact]
    public void Constructor_RequiresExactEsmPackageShape()
    {
        var payload = CreatePayload();
        File.WriteAllText(
            payload.Artifacts["package.json"],
            "{\"type\":\"module\",\"private\":true}");
        WriteManifest(payload);

        var error = Assert.Throws<AppHostOptionsException>(() =>
            new PublishedNodeProbeRoleLaunchPlanProvider(payload.Root, () => 43_123));

        Assert.Equal("node-probe-payload-invalid", error.ReasonCode);
    }

    [Fact]
    public void Constructor_RejectsReparseArtifactWhenSymbolicLinksAreAvailable()
    {
        var payload = CreatePayload();
        var outside = Path.Combine(Path.GetDirectoryName(payload.Root)!, $"outside-{Guid.NewGuid():N}.js");
        File.WriteAllText(outside, "shared");
        File.Delete(payload.SharedModule);
        try
        {
            File.CreateSymbolicLink(payload.SharedModule, outside);
        }
        catch (Exception error) when (
            error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            File.Delete(outside);
            return;
        }

        try
        {
            var error = Assert.Throws<AppHostOptionsException>(() =>
                new PublishedNodeProbeRoleLaunchPlanProvider(payload.Root, () => 43_123));

            Assert.Equal("node-probe-payload-invalid", error.ReasonCode);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    private PayloadLayout CreatePayload()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app", "probes"));
        var node = Path.Combine(_root, "node.exe");
        var server = Path.Combine(_root, "app", "probes", "server-probe.js");
        var worker = Path.Combine(_root, "app", "probes", "worker-probe.js");
        var shared = Path.Combine(_root, "app", "probes", "probe-orchestrator.js");
        var package = Path.Combine(_root, "package.json");
        File.WriteAllText(node, "node");
        File.WriteAllText(server, "server");
        File.WriteAllText(worker, "worker");
        File.WriteAllText(shared, "shared");
        File.WriteAllText(package, "{\"type\":\"module\"}");
        var payload = new PayloadLayout(
            _root,
            Path.Combine(_root, TrustedNodePayloadValidator.ManifestFileName),
            server,
            worker,
            shared,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["node.exe"] = node,
                ["package.json"] = package,
                ["app/probes/server-probe.js"] = server,
                ["app/probes/worker-probe.js"] = worker,
                ["app/probes/probe-orchestrator.js"] = shared,
            });
        WriteManifest(payload);
        return payload;
    }

    private static void WriteManifest(
        PayloadLayout payload,
        string serverEntrypoint = "app/probes/server-probe.js",
        string workerEntrypoint = "app/probes/worker-probe.js",
        bool duplicateFirstArtifact = false,
        string buildIdentity = "published-node-probe/1",
        long lengthAdjustment = 0)
    {
        var artifacts = payload.Artifacts.Select(pair => new
        {
            path = pair.Key,
            length = new FileInfo(pair.Value).Length + lengthAdjustment,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Value))),
        }).ToList();
        if (duplicateFirstArtifact)
        {
            artifacts.Add(artifacts[0]);
        }

        var manifest = new
        {
            version = 1,
            buildIdentity,
            nodeExecutable = "node.exe",
            serverEntrypoint,
            workerEntrypoint,
            artifacts,
        };
        File.WriteAllText(payload.Manifest, JsonSerializer.Serialize(manifest));
    }

    private static void WriteOutsideRootManifest(PayloadLayout payload)
    {
        var artifacts = payload.Artifacts.Select(pair => new
        {
            path = pair.Key,
            length = new FileInfo(pair.Value).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Value))),
        }).ToList();
        artifacts.Add(new
        {
            path = "../outside.js",
            length = 0L,
            sha256 = new string('0', 64),
        });
        File.WriteAllText(payload.Manifest, JsonSerializer.Serialize(new
        {
            version = 1,
            buildIdentity = "published-node-probe/1",
            nodeExecutable = "node.exe",
            serverEntrypoint = "app/probes/server-probe.js",
            workerEntrypoint = "app/probes/worker-probe.js",
            artifacts,
        }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed record PayloadLayout(
        string Root,
        string Manifest,
        string ServerEntrypoint,
        string WorkerEntrypoint,
        string SharedModule,
        Dictionary<string, string> Artifacts);
}
