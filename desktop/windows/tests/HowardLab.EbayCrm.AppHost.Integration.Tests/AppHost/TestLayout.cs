using System.Net;
using System.Net.Sockets;
using HowardLab.EbayCrm.AppHost.Fixture;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

internal sealed class TestLayout : IDisposable
{
    private TestLayout(string root, string? postgresBin = null, string? fixturePath = null, int port = 15432)
    {
        Root = root;
        ProfileRoot = Path.Combine(root, "profile");
        PostgresBin = postgresBin ?? Path.Combine(root, "postgres", "bin");
        FixturePath = fixturePath ?? Path.Combine(root, "HowardLab.EbayCrm.AppHost.Fixture.exe");
        Port = port;
        Directory.CreateDirectory(ProfileRoot);
        if (postgresBin is null) Directory.CreateDirectory(PostgresBin);
        if (fixturePath is null) File.WriteAllBytes(FixturePath, [0]);
    }

    internal string Root { get; }
    internal string ProfileRoot { get; }
    internal string PostgresBin { get; }
    internal string FixturePath { get; }
    internal int Port { get; }

    internal static TestLayout Create() => new(Path.Combine(
        Path.GetTempPath(), $"ebaycrm-task9-options-{Guid.NewGuid():N}"));

    internal static TestLayout CreateReal(string prefix = "ebaycrm-task10-real")
    {
        var postgres = Environment.GetEnvironmentVariable("EBAYCRM_POSTGRES_BIN")
            ?? throw new InvalidOperationException("EBAYCRM_POSTGRES_BIN is unavailable.");
        return new TestLayout(
            Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}"),
            postgres,
            Path.ChangeExtension(typeof(FixtureMode).Assembly.Location, ".exe"),
            ReserveLoopbackPort());
    }

    internal static TestLayout CreatePublished(string prefix = "ebaycrm-task10-published")
    {
        var postgres = Environment.GetEnvironmentVariable("EBAYCRM_POSTGRES_BIN")
            ?? throw new InvalidOperationException("EBAYCRM_POSTGRES_BIN is unavailable.");
        var publish = FindPublishedDirectory();
        return new TestLayout(
            Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}"),
            postgres,
            Path.Combine(publish, "HowardLab.EbayCrm.AppHost.Fixture.exe"),
            ReserveLoopbackPort());
    }

    internal static string FindPublishedDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "desktop", "windows", "artifacts", "win-x64");
            if (File.Exists(Path.Combine(candidate, "HowardLab.EbayCrm.AppHost.exe"))) return candidate;
            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Published AppHost is unavailable. Publish desktop/windows/artifacts/win-x64 before acceptance tests.");
    }

    internal string[] Arguments(
        string mode,
        string runtimeBackend = "redis",
        string roleTarget = "controlled-fixture") =>
    [
        "--profile-root", ProfileRoot,
        "--postgres-bin", PostgresBin,
        "--fixture-path", FixturePath,
        "--port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--mode", mode,
        "--runtime-backend", runtimeBackend,
        "--role-target", roleTarget,
    ];

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
