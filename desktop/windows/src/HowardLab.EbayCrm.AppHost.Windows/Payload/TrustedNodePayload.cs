using System.Collections.ObjectModel;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public sealed class TrustedNodePayload : IDisposable
{
    private readonly Action _verifyClosure;
    private readonly object _lifetimeGate = new();
    private SafeFileHandle[]? _pathLeaseHandles;

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
            if (_pathLeaseHandles is { } handles)
            {
                for (var index = handles.Length - 1; index >= 0; index--)
                {
                    handles[index].Dispose();
                }
            }

            _pathLeaseHandles = null;
        }

        GC.SuppressFinalize(this);
    }
}
