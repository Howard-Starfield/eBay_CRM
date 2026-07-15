using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Windows.Payload;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Payload;

public sealed class TrustedNodePayloadValidatorTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "ebay-crm-payload-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Validate_ValidClosureReturnsCanonicalPayloadAndRepeatVerificationSucceeds()
    {
        using var fixture = CreateFixture();
        using var payload = Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot);

        Assert.Equal(Path.GetFullPath(fixture.PayloadRoot), payload.CanonicalRoot);
        Assert.Equal(Path.Combine(fixture.PayloadRoot, "node.exe"), payload.NodeExecutable);
        Assert.Equal(
            Path.Combine(fixture.PayloadRoot, "app", "probes", "server-probe.js"),
            payload.ServerEntrypoint);
        Assert.Equal(
            Path.Combine(fixture.PayloadRoot, "app", "probes", "worker-probe.js"),
            payload.WorkerEntrypoint);
        Assert.Equal(3, payload.ArtifactPaths.Count);

        payload.VerifyClosure();
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("extra-file")]
    [InlineData("extra-directory")]
    [InlineData("environment-file")]
    [InlineData("hash")]
    [InlineData("length")]
    public void Validate_InvalidClosureFailsClosed(string mutation)
    {
        using var fixture = CreateFixture();
        switch (mutation)
        {
            case "malformed":
                File.WriteAllText(fixture.ManifestPath, "{}");
                break;
            case "missing":
                File.Delete(fixture.ArtifactPath("app/probes/worker-probe.js"));
                break;
            case "extra-file":
                File.WriteAllText(Path.Combine(fixture.PayloadRoot, "extra.txt"), "extra");
                break;
            case "extra-directory":
                Directory.CreateDirectory(Path.Combine(fixture.PayloadRoot, "extra"));
                break;
            case "environment-file":
                File.WriteAllText(Path.Combine(fixture.PayloadRoot, ".env.production"), "secret");
                break;
            case "hash":
                fixture.WriteManifest(hashOverride: new string('0', 64));
                break;
            case "length":
                fixture.WriteManifest(lengthOverride: 999);
                break;
        }

        AssertTrustFailure(() => Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot));
    }

    [Theory]
    [InlineData("node")]
    [InlineData("server")]
    [InlineData("worker")]
    public void Validate_BootstrapArtifactsMustHaveProductionLeaves(string role)
    {
        using var fixture = CreateFixture();
        if (role == "node")
        {
            fixture.AddArtifact("bin/runtime.exe", "runtime");
            fixture.WriteManifest(nodeExecutable: "bin/runtime.exe");
        }
        else if (role == "server")
        {
            fixture.AddArtifact("app/probes/server-probe.ts", "server-ts");
            fixture.WriteManifest(serverEntrypoint: "app/probes/server-probe.ts");
        }
        else
        {
            fixture.AddArtifact("app/probes/worker-probe.ts", "worker-ts");
            fixture.WriteManifest(workerEntrypoint: "app/probes/worker-probe.ts");
        }

        AssertTrustFailure(() => Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot));
    }

    [Theory]
    [InlineData("ancestor")]
    [InlineData("root")]
    [InlineData("intermediate")]
    [InlineData("file")]
    [InlineData("manifest")]
    public void Validate_ReparseAtAnyTrustBoundaryFailsClosed(string component)
    {
        using var fixture = CreateFixture();
        var target = component switch
        {
            "ancestor" => Directory.GetParent(fixture.PayloadRoot)!.FullName,
            "root" => fixture.PayloadRoot,
            "intermediate" => Path.Combine(fixture.PayloadRoot, "app"),
            "file" => fixture.ArtifactPath("node.exe"),
            "manifest" => fixture.ManifestPath,
            _ => throw new InvalidOperationException(),
        };
        var validator = Validator((_, path, directory) => new TrustedNodePayloadPathInspection(
            Path.GetFullPath(path),
            directory,
            StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), Path.GetFullPath(target))));

        AssertTrustFailure(() => validator.Validate(fixture.PayloadRoot, fixture.ProfileRoot));
    }

    [Fact]
    public void Validate_PayloadInsideProfileFailsClosed()
    {
        using var fixture = CreateFixture(payloadInsideProfile: true);

        AssertTrustFailure(() => Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot));
    }

    [Fact]
    public void Validate_RelativeOrUncRootFailsClosed()
    {
        using var fixture = CreateFixture();

        AssertTrustFailure(() => Validator().Validate("relative-payload", fixture.ProfileRoot));
        AssertTrustFailure(() => Validator().Validate(@"\\server\share\payload", fixture.ProfileRoot));
    }

    [Fact]
    public void Validate_FinalHandlePathEscapeFailsClosed()
    {
        using var fixture = CreateFixture();
        var target = fixture.ArtifactPath("node.exe");
        var escaped = Path.Combine(_testRoot, "escaped-node.exe");
        var validator = Validator((_, path, directory) => new TrustedNodePayloadPathInspection(
            StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), Path.GetFullPath(target))
                ? escaped
                : Path.GetFullPath(path),
            directory,
            IsReparsePoint: false));

        AssertTrustFailure(() => validator.Validate(fixture.PayloadRoot, fixture.ProfileRoot));
    }

    [Fact]
    public void Validate_AclPolicyRunsForRootManifestRequiredDirectoriesAndArtifacts()
    {
        using var fixture = CreateFixture();
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validator = new TrustedNodePayloadValidator(
            inspectHandle: null,
            readSecurityDescriptor: (_, path) =>
            {
                checkedPaths.Add(Path.GetFullPath(path));
                return TrustedDescriptor();
            });

        using var payload = validator.Validate(fixture.PayloadRoot, fixture.ProfileRoot);

        var expected = new[]
        {
            fixture.PayloadRoot,
            fixture.ManifestPath,
            Path.Combine(fixture.PayloadRoot, "app"),
            Path.Combine(fixture.PayloadRoot, "app", "probes"),
            fixture.ArtifactPath("node.exe"),
            fixture.ArtifactPath("app/probes/server-probe.js"),
            fixture.ArtifactPath("app/probes/worker-probe.js"),
        }.Select(Path.GetFullPath);
        Assert.Equal(
            expected.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            checkedPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RootLeaseBlocksReplacementUntilDisposed()
    {
        using var fixture = CreateFixture();
        SafeFileHandle? observedRootHandle = null;
        var validator = Validator((handle, path, directory) =>
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(fixture.PayloadRoot)))
            {
                observedRootHandle = handle;
            }

            return new TrustedNodePayloadPathInspection(
                Path.GetFullPath(path),
                directory,
                IsReparsePoint: false);
        });
        var payload = validator.Validate(fixture.PayloadRoot, fixture.ProfileRoot);
        var replacement = Path.Combine(_testRoot, "replacement-payload");

        Assert.NotNull(observedRootHandle);
        Assert.False(observedRootHandle.IsClosed);
        Assert.ThrowsAny<IOException>(() => Directory.Move(fixture.PayloadRoot, replacement));

        payload.Dispose();
        payload.Dispose();

        Assert.True(observedRootHandle.IsClosed);
        Directory.Move(fixture.PayloadRoot, replacement);
        AssertTrustFailure(payload.VerifyClosure);
    }

    [Fact]
    public void Validate_AncestorChainLeaseBlocksParentReplacementUntilDisposed()
    {
        using var fixture = CreateFixture();
        var payloadAncestors = Ancestors(fixture.PayloadRoot).ToArray();
        var ancestorSet = new HashSet<string>(payloadAncestors, StringComparer.OrdinalIgnoreCase);
        var observedHandles = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
        var validator = Validator((handle, path, directory) =>
        {
            var canonicalPath = Path.GetFullPath(path);
            if (ancestorSet.Contains(canonicalPath))
            {
                observedHandles[canonicalPath] = handle;
            }

            return new TrustedNodePayloadPathInspection(
                canonicalPath,
                directory,
                IsReparsePoint: false);
        });
        var payload = validator.Validate(fixture.PayloadRoot, fixture.ProfileRoot);
        var parent = Directory.GetParent(fixture.PayloadRoot)!.FullName;
        var replacement = Path.Combine(_testRoot, "replacement-application");

        Assert.Equal(payloadAncestors.Length, observedHandles.Count);
        Assert.All(observedHandles.Values, handle => Assert.False(handle.IsClosed));
        Assert.ThrowsAny<IOException>(() => Directory.Move(parent, replacement));

        payload.Dispose();
        payload.Dispose();

        Assert.All(observedHandles.Values, handle => Assert.True(handle.IsClosed));
        Directory.Move(parent, replacement);
    }

    [Fact]
    public void Validate_ReparseProfileRootAliasingPayloadFailsBeforeContainment()
    {
        using var fixture = CreateFixture();
        var validator = Validator((_, path, directory) =>
        {
            var isProfileRoot = StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(fixture.ProfileRoot));
            return new TrustedNodePayloadPathInspection(
                isProfileRoot ? fixture.PayloadRoot : Path.GetFullPath(path),
                directory,
                isProfileRoot);
        });

        AssertTrustFailure(() => validator.Validate(fixture.PayloadRoot, fixture.ProfileRoot));
    }

    [Fact]
    public void VerifyClosure_TamperAfterInitialValidationFailsClosed()
    {
        using var fixture = CreateFixture();
        using var payload = Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot);

        File.WriteAllText(fixture.ArtifactPath("node.exe"), "NODE");

        AssertTrustFailure(payload.VerifyClosure);
    }

    [Fact]
    public void Validate_FailureNeverLeaksPathHashOrWin32Details()
    {
        using var fixture = CreateFixture();
        const string canary = "sensitive-payload-canary";
        File.WriteAllText(Path.Combine(fixture.PayloadRoot, canary + ".txt"), canary);

        var error = Assert.Throws<NodePayloadManifestException>(() =>
            Validator().Validate(fixture.PayloadRoot, fixture.ProfileRoot));

        Assert.Equal(NodePayloadManifestException.TrustFailureReason, error.ReasonCode);
        Assert.Equal(NodePayloadManifestException.TrustFailureReason, error.Message);
        Assert.DoesNotContain(canary, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.PayloadRoot, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.InnerException);
    }

    private TrustedNodePayloadValidator Validator(
        Func<SafeFileHandle, string, bool, TrustedNodePayloadPathInspection>? inspectHandle = null) =>
        new(inspectHandle, (_, _) => TrustedDescriptor());

    private PayloadFixture CreateFixture(bool payloadInsideProfile = false)
    {
        Directory.CreateDirectory(_testRoot);
        var profile = Path.Combine(_testRoot, "profile");
        var payload = payloadInsideProfile
            ? Path.Combine(profile, "payload")
            : Path.Combine(_testRoot, "application", "payload");
        Directory.CreateDirectory(profile);
        return new PayloadFixture(payload, profile);
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

    private static void AssertTrustFailure(Action action)
    {
        var error = Assert.Throws<NodePayloadManifestException>(action);
        Assert.Equal(NodePayloadManifestException.TrustFailureReason, error.ReasonCode);
        Assert.Equal(NodePayloadManifestException.TrustFailureReason, error.Message);
        Assert.Null(error.InnerException);
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        for (var current = Path.GetFullPath(path);
             current is not null;
             current = Directory.GetParent(current)?.FullName)
        {
            yield return current;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class PayloadFixture : IDisposable
    {
        private readonly Dictionary<string, byte[]> _artifacts =
            new(StringComparer.OrdinalIgnoreCase);

        internal PayloadFixture(string payloadRoot, string profileRoot)
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

        internal string ArtifactPath(string relativePath) => Path.Combine(
            PayloadRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        internal void AddArtifact(string relativePath, string content)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            _artifacts[relativePath] = bytes;
            var path = ArtifactPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        internal void WriteManifest(
            string nodeExecutable = "node.exe",
            string serverEntrypoint = "app/probes/server-probe.js",
            string workerEntrypoint = "app/probes/worker-probe.js",
            string? hashOverride = null,
            long? lengthOverride = null)
        {
            var manifest = new
            {
                version = 1,
                buildIdentity = "test-build/1",
                nodeExecutable,
                serverEntrypoint,
                workerEntrypoint,
                artifacts = _artifacts.Select(pair => new
                {
                    path = pair.Key,
                    length = lengthOverride ?? pair.Value.LongLength,
                    sha256 = hashOverride ?? Convert.ToHexString(SHA256.HashData(pair.Value)),
                }).ToArray(),
            };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
        }

        public void Dispose()
        {
        }
    }
}
