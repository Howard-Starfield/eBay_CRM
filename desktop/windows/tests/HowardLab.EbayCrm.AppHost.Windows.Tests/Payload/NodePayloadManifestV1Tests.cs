using System.Text;
using System.Text.Json;
using HowardLab.EbayCrm.AppHost.Windows.Payload;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Payload;

public sealed class NodePayloadManifestV1Tests
{
    private const string ValidHash =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void Parse_ValidManifest_ReturnsStrictVersionedModel()
    {
        var manifest = NodePayloadManifestV1.Parse(Utf8(ValidManifest()));

        Assert.Equal(1, manifest.Version);
        Assert.Equal("node-probe/1", manifest.BuildIdentity);
        Assert.Equal("node.exe", manifest.NodeExecutable);
        Assert.Equal("app/probes/server-probe.js", manifest.ServerEntrypoint);
        Assert.Equal("app/probes/worker-probe.js", manifest.WorkerEntrypoint);
        Assert.Equal(3, manifest.Artifacts.Count);
        Assert.Equal("node.exe", manifest.Artifacts[0].Path);
        Assert.Equal(100, manifest.Artifacts[0].Length);
        Assert.Equal(ValidHash, manifest.Artifacts[0].Sha256);
    }

    [Fact]
    public void Parse_OwnsArtifactCollection()
    {
        var manifest = NodePayloadManifestV1.Parse(Utf8(ValidManifest()));

        Assert.False(manifest.Artifacts is NodePayloadArtifactV1[]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<NodePayloadArtifactV1>)manifest.Artifacts).Add(
                new NodePayloadArtifactV1("extra.js", 1, ValidHash)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Parse_EmptyJson_FailsClosed(string json) => AssertTrustFailure(Utf8(json));

    [Fact]
    public void Parse_ManifestOverByteLimit_FailsBeforeJsonParsing()
    {
        var oversized = new byte[NodePayloadManifestV1.MaxManifestBytes + 1];
        Array.Fill(oversized, (byte)'x');

        AssertTrustFailure(oversized);
    }

    [Fact]
    public void Parse_InvalidUtf8_FailsClosed() =>
        AssertTrustFailure([0x7B, 0x22, 0xC3, 0x28, 0x22, 0x3A, 0x31, 0x7D]);

    public static TheoryData<string> InvalidRootManifests() => new()
    {
        """
        {"version":1,"version":1,"buildIdentity":"node-probe/1","nodeExecutable":"node.exe","serverEntrypoint":"app/probes/server-probe.js","workerEntrypoint":"app/probes/worker-probe.js","artifacts":[]}
        """,
        """
        {"version":1,"buildIdentity":"node-probe/1","nodeExecutable":"node.exe","serverEntrypoint":"app/probes/server-probe.js","workerEntrypoint":"app/probes/worker-probe.js","unexpected":true,"artifacts":[]}
        """,
        """
        {"version":1,"nodeExecutable":"node.exe","serverEntrypoint":"app/probes/server-probe.js","workerEntrypoint":"app/probes/worker-probe.js","artifacts":[]}
        """,
        """
        {"version":2,"buildIdentity":"node-probe/1","nodeExecutable":"node.exe","serverEntrypoint":"app/probes/server-probe.js","workerEntrypoint":"app/probes/worker-probe.js","artifacts":[]}
        """,
        """
        {"version":"1","buildIdentity":"node-probe/1","nodeExecutable":"node.exe","serverEntrypoint":"app/probes/server-probe.js","workerEntrypoint":"app/probes/worker-probe.js","artifacts":[]}
        """,
        """
        []
        """,
        """
        {"version":1,"buildIdentity":"node-probe/1","nodeExecutable":"node.exe","serverEntrypoint":"app/probes/server-probe.js","workerEntrypoint":"app/probes/worker-probe.js","artifacts":[],"artifacts":[]}
        """,
    };

    [Theory]
    [MemberData(nameof(InvalidRootManifests))]
    public void Parse_NonExactRootSchema_FailsClosed(string json) => AssertTrustFailure(Utf8(json));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0identity")]
    public void Parse_InvalidBuildIdentity_FailsClosed(string buildIdentity) =>
        AssertTrustFailure(Utf8(ValidManifest(buildIdentity: buildIdentity)));

    [Fact]
    public void Parse_OversizedBuildIdentity_FailsClosed() =>
        AssertTrustFailure(Utf8(ValidManifest(
            buildIdentity: new string('b', NodePayloadManifestV1.MaxBuildIdentityChars + 1))));

    [Theory]
    [InlineData("/node.exe")]
    [InlineData("//server/share/node.exe")]
    [InlineData("C:/payload/node.exe")]
    [InlineData("C:node.exe")]
    [InlineData("node.exe:stream")]
    [InlineData("app\\node.exe")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("app/./node.exe")]
    [InlineData("app/../node.exe")]
    [InlineData("app//node.exe")]
    [InlineData("app/")]
    [InlineData("app/node.exe.")]
    [InlineData("app/node.exe ")]
    [InlineData("CON")]
    [InlineData("app/NUL.txt")]
    [InlineData("app/file?.js")]
    [InlineData("app/file*.js")]
    public void Parse_NonCanonicalBootstrapPath_FailsClosed(string path) =>
        AssertTrustFailure(Utf8(ValidManifest(nodeExecutable: path)));

    [Theory]
    [InlineData("COM¹")]
    [InlineData("COM².txt")]
    [InlineData("nested/COM³.js")]
    [InlineData("LPT¹")]
    [InlineData("LPT².txt")]
    [InlineData("nested/LPT³.js")]
    public void Parse_SuperscriptDosDeviceAlias_FailsEvenWhenDeclared(string path)
    {
        var artifacts = BootstrapArtifacts(
            path,
            "app/probes/server-probe.js",
            "app/probes/worker-probe.js");

        AssertTrustFailure(Utf8(ValidManifest(
            nodeExecutable: path,
            artifacts: artifacts)));
    }

    [Fact]
    public void Parse_OversizedPath_FailsClosed() =>
        AssertTrustFailure(Utf8(ValidManifest(
            nodeExecutable: new string('p', NodePayloadManifestV1.MaxPathChars + 1))));

    [Fact]
    public void Parse_CaseInsensitiveArtifactDuplicate_FailsClosed()
    {
        var artifacts = $$"""
        [
          {"path":"node.exe","length":100,"sha256":"{{ValidHash}}"},
          {"path":"NODE.EXE","length":100,"sha256":"{{ValidHash}}"},
          {"path":"app/probes/server-probe.js","length":200,"sha256":"{{ValidHash}}"},
          {"path":"app/probes/worker-probe.js","length":300,"sha256":"{{ValidHash}}"}
        ]
        """;

        AssertTrustFailure(Utf8(ValidManifest(artifacts: artifacts)));
    }

    public static TheoryData<string> InvalidArtifactObjects() => new()
    {
        $$"""{"path":"node.exe","path":"node.exe","length":100,"sha256":"{{ValidHash}}"}""",
        $$"""{"path":"node.exe","length":100,"sha256":"{{ValidHash}}","unexpected":true}""",
        $$"""{"length":100,"sha256":"{{ValidHash}}"}""",
        $$"""{"path":"node.exe","sha256":"{{ValidHash}}"}""",
        """{"path":"node.exe","length":100}""",
        $$"""{"path":"node.exe","length":-1,"sha256":"{{ValidHash}}"}""",
        $$"""{"path":"node.exe","length":1.5,"sha256":"{{ValidHash}}"}""",
        $$"""{"path":"node.exe","length":"1","sha256":"{{ValidHash}}"}""",
        $$"""{"path":"node.exe","length":9223372036854775808,"sha256":"{{ValidHash}}"}""",
        """null""",
    };

    [Theory]
    [MemberData(nameof(InvalidArtifactObjects))]
    public void Parse_InvalidArtifactShapeOrLength_FailsClosed(string artifact)
    {
        var artifacts = $"[{artifact}]";

        AssertTrustFailure(Utf8(ValidManifest(artifacts: artifacts)));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("G123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789ABCDEF")]
    [InlineData("")]
    public void Parse_NonCanonicalSha256_FailsClosed(string sha256)
    {
        var artifacts = $$"""
        [
          {"path":"node.exe","length":100,"sha256":{{JsonSerializer.Serialize(sha256)}}},
          {"path":"app/probes/server-probe.js","length":200,"sha256":"{{ValidHash}}"},
          {"path":"app/probes/worker-probe.js","length":300,"sha256":"{{ValidHash}}"}
        ]
        """;

        AssertTrustFailure(Utf8(ValidManifest(artifacts: artifacts)));
    }

    [Fact]
    public void Parse_ArtifactCountOverLimit_FailsClosed()
    {
        var artifacts = ArtifactsWithCount(NodePayloadManifestV1.MaxArtifactCount + 1);

        AssertTrustFailure(Utf8(ValidManifest(artifacts: artifacts)));
    }

    [Fact]
    public void Parse_ArtifactCountAtLimit_RemainsValid()
    {
        var manifest = NodePayloadManifestV1.Parse(Utf8(ValidManifest(
            artifacts: ArtifactsWithCount(NodePayloadManifestV1.MaxArtifactCount))));

        Assert.Equal(NodePayloadManifestV1.MaxArtifactCount, manifest.Artifacts.Count);
    }

    [Theory]
    [InlineData("nodeExecutable", "different-node.exe")]
    [InlineData("serverEntrypoint", "app/probes/different-server.js")]
    [InlineData("workerEntrypoint", "app/probes/different-worker.js")]
    public void Parse_UndeclaredBootstrapArtifact_FailsClosed(string field, string path)
    {
        var json = field switch
        {
            "nodeExecutable" => ValidManifest(nodeExecutable: path),
            "serverEntrypoint" => ValidManifest(serverEntrypoint: path),
            "workerEntrypoint" => ValidManifest(workerEntrypoint: path),
            _ => throw new InvalidOperationException(),
        };

        AssertTrustFailure(Utf8(json));
    }

    [Fact]
    public void Parse_AllBootstrapFieldsReferencingSameArtifact_FailsClosed()
    {
        const string shared = "app/probes/shared.js";
        var artifacts = $$"""
        [{"path":"{{shared}}","length":100,"sha256":"{{ValidHash}}"}]
        """;

        AssertTrustFailure(Utf8(ValidManifest(
            nodeExecutable: shared,
            serverEntrypoint: shared,
            workerEntrypoint: shared,
            artifacts: artifacts)));
    }

    [Theory]
    [InlineData("node-server")]
    [InlineData("node-worker")]
    [InlineData("server-worker")]
    public void Parse_CaseOnlyBootstrapPathCollision_FailsClosed(string pair)
    {
        var node = pair is "node-server" or "node-worker" ? "app/probes/role.js" : "node.exe";
        var server = pair == "node-server" ? "APP/PROBES/ROLE.JS" : "app/probes/server.js";
        var worker = pair switch
        {
            "node-worker" => "APP/PROBES/ROLE.JS",
            "server-worker" => "APP/PROBES/SERVER.JS",
            _ => "app/probes/worker.js",
        };
        var declaredPaths = new HashSet<string>([node, server, worker], StringComparer.OrdinalIgnoreCase);
        var artifacts = "[" + string.Join(',', declaredPaths.Select((path, index) =>
            $$"""{"path":"{{path}}","length":{{index + 1}},"sha256":"{{ValidHash}}"}""")) + "]";

        AssertTrustFailure(Utf8(ValidManifest(
            nodeExecutable: node,
            serverEntrypoint: server,
            workerEntrypoint: worker,
            artifacts: artifacts)));
    }

    [Fact]
    public void Parse_BootstrapDeclarationCasingMustExactlyMatchArtifactPath()
    {
        var artifacts = BootstrapArtifacts(
            "NODE.EXE",
            "app/probes/server-probe.js",
            "app/probes/worker-probe.js");

        AssertTrustFailure(Utf8(ValidManifest(artifacts: artifacts)));
    }

    [Fact]
    public void Parse_FailureNeverLeaksManifestValues()
    {
        const string canary = "sensitive-path-canary";
        var error = Assert.Throws<NodePayloadManifestException>(() =>
            NodePayloadManifestV1.Parse(Utf8(ValidManifest(nodeExecutable: canary + ":stream"))));

        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Equal("role-payload-trust-failed", error.Message);
        Assert.DoesNotContain(canary, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    private static string ValidManifest(
        string buildIdentity = "node-probe/1",
        string nodeExecutable = "node.exe",
        string serverEntrypoint = "app/probes/server-probe.js",
        string workerEntrypoint = "app/probes/worker-probe.js",
        string? artifacts = null) => $$"""
        {
          "version": 1,
          "buildIdentity": {{JsonSerializer.Serialize(buildIdentity)}},
          "nodeExecutable": {{JsonSerializer.Serialize(nodeExecutable)}},
          "serverEntrypoint": {{JsonSerializer.Serialize(serverEntrypoint)}},
          "workerEntrypoint": {{JsonSerializer.Serialize(workerEntrypoint)}},
          "artifacts": {{artifacts ?? $$"""
          [
            {"path":"node.exe","length":100,"sha256":"{{ValidHash}}"},
            {"path":"app/probes/server-probe.js","length":200,"sha256":"{{ValidHash}}"},
            {"path":"app/probes/worker-probe.js","length":300,"sha256":"{{ValidHash}}"}
          ]
          """}}
        }
        """;

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static string ArtifactsWithCount(int count)
    {
        var artifacts = new List<string>
        {
            $$"""{"path":"node.exe","length":100,"sha256":"{{ValidHash}}"}""",
            $$"""{"path":"app/probes/server-probe.js","length":200,"sha256":"{{ValidHash}}"}""",
            $$"""{"path":"app/probes/worker-probe.js","length":300,"sha256":"{{ValidHash}}"}""",
        };
        for (var index = artifacts.Count; index < count; index++)
        {
            artifacts.Add(
                $$"""{"path":"app/modules/file-{{index}}.js","length":1,"sha256":"{{ValidHash}}"}""");
        }

        return "[" + string.Join(',', artifacts) + "]";
    }

    private static string BootstrapArtifacts(
        string node,
        string server,
        string worker) => $$"""
        [
          {"path":{{JsonSerializer.Serialize(node)}},"length":100,"sha256":"{{ValidHash}}"},
          {"path":{{JsonSerializer.Serialize(server)}},"length":200,"sha256":"{{ValidHash}}"},
          {"path":{{JsonSerializer.Serialize(worker)}},"length":300,"sha256":"{{ValidHash}}"}
        ]
        """;

    private static void AssertTrustFailure(byte[] utf8Json)
    {
        var error = Assert.Throws<NodePayloadManifestException>(() =>
            NodePayloadManifestV1.Parse(utf8Json));
        Assert.Equal("role-payload-trust-failed", error.ReasonCode);
        Assert.Equal("role-payload-trust-failed", error.Message);
        Assert.Null(error.InnerException);
    }
}
