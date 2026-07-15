namespace HowardLab.EbayCrm.AppHost.Windows.Postgres;

public sealed record PostgresClusterPaths(
    string RuntimeDirectory,
    string DataDirectory,
    string LogFile,
    string ServerLogDirectory,
    string PostmasterPidFile)
{
    public static PostgresClusterPaths Create(string disposableProfileRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(disposableProfileRoot);
        if (!Path.IsPathFullyQualified(disposableProfileRoot))
        {
            throw new ArgumentException("The profile root must be absolute.", nameof(disposableProfileRoot));
        }

        var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(disposableProfileRoot));
        var runtime = Path.Combine(profile, "runtime");
        var data = Path.Combine(profile, "postgres-data");
        return new PostgresClusterPaths(
            runtime,
            data,
            Path.Combine(runtime, "postgres.log"),
            Path.Combine(runtime, "postgres-logs"),
            Path.Combine(data, "postmaster.pid"));
    }
}
