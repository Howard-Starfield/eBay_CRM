using System.Runtime.InteropServices;

namespace HowardLab.EbayCrm.AppHost.Windows.Postgres;

public sealed record PostgresBinaryLayout(
    string CanonicalBinDirectory,
    string InitDbExe,
    string PgCtlExe,
    string PostgresExe,
    string PsqlExe,
    string PgIsReadyExe)
{
    private static readonly string[] RequiredNames =
        ["initdb.exe", "pg_ctl.exe", "postgres.exe", "psql.exe", "pg_isready.exe"];

    public static PostgresBinaryLayout Validate(string binDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binDirectory);
        if (!Path.IsPathFullyQualified(binDirectory))
        {
            throw new ArgumentException("The PostgreSQL bin directory must be absolute.", nameof(binDirectory));
        }

        var requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binDirectory));
        if (!Directory.Exists(requested))
        {
            throw new PostgresBinaryLayoutException("postgres-bin-not-found");
        }

        var requestedPaths = RequiredNames.Select(name => Path.Combine(requested, name)).ToArray();
        if (requestedPaths.Any(path => !File.Exists(path)))
        {
            throw new PostgresBinaryLayoutException("postgres-bin-incomplete");
        }

        var canonical = ResolveFinalPath(requested, directory: true);
        var paths = requestedPaths.Select(path => ResolveFinalPath(path, directory: false)).ToArray();
        if (paths.Any(path => !StringComparer.OrdinalIgnoreCase.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!),
            canonical)))
        {
            throw new PostgresBinaryLayoutException("postgres-bin-final-path-escape");
        }

        return new PostgresBinaryLayout(canonical, paths[0], paths[1], paths[2], paths[3], paths[4]);
    }

    private static unsafe string ResolveFinalPath(string path, bool directory)
    {
        var flags = directory ? PostgresNativeMethods.FileFlagBackupSemantics : PostgresNativeMethods.FileAttributeNormal;
        using var handle = PostgresNativeMethods.CreateFile(
            path,
            desiredAccess: 0,
            PostgresNativeMethods.FileShareRead | PostgresNativeMethods.FileShareWrite | PostgresNativeMethods.FileShareDelete,
            securityAttributes: null,
            PostgresNativeMethods.OpenExisting,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid) throw new PostgresBinaryLayoutException($"postgres-bin-final-path-open-{Marshal.GetLastPInvokeError()}");

        var buffer = new char[32768];
        fixed (char* pointer = buffer)
        {
            var length = PostgresNativeMethods.GetFinalPathNameByHandle(handle, pointer, checked((uint)buffer.Length), 0);
            if (length == 0 || length >= buffer.Length)
                throw new PostgresBinaryLayoutException($"postgres-bin-final-path-query-{Marshal.GetLastPInvokeError()}");
            var resolved = new string(pointer, 0, checked((int)length));
            if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                resolved = @"\\" + resolved[8..];
            else if (resolved.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                resolved = resolved[4..];
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
        }
    }
}

public sealed class PostgresBinaryLayoutException : Exception
{
    public PostgresBinaryLayoutException(string reasonCode) : base(reasonCode) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}
