using System.Globalization;
using System.Text;

namespace HowardLab.EbayCrm.AppHost.Windows.Postgres;

public sealed record PostmasterPidFile(
    int ProcessId,
    string CanonicalDataDirectory,
    DateTimeOffset StartTimeUtc,
    int Port,
    string Status)
{
    public static PostmasterPidFile Read(string path, string expectedDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The PID-file path must be absolute.", nameof(path));
        }

        try
        {
            return Parse(ReadBounded(path), expectedDataDirectory);
        }
        catch (PostmasterPidFileException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PostmasterPidFileException("postmaster-pid-unavailable", error);
        }
    }

    private static string ReadBounded(string path)
    {
        const int maxBytes = 16 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total > maxBytes) throw new PostmasterPidFileException("postmaster-pid-too-large");
        try { return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(buffer, 0, total); }
        catch (DecoderFallbackException error) { throw new PostmasterPidFileException("postmaster-pid-malformed", error); }
    }

    public static PostmasterPidFile Parse(string contents, string expectedDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDataDirectory);
        if (!Path.IsPathFullyQualified(expectedDataDirectory))
        {
            throw new ArgumentException("The expected data directory must be absolute.", nameof(expectedDataDirectory));
        }

        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 8 ||
            !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            string.IsNullOrWhiteSpace(lines[1]) || !Path.IsPathFullyQualified(lines[1]) ||
            !long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 ||
            !int.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is <= 0 or > 65535 ||
            string.IsNullOrWhiteSpace(lines[7]))
        {
            throw new PostmasterPidFileException("postmaster-pid-malformed");
        }

        var actualDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lines[1]));
        var expectedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDataDirectory));
        if (!StringComparer.OrdinalIgnoreCase.Equals(actualDirectory, expectedDirectory))
        {
            throw new PostmasterPidFileException("postmaster-pid-foreign-data-directory");
        }

        DateTimeOffset startTime;
        try
        {
            startTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new PostmasterPidFileException("postmaster-pid-malformed", error);
        }

        return new PostmasterPidFile(pid, actualDirectory, startTime, port, lines[7]);
    }
}

public sealed class PostmasterPidFileException : Exception
{
    public PostmasterPidFileException(string reasonCode, Exception? innerException = null)
        : base(reasonCode, innerException) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
