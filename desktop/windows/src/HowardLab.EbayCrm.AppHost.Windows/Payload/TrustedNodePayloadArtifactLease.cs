using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public sealed class TrustedNodePayloadArtifactLease : IDisposable
{
    private readonly object _lifetimeGate = new();
    private SafeFileHandle[]? _handles;
    private TrustedNodePayload? _payload;
    private IDisposable? _payloadLifetimeLease;

    public TrustedNodePayloadArtifactLease(TrustedNodePayload payload)
    {
        var acquired = new List<SafeFileHandle>();
        IDisposable? payloadLifetimeLease = null;
        try
        {
            payloadLifetimeLease = payload?.OpenLifetimeLease() ?? throw Failure();
            var expectedPaths = ValidateExpectedPaths(payload);
            foreach (var expectedPath in expectedPaths)
            {
                acquired.Add(OpenExactArtifact(expectedPath));
            }

            payload.VerifyClosure();
            _handles = acquired.ToArray();
            _payload = payload;
            _payloadLifetimeLease = payloadLifetimeLease;
            payloadLifetimeLease = null;
        }
        catch (NodePayloadManifestException)
        {
            DisposeHandles(acquired);
            throw;
        }
        catch
        {
            DisposeHandles(acquired);
            payloadLifetimeLease?.Dispose();
            throw Failure();
        }
        finally
        {
            payloadLifetimeLease?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_handles is { } handles)
            {
                DisposeHandles(handles);
            }

            _handles = null;
            _payload = null;
            _payloadLifetimeLease?.Dispose();
            _payloadLifetimeLease = null;
        }

        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<string> ValidateExpectedPaths(TrustedNodePayload? payload)
    {
        if (payload is null)
        {
            throw Failure();
        }

        var canonicalRoot = CanonicalLocalAbsolute(payload.CanonicalRoot);
        var expectedManifest = Path.Combine(
            canonicalRoot,
            TrustedNodePayloadValidator.ManifestFileName);
        var paths = new[]
        {
            payload.ManifestPath,
            payload.NodeExecutable,
            payload.ServerEntrypoint,
            payload.WorkerEntrypoint,
        };
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                CanonicalLocalAbsolute(payload.ManifestPath),
                expectedManifest) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFileName(payload.NodeExecutable),
                "node.exe") ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetExtension(payload.ServerEntrypoint),
                ".js") ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetExtension(payload.WorkerEntrypoint),
                ".js") ||
            paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
        {
            throw Failure();
        }

        foreach (var path in paths)
        {
            var canonicalPath = CanonicalLocalAbsolute(path);
            if (!StringComparer.OrdinalIgnoreCase.Equals(path, canonicalPath) ||
                !IsStrictlyUnder(canonicalPath, canonicalRoot))
            {
                throw Failure();
            }
        }

        return paths;
    }

    private static unsafe SafeFileHandle OpenExactArtifact(string expectedPath)
    {
        var handle = NativeMethods.CreateFile(
            expectedPath,
            NativeMethods.GenericRead | NativeMethods.FileReadAttributes,
            NativeMethods.FileShareRead,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Failure();
        }

        try
        {
            if (!NativeMethods.GetFileInformationByHandleEx(
                    handle,
                    NativeMethods.FileAttributeTagInfo,
                    out var information,
                    checked((uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInformation>())))
            {
                throw Failure();
            }

            var attributes = (FileAttributes)information.FileAttributes;
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw Failure();
            }

            var buffer = new char[32_768];
            fixed (char* pointer = buffer)
            {
                var length = NativeMethods.GetFinalPathNameByHandle(
                    handle,
                    pointer,
                    checked((uint)buffer.Length),
                    flags: 0);
                if (length == 0 || length >= buffer.Length)
                {
                    throw Failure();
                }

                var finalPath = CanonicalLocalAbsolute(NormalizeNativeFinalPath(
                    new string(pointer, 0, checked((int)length))));
                if (!StringComparer.OrdinalIgnoreCase.Equals(finalPath, expectedPath))
                {
                    throw Failure();
                }
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string NormalizeNativeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    private static string CanonicalLocalAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Contains('\0'))
        {
            throw Failure();
        }

        var root = Path.GetPathRoot(path);
        if (root is null || path.IndexOf(':', root.Length) >= 0)
        {
            throw Failure();
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsStrictlyUnder(string candidate, string root) =>
        candidate.StartsWith(
            Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void DisposeHandles(IReadOnlyList<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }

    private static NodePayloadManifestException Failure() => new();
}
