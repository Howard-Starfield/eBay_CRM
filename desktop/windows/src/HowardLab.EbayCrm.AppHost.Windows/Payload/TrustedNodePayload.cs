using System.Collections.ObjectModel;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public sealed class TrustedNodePayload : IDisposable
{
    private readonly Action _verifyClosure;
    private readonly object _lifetimeGate = new();
    private SafeFileHandle[]? _pathLeaseHandles;
    private int _activeLifetimeLeaseCount;
    private bool _ownerDisposed;

    internal TrustedNodePayload(
        string canonicalRoot,
        string manifestPath,
        NodePayloadManifestV1 manifest,
        IReadOnlyDictionary<string, string> artifactPaths,
        IReadOnlyList<SafeFileHandle> pathLeaseHandles,
        Action verifyClosure)
    {
        CanonicalRoot = canonicalRoot;
        ManifestPath = manifestPath;
        Manifest = manifest;
        ArtifactPaths = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(artifactPaths, StringComparer.OrdinalIgnoreCase));
        NodeExecutable = ArtifactPaths[manifest.NodeExecutable];
        ServerEntrypoint = ArtifactPaths[manifest.ServerEntrypoint];
        WorkerEntrypoint = ArtifactPaths[manifest.WorkerEntrypoint];
        _pathLeaseHandles = pathLeaseHandles.ToArray();
        _verifyClosure = verifyClosure;
    }

    public string CanonicalRoot { get; }

    public string ManifestPath { get; }

    public NodePayloadManifestV1 Manifest { get; }

    public IReadOnlyDictionary<string, string> ArtifactPaths { get; }

    public string NodeExecutable { get; }

    public string ServerEntrypoint { get; }

    public string WorkerEntrypoint { get; }

    internal IDisposable OpenLifetimeLease()
    {
        lock (_lifetimeGate)
        {
            if (_ownerDisposed || _pathLeaseHandles is null)
            {
                throw new NodePayloadManifestException();
            }

            var lease = new LifetimeLease(this);
            _activeLifetimeLeaseCount++;
            return lease;
        }
    }

    public void VerifyClosure()
    {
        lock (_lifetimeGate)
        {
            if (_pathLeaseHandles is null)
            {
                throw new NodePayloadManifestException();
            }

            try
            {
                _verifyClosure();
            }
            catch (NodePayloadManifestException)
            {
                throw;
            }
            catch
            {
                throw new NodePayloadManifestException();
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            _ownerDisposed = true;
            ReleasePathHandlesWhenUnreferenced();
        }

        GC.SuppressFinalize(this);
    }

    private void ReleaseLifetimeLease()
    {
        lock (_lifetimeGate)
        {
            if (_activeLifetimeLeaseCount <= 0)
            {
                return;
            }

            _activeLifetimeLeaseCount--;
            ReleasePathHandlesWhenUnreferenced();
        }
    }

    private void ReleasePathHandlesWhenUnreferenced()
    {
        if (!_ownerDisposed || _activeLifetimeLeaseCount != 0 || _pathLeaseHandles is not { } handles)
        {
            return;
        }

        for (var index = handles.Length - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }

        _pathLeaseHandles = null;
    }

    private sealed class LifetimeLease(TrustedNodePayload owner) : IDisposable
    {
        private TrustedNodePayload? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseLifetimeLease();
            GC.SuppressFinalize(this);
        }
    }
}
