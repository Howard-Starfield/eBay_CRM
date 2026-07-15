using System.Net;
using System.Net.Sockets;
using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;
using HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Node;

public sealed class NodeProbeAppHostSmokeTests
{
    [PostgresFact, Trait("Category", "NodeProbe")]
    public async Task ImmediateReadyNodeProbes_CompleteOneFullAppHostLifecycleWithoutDescendants()
    {
        using var layout = TestLayout.CreateReal("ebaycrm-node-probe-smoke");
        var repositoryRoot = FindRepositoryRoot();
        var nodeExecutable = FindNodeExecutable();
        var probes = Path.Combine(repositoryRoot, "desktop", "windows", "node", "src", "probes");
        var leaseFactory = new CountingLeaseFactory();
        var provider = new RecordingProvider(new NodeProbeRoleLaunchPlanProvider(
            nodeExecutable,
            repositoryRoot,
            Path.Combine(probes, "server-probe.ts"),
            Path.Combine(probes, "worker-probe.ts"),
            leaseFactory.Open,
            ReserveLoopbackPort));
        var runtime = AppHostComposition.CreateForTests(
            AppHostOptions.Parse(layout.Arguments("run")),
            roleLaunchPlanProvider: provider);
        var retained = new List<ProcessIdentitySnapshot>();
        try
        {
            await runtime.Orchestrator.StartAsync().WaitAsync(TimeSpan.FromMinutes(2));

            Assert.Equal(RuntimeState.Ready, runtime.Orchestrator.State);
            var started = runtime.Executor.SnapshotForTests();
            Assert.Equal(1, started.ServerGeneration?.Value);
            Assert.Equal(1, started.WorkerGeneration?.Value);
            Assert.Equal(RuntimeRole.Server, started.ServerGeneration?.Role);
            Assert.Equal(RuntimeRole.Worker, started.WorkerGeneration?.Role);
            Assert.Equal(
                [RuntimeRole.Server, RuntimeRole.Worker],
                provider.Requests.Select(request => request.Role).ToArray());
            Assert.Equal(2, leaseFactory.OpenCount);
            Assert.Equal(2, leaseFactory.DisposeCount);

            foreach (var processId in new[]
                     {
                         started.DatabaseProcessId,
                         started.ServerProcessId,
                         started.WorkerProcessId,
                     })
            {
                Assert.NotNull(processId);
                foreach (var item in ProcessIdentitySnapshot.EnumerateTree(processId.Value))
                {
                    if (ProcessIdentitySnapshot.TryOpen(item.ProcessId, item.ParentProcessId, out var identity))
                    {
                        retained.Add(identity!);
                    }
                }
            }

            Assert.Equal(2, retained.Count(identity =>
                identity.ImageName.Equals("node.exe", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(retained, identity =>
                identity.ImageName.Equals("postgres.exe", StringComparison.OrdinalIgnoreCase));

            await runtime.Orchestrator.StopAsync().WaitAsync(TimeSpan.FromSeconds(45));

            Assert.Equal(RuntimeState.Stopped, runtime.Orchestrator.State);
            Assert.False(File.Exists(Path.Combine(layout.ProfileRoot, "postgres-data", "postmaster.pid")));
            Assert.All(retained, identity =>
            {
                Assert.True(identity.HasExited, $"Retained process is still running: {identity.ImageName}:{identity.ProcessId}");
                Assert.True(identity.SameIdentityIfReopened());
            });
        }
        finally
        {
            await runtime.Orchestrator.DisposeAsync();
            foreach (var identity in retained)
            {
                identity.Dispose();
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json")) &&
                File.Exists(Path.Combine(
                    current.FullName,
                    "desktop",
                    "windows",
                    "node",
                    "src",
                    "probes",
                    "server-probe.ts")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException("repository-root-unavailable");
    }

    private static string FindNodeExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("EBAYCRM_NODE_EXE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "node.exe");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new InvalidOperationException("node-executable-unavailable");
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

    private sealed class RecordingProvider(IRoleLaunchPlanProvider inner) : IRoleLaunchPlanProvider
    {
        private readonly List<RoleLaunchRequest> _requests = [];

        internal IReadOnlyList<RoleLaunchRequest> Requests => _requests;

        public RoleLaunchPlan Create(RoleLaunchRequest request)
        {
            _requests.Add(request);
            return inner.Create(request);
        }
    }

    private sealed class CountingLeaseFactory
    {
        private int _openCount;
        private int _disposeCount;

        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal IDisposable Open()
        {
            Interlocked.Increment(ref _openCount);
            return new CallbackDisposable(() => Interlocked.Increment(ref _disposeCount));
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }
}
