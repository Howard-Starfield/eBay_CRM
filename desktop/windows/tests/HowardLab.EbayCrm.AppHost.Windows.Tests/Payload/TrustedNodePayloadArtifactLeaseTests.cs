using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Windows.Payload;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Payload;

public sealed class TrustedNodePayloadArtifactLeaseTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "ebay-crm-artifact-lease-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Constructor_ValidPayloadBlocksMutationDeleteAndRenameForEveryCriticalFile()
    {
        using var fixture = CreateFixture();
        using var payload = Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot);
        var lease = new TrustedNodePayloadArtifactLease(payload);

        foreach (var path in fixture.CriticalPaths)
        {
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "replacement"));
            Assert.ThrowsAny<IOException>(() => File.Delete(path));
            Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
        }

        lease.Dispose();
        lease.Dispose();

        foreach (var path in fixture.CriticalPaths)
        {
            File.Move(path, path + ".moved");
        }
    }

    [Fact]
    public void Constructor_DisposedPayloadFailsClosedWithoutLingeringArtifactLease()
    {
        using var fixture = CreateFixture();
        var payload = Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot);
        payload.Dispose();

        AssertTrustFailure(() => new TrustedNodePayloadArtifactLease(payload), fixture.PayloadRoot);
        File.WriteAllText(fixture.ArtifactPath("node.exe"), "released");
    }

    [Fact]
    public void LifetimeLease_OwnerDisposeDefersAncestorReleaseAndAllowsVerification()
    {
        using var fixture = CreateFixture();
        var payload = Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot);
        var lifetimeLease = payload.OpenLifetimeLease();
        var replacement = fixture.PayloadRoot + ".replacement";

        payload.Dispose();
        payload.Dispose();

        payload.VerifyClosure();
        Assert.ThrowsAny<IOException>(() => Directory.Move(fixture.PayloadRoot, replacement));
        AssertTrustFailure(() => payload.OpenLifetimeLease(), fixture.PayloadRoot);

        lifetimeLease.Dispose();
        lifetimeLease.Dispose();
        Directory.Move(fixture.PayloadRoot, replacement);
        AssertTrustFailure(payload.VerifyClosure, replacement);
    }

    [Fact]
    public void LifetimeLease_ConcurrentOwnerDisposeAndAcquireIsRaceSafeAndCannotResurrect()
    {
        using var fixture = CreateFixture();
        var payload = Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot);
        using var start = new Barrier(2);
        IDisposable? acquired = null;
        Exception? acquisitionError = null;

        Parallel.Invoke(
            () =>
            {
                start.SignalAndWait();
                payload.Dispose();
            },
            () =>
            {
                start.SignalAndWait();
                try
                {
                    acquired = payload.OpenLifetimeLease();
                }
                catch (Exception error)
                {
                    acquisitionError = error;
                }
            });

        if (acquired is not null)
        {
            Assert.Null(acquisitionError);
            payload.VerifyClosure();
            acquired.Dispose();
            acquired.Dispose();
        }
        else
        {
            Assert.IsType<NodePayloadManifestException>(acquisitionError);
        }

        AssertTrustFailure(() => payload.OpenLifetimeLease(), fixture.PayloadRoot);
        Directory.Move(fixture.PayloadRoot, fixture.PayloadRoot + ".released");
    }

    [Fact]
    public void ArtifactLease_RetainsPayloadLifetimeAcrossConcurrentOwnerDispose()
    {
        using var fixture = CreateFixture();
        var payload = Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot);
        var artifactLease = new TrustedNodePayloadArtifactLease(payload);
        var replacement = fixture.PayloadRoot + ".replacement";

        payload.Dispose();

        payload.VerifyClosure();
        Assert.ThrowsAny<IOException>(() => Directory.Move(fixture.PayloadRoot, replacement));
        artifactLease.Dispose();
        artifactLease.Dispose();
        Directory.Move(fixture.PayloadRoot, replacement);
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("extra")]
    [InlineData("missing")]
    public void Constructor_InvalidClosureFailsWithoutLeavingPartialHandles(string mutation)
    {
        using var fixture = CreateFixture();
        using var payload = Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot);
        var writableAfterFailure = fixture.ManifestPath;
        switch (mutation)
        {
            case "tamper":
                writableAfterFailure = fixture.ArtifactPath("node.exe");
                File.WriteAllText(writableAfterFailure, "tampered");
                break;
            case "extra":
                writableAfterFailure = fixture.ArtifactPath("node.exe");
                File.WriteAllText(Path.Combine(fixture.PayloadRoot, "extra.txt"), "extra");
                break;
            case "missing":
                File.Delete(fixture.ArtifactPath("app/probes/server-probe.js"));
                break;
        }

        AssertTrustFailure(() => new TrustedNodePayloadArtifactLease(payload), fixture.PayloadRoot);
        File.WriteAllText(writableAfterFailure, "released");
    }

    [Fact]
    public void Constructor_NullPayloadFailsWithSanitizedTrustError()
    {
        AssertTrustFailure(
            () => new TrustedNodePayloadArtifactLease(null!),
            "constructor-open-canary");
    }

    [Fact]
    public void Dispose_ConcurrentCallsAreSafeAndReleaseEveryHandle()
    {
        using var fixture = CreateFixture();
        using var payload = Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot);
        var lease = new TrustedNodePayloadArtifactLease(payload);

        Parallel.For(0, 32, _ => lease.Dispose());

        foreach (var path in fixture.CriticalPaths)
        {
            File.Move(path, path + ".moved");
        }
    }

    private TrustedNodePayloadValidator Validator() =>
        new(inspectHandle: null, readSecurityDescriptor: (_, _) => TrustedDescriptor());

    private LeaseFixture CreateFixture()
    {
        Directory.CreateDirectory(_testRoot);
        var profile = Path.Combine(_testRoot, "profile");
        var payload = Path.Combine(_testRoot, "application", "payload");
        Directory.CreateDirectory(profile);
        return new LeaseFixture(payload, profile);
    }

    private static RawSecurityDescriptor TrustedDescriptor()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        return new RawSecurityDescriptor(
            ControlFlags.SelfRelative |
                ControlFlags.DiscretionaryAclPresent |
                ControlFlags.DiscretionaryAclProtected,
            administrators,
            group: null,
            systemAcl: null,
            discretionaryAcl: new RawAcl(GenericAcl.AclRevision, 0));
    }

    private static void AssertTrustFailure(Action action, string canary)
    {
        var error = Assert.Throws<NodePayloadManifestException>(action);
        Assert.Equal(NodePayloadManifestException.TrustFailureReason, error.ReasonCode);
        Assert.Equal(NodePayloadManifestException.TrustFailureReason, error.Message);
        Assert.DoesNotContain(canary, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.InnerException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class LeaseFixture : IDisposable
    {
        private readonly Dictionary<string, byte[]> _artifacts =
            new(StringComparer.OrdinalIgnoreCase);

        internal LeaseFixture(string payloadRoot, string profileRoot)
        {
            PayloadRoot = payloadRoot;
            ProfileRoot = profileRoot;
            Directory.CreateDirectory(PayloadRoot);
            AddArtifact("node.exe", "node");
            AddArtifact("app/probes/server-probe.js", "server");
            AddArtifact("app/probes/worker-probe.js", "worker");
            WriteManifest();
        }

        internal string PayloadRoot { get; }

        internal string ProfileRoot { get; }

        internal string ManifestPath => Path.Combine(
            PayloadRoot,
            TrustedNodePayloadValidator.ManifestFileName);

        internal IReadOnlyList<string> CriticalPaths =>
            [
                ManifestPath,
                ArtifactPath("node.exe"),
                ArtifactPath("app/probes/server-probe.js"),
                ArtifactPath("app/probes/worker-probe.js"),
            ];

        internal string ArtifactPath(string relativePath) => Path.Combine(
            PayloadRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        private void AddArtifact(string relativePath, string content)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            _artifacts.Add(relativePath, bytes);
            var path = ArtifactPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        private void WriteManifest()
        {
            var manifest = new
            {
                version = 1,
                buildIdentity = "lease-test/1",
                nodeExecutable = "node.exe",
                serverEntrypoint = "app/probes/server-probe.js",
                workerEntrypoint = "app/probes/worker-probe.js",
                artifacts = _artifacts.Select(pair => new
                {
                    path = pair.Key,
                    length = pair.Value.LongLength,
                    sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)),
                }).ToArray(),
            };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
        }

        public void Dispose()
        {
        }
    }
}
