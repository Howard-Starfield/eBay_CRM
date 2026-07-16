using System.Security.Cryptography;
using HowardLab.EbayCrm.AppHost.Windows.Payload;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Payload;

public sealed class ProductionPayloadValidatorTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "ebay-crm-production-payload-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RedirectToAnotherDeclaredFileFailsBeforeLeaseOpen()
    {
        using var fixture = CreateFixture();
        var anchoredDigest = fixture.WriteManifest();
        var manifest = fixture.Manifest with
        {
            Header = fixture.Manifest.Header with { ServerEntrypoint = "app/desktop/worker.js" },
            CanonicalDigest = anchoredDigest,
        };
        fixture.WriteRawManifest(manifest.Serialize());
        var leaseOpens = 0;

        var error = Assert.Throws<ProductionPayloadValidationException>(() =>
            Validator(_ => { leaseOpens++; return new TestLease(); })
                .Validate(fixture.PayloadRoot, fixture.ProfileRoot, Catalog(anchoredDigest)));

        Assert.Equal("production-manifest-digest-mismatch", error.ReasonCode);
        Assert.Equal(0, leaseOpens);
    }

    [Theory]
    [InlineData("missing", "production-payload-file-missing")]
    [InlineData("extra", "production-payload-file-extra")]
    public void EnumerationMustEqualEveryDeclaredOrdinaryFile(string mutation, string expectedReason)
    {
        using var fixture = CreateFixture();
        var digest = fixture.WriteManifest();
        if (mutation == "missing")
        {
            File.Delete(fixture.PathFor("app/desktop/server.js"));
        }
        else
        {
            File.WriteAllText(fixture.PathFor("extra.txt"), "extra");
        }
        var leaseOpens = 0;

        var error = Assert.Throws<ProductionPayloadValidationException>(() =>
            Validator(_ => { leaseOpens++; return new TestLease(); })
                .Validate(fixture.PayloadRoot, fixture.ProfileRoot, Catalog(digest)));

        Assert.Equal(expectedReason, error.ReasonCode);
        Assert.Equal(0, leaseOpens);
    }

    [Fact]
    public void ValidPayloadReturnsNormalizedEntrypointsAndVerifiesClosure()
    {
        using var fixture = CreateFixture();
        var digest = fixture.WriteManifest();
        using var payload = Validator(_ => new TestLease())
            .Validate(fixture.PayloadRoot, fixture.ProfileRoot, Catalog(digest));

        Assert.Equal(Path.GetFullPath(fixture.PayloadRoot), payload.Root);
        Assert.Equal(fixture.PathFor("node.exe"), payload.NodeExecutable);
        Assert.Equal(fixture.PathFor("app/desktop/server.js"), payload.ServerEntrypoint);
        Assert.Equal("phase-1c-b/test-build", payload.Header.BuildIdentity);
        Assert.Equal("apphost-control-v1", payload.Header.ProtocolIdentity);
        using var lifetime = payload.OpenLifetimeLease();
        using var bootstrap = payload.OpenBootstrapLease(payload.ServerEntrypoint);
        payload.VerifyClosure();
    }

    [Fact]
    public void HashOrLengthMismatchFailsClosed()
    {
        using var fixture = CreateFixture();
        var digest = fixture.WriteManifest();
        File.WriteAllText(fixture.PathFor("node.exe"), "NODE-tampered");

        var error = Assert.Throws<ProductionPayloadValidationException>(() =>
            Validator(_ => new TestLease())
                .Validate(fixture.PayloadRoot, fixture.ProfileRoot, Catalog(digest)));

        Assert.Equal("production-payload-file-mismatch", error.ReasonCode);
    }

    [Fact]
    public void UntrustedAclOrAncestorReparseFailsBeforeLeaseOpen()
    {
        using var fixture = CreateFixture();
        var digest = fixture.WriteManifest();
        var leaseOpens = 0;
        var untrusted = new ProductionPayloadValidator(
            _ => false,
            _ => false,
            _ => { leaseOpens++; return new TestLease(); });

        var aclError = Assert.Throws<ProductionPayloadValidationException>(() =>
            untrusted.Validate(fixture.PayloadRoot, fixture.ProfileRoot, Catalog(digest)));
        Assert.Equal("production-payload-untrusted-acl", aclError.ReasonCode);

        var reparse = new ProductionPayloadValidator(
            _ => true,
            path => path.EndsWith("application", StringComparison.OrdinalIgnoreCase),
            _ => { leaseOpens++; return new TestLease(); });
        var reparseError = Assert.Throws<ProductionPayloadValidationException>(() =>
            reparse.Validate(fixture.PayloadRoot, fixture.ProfileRoot, Catalog(digest)));
        Assert.Equal("production-payload-reparse-point", reparseError.ReasonCode);
        Assert.Equal(0, leaseOpens);
    }

    [Fact]
    public void DefaultCriticalLeaseDeniesWriteDeleteAndRenameUntilReleased()
    {
        using var fixture = CreateFixture();
        var digest = fixture.WriteManifest();
        var validator = new ProductionPayloadValidator(
            _ => true,
            _ => false,
            openLease: null);
        var payload = validator.Validate(fixture.PayloadRoot, fixture.ProfileRoot, Catalog(digest));
        var lease = payload.OpenBootstrapLease(payload.ServerEntrypoint);
        var path = fixture.PathFor("app/desktop/server.js");

        Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "replace"));
        Assert.ThrowsAny<IOException>(() => File.Delete(path));
        Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));

        lease.Dispose();
        payload.Dispose();
        File.Move(path, path + ".moved");
    }

    [Fact]
    public void BootstrapLeaseRechecksCompleteClosureBeforeOpeningSelectedFile()
    {
        using var fixture = CreateFixture();
        var digest = fixture.WriteManifest();
        var leaseOpens = 0;
        using var payload = Validator(_ => { leaseOpens++; return new TestLease(); })
            .Validate(fixture.PayloadRoot, fixture.ProfileRoot, Catalog(digest));
        var opensAfterValidation = leaseOpens;
        File.WriteAllText(fixture.PathFor("app/desktop/worker.js"), "tampered");

        var error = Assert.Throws<ProductionPayloadValidationException>(() =>
            payload.OpenBootstrapLease(payload.ServerEntrypoint));

        Assert.Equal("production-payload-file-mismatch", error.ReasonCode);
        Assert.Equal(opensAfterValidation, leaseOpens);
    }

    [Fact]
    public void BoundsRejectFileCountPathManifestAndAggregateOverflow()
    {
        var fixedBounds = new ProductionPayloadBounds();
        Assert.Throws<ProductionPayloadValidationException>(() =>
            new ProductionPayloadBounds(MaxFiles: fixedBounds.MaxFiles + 1).Validate());
        Assert.Throws<ProductionPayloadValidationException>(() =>
            new ProductionPayloadBounds(MaxRelativePathChars: fixedBounds.MaxRelativePathChars + 1).Validate());
        Assert.Throws<ProductionPayloadValidationException>(() =>
            new ProductionPayloadBounds(MaxManifestBytes: fixedBounds.MaxManifestBytes + 1).Validate());
        Assert.Throws<ProductionPayloadValidationException>(() =>
            new ProductionPayloadBounds(MaxAggregateBytes: fixedBounds.MaxAggregateBytes + 1).Validate());
    }

    [Fact]
    public void EmptyReleaseCatalogFailsWithBoundedUnavailableReason()
    {
        using var fixture = CreateFixture();
        fixture.WriteManifest();

        var error = Assert.Throws<ProductionPayloadValidationException>(() =>
            Validator(_ => new TestLease()).Validate(
                fixture.PayloadRoot,
                fixture.ProfileRoot,
                ProductionReleaseCatalog.Unavailable));

        Assert.Equal("production-release-catalog-unavailable", error.ReasonCode);
    }

    [Fact]
    public void FilesystemTraversalAbortsAtDeclaredFilePathAndEntryBounds()
    {
        using (var fileBound = CreateFixture())
        {
            fileBound.Manifest = fileBound.Manifest with
            {
                Header = fileBound.Manifest.Header with
                {
                    Bounds = fileBound.Manifest.Header.Bounds with
                    {
                        MaxFiles = fileBound.Manifest.Files.Count,
                    },
                },
            };
            var digest = fileBound.WriteManifest();
            File.WriteAllText(fileBound.PathFor("extra-one.txt"), "1");
            var error = Assert.Throws<ProductionPayloadValidationException>(() =>
                Validator(_ => new TestLease()).Validate(
                    fileBound.PayloadRoot, fileBound.ProfileRoot, Catalog(digest)));
            Assert.Equal("production-payload-file-extra", error.ReasonCode);
        }

        using (var pathBound = CreateFixture())
        {
            pathBound.Manifest = pathBound.Manifest with
            {
                Header = pathBound.Manifest.Header with
                {
                    Bounds = pathBound.Manifest.Header.Bounds with { MaxRelativePathChars = 64 },
                },
            };
            var digest = pathBound.WriteManifest();
            File.WriteAllText(pathBound.PathFor(new string('x', 65)), "x");
            var error = Assert.Throws<ProductionPayloadValidationException>(() =>
                Validator(_ => new TestLease()).Validate(
                    pathBound.PayloadRoot, pathBound.ProfileRoot, Catalog(digest)));
            Assert.Equal("production-payload-bounds-invalid", error.ReasonCode);
        }

        using (var entryBound = CreateFixture())
        {
            entryBound.Manifest = entryBound.Manifest with
            {
                Header = entryBound.Manifest.Header with
                {
                    Bounds = entryBound.Manifest.Header.Bounds with
                    {
                        MaxFiles = entryBound.Manifest.Files.Count,
                    },
                },
            };
            var digest = entryBound.WriteManifest();
            for (var index = 0; index < 16; index++)
            {
                Directory.CreateDirectory(entryBound.PathFor($"empty-{index:D2}"));
            }
            var error = Assert.Throws<ProductionPayloadValidationException>(() =>
                Validator(_ => new TestLease()).Validate(
                    entryBound.PayloadRoot, entryBound.ProfileRoot, Catalog(digest)));
            Assert.Equal("production-payload-bounds-invalid", error.ReasonCode);
        }
    }

    [Fact]
    public void StreamingClosureHandlesLargeReducedFixtureAtTightDeclaredBounds()
    {
        var additional = Enumerable.Range(0, 256).ToDictionary(
            index => $"bulk/file-{index:D4}.txt",
            index => System.Text.Encoding.UTF8.GetBytes($"value-{index:D4}"),
            StringComparer.Ordinal);
        using var fixture = CreateFixture(additional);
        var exactAggregate = fixture.Manifest.Files.Sum(file => file.Length);
        fixture.Manifest = fixture.Manifest with
        {
            Header = fixture.Manifest.Header with
            {
                Bounds = new ProductionPayloadBounds(
                    MaxFiles: fixture.Manifest.Files.Count,
                    MaxRelativePathChars: 64,
                    MaxManifestBytes: 128 * 1024,
                    MaxAggregateBytes: exactAggregate),
            },
        };
        var digest = fixture.WriteManifest();

        using var payload = Validator(_ => new TestLease()).Validate(
            fixture.PayloadRoot,
            fixture.ProfileRoot,
            Catalog(digest));

        Assert.Equal(fixture.Manifest.Files.Count, payload.Files.Count);
    }

    [Fact]
    public void HostileExtraFileAbortsBeforeLaterExpectedHashValidation()
    {
        using var fixture = CreateFixture();
        var digest = fixture.WriteManifest();
        File.WriteAllText(fixture.PathFor("000-hostile-extra.txt"), "extra");
        File.WriteAllText(fixture.PathFor("node.exe"), "tampered-after-extra");

        var error = Assert.Throws<ProductionPayloadValidationException>(() =>
            Validator(_ => new TestLease()).Validate(
                fixture.PayloadRoot,
                fixture.ProfileRoot,
                Catalog(digest)));

        Assert.Equal("production-payload-file-extra", error.ReasonCode);
    }

    [Fact]
    public void OrdinalFilesystemMergeHandlesNestedDirectoryAndSiblingPunctuationOrdering()
    {
        using var fixture = CreateFixture(new Dictionary<string, byte[]>
        {
            ["a.js"] = [1],
            ["a/0.txt"] = [2],
            ["a-z.txt"] = [3],
            ["a0.txt"] = [4],
            ["nested/deeper/!.txt"] = [5],
            ["nested/deeper/z.txt"] = [6],
            ["nested-z.txt"] = [7],
        });
        var digest = fixture.WriteManifest();

        using var payload = Validator(_ => new TestLease()).Validate(
            fixture.PayloadRoot,
            fixture.ProfileRoot,
            Catalog(digest));

        Assert.Equal(fixture.Manifest.Files.Count, payload.Files.Count);
    }

    [Fact]
    public void TightNestedMergeReportsExactMissingAndExtraWithoutExpectedClone()
    {
        using (var missing = CreateFixture(new Dictionary<string, byte[]>
        {
            ["merge/a/one.txt"] = [1],
            ["merge/z/two.txt"] = [2],
        }))
        {
            var digest = missing.WriteManifest();
            File.Delete(missing.PathFor("merge/z/two.txt"));
            var error = Assert.Throws<ProductionPayloadValidationException>(() =>
                Validator(_ => new TestLease()).Validate(
                    missing.PayloadRoot, missing.ProfileRoot, Catalog(digest)));
            Assert.Equal("production-payload-file-missing", error.ReasonCode);
        }

        using (var extra = CreateFixture(new Dictionary<string, byte[]>
        {
            ["merge/a/one.txt"] = [1],
            ["merge/z/two.txt"] = [2],
        }))
        {
            var digest = extra.WriteManifest();
            var extraPath = extra.PathFor("merge/m/extra.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
            File.WriteAllBytes(extraPath, [3]);
            var error = Assert.Throws<ProductionPayloadValidationException>(() =>
                Validator(_ => new TestLease()).Validate(
                    extra.PayloadRoot, extra.ProfileRoot, Catalog(digest)));
            Assert.Equal("production-payload-file-extra", error.ReasonCode);
        }

        var duplicateClosureFields = typeof(ProductionPayloadValidator)
            .GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
            .SelectMany(type => type.GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public))
            .Where(field => field.FieldType.IsGenericType &&
                field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            .ToArray();
        Assert.Empty(duplicateClosureFields);
    }

    [Fact]
    public void PerDirectoryBatchCollisionCheckRejectsNonAdjacentCaseVariants()
    {
        var method = typeof(ProductionPayloadValidator).GetMethod(
            "ValidateFilesystemBatchCaseCollisions",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        Assert.NotNull(method);
        var batch = new List<string>
        {
            "app/Foo.js",
            "app/Zed.js",
            "app/foo.js",
        };
        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method!.MakeGenericMethod(typeof(string)).Invoke(
                null,
                [batch, (Func<string, string>)(path => path)]));

        var error = Assert.IsType<ProductionPayloadValidationException>(invocation.InnerException);
        Assert.Equal("production-manifest-record-invalid", error.ReasonCode);
    }

    [Fact]
    public void ValidatedAuthenticatedInventoryIsReadOnlyAndDigestStableAfterMutationAttempt()
    {
        using var fixture = CreateFixture();
        var digest = fixture.WriteManifest();
        using var payload = Validator(_ => new TestLease()).Validate(
            fixture.PayloadRoot,
            fixture.ProfileRoot,
            Catalog(digest));
        var inventory = payload.Files.Select(file => file.RelativePath).ToArray();

        Assert.False(payload.Files is List<ProductionPayloadFileRecord>);
        Assert.False(payload.Files is ProductionPayloadFileRecord[]);
        var mutableView = Assert.IsAssignableFrom<IList<ProductionPayloadFileRecord>>(payload.Files);
        Assert.Throws<NotSupportedException>(() => mutableView.RemoveAt(0));
        Assert.Equal(inventory, payload.Files.Select(file => file.RelativePath));
        Assert.Equal(digest, ProductionPayloadCanonicalizer.ComputeDigest(payload.Header, payload.Files));
    }

    [Fact]
    public void UndeclaredNtfsAlternateDataStreamFailsClosed()
    {
        using var fixture = CreateFixture();
        var digest = fixture.WriteManifest();
        File.WriteAllText(fixture.PathFor("node.exe") + ":task1-hidden", "hidden");

        var error = Assert.Throws<ProductionPayloadValidationException>(() =>
            Validator(_ => new TestLease()).Validate(
                fixture.PayloadRoot, fixture.ProfileRoot, Catalog(digest)));

        Assert.Equal("production-payload-alternate-data-stream", error.ReasonCode);
    }

    [Theory]
    [InlineData("non-pe", 0)]
    [InlineData("truncated", 0x8664)]
    [InlineData("wrong-machine", 0x014c)]
    public void NativeAddonRejectsNonPeTruncatedAndWrongMachineInputs(string kind, int machine)
    {
        var bytes = kind switch
        {
            "non-pe" => System.Text.Encoding.ASCII.GetBytes("not-a-portable-executable"),
            "truncated" => PeBytes(checked((ushort)machine), length: 64),
            _ => PeBytes(checked((ushort)machine), length: 512),
        };
        using var fixture = CreateFixture(new Dictionary<string, byte[]>
        {
            ["native/addon.node"] = bytes,
        });
        var digest = fixture.WriteManifest();

        var error = Assert.Throws<ProductionPayloadValidationException>(() =>
            Validator(_ => new TestLease()).Validate(
                fixture.PayloadRoot, fixture.ProfileRoot, Catalog(digest)));

        Assert.Equal("production-payload-native-addon-invalid", error.ReasonCode);
    }

    [Fact]
    public void NativeAddonAcceptsBoundedPeX64HeaderAtCanonicalPayloadIdentity()
    {
        using var fixture = CreateFixture(new Dictionary<string, byte[]>
        {
            ["native/addon.node"] = PeBytes(0x8664, length: 512),
        });
        var digest = fixture.WriteManifest();

        using var payload = Validator(_ => new TestLease()).Validate(
            fixture.PayloadRoot, fixture.ProfileRoot, Catalog(digest));

        Assert.Contains(payload.Files, file => file.RelativePath == "native/addon.node");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private Fixture CreateFixture(IReadOnlyDictionary<string, byte[]>? additionalFiles = null)
    {
        var fixtureRoot = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(fixtureRoot, "profile");
        var payload = Path.Combine(fixtureRoot, "application", "payload");
        Directory.CreateDirectory(profile);
        return new Fixture(payload, profile, additionalFiles);
    }

    private static ProductionPayloadValidator Validator(Func<string, IDisposable> openLease) =>
        new(_ => true, _ => false, openLease);

    private static ProductionReleaseCatalog Catalog(string digest) =>
        new(true, digest, "compatibility/test-v1");

    private static byte[] PeBytes(ushort machine, int length)
    {
        var bytes = new byte[length];
        if (length >= 2)
        {
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
        }
        if (length >= 64)
        {
            BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        }
        if (length >= 0x86)
        {
            bytes[0x80] = (byte)'P';
            bytes[0x81] = (byte)'E';
            bytes[0x82] = 0;
            bytes[0x83] = 0;
            BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        }
        return bytes;
    }

    private sealed class TestLease : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        internal Fixture(
            string payloadRoot,
            string profileRoot,
            IReadOnlyDictionary<string, byte[]>? additionalFiles)
        {
            PayloadRoot = payloadRoot;
            ProfileRoot = profileRoot;
            Directory.CreateDirectory(PayloadRoot);
            Add("node.exe", "node");
            Add("app/desktop/server.js", "server");
            Add("app/desktop/worker.js", "worker");
            Add("app/desktop/setup.js", "setup");
            Add("app/desktop/migrate.js", "migrate");
            Add("app/desktop/acceptance.js", "accept");
            Add("app/desktop/acceptance-cleanup.js", "cleanup");
            Add("app/desktop/compatibility-preflight.js", "preflight");
            Add("app/dist/front/index.html", "<html></html>");
            if (additionalFiles is not null)
            {
                foreach (var pair in additionalFiles)
                {
                    Add(pair.Key, pair.Value);
                }
            }

            var header = new ProductionPayloadHeader(
                2,
                new string('a', 40),
                "phase-1c-b/test-build",
                "24.18.0",
                "4.13.0",
                "win-x64",
                "apphost-control-v1",
                "role-generation-v1",
                "node.exe",
                "app/desktop/server.js",
                "app/desktop/worker.js",
                "app/desktop/setup.js",
                "app/desktop/migrate.js",
                "app/desktop/acceptance.js",
                "app/desktop/acceptance-cleanup.js",
                "app/desktop/compatibility-preflight.js",
                "app/dist/front/index.html",
                new string('d', 64),
                new string('f', 64),
                new());
            var records = _files
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select((pair, ordinal) => new ProductionPayloadFileRecord(
                    ordinal,
                    pair.Key,
                    pair.Value.LongLength,
                    Convert.ToHexString(SHA256.HashData(pair.Value))))
                .ToArray();
            Manifest = new(header, records, new string('0', 64));
        }

        internal string PayloadRoot { get; }
        internal string ProfileRoot { get; }
        internal ProductionPayloadManifestV2 Manifest { get; set; }

        internal string PathFor(string relative) => Path.Combine(
            PayloadRoot,
            relative.Replace('/', Path.DirectorySeparatorChar));

        internal string WriteManifest()
        {
            var digest = ProductionPayloadCanonicalizer.ComputeDigest(Manifest.Header, Manifest.Files);
            Manifest = Manifest with { CanonicalDigest = digest };
            File.WriteAllBytes(
                Path.Combine(PayloadRoot, ProductionPayloadValidator.ManifestFileName),
                Manifest.Serialize());
            return digest;
        }

        internal void WriteRawManifest(byte[] bytes) => File.WriteAllBytes(
            Path.Combine(PayloadRoot, ProductionPayloadValidator.ManifestFileName),
            bytes);

        private void Add(string relative, string value)
        {
            Add(relative, System.Text.Encoding.UTF8.GetBytes(value));
        }

        private void Add(string relative, byte[] bytes)
        {
            _files.Add(relative, bytes);
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        public void Dispose() { }
    }
}
