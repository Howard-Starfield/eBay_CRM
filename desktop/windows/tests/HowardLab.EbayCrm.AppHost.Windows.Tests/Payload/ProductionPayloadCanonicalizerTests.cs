using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Xml.Linq;
using HowardLab.EbayCrm.AppHost.Windows.Payload;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Payload;

public sealed class ProductionPayloadCanonicalizerTests
{
    private static readonly string[] MutationNames =
    [
        "ManifestSchemaVersion",
        "SourceCommit",
        "BuildIdentity",
        "NodeVersion",
        "YarnVersion",
        "TargetRid",
        "ProtocolIdentity",
        "GenerationIdentity",
        "NodeExecutable",
        "ServerEntrypoint",
        "WorkerEntrypoint",
        "SetupEntrypoint",
        "InstanceCommandEntrypoint",
        "AcceptanceEntrypoint",
        "AcceptanceCleanupEntrypoint",
        "CompatibilityPreflightEntrypoint",
        "FrontendEntrypoint",
        "DatabaseManifestDigest",
        "FrontendConfigurationDigest",
        "Bounds.MaxFiles",
        "Bounds.MaxRelativePathChars",
        "Bounds.MaxManifestBytes",
        "Bounds.MaxAggregateBytes",
        "Files.Ordinal",
        "Files.RelativePath",
        "Files.Length",
        "Files.Sha256",
    ];

    public static IEnumerable<object[]> SemanticMutationNames =>
        MutationNames.Select(name => new object[] { name });

