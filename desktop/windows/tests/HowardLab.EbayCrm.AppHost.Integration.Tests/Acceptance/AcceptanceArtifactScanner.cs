using System.IO.Compression;
using System.Text;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

internal sealed record ArtifactFinding(string Kind, string RelativeName);

internal sealed record ArtifactScanResult(int ScannedArtifacts, IReadOnlyList<ArtifactFinding> Findings);

internal static class AcceptanceArtifactScanner
{
    private const int BufferSize = 4096;

    internal static async Task<ArtifactScanResult> ScanAsync(
        string root,
        IReadOnlyCollection<string> canaries,
        long maxFileBytes,
        int maxFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (maxFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (maxFiles <= 0) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        var canonicalRoot = Path.GetFullPath(root);
        var files = Directory.EnumerateFiles(canonicalRoot, "*", SearchOption.AllDirectories).ToArray();
        var findings = new List<ArtifactFinding>();
        if (files.Length > maxFiles) findings.Add(new("file-count-bound", "."));
        var scanned = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(canonicalRoot, file);
            CheckName(relative, canaries, findings);
            var info = new FileInfo(file);
            if (info.Length > maxFileBytes) findings.Add(new("file-size-bound", relative));
            scanned++;
            await using (var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (await ContainsAnyCanaryAsync(stream, canaries, cancellationToken))
                    findings.Add(new("canary-content", relative));
            }

            if (!Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var archive = ZipFile.OpenRead(file);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryName = relative + "!/" + entry.FullName.Replace('\\', '/');
                    CheckName(entryName, canaries, findings);
                    scanned++;
                    if (scanned > maxFiles && findings.All(finding => finding.Kind != "file-count-bound"))
                        findings.Add(new("file-count-bound", entryName));
                    if (entry.Length > maxFileBytes) findings.Add(new("file-size-bound", entryName));
                    await using var entryStream = entry.Open();
                    if (await ContainsAnyCanaryAsync(entryStream, canaries, cancellationToken))
                        findings.Add(new("canary-content", entryName));
                }
            }
            catch (InvalidDataException)
            {
                findings.Add(new("invalid-zip", relative));
            }
        }

        return new ArtifactScanResult(scanned, findings);
    }

    private static void CheckName(
        string relativeName,
        IEnumerable<string> canaries,
        ICollection<ArtifactFinding> findings)
    {
        if (canaries.Any(canary => relativeName.Contains(canary, StringComparison.Ordinal)))
            findings.Add(new("canary-name", relativeName));
    }

    private static async Task<bool> ContainsAnyCanaryAsync(
        Stream stream,
        IReadOnlyCollection<string> canaries,
        CancellationToken cancellationToken)
    {
        var values = canaries.Where(value => !string.IsNullOrEmpty(value)).ToArray();
        if (values.Length == 0) return false;
        var maxCanaryBytes = values.Max(value => Encoding.UTF8.GetByteCount(value));
        var carry = Array.Empty<byte>();
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return false;
            var combined = new byte[carry.Length + read];
            carry.CopyTo(combined, 0);
            buffer.AsSpan(0, read).CopyTo(combined.AsSpan(carry.Length));
            var text = Encoding.UTF8.GetString(combined);
            if (values.Any(canary => text.Contains(canary, StringComparison.Ordinal))) return true;
            var keep = Math.Min(combined.Length, Math.Max(0, maxCanaryBytes - 1));
            carry = combined.AsSpan(combined.Length - keep, keep).ToArray();
        }
    }
}
