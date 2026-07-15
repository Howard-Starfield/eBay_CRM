using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Node;

public sealed class NodeProbeRoleLaunchPlanProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ebaycrm-node-provider-{Guid.NewGuid():N}");

    public NodeProbeRoleLaunchPlanProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData(RuntimeRole.Server, "server-probe.ts")]
    [InlineData(RuntimeRole.Worker, "worker-probe.ts")]
    public void Create_BindsExactRoleGenerationAndNodeLaunchShape(
        RuntimeRole role,
        string expectedEntrypoint)
    {
        var paths = CreatePaths();
        var generation = new ProcessGeneration(role, 7, Guid.NewGuid());
        var lease = new TrackingLease();
        var provider = new NodeProbeRoleLaunchPlanProvider(
            paths.Node,
            paths.WorkingDirectory,
            paths.ServerEntrypoint,
            paths.WorkerEntrypoint,
            () => lease,
            () => 43_123);

        var plan = provider.Create(new RoleLaunchRequest(role, generation));

        Assert.Equal(role, plan.Role);
        Assert.Equal(generation, plan.Generation);
        Assert.Equal(paths.Node, plan.ApplicationPath);
        Assert.Equal(
            ["--import", "tsx", Path.Combine(paths.WorkingDirectory, "probes", expectedEntrypoint), "43123"],
            plan.Arguments);
        Assert.Equal(paths.WorkingDirectory, plan.WorkingDirectory);
        Assert.Equal("node-probe/1", plan.BuildIdentity);
        Assert.Equal(RoleReadinessStrategy.IdentityBoundHttp, plan.ReadinessStrategy);
        Assert.Equal(43_123, plan.HealthPort);
        Assert.Equal(TimeSpan.FromMilliseconds(100), plan.OutputDrainTimeout);
        Assert.Empty(plan.SecretEnvironment);
        Assert.Equal(
            Environment.GetEnvironmentVariable("SystemRoot"),
            Assert.Single(plan.Environment).Value);
        Assert.Equal("SystemRoot", Assert.Single(plan.Environment).Key);
        Assert.DoesNotContain(plan.Environment.Keys, RoleLaunchPlan.ReservedEnvironmentKeys.Contains);
        Assert.Same(lease, plan.OpenBootstrapArtifactLease());
    }

    [Fact]
    public void Create_OwnsPlanCollectionsAcrossSubsequentRequests()
    {
        var paths = CreatePaths();
        var ports = new Queue<int>([43_123, 43_124]);
        var provider = new NodeProbeRoleLaunchPlanProvider(
            paths.Node,
            paths.WorkingDirectory,
            paths.ServerEntrypoint,
            paths.WorkerEntrypoint,
            () => new TrackingLease(),
            ports.Dequeue);
        var first = provider.Create(new RoleLaunchRequest(
            RuntimeRole.Server,
            new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid())));
        var second = provider.Create(new RoleLaunchRequest(
            RuntimeRole.Worker,
            new ProcessGeneration(RuntimeRole.Worker, 2, Guid.NewGuid())));

        Assert.Equal(43_123, first.HealthPort);
        Assert.Equal("43123", first.Arguments[^1]);
        Assert.EndsWith("server-probe.ts", first.Arguments[^2], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(43_124, second.HealthPort);
        Assert.Equal("43124", second.Arguments[^1]);
        Assert.EndsWith("worker-probe.ts", second.Arguments[^2], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RuntimeRole.Database)]
    public void Create_RejectsUnsupportedRole(RuntimeRole role)
    {
        var paths = CreatePaths();
        var provider = CreateProvider(paths);
        var generation = new ProcessGeneration(role, 1, Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() =>
            provider.Create(new RoleLaunchRequest(role, generation)));
    }

    [Fact]
    public void Create_RejectsRoleGenerationMismatch()
    {
        var paths = CreatePaths();
        var provider = CreateProvider(paths);
        var generation = new ProcessGeneration(RuntimeRole.Worker, 1, Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() =>
            provider.Create(new RoleLaunchRequest(RuntimeRole.Server, generation)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void Create_RejectsNonCanonicalProbePort(int port)
    {
        var paths = CreatePaths();
        var provider = CreateProvider(paths, () => port);
        var generation = new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() =>
            provider.Create(new RoleLaunchRequest(RuntimeRole.Server, generation)));
    }

    [Theory]
    [InlineData("node")]
    [InlineData("working-directory")]
    [InlineData("server")]
    [InlineData("worker")]
    public void Constructor_RejectsMissingLaunchInput(string missingInput)
    {
        var paths = CreatePaths();
        var missing = Path.Combine(_root, $"missing-{Guid.NewGuid():N}");

        Assert.Throws<ArgumentException>(() => new NodeProbeRoleLaunchPlanProvider(
            missingInput == "node" ? missing : paths.Node,
            missingInput == "working-directory" ? missing : paths.WorkingDirectory,
            missingInput == "server" ? missing : paths.ServerEntrypoint,
            missingInput == "worker" ? missing : paths.WorkerEntrypoint,
            () => new TrackingLease(),
            () => 43_123));
    }

    private NodeProbeRoleLaunchPlanProvider CreateProvider(
        NodePaths paths,
        Func<int>? portAllocator = null) =>
        new(
            paths.Node,
            paths.WorkingDirectory,
            paths.ServerEntrypoint,
            paths.WorkerEntrypoint,
            () => new TrackingLease(),
            portAllocator ?? (() => 43_123));

    private NodePaths CreatePaths()
    {
        var node = Path.Combine(_root, "node.exe");
        File.WriteAllBytes(node, [0]);
        var workingDirectory = Path.Combine(_root, "repo");
        var probes = Path.Combine(workingDirectory, "probes");
        Directory.CreateDirectory(probes);
        var server = Path.Combine(probes, "server-probe.ts");
        var worker = Path.Combine(probes, "worker-probe.ts");
        File.WriteAllText(server, string.Empty);
        File.WriteAllText(worker, string.Empty);
        return new NodePaths(node, workingDirectory, server, worker);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed record NodePaths(
        string Node,
        string WorkingDirectory,
        string ServerEntrypoint,
        string WorkerEntrypoint);

    private sealed class TrackingLease : IDisposable
    {
        public void Dispose() { }
    }
}