    [Fact]
    public void SemanticMutationCasesMechanicallyCoverEveryDeclaredHeaderAndBoundProperty()
    {
        var declared = typeof(ProductionPayloadHeader)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(property => property.PropertyType == typeof(ProductionPayloadBounds)
                ? typeof(ProductionPayloadBounds).GetProperties().Select(bound => $"Bounds.{bound.Name}")
                : [property.Name])
            .Order(StringComparer.Ordinal)
            .ToArray();
        var cases = MutationNames
            .Where(name => !name.StartsWith("Files.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, cases);
    }

    [Theory]
    [MemberData(nameof(SemanticMutationNames))]
    public void AnySemanticMutationChangesCanonicalDigest(string mutation)
    {
        var original = Fixture();
        var changedHeader = MutateHeader(original.Header, mutation);
        var changedFiles = MutateFiles(original.Files, mutation);

        var first = ProductionPayloadCanonicalizer.ComputeDigest(original.Header, original.Files);
        if (mutation == "Files.Ordinal")
        {
            Assert.Throws<ProductionPayloadValidationException>(() =>
                ProductionPayloadCanonicalizer.ComputeDigest(changedHeader, changedFiles));
            return;
        }
        var second = ProductionPayloadCanonicalizer.ComputeDigest(changedHeader, changedFiles);

        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CanonicalizerRejectsCallerEnumerationOutsideCanonicalOrdinalOrder()
    {
        var manifest = Fixture();
        var reversed = manifest.Files.Reverse().ToArray();

        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadCanonicalizer.ComputeDigest(manifest.Header, reversed));
    }

    [Fact]
    public void SerializationIsStrictUtf8WithoutBomAndRoundTripsCanonicalDigest()
    {
        var fixture = Fixture();
        var digest = ProductionPayloadCanonicalizer.ComputeDigest(fixture.Header, fixture.Files);
        var manifest = fixture with { CanonicalDigest = digest };

        var bytes = manifest.Serialize();
        var parsed = ProductionPayloadManifestV2.Parse(bytes);

        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal(digest, parsed.CanonicalDigest);
        Assert.Equal(digest, ProductionPayloadCanonicalizer.ComputeDigest(parsed.Header, parsed.Files));
    }

    [Fact]
    public void ParseRejectsEveryNonCanonicalByteRepresentation()
    {
        var fixture = Fixture();
        var canonical = (fixture with
        {
            CanonicalDigest = ProductionPayloadCanonicalizer.ComputeDigest(fixture.Header, fixture.Files),
        }).Serialize();
        var text = Encoding.UTF8.GetString(canonical);
        var whitespace = Encoding.UTF8.GetBytes(text.Replace("{\"header\"", "{ \"header\"", StringComparison.Ordinal));
        var escaped = Encoding.UTF8.GetBytes(text.Replace("node.exe", "node\\u002eexe", StringComparison.Ordinal));
        var reordered = Encoding.UTF8.GetBytes(
            text[..^1].Replace("{\"header\":", "{\"files\":[],\"header\":", StringComparison.Ordinal) + "}");

        Assert.Throws<ProductionPayloadValidationException>(() => ProductionPayloadManifestV2.Parse(whitespace));
        Assert.Throws<ProductionPayloadValidationException>(() => ProductionPayloadManifestV2.Parse(escaped));
        Assert.Throws<ProductionPayloadValidationException>(() => ProductionPayloadManifestV2.Parse(reordered));
    }

    [Fact]
    public void StreamingParserHandlesTokensAcrossTinyChunkBoundariesWithoutWholeInputHelper()
    {
        var fixture = Fixture();
        var manifest = fixture with
        {
            Header = fixture.Header with
            {
                BuildIdentity = "phase-1c-b/chunk-boundary-" + new string('x', 127),
            },
        };
        manifest = manifest with
        {
            CanonicalDigest = ProductionPayloadCanonicalizer.ComputeDigest(manifest.Header, manifest.Files),
        };
        var canonical = manifest.Serialize();

        foreach (var chunkSize in new[] { 1, 2, 3, 7, 17, 257 })
        {
            using var stream = new MemoryStream(canonical, writable: false);
            var parsed = ProductionPayloadManifestV2.Parse(stream, chunkSize);
            Assert.Equal(manifest.CanonicalDigest, parsed.CanonicalDigest);
            Assert.Equal(manifest.Files, parsed.Files);
        }

        Assert.Null(typeof(ProductionPayloadValidator).GetMethod(
            "ReadManifest",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(ProductionPayloadValidator).GetMethod(
            "EnumerateOrdinaryFiles",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Theory]
    [InlineData("app/Foo.js", "app/foo.js")]
    [InlineData("app/one.js", "app/one.js")]
    public void ParseRejectsCaseCollidingOrDuplicatePaths(string first, string second)
    {
        var fixture = Fixture() with
        {
            Files =
            [
                new(0, first, 1, Hash('a')),
                new(1, second, 1, Hash('b')),
            ],
        };
        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadManifestV2.Parse(SerializeUnchecked(fixture)));
    }

    [Fact]
    public void ParseRejectsPunctuatedCaseVariantCollisionInCanonicalOrdinalSequence()
    {
        var fixture = Fixture() with
        {
            Files =
            [
                new(0, "app/A-[x].js", 1, Hash('1')),
                new(1, "app/a-[x].js", 1, Hash('2')),
            ],
        };

        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadManifestV2.Parse(SerializeUnchecked(fixture)));
    }

    [Theory]
    [InlineData("app/Foo.js", "app/Zed.js", "app/foo.js")]
    [InlineData("app/A.js", "app/B.js", "app/a.js")]
    public void ParseRejectsNonAdjacentCaseCollisionInOrdinalCanonicalSequence(
        string first,
        string middle,
        string last)
    {
        var fixture = NonAdjacentCaseCollisionFixture(first, middle, last);

        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadManifestV2.Parse(SerializeUnchecked(fixture)));
    }

    [Theory]
    [InlineData("app/Foo.js", "app/Zed.js", "app/foo.js")]
    [InlineData("app/A.js", "app/B.js", "app/a.js")]
    public void CanonicalizerRejectsNonAdjacentCaseCollisionInOrdinalCanonicalSequence(
        string first,
        string middle,
        string last)
    {
        var fixture = NonAdjacentCaseCollisionFixture(first, middle, last);

        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadCanonicalizer.ComputeDigest(fixture.Header, fixture.Files));
    }

    [Theory]
    [InlineData("app/Foo.js", "app/Zed.js", "app/foo.js")]
    [InlineData("app/A.js", "app/B.js", "app/a.js")]
    public void SerializationRejectsNonAdjacentCaseCollisionInOrdinalCanonicalSequence(
        string first,
        string middle,
        string last)
    {
        var fixture = NonAdjacentCaseCollisionFixture(first, middle, last);

        Assert.Throws<ProductionPayloadValidationException>(() => fixture.Serialize());
    }

    [Fact]
    public void ParsedAuthenticatedInventoryIsReadOnlyAndDigestStableAfterMutationAttempt()
    {
        var fixture = Fixture();
        fixture = fixture with
        {
            CanonicalDigest = ProductionPayloadCanonicalizer.ComputeDigest(
                fixture.Header,
                fixture.Files),
        };
        var parsed = ProductionPayloadManifestV2.Parse(fixture.Serialize());
        var inventory = parsed.Files.Select(file => file.RelativePath).ToArray();
        var digest = ProductionPayloadCanonicalizer.ComputeDigest(parsed.Header, parsed.Files);

        Assert.False(parsed.Files is List<ProductionPayloadFileRecord>);
        Assert.False(parsed.Files is ProductionPayloadFileRecord[]);
        var mutableView = Assert.IsAssignableFrom<IList<ProductionPayloadFileRecord>>(parsed.Files);
        Assert.Throws<NotSupportedException>(() => mutableView.RemoveAt(0));
        Assert.Equal(inventory, parsed.Files.Select(file => file.RelativePath));
        Assert.Equal(digest, ProductionPayloadCanonicalizer.ComputeDigest(parsed.Header, parsed.Files));
        Assert.Equal(parsed.CanonicalDigest, digest);
    }

    [Theory]
    [InlineData("../escape.js")]
    [InlineData("app\\escape.js")]
    [InlineData("app/file.js:stream")]
    [InlineData("CON.txt")]
    [InlineData("app//file.js")]
    public void ParseRejectsNonCanonicalPaths(string relativePath)
    {
        var fixture = Fixture() with
        {
            Files = [new(0, relativePath, 1, Hash('a'))],
        };

        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadManifestV2.Parse(SerializeUnchecked(fixture)));
    }

    [Fact]
    public void ParseEnforcesDeclaredManifestFileAggregateAndOrderingBounds()
    {
        var fixture = Fixture();
        var tooSmallManifestBound = fixture with
        {
            Header = fixture.Header with
            {
                Bounds = fixture.Header.Bounds with { MaxManifestBytes = 1 },
            },
        };
        var tooFewFiles = fixture with
        {
            Header = fixture.Header with
            {
                Bounds = fixture.Header.Bounds with { MaxFiles = 1 },
            },
        };
        var tooSmallAggregate = fixture with
        {
            Header = fixture.Header with
            {
                Bounds = fixture.Header.Bounds with { MaxAggregateBytes = 1 },
            },
        };
        var pathOrderMismatch = fixture with
        {
            Files =
            [
                fixture.Files[0] with { RelativePath = "z.js" },
                fixture.Files[1] with { RelativePath = "a.js" },
            ],
        };
        var lowercaseHash = fixture with
        {
            Files = [fixture.Files[0] with { Sha256 = new string('a', 64) }, fixture.Files[1]],
        };

        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadManifestV2.Parse(SerializeUnchecked(tooSmallManifestBound)));
        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadManifestV2.Parse(SerializeUnchecked(tooFewFiles)));
        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadManifestV2.Parse(SerializeUnchecked(tooSmallAggregate)));
        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadManifestV2.Parse(SerializeUnchecked(pathOrderMismatch)));
        Assert.Throws<ProductionPayloadValidationException>(() =>
            ProductionPayloadManifestV2.Parse(SerializeUnchecked(lowercaseHash)));
    }

    [Fact]
    public void ReleaseCatalogParserRejectsUnknownFieldsAndRequiresCompleteAvailableTuple()
    {
        var digest = new string('a', 64);
        var valid = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"version":1,"available":true,"manifestDigest":"{{digest}}","compatibilityIdentity":"compatibility/v1"}""");
        var unknown = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"version":1,"available":true,"manifestDigest":"{{digest}}","compatibilityIdentity":"compatibility/v1","extra":1}""");
        var incomplete = System.Text.Encoding.UTF8.GetBytes(
            """{"version":1,"available":true,"manifestDigest":null,"compatibilityIdentity":null}""");

        var parsed = ProductionReleaseCatalog.Parse(valid);

        Assert.True(parsed.IsAvailable);
        Assert.Equal(digest, parsed.ManifestDigest);
        Assert.Throws<ProductionPayloadValidationException>(() => ProductionReleaseCatalog.Parse(unknown));
        Assert.Throws<ProductionPayloadValidationException>(() => ProductionReleaseCatalog.Parse(incomplete));
    }

    [Fact]
    public void EntrypointInventoryParsesCheckedInFrontendAndRejectsInvalidDiscriminatedRecords()
    {
        var repository = FindRepositoryRoot();
        var checkedIn = File.ReadAllBytes(Path.Combine(
            repository,
            "desktop", "windows", "runtime", "production", "production-entrypoints-v1.json"));

        var inventory = ProductionEntrypointInventoryV1.Parse(checkedIn);

        var frontend = Assert.IsType<ProductionFrontendAssetEntrypointV1>(Assert.Single(inventory.Records));
        Assert.Equal("frontend", frontend.Role);
        Assert.Equal("packages/twenty-server/dist/front/index.html", frontend.EmittedPath);

        var invalid = new[]
        {
            """{"version":2,"records":[]}""",
            """{"version":1,"records":[{"kind":"unknown","role":"server","sourcePath":"a.js","emittedPath":"b.js","classification":"sideEffectFreeImport"}]}""",
            """{"version":1,"records":[{"kind":"launchExecutableJs","role":"server","sourcePath":"a.js","emittedPath":"b.js"}]}""",
            """{"version":1,"records":[{"kind":"launchExecutableJs","role":"server","sourcePath":"../a.js","emittedPath":"b.js","classification":"immutableDesktopGuarded"}]}""",
            """{"version":1,"records":[{"kind":"launchExecutableJs","role":"server","sourcePath":"a.js","emittedPath":"b.js","classification":"invalid"}]}""",
            """{"version":1,"records":[{"kind":"importRootJs","ownerRole":"SERVER!","sourcePath":"a.js","emittedPath":"b.js","classification":"sideEffectFreeImport"}]}""",
            """{"version":1,"records":[{"kind":"frontendAsset","role":"frontend","sourcePath":"a.html","emittedPath":"b.html","buildProvenance":"bad provenance"}]}""",
            """{"version":1,"records":[{"kind":"frontendAsset","role":"frontend","sourcePath":"a.html","emittedPath":"b.html","buildProvenance":"vite-v1","extra":true}]}""",
            """{"version":1,"records":[{"kind":"launchExecutableJs","role":"server","sourcePath":"a.js","emittedPath":"b.js","classification":"immutableDesktopGuarded"},{"kind":"launchExecutableJs","role":"SERVER","sourcePath":"c.js","emittedPath":"d.js","classification":"immutableDesktopGuarded"}]}""",
            """{"version":1,"records":[{"kind":"launchExecutableJs","role":"server","sourcePath":"a.js","emittedPath":"b.js","classification":"immutableDesktopGuarded"},{"kind":"importRootJs","ownerRole":"server","sourcePath":"A.js","emittedPath":"c.js","classification":"sideEffectFreeImport"}]}""",
            """{"version":1,"records":[{"kind":"launchExecutableJs","role":"server","sourcePath":"a.js","emittedPath":"b.js","classification":"immutableDesktopGuarded"},{"kind":"importRootJs","ownerRole":"server","sourcePath":"c.js","emittedPath":"B.js","classification":"sideEffectFreeImport"}]}""",
        };
        foreach (var json in invalid)
        {
            Assert.Throws<ProductionPayloadValidationException>(() =>
                ProductionEntrypointInventoryV1.Parse(Encoding.UTF8.GetBytes(json)));
        }
    }

    [Fact]
    public void ReleaseCatalogLoadExercisesOrdinaryAndSuppliedAppHostResources()
    {
        var repository = FindRepositoryRoot();
        var project = Path.Combine(
            repository,
            "desktop", "windows", "src", "HowardLab.EbayCrm.AppHost",
            "HowardLab.EbayCrm.AppHost.csproj");
        var root = Path.Combine(Path.GetTempPath(), "production-catalog-load", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var document = XDocument.Load(project);
            XNamespace ns = document.Root!.Name.Namespace;
            var defaultProperty = Assert.Single(document.Descendants(ns + "ProductionReleaseCatalogPath"));
            Assert.Equal("'$(ProductionReleaseCatalogPath)' == ''", defaultProperty.Attribute("Condition")?.Value);
            Assert.EndsWith(
                @"\runtime\production\empty-release-catalog-v1.json",
                defaultProperty.Value,
                StringComparison.OrdinalIgnoreCase);
            var resource = Assert.Single(document.Descendants(ns + "EmbeddedResource"), element =>
                element.Attribute("Include")?.Value == "$(ProductionReleaseCatalogPath)");
            Assert.Equal(
                ProductionReleaseCatalog.ResourceName,
                resource.Attribute("LogicalName")?.Value);

            var ordinaryOutput = Path.Combine(root, "ordinary");
            BuildActualAppHost(project, ordinaryOutput, catalogPath: null);
            var ordinaryAssemblyPath = Path.Combine(ordinaryOutput, "HowardLab.EbayCrm.AppHost.dll");
            var ordinaryContext = new AssemblyLoadContext("ordinary-catalog", isCollectible: true);
            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(ordinaryAssemblyPath));
                var assembly = ordinaryContext.LoadFromStream(stream);
                Assert.Contains(ProductionReleaseCatalog.ResourceName, assembly.GetManifestResourceNames());
                Assert.Equal(ProductionReleaseCatalog.Unavailable, ProductionReleaseCatalog.Load(assembly));
            }
            finally
            {
                ordinaryContext.Unload();
            }

            var digest = new string('a', 64);
            var catalogPath = Path.Combine(root, "catalog.json");
            File.WriteAllText(
                catalogPath,
                $$"""{"version":1,"available":true,"manifestDigest":"{{digest}}","compatibilityIdentity":"compatibility/v1"}""",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.Equal(
                Path.GetFullPath(catalogPath),
                Path.GetFullPath(EvaluateCatalogOverride(project, catalogPath)));
            var suppliedOutput = Path.Combine(root, "supplied");
            BuildActualAppHost(project, suppliedOutput, catalogPath);
            var suppliedContext = new AssemblyLoadContext("supplied-catalog", isCollectible: true);
            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(
                    Path.Combine(suppliedOutput, "HowardLab.EbayCrm.AppHost.dll")));
                var assembly = suppliedContext.LoadFromStream(stream);
                var supplied = ProductionReleaseCatalog.Load(assembly);
                Assert.True(supplied.IsAvailable);
                Assert.Equal(digest, supplied.ManifestDigest);
            }
            finally
            {
                suppliedContext.Unload();
            }

            Assert.Equal(
                ProductionReleaseCatalog.Unavailable,
                ProductionReleaseCatalog.Load(typeof(ProductionPayloadCanonicalizerTests).Assembly));

            File.WriteAllText(catalogPath, "{}", new UTF8Encoding(false));
            var malformedOutput = Path.Combine(root, "malformed");
            BuildActualAppHost(project, malformedOutput, catalogPath);
            var malformedContext = new AssemblyLoadContext("malformed-catalog", isCollectible: true);
            try
            {
                using var stream = new MemoryStream(File.ReadAllBytes(
                    Path.Combine(malformedOutput, "HowardLab.EbayCrm.AppHost.dll")));
                var malformedAssembly = malformedContext.LoadFromStream(stream);
                Assert.Throws<ProductionPayloadValidationException>(() =>
                    ProductionReleaseCatalog.Load(malformedAssembly));
            }
            finally
            {
                malformedContext.Unload();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTestDirectoryWithBoundedRetry(root);
            }
            var parent = Path.GetDirectoryName(root)!;
            if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
            {
                Directory.Delete(parent);
            }
        }
    }

    private static ProductionPayloadManifestV2 Fixture()
    {
        var header = new ProductionPayloadHeader(
            ManifestSchemaVersion: 2,
            SourceCommit: new string('a', 40),
            BuildIdentity: "phase-1c-b/test-build",
            NodeVersion: "24.18.0",
            YarnVersion: "4.13.0",
            TargetRid: "win-x64",
            ProtocolIdentity: "apphost-control-v1",
            GenerationIdentity: "role-generation-v1",
            NodeExecutable: "node.exe",
            ServerEntrypoint: "app/desktop/server.js",
            WorkerEntrypoint: "app/desktop/worker.js",
            SetupEntrypoint: "app/desktop/setup.js",
            InstanceCommandEntrypoint: "app/desktop/migrate.js",
            AcceptanceEntrypoint: "app/desktop/acceptance.js",
            AcceptanceCleanupEntrypoint: "app/desktop/acceptance-cleanup.js",
            CompatibilityPreflightEntrypoint: "app/desktop/compatibility-preflight.js",
            FrontendEntrypoint: "app/dist/front/index.html",
            DatabaseManifestDigest: Hash('d'),
            FrontendConfigurationDigest: Hash('f'),
            Bounds: new());
        var files = new ProductionPayloadFileRecord[]
        {
            new(0, "app/desktop/server.js", 10, Hash('1')),
            new(1, "node.exe", 20, Hash('2')),
        };
        return new ProductionPayloadManifestV2(header, files, new string('0', 64));
    }

    private static ProductionPayloadManifestV2 NonAdjacentCaseCollisionFixture(
        string first,
        string middle,
        string last) =>
        Fixture() with
        {
            Files =
            [
                new(0, first, 1, Hash('1')),
                new(1, middle, 1, Hash('2')),
                new(2, last, 1, Hash('3')),
            ],
        };

    private static ProductionPayloadHeader MutateHeader(ProductionPayloadHeader value, string mutation) =>
        mutation switch
        {
            "ManifestSchemaVersion" => value with { ManifestSchemaVersion = 3 },
            "SourceCommit" => value with { SourceCommit = new string('b', 40) },
            "BuildIdentity" => value with { BuildIdentity = "phase-1c-b/other-build" },
            "NodeVersion" => value with { NodeVersion = "24.18.1" },
            "YarnVersion" => value with { YarnVersion = "4.13.1" },
            "TargetRid" => value with { TargetRid = "win-arm64" },
            "ProtocolIdentity" => value with { ProtocolIdentity = "apphost-control-v2" },
            "GenerationIdentity" => value with { GenerationIdentity = "role-generation-v2" },
            "NodeExecutable" => value with { NodeExecutable = "runtime/node.exe" },
            "ServerEntrypoint" => value with { ServerEntrypoint = "app/desktop/server-2.js" },
            "WorkerEntrypoint" => value with { WorkerEntrypoint = "app/desktop/worker-2.js" },
            "SetupEntrypoint" => value with { SetupEntrypoint = "app/desktop/setup-2.js" },
            "InstanceCommandEntrypoint" => value with { InstanceCommandEntrypoint = "app/desktop/migrate-2.js" },
            "AcceptanceEntrypoint" => value with { AcceptanceEntrypoint = "app/desktop/acceptance-2.js" },
            "AcceptanceCleanupEntrypoint" => value with { AcceptanceCleanupEntrypoint = "app/desktop/acceptance-cleanup-2.js" },
            "CompatibilityPreflightEntrypoint" => value with { CompatibilityPreflightEntrypoint = "app/desktop/compatibility-preflight-2.js" },
            "FrontendEntrypoint" => value with { FrontendEntrypoint = "app/dist/front/other.html" },
            "DatabaseManifestDigest" => value with { DatabaseManifestDigest = Hash('e') },
            "FrontendConfigurationDigest" => value with { FrontendConfigurationDigest = Hash('0') },
            "Bounds.MaxFiles" => value with { Bounds = value.Bounds with { MaxFiles = 499_999 } },
            "Bounds.MaxRelativePathChars" => value with { Bounds = value.Bounds with { MaxRelativePathChars = 511 } },
            "Bounds.MaxManifestBytes" => value with { Bounds = value.Bounds with { MaxManifestBytes = value.Bounds.MaxManifestBytes - 1 } },
            "Bounds.MaxAggregateBytes" => value with { Bounds = value.Bounds with { MaxAggregateBytes = value.Bounds.MaxAggregateBytes - 1 } },
            _ => value,
        };

    private static IReadOnlyList<ProductionPayloadFileRecord> MutateFiles(
        IReadOnlyList<ProductionPayloadFileRecord> values,
        string mutation)
    {
        var files = values.ToArray();
        files[0] = mutation switch
        {
            "Files.Ordinal" => files[0] with { Ordinal = 7 },
            "Files.RelativePath" => files[0] with { RelativePath = "app/desktop/server-other.js" },
            "Files.Length" => files[0] with { Length = 11 },
            "Files.Sha256" => files[0] with { Sha256 = Hash('3') },
            _ => files[0],
        };
        return files;
    }

    private static string Hash(char value) => new(value, 64);

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "desktop", "windows", "EbayCrm.Desktop.sln")))
            {
                return current.FullName;
            }
        }
        throw new InvalidOperationException("repository-root-not-found");
    }

    private static void BuildActualAppHost(string project, string output, string? catalogPath)
    {
        Directory.CreateDirectory(output);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = RunActualAppHostBuild(project, output, catalogPath);
            if (result.ExitCode == 0)
            {
                return;
            }
            if (attempt < 4 &&
                result.Output.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(200);
                continue;
            }
            Assert.Fail(result.Output);
        }
        Assert.Fail("bounded-apphost-build-retry-exhausted");
    }

    private static (int ExitCode, string Output) RunActualAppHostBuild(
        string project,
        string output,
        string? catalogPath)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("build");
        process.StartInfo.ArgumentList.Add(project);
        process.StartInfo.ArgumentList.Add("--configuration");
        process.StartInfo.ArgumentList.Add("Release");
        process.StartInfo.ArgumentList.Add("--no-restore");
        process.StartInfo.ArgumentList.Add("--nologo");
        process.StartInfo.ArgumentList.Add("--disable-build-servers");
        process.StartInfo.ArgumentList.Add("-m:1");
        process.StartInfo.ArgumentList.Add("-p:BuildProjectReferences=false");
        process.StartInfo.ArgumentList.Add("--output");
        process.StartInfo.ArgumentList.Add(output);
        if (catalogPath is not null)
        {
            process.StartInfo.ArgumentList.Add($"-p:ProductionReleaseCatalogPath={catalogPath}");
        }
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("bounded-apphost-build-timeout");
        }
        var combined = (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        Assert.True(combined.Length < 1_000_000);
        return (process.ExitCode, combined);
    }

    private static void DeleteTestDirectoryWithBoundedRetry(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException &&
                deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(200);
            }
        }
    }

    private static string EvaluateCatalogOverride(string project, string catalogPath)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("msbuild");
        process.StartInfo.ArgumentList.Add(project);
        process.StartInfo.ArgumentList.Add("-nologo");
        process.StartInfo.ArgumentList.Add("-getProperty:ProductionReleaseCatalogPath");
        process.StartInfo.ArgumentList.Add($"-p:ProductionReleaseCatalogPath={catalogPath}");
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("bounded-msbuild-property-timeout");
        }
        var output = stdout.GetAwaiter().GetResult().Trim();
        var error = stderr.GetAwaiter().GetResult();
        Assert.True(output.Length + error.Length < 100_000);
        Assert.True(process.ExitCode == 0, output + error);
        return output;
    }

    private static byte[] SerializeUnchecked(ProductionPayloadManifestV2 manifest) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
}
