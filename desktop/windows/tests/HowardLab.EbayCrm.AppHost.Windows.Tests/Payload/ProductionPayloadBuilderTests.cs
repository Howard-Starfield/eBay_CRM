using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HowardLab.EbayCrm.AppHost.Windows.Payload;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Payload;

public sealed class ProductionPayloadBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "phase-1c-b-builder-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExactPinnedNodeAndYarnIdentitiesCannotDrift()
    {
        Assert.Equal("24.18.0", ProductionPayloadBuilder.NodeVersion);
        Assert.Equal("4.13.0", ProductionPayloadBuilder.YarnVersion);
        Assert.Equal(
            "0AE68406B42D7725661DA979B1403EC9926DA205C6770827F33AAC9D8F26E821",
            ProductionPayloadBuilder.NodeArchiveSha256);
        Assert.Equal(
            "9A4EB5F1C29C6A2E93852EAD46B999E284A6A5CA8BAB4D4E241D587D025A52DE",
            ProductionPayloadBuilder.NodeExecutableSha256);
        Assert.Equal(
            "CN=OpenJS Foundation, O=OpenJS Foundation, L=San Francisco, S=California, C=US",
            ProductionPayloadBuilder.NodeAuthenticodeSubject);
        Assert.Equal("2.55.0.windows.2", ProductionPayloadBuilder.MinGitVersion);
        Assert.Equal(
            "E3EA2944CEA4B3FABCD69C7C1669EF69B1B66C05AC7806D81224D0ABAD2DEC31",
            ProductionPayloadBuilder.MinGitArchiveSha256);
        Assert.Equal(38_839_825, ProductionPayloadBuilder.MinGitArchiveLength);
        Assert.Equal(
            "22FEAD8244EF3A7225FB800099A4E43ECA8BCEC0466774917669599C2F19A05A",
            ProductionPayloadBuilder.MinGitExecutableSha256);
        Assert.Equal(46_936, ProductionPayloadBuilder.MinGitExecutableLength);
        Assert.Equal(
            "CN=Johannes Schindelin, O=Johannes Schindelin, L=Bruehl, C=DE",
            ProductionPayloadBuilder.MinGitAuthenticodeSubject);
        Assert.Equal(
            "@electron-node-gyp-https-d0f303c37e-e8c97bb534.zip",
            ProductionPayloadBuilder.MinGitLocatorCacheFileName);
    }

    [Fact]
    public void ScriptPinsAndValidatesBuildOnlyMinGitBeforeFocusedDevelopmentInstall()
    {
        var repository = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));

        Assert.Contains("[string] $GitArchivePath", script, StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.2/MinGit-2.55.0.2-64-bit.zip",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "E3EA2944CEA4B3FABCD69C7C1669EF69B1B66C05AC7806D81224D0ABAD2DEC31",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Expand-PinnedMinGitArchive", script, StringComparison.Ordinal);
        Assert.Contains("Assert-ExactOrdinaryTreeFromZip", script, StringComparison.Ordinal);
        Assert.Contains("phase1cb-offline-git-archive-required", script, StringComparison.Ordinal);
        Assert.Contains("'--git-root', $pinnedMinGit", script, StringComparison.Ordinal);
        Assert.Contains("Assert-MinGitAbsentFromPayload", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Expand-Archive -LiteralPath $gitArchive", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedDefaultSourceStagingRequiresCleanHeadAndCopiesTrackedFilesOnly()
    {
        var repository = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));

        Assert.Contains("Assert-TrustedSourceCheckout", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$status = @(& git", script, StringComparison.Ordinal);
        Assert.Contains("-StatusPorcelain -OnPath", script, StringComparison.Ordinal);
        Assert.Contains("if ($IncludeUntracked)", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-StreamingGitInventory", script, StringComparison.Ordinal);
        Assert.Contains("'-z', '--cached'", script, StringComparison.Ordinal);
        Assert.Contains("@('ls-files', '-z', '--cached', '--others', '--exclude-standard')", script, StringComparison.Ordinal);
        Assert.Contains("-AllowUntrustedSource:$ClosureOnly", script, StringComparison.Ordinal);
        Assert.Contains("production-source-byte-mismatch", script, StringComparison.Ordinal);
        Assert.Contains("production-source-head-changed", script, StringComparison.Ordinal);
    }

    [Fact]
    public void FakeRunnerObservesTheExactPinnedArgumentVectors()
    {
        var build = Path.Combine(_root, "build");
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var runner = new RecordingRunner();
        var builder = new ProductionPayloadBuilder(
            runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
            new AcceptedMinGitProbe());

        builder.RunBuildCommands(node, minGit, build, compiled);

        var expected = ExpectedCommands(node, build, compiled);
        Assert.Equal(expected.Count, runner.Invocations.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var wanted = expected[index];
            var invocation = runner.Invocations[index];
            Assert.Equal(wanted.FileName, invocation.FileName);
            Assert.Equal(wanted.Arguments, invocation.Arguments);
            Assert.Equal(wanted.WorkingDirectory, invocation.WorkingDirectory);
            Assert.Equal(wanted.UseShellExecute, invocation.UseShellExecute);
            Assert.Equal(
                wanted.Environment?.OrderBy(pair => pair.Key),
                invocation.Environment?.OrderBy(pair => pair.Key));
            Assert.Equal(node, invocation.FileName);
            Assert.Equal(build, invocation.WorkingDirectory);
            Assert.False(invocation.UseShellExecute);
            Assert.Equal(
                Path.Combine(build, ".phase1cb-toolchain") + Path.PathSeparator +
                Path.Combine(minGit, "cmd") + Path.PathSeparator +
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
                invocation.Environment!["PATH"]);
            Assert.Equal("false", invocation.Environment["NX_DAEMON"]);
            Assert.Equal(
                new[] { "NODE_OPTIONS", "NODE_PATH" },
                invocation.RemovedEnvironmentVariables);
            Assert.Equal(
                new[] { "npm_", "YARN_", "COREPACK_" },
                invocation.RemovedEnvironmentVariablePrefixes);
        }
        Assert.False(runner.Invocations[0].Environment!.ContainsKey("NODE_OPTIONS"));
        Assert.Equal(
            "true",
            runner.Invocations[0].Environment!["YARN_ENABLE_IMMUTABLE_INSTALLS"]);
        Assert.Equal(
            "true",
            runner.Invocations[10].Environment!["YARN_ENABLE_IMMUTABLE_INSTALLS"]);
        Assert.All(
            runner.Invocations.Skip(1).Take(9),
            invocation => Assert.False(
                invocation.Environment!.ContainsKey("YARN_ENABLE_IMMUTABLE_INSTALLS")));
        Assert.Equal(
            "--max-old-space-size=8192",
            runner.Invocations[8].Environment!["NODE_OPTIONS"]);
    }

    [Fact]
    public void InitialInstallIsTheApprovedDevelopmentFocusedInstallUsingPinnedMinGit()
    {
        var build = Path.Combine(_root, "development-focused-install-plan");
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var runner = new RecordingRunner();

        new ProductionPayloadBuilder(
                runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
                new AcceptedMinGitProbe())
            .RunBuildCommands(node, minGit, build, compiled);

        var initial = runner.Invocations[0];
        Assert.Equal(
            new[]
            {
                ".yarn/releases/yarn-4.13.0.cjs",
                "workspaces",
                "focus",
                "twenty",
                "twenty-server",
                "twenty-front",
                "twenty-emails",
            },
            initial.Arguments);
        Assert.Equal("true", initial.Environment!["YARN_ENABLE_IMMUTABLE_INSTALLS"]);
        Assert.Equal(
            new[]
            {
                Path.Combine(build, ".phase1cb-toolchain"),
                Path.Combine(minGit, "cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
            },
            initial.Environment["PATH"].Split(Path.PathSeparator));

        var repository = FindRepositoryRoot();
        using var rootPackage = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repository, "package.json")));
        Assert.Equal("twenty", rootPackage.RootElement.GetProperty("name").GetString());
        var rootDevelopmentDependencies = rootPackage.RootElement.GetProperty("devDependencies");
        Assert.True(rootDevelopmentDependencies.TryGetProperty("nx", out _));
        Assert.True(rootDevelopmentDependencies.TryGetProperty("typescript", out _));
        foreach (var workspace in new[]
                 {
                     (Name: "twenty-server", Path: "packages/twenty-server/package.json"),
                     (Name: "twenty-front", Path: "packages/twenty-front/package.json"),
                     (Name: "twenty-emails", Path: "packages/twenty-emails/package.json"),
                 })
        {
            using var package = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(
                    repository,
                    workspace.Path.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(workspace.Name, package.RootElement.GetProperty("name").GetString());
            var developmentDependencies = package.RootElement.GetProperty("devDependencies");
            Assert.True(developmentDependencies.TryGetProperty("@lingui/cli", out _));
            if (StringComparer.Ordinal.Equals(workspace.Name, "twenty-server"))
            {
                Assert.True(developmentDependencies.TryGetProperty("@nestjs/cli", out _));
                Assert.True(developmentDependencies.TryGetProperty("rimraf", out _));
            }
        }

        using var companion = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(
                repository,
                "packages",
                "twenty-companion",
                "package.json")));
        Assert.Equal("twenty-desktop", companion.RootElement.GetProperty("name").GetString());
        var lockText = File.ReadAllText(Path.Combine(repository, "yarn.lock"));
        Assert.Contains(
            "\"@electron/node-gyp@git+https://github.com/electron/node-gyp.git#" +
            "06b29aafb7708acef8b3669835c8a7857ebc92d2\":",
            lockText,
            StringComparison.Ordinal);
        Assert.Contains(
            "  resolution: \"@electron/node-gyp@https://github.com/electron/node-gyp.git" +
            "#commit=06b29aafb7708acef8b3669835c8a7857ebc92d2\"",
            lockText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MinGitCanaryUsesTwoExactBoundedSupervisedArgumentVectors()
    {
        var build = Path.Combine(_root, "exact-git-canary");
        var minGit = CreateFakeMinGit(build);
        var git = Path.Combine(minGit, "cmd", "git.exe");
        Directory.CreateDirectory(Path.Combine(build, ".phase1cb-cache", "git-trace"));
        var runner = new ProbeRecordingRunner();

        new ProductionMinGitProbe(runner).Verify(git, build, minGit);

        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal(
            new[]
            {
                "init",
                "--bare",
                Path.Combine(".phase1cb-cache", "git-probe", "repository.git"),
            },
            runner.Invocations[0].Arguments);
        Assert.Equal(
            new[]
            {
                "--git-dir",
                Path.Combine(".phase1cb-cache", "git-probe", "repository.git"),
                "fetch",
                "--depth=1",
                "https://github.com/electron/node-gyp.git",
                "06b29aafb7708acef8b3669835c8a7857ebc92d2",
            },
            runner.Invocations[1].Arguments);
        Assert.All(runner.Invocations, invocation =>
        {
            Assert.Equal(git, invocation.FileName);
            Assert.Equal(build, invocation.WorkingDirectory);
            Assert.False(invocation.UseShellExecute);
            Assert.Equal(TimeSpan.FromMinutes(5), invocation.Timeout);
            Assert.Equal(1024 * 1024, invocation.MaximumOutputBytes);
            Assert.Equal(
                Path.Combine(build, ".phase1cb-toolchain") + Path.PathSeparator +
                Path.Combine(minGit, "cmd") + Path.PathSeparator +
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
                invocation.Environment!["PATH"]);
            Assert.Equal("0", invocation.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("Never", invocation.Environment["GCM_INTERACTIVE"]);
            Assert.Equal(
                Path.Combine(build, ".phase1cb-cache", "git-trace", "event.jsonl"),
                invocation.Environment["GIT_TRACE2_EVENT"]);
            Assert.Equal(new[] { "NODE_OPTIONS", "NODE_PATH" },
                invocation.RemovedEnvironmentVariables);
            Assert.Equal(new[] { "npm_", "YARN_", "COREPACK_" },
                invocation.RemovedEnvironmentVariablePrefixes);
        });
    }

    [Theory]
    [InlineData("key")]
    [InlineData("resolution")]
    [InlineData("dependency")]
    public void MinGitCanaryRejectsAnyDriftInTheExactYarnLockBinding(string mutation)
    {
        var build = Path.Combine(_root, "git-lock-" + mutation);
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var lockPath = Path.Combine(build, "yarn.lock");
        var lockText = File.ReadAllText(lockPath);
        var needle = mutation switch
        {
            "key" => "\"@electron/node-gyp@git+https://github.com/electron/node-gyp.git#06b29aafb7708acef8b3669835c8a7857ebc92d2\":",
            "resolution" => "  resolution: \"@electron/node-gyp@https://github.com/electron/node-gyp.git#commit=06b29aafb7708acef8b3669835c8a7857ebc92d2\"",
            _ => "    \"@electron/node-gyp\": \"git+https://github.com/electron/node-gyp.git#06b29aafb7708acef8b3669835c8a7857ebc92d2\"",
        };
        File.WriteAllText(lockPath, lockText.Replace(needle, needle + "-drift", StringComparison.Ordinal));
        var runner = new RecordingRunner();

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(
                    runner,
                    new AcceptedNodeTrust(),
                    new AcceptedMinGitTrust(),
                    new AcceptedMinGitProbe())
                .RunBuildCommands(node, minGit, build, compiled));

        Assert.Equal("production-build-git-lock-invalid", error.ReasonCode);
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("locator")]
    [InlineData("commit")]
    [InlineData("malformed")]
    public void FocusedInstallRequiresBoundedPinnedMinGitTraceProof(string mutation)
    {
        var build = Path.Combine(_root, "git-trace-" + mutation);
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var runner = new RecordingRunner();

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(
                    runner,
                    new AcceptedNodeTrust(),
                    new AcceptedMinGitTrust(),
                    new MutatedMinGitProbe(mutation))
                .RunBuildCommands(node, minGit, build, compiled));

        Assert.Equal("production-build-git-trace-invalid", error.ReasonCode);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void FocusedInstallCacheProbeRejectsForbiddenFileAndAcceptsItsAbsence()
    {
        var build = Path.Combine(_root, "focused-cache-probe-file");
        var cache = ElectronLocatorCachePath(build);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "retired-electron-locator");
        var probe = new ProductionMinGitProbe(new ProbeRecordingRunner());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            probe.VerifyLocatorCacheIsAbsent(build));

        Assert.Equal("production-build-git-cache-invalid", error.ReasonCode);

        File.Delete(cache);

        probe.VerifyLocatorCacheIsAbsent(build);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("reparse")]
    public void FocusedInstallCacheProbeRejectsEveryNonFileLocatorEntry(string entryKind)
    {
        var build = Path.Combine(_root, "focused-cache-probe-" + entryKind);
        var cache = ElectronLocatorCachePath(build);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        if (StringComparer.Ordinal.Equals(entryKind, "directory"))
        {
            Directory.CreateDirectory(cache);
        }
        else
        {
            var target = Path.Combine(build, "reparse-target");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(cache, target);
        }

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionMinGitProbe(new ProbeRecordingRunner()).VerifyLocatorCacheIsAbsent(build));

        Assert.Equal("production-build-git-cache-invalid", error.ReasonCode);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("subject")]
    [InlineData("chain")]
    public void FocusedInstallRejectsMutatedStagedMinGitIdentityBeforeYarn(string mutation)
    {
        var build = Path.Combine(_root, "git-identity-" + mutation);
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var runner = new RecordingRunner();

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(
                    runner,
                    new AcceptedNodeTrust(),
                    new MutatedMinGitTrust(mutation),
                    new AcceptedMinGitProbe())
                .RunBuildCommands(node, minGit, build, compiled));

        Assert.Equal("production-mingit-identity-invalid", error.ReasonCode);
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void BuildCommandFailureIdentifiesEachOneBasedOrdinalWithoutCommandDetails(
        int failingOrdinal)
    {
        var build = Path.Combine(_root, $"build-command-{failingOrdinal}");
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var runner = new OrdinalRejectingRunner(
            failingOrdinal,
            new ProductionPayloadBuildException("production-build-process-failed"));

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(
                    runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
                    new AcceptedMinGitProbe())
                .RunBuildCommands(node, minGit, build, compiled));

        Assert.Equal($"production-build-command-{failingOrdinal}-failed", error.ReasonCode);
        Assert.Equal(failingOrdinal, runner.InvocationCount);
        Assert.DoesNotContain("yarn", error.ReasonCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(build, error.ReasonCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionBuildExceptionCarriesAnOptionalDiagnostic()
    {
        var constructor = typeof(ProductionPayloadBuildException).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(string)],
            modifiers: null);
        Assert.NotNull(constructor);

        var error = Assert.IsType<ProductionPayloadBuildException>(constructor.Invoke(
            ["production-build-process-failed", "safe diagnostic"]));

        var diagnostic = typeof(ProductionPayloadBuildException).GetProperty("Diagnostic");
        Assert.NotNull(diagnostic);
        Assert.Equal("safe diagnostic", diagnostic.GetValue(error));
    }

    [Fact]
    public void CommandTwoFailurePreservesDiagnosticThroughOrdinalMapping()
    {
        var build = Path.Combine(_root, "build-command-diagnostic");
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var runner = new OrdinalRejectingRunner(
            2,
            new ProductionPayloadBuildException(
                "production-build-process-failed", "safe diagnostic"));

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(
                    runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
                    new AcceptedMinGitProbe())
                .RunBuildCommands(node, minGit, build, compiled));

        Assert.Equal("production-build-command-2-failed", error.ReasonCode);
        Assert.Equal("safe diagnostic", error.Diagnostic);
    }

    [Fact]
    public void ProcessRunnerBuildsBoundedSanitizedDiagnosticFromChildSnapshotsOnly()
    {
        Directory.CreateDirectory(_root);
        var probe = Path.Combine(_root, "diagnostic-probe.mjs");
        File.WriteAllText(
            probe,
            "const root=process.argv[2];\n" +
            "process.stdout.write('\\x1b[31mroot='+root+'\\u0001 password=alpha passwd: bravo secret=charlie token=delta api_key=echo apikey=foxtrot access_key=golf private_key=hotel connectionstring=india connection_string=juliet\\r\\n');\n" +
            "for(let i=0;i<250;i++)process.stderr.write('line-'+i+' '+ 'x'.repeat(256)+'\\n');\n" +
            "process.exit(27);\n",
            new UTF8Encoding(false));
        var rootWithDifferentCasing = _root.ToUpperInvariant();

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadProcessRunner().Run(new ProductionPayloadProcessSpec(
                FindNodeExecutable(),
                [probe, rootWithDifferentCasing, "UNPRINTED_COMMAND_ARGUMENT"],
                _root,
                UseShellExecute: false,
                Environment: null)
            {
                Timeout = TimeSpan.FromSeconds(10),
            }));

        Assert.Equal("production-build-process-failed", error.ReasonCode);
        Assert.NotNull(error.Diagnostic);
        var diagnostic = error.Diagnostic!;
        Assert.Contains("<BUILD_ROOT>", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\u001b", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0001", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bravo", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("charlie", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delta", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("echo", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foxtrot", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("golf", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hotel", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("india", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("juliet", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("UNPRINTED_COMMAND_ARGUMENT", diagnostic, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(diagnostic) <= 32 * 1024);
        Assert.True(diagnostic.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 200);
    }

    [Fact]
    public void ProcessRunnerFailureWithoutOutputDoesNotCreateDiagnostic()
    {
        Directory.CreateDirectory(_root);
        var probe = Path.Combine(_root, "silent-diagnostic-probe.mjs");
        File.WriteAllText(probe, "process.exit(27);\n", new UTF8Encoding(false));

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadProcessRunner().Run(new ProductionPayloadProcessSpec(
                FindNodeExecutable(),
                [probe],
                _root,
                UseShellExecute: false,
                Environment: null)
            {
                Timeout = TimeSpan.FromSeconds(10),
            }));

        Assert.Equal("production-build-process-failed", error.ReasonCode);
        Assert.Null(error.Diagnostic);
    }

    [Fact]
    public void ProcessRunnerSanitizesEscapedSecretsYamlBlocksAnsiFamiliesAndDenseOutput()
    {
        Directory.CreateDirectory(_root);
        var probe = Path.Combine(_root, "adversarial-diagnostic-probe.mjs");
        File.WriteAllText(
            probe,
            "process.stdout.write('{\\\"token\\\":\\\"abc\\\\\\\\\\\"def\\\"}\\nToken: |\\n  yaml-secret-tail\\nnext: safe\\nfirst\\u2028second\\u2029third\\n\\x1b(Bcharset\\x1b[31mcsi\\x9b31mc1-csi\\x1b]osc-secret\\x07\\x1bPdcs-secret\\x1b\\\\n');\n" +
            "const line='x'.repeat(1023)+'\\n'; for(let i=0;i<16384;i++)process.stderr.write(line); process.exit(27);\n",
            new UTF8Encoding(false));
        var watch = Stopwatch.StartNew();
        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadProcessRunner().Run(new ProductionPayloadProcessSpec(
                FindNodeExecutable(), [probe], _root, false, null)
            { Timeout = TimeSpan.FromSeconds(20) }));
        watch.Stop();

        Assert.NotNull(error.Diagnostic);
        var diagnostic = error.Diagnostic!;
        Assert.DoesNotContain("abc", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("def", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("yaml-secret-tail", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("osc-secret", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dcs-secret", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("next: safe", diagnostic, StringComparison.Ordinal);
        Assert.Contains("first\nsecond\nthird", diagnostic, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(diagnostic) <= 32 * 1024);
        Assert.True(diagnostic.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= 200);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void ProcessRunnerKeepsYamlCredentialBlockRedactionAcrossBlankLinesAndEscIntermediates()
    {
        Directory.CreateDirectory(_root);
        var probe = Path.Combine(_root, "yaml-blank-and-esc-probe.mjs");
        File.WriteAllText(probe,
            "process.stdout.write('token: |\\n  first-secret\\n\\n  second-secret\\nnext: safe\\n\\x1b#8percent\\x1b%Gdone\\n'); process.exit(27);\n",
            new UTF8Encoding(false));

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadProcessRunner().Run(new ProductionPayloadProcessSpec(
                FindNodeExecutable(), [probe], _root, false, null)
            { Timeout = TimeSpan.FromSeconds(10) }));

        Assert.NotNull(error.Diagnostic);
        var diagnostic = error.Diagnostic!;
        Assert.DoesNotContain("first-secret", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("second-secret", diagnostic, StringComparison.Ordinal);
        Assert.Contains("next: safe", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("8percent", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("Gdone", diagnostic, StringComparison.Ordinal);
        Assert.Contains("percentdone", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadToolFailureOutputKeepsReasonOnlyWithoutDiagnosticAndMarkersWithDiagnostic()
    {
        var outputType = typeof(PayloadToolPathPolicy).Assembly.GetType("PayloadToolFailureOutput");
        Assert.NotNull(outputType);
        var write = outputType.GetMethod("Write", System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Static);
        Assert.NotNull(write);

        using var reasonOnly = new StringWriter();
        write.Invoke(null, [reasonOnly, "production-build-command-2-failed", null]);
        Assert.Equal(
            "production-build-command-2-failed" + Environment.NewLine,
            reasonOnly.ToString());

        using var withDiagnostic = new StringWriter();
        write.Invoke(null, [withDiagnostic, "production-build-command-2-failed", "safe diagnostic"]);
        Assert.Equal(
            "production-build-command-2-failed" + Environment.NewLine +
            "production-build-diagnostic-begin" + Environment.NewLine +
            "safe diagnostic\n" +
            "production-build-diagnostic-end" + Environment.NewLine,
            withDiagnostic.ToString());
    }

    [Fact]
    public void PayloadToolFailureOutputRewritesChildMarkerCollisions()
    {
        using var output = new StringWriter();
        PayloadToolFailureOutput.Write(
            output,
            "production-build-command-2-failed",
            "production-build-diagnostic-begin\nchild\nproduction-build-diagnostic-end\n");

        var text = output.ToString();
        Assert.Single(Regex.Matches(text, "^production-build-diagnostic-begin\\r?$", RegexOptions.Multiline).Cast<Match>());
        Assert.Single(Regex.Matches(text, "^production-build-diagnostic-end\\r?$", RegexOptions.Multiline).Cast<Match>());
        Assert.DoesNotContain("\nproduction-build-diagnostic-begin\nchild", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadToolFailureOutputPreservesDiagnosticBodyCapsAfterMarkerDefense()
    {
        var diagnostic = PayloadToolFailureOutput.BeginMarker +
            new string('x', 32 * 1024 - Encoding.UTF8.GetByteCount(
                PayloadToolFailureOutput.BeginMarker));
        Assert.Equal(32 * 1024, Encoding.UTF8.GetByteCount(diagnostic));
        using var output = new StringWriter();
        PayloadToolFailureOutput.Write(output, "production-build-command-2-failed", diagnostic);

        var text = output.ToString();
        var begin = text.IndexOf(PayloadToolFailureOutput.BeginMarker + Environment.NewLine, StringComparison.Ordinal);
        var bodyStart = begin + PayloadToolFailureOutput.BeginMarker.Length + Environment.NewLine.Length;
        var bodyEnd = text.LastIndexOf(PayloadToolFailureOutput.EndMarker, StringComparison.Ordinal);
        var body = text[bodyStart..bodyEnd];
        Assert.True(Encoding.UTF8.GetByteCount(body) <= 32 * 1024);
        Assert.True(body.Count(character => character == '\n') <= 200);
    }

    [Fact]
    public void PayloadToolFailureOutputReservesSpaceForMissingFinalNewlineAtExactByteCap()
    {
        var diagnostic = new string('x', 32 * 1024);
        Assert.Equal(32 * 1024, Encoding.UTF8.GetByteCount(diagnostic));
        using var output = new StringWriter();
        PayloadToolFailureOutput.Write(output, "production-build-command-2-failed", diagnostic);

        var text = output.ToString();
        var begin = text.IndexOf(PayloadToolFailureOutput.BeginMarker + Environment.NewLine, StringComparison.Ordinal);
        var bodyStart = begin + PayloadToolFailureOutput.BeginMarker.Length + Environment.NewLine.Length;
        var bodyEnd = text.LastIndexOf(PayloadToolFailureOutput.EndMarker, StringComparison.Ordinal);
        var body = text[bodyStart..bodyEnd];
        Assert.True(Encoding.UTF8.GetByteCount(body) <= 32 * 1024);
        Assert.EndsWith("\n", body, StringComparison.Ordinal);
        Assert.True(body.Count(character => character == '\n') <= 200);
    }

    [Fact]
    public void PowerShellPayloadToolRelayWritesBoundedStderrBeforeFailure()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        var boundary = script.IndexOf("try {\n    Clear-BuildInjectionEnvironment", StringComparison.Ordinal);
        Assert.True(boundary > 0);
        Directory.CreateDirectory(_root);
        var harness = Path.Combine(_root, "payload-tool-relay.ps1");
        var profile = Path.Combine(_root, "payload-tool-profile");
        File.WriteAllText(
            harness,
            script[..boundary] + Environment.NewLine +
            "Invoke-BoundedPayloadTool -FilePath '" + PowerShellLiteral(FindExecutable("cmd.exe")) +
            "' -Arguments @('/d','/c','echo production-build-diagnostic-begin 1>&2 & echo safe-diagnostic 1>&2 & echo production-build-diagnostic-end 1>&2 & exit /b 9') -ProfileRoot '" +
            PowerShellLiteral(profile) + "' -TimeoutSeconds 20 -MaximumOutputBytes 4096" + Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(FindExecutable("powershell.exe"), _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);
        var output = result.StandardOutput + result.StandardError;
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("production-build-diagnostic-begin", output, StringComparison.Ordinal);
        Assert.Contains("safe-diagnostic", output, StringComparison.Ordinal);
        Assert.Contains("production-build-diagnostic-end", output, StringComparison.Ordinal);
        Assert.Contains("phase1cb-command-failed:9", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("production-build-process-start-failed", "start")]
    [InlineData("production-build-process-timeout", "timeout")]
    public void BuildCommandFailurePreservesOnlyTheReviewedRunnerCategory(
        string runnerReason,
        string category)
    {
        var build = Path.Combine(_root, $"build-command-category-{category}");
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var runner = new OrdinalRejectingRunner(
            6,
            new ProductionPayloadBuildException(runnerReason));

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(
                    runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
                    new AcceptedMinGitProbe())
                .RunBuildCommands(node, minGit, build, compiled));

        Assert.Equal($"production-build-command-6-{category}", error.ReasonCode);
        Assert.Equal(6, runner.InvocationCount);
    }

    [Fact]
    public void BuildCommandFailureSanitizesAnUnexpectedRunnerException()
    {
        var build = Path.Combine(_root, "build-command-unexpected");
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var runner = new OrdinalRejectingRunner(
            3,
            new InvalidOperationException("secret argument and output"));

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(
                    runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
                    new AcceptedMinGitProbe())
                .RunBuildCommands(node, minGit, build, compiled));

        Assert.Equal("production-build-command-3-failed", error.ReasonCode);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicRunBuildCommandsRejectsNodeAncestorReparseBeforeRunnerInvocation()
    {
        var build = Path.Combine(_root, "run-command-reparse-build");
        var target = Path.Combine(build, "node-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "node.exe"), "node");
        var alias = Path.Combine(build, "node-alias");
        Directory.CreateSymbolicLink(alias, target);
        var runner = new RecordingRunner();
        var builder = new ProductionPayloadBuilder(
            runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
            new AcceptedMinGitProbe());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.RunBuildCommands(
                Path.Combine(alias, "node.exe"),
                Path.Combine(build, ".phase1cb-toolchain", "mingit"),
                build,
                Path.Combine(build, "desktop", "windows", "node", "publish")));

        Assert.Equal("production-build-path-invalid", error.ReasonCode);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void PublicProcessRunnerRejectsRelativeExecutableBeforeLaunch()
    {
        Directory.CreateDirectory(_root);
        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadProcessRunner().Run(new ProductionPayloadProcessSpec(
                "node.exe",
                [],
                _root,
                UseShellExecute: false,
                Environment: null)));

        Assert.Equal("production-build-path-invalid", error.ReasonCode);
    }

    [Fact]
    public void VerifyPinnedNodeRejectsArchiveReparseBeforeTrustInspection()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "archive.zip");
        var archiveAlias = Path.Combine(_root, "archive-alias.zip");
        var node = Path.Combine(_root, "node.exe");
        File.WriteAllText(archive, "archive");
        File.WriteAllText(node, "node");
        File.CreateSymbolicLink(archiveAlias, archive);
        var trust = new CountingAcceptedNodeTrust();
        var builder = new ProductionPayloadBuilder(new RecordingRunner(), trust);

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.VerifyPinnedNode(archiveAlias, node));

        Assert.Equal("production-build-path-invalid", error.ReasonCode);
        Assert.Equal(0, trust.InspectionCount);
    }

    [Fact]
    public void PowerShellBuildClearsEveryReviewedInjectionFamilyAndRestoresItFinally()
    {
        var repository = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));

        Assert.Contains("Clear-BuildInjectionEnvironment", script, StringComparison.Ordinal);
        Assert.Contains("Restore-BuildInjectionEnvironment", script, StringComparison.Ordinal);
        Assert.Contains("@('NODE_OPTIONS', 'NODE_PATH')", script, StringComparison.Ordinal);
        Assert.Contains("@('npm_', 'YARN_', 'COREPACK_')", script, StringComparison.Ordinal);
        Assert.Contains("'build', 'execute'", script, StringComparison.Ordinal);
        Assert.Contains("Restore-BuildInjectionEnvironment -Original $originalBuildInjectionEnvironment", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellCleanupUnlinksDescendantReparseWithoutTouchingExternalSentinel()
    {
        var repository = FindRepositoryRoot();
        var sourceScript = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));
        var functionBoundary = sourceScript.IndexOf(
            "function ConvertTo-WindowsCommandLineArgument",
            StringComparison.Ordinal);
        Assert.True(functionBoundary > 0);
        Assert.DoesNotContain(
            "Remove-Item -LiteralPath $canonical -Recurse -Force",
            sourceScript,
            StringComparison.Ordinal);
        var cleanupStart = sourceScript.IndexOf(
            "function Remove-OwnedOutputRoot",
            StringComparison.Ordinal);
        Assert.True(cleanupStart >= 0);
        var cleanupFunction = sourceScript[cleanupStart..functionBoundary];
        Assert.Contains("[int] $maximumCleanupDepth = 128", cleanupFunction, StringComparison.Ordinal);
        Assert.Contains("[long] $maximumCleanupEntries = 1000000", cleanupFunction, StringComparison.Ordinal);
        Assert.Contains("[long] $maximumCleanupBytes = 68719476736", cleanupFunction, StringComparison.Ordinal);
        Assert.Contains("($Depth + 1) -gt $maximumCleanupDepth", cleanupFunction, StringComparison.Ordinal);
        Assert.Contains("[long]$Budget.Entries -ge $maximumCleanupEntries", cleanupFunction, StringComparison.Ordinal);
        Assert.Contains("$maximumCleanupBytes - $bytes", cleanupFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("[long]$Budget.Entries -ge 250000", cleanupFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("[long]17179869184 - $bytes", cleanupFunction, StringComparison.Ordinal);
        var allowed = Path.Combine(_root, "cleanup-allowed");
        var owned = Path.Combine(allowed, "owned-output");
        var external = Path.Combine(_root, "cleanup-external");
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(external);
        Write(owned, "ordinary/inside.txt", "inside");
        var readOnly = Path.Combine(owned, "ordinary", "inside.txt");
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        Directory.CreateSymbolicLink(Path.Combine(owned, "external-link"), external);
        var harness = Path.Combine(_root, "cleanup-harness.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..functionBoundary] + Environment.NewLine +
            $"Remove-OwnedOutputRoot -Path '{PowerShellLiteral(owned)}' -AllowedRoot '{PowerShellLiteral(allowed)}'" +
            Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        var ownedStillExists = Directory.Exists(owned);
        if (File.Exists(readOnly))
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
        }

        Assert.True(
            result.ExitCode == 0,
            $"cleanup failed: {result.StandardError}{Environment.NewLine}{result.StandardOutput}");
        Assert.False(ownedStillExists);
        Assert.True(File.Exists(sentinel));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    [Fact]
    public void PowerShellCleanupRejectsAllowedRootReachedThroughReparseAncestor()
    {
        var repository = FindRepositoryRoot();
        var sourceScript = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));
        var functionBoundary = sourceScript.IndexOf(
            "function ConvertTo-WindowsCommandLineArgument",
            StringComparison.Ordinal);
        Assert.True(functionBoundary > 0);
        var realParent = Path.Combine(_root, "cleanup-real-parent");
        var allowed = Path.Combine(realParent, "allowed");
        var owned = Path.Combine(allowed, "owned-output");
        Directory.CreateDirectory(owned);
        var sentinel = Path.Combine(owned, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var parentLink = Path.Combine(_root, "cleanup-parent-link");
        Directory.CreateSymbolicLink(parentLink, realParent);
        var linkedAllowed = Path.Combine(parentLink, "allowed");
        var linkedOwned = Path.Combine(linkedAllowed, "owned-output");
        var harness = Path.Combine(_root, "cleanup-root-chain-harness.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..functionBoundary] + Environment.NewLine +
            $"Remove-OwnedOutputRoot -Path '{PowerShellLiteral(linkedOwned)}' -AllowedRoot '{PowerShellLiteral(linkedAllowed)}'" +
            Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(sentinel));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    [Fact]
    public void PowerShellCleanupRejectsExcessiveTraversalDepthWithStableReason()
    {
        var repository = FindRepositoryRoot();
        var sourceScript = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));
        var functionBoundary = sourceScript.IndexOf(
            "function ConvertTo-WindowsCommandLineArgument",
            StringComparison.Ordinal);
        Assert.True(functionBoundary > 0);
        var allowed = Path.Combine(_root, "cleanup-depth-allowed");
        var owned = Path.Combine(allowed, "owned-output");
        var current = owned;
        Directory.CreateDirectory(current);
        for (var index = 0; index < 130; index++)
        {
            current = Path.Combine(current, "d");
            Directory.CreateDirectory(current);
        }
        File.WriteAllText(Path.Combine(current, "sentinel.txt"), "bounded");
        var harness = Path.Combine(_root, "cleanup-depth-harness.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..functionBoundary] + Environment.NewLine +
            $"Remove-OwnedOutputRoot -Path '{PowerShellLiteral(owned)}' -AllowedRoot '{PowerShellLiteral(allowed)}'" +
            Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "phase1cb-cleanup-traversal-budget",
            result.StandardError + result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessRunnerRejectsAReviewedInjectionVariableFromTheExplicitEnvironment()
    {
        var runner = new ProductionPayloadProcessRunner();
        var specification = new ProductionPayloadProcessSpec(
            "does-not-start.exe",
            [],
            _root,
            UseShellExecute: false,
            new Dictionary<string, string>
            {
                ["npm_config_userconfig"] = "attacker-controlled",
            })
        {
            RemovedEnvironmentVariables = ["NODE_OPTIONS", "NODE_PATH"],
            RemovedEnvironmentVariablePrefixes = ["npm_", "YARN_", "COREPACK_"],
        };

        var error = Assert.Throws<ProductionPayloadBuildException>(() => runner.Run(specification));

        Assert.Equal("production-build-process-spec-invalid", error.ReasonCode);
    }

    [Fact]
    public void RealCommandPlanRunsWithExactIsolatedCacheEnvironment()
    {
        var build = Path.Combine(_root, "real-command-plan");
        var node = Path.Combine(build, ".phase1cb-toolchain", "runtime-probe.exe");
        var yarn = Path.Combine(build, ".yarn", "releases", "yarn-4.13.0.cjs");
        var output = Path.Combine(build, "observed-command-environment.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        Directory.CreateDirectory(Path.GetDirectoryName(yarn)!);
        CreateTestHardLink(node, FindNodeExecutable());
        var minGit = CreateFakeMinGit(build);
        File.WriteAllText(
            yarn,
            "const fs=require('node:fs');" +
            "const names=['YARN_ENABLE_GLOBAL_CACHE','YARN_ENABLE_IMMUTABLE_INSTALLS'," +
            "'YARN_CACHE_FOLDER','NX_CACHE_DIRECTORY'," +
            "'NX_WORKSPACE_DATA_DIRECTORY','YARN_RC_FILENAME','NX_TASKS_RUNNER'];" +
            "if(process.env.GIT_TRACE2_EVENT&&!fs.existsSync(process.env.GIT_TRACE2_EVENT)){" +
            "fs.writeFileSync(process.env.GIT_TRACE2_EVENT," +
            "JSON.stringify({event:'version',exe:'2.55.0.windows.2'})+'\\n'+" +
            "JSON.stringify({event:'start',argv:['git.exe','fetch'," +
            "'https://github.com/electron/node-gyp.git'," +
            "'06b29aafb7708acef8b3669835c8a7857ebc92d2']})+'\\n');}" +
            $"fs.appendFileSync({System.Text.Json.JsonSerializer.Serialize(output)}," +
            "JSON.stringify(Object.fromEntries(names.filter(n=>process.env[n]!==undefined)" +
            ".map(n=>[n,process.env[n]])))+'\\n');",
            new UTF8Encoding(false));
        var ambient = new Dictionary<string, string?>
        {
            ["YARN_RC_FILENAME"] = Environment.GetEnvironmentVariable("YARN_RC_FILENAME"),
            ["NX_TASKS_RUNNER"] = Environment.GetEnvironmentVariable("NX_TASKS_RUNNER"),
        };
        try
        {
            Environment.SetEnvironmentVariable("YARN_RC_FILENAME", "ambient-yarn-canary");
            Environment.SetEnvironmentVariable("NX_TASKS_RUNNER", "ambient-nx-canary");
            new ProductionPayloadBuilder(
                    new ProductionPayloadProcessRunner(),
                    new AcceptedNodeTrust(),
                    new AcceptedMinGitTrust(),
                    new AcceptedMinGitProbe())
                .RunBuildCommands(
                    node,
                    minGit,
                    build,
                    Path.Combine(build, "desktop", "windows", "node", "publish"));
        }
        finally
        {
            foreach (var pair in ambient)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        var observations = File.ReadAllLines(output);
        Assert.Equal(11, observations.Length);
        for (var index = 0; index < observations.Length; index++)
        {
            var line = observations[index];
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var environment = document.RootElement;
            Assert.Equal("false", environment.GetProperty("YARN_ENABLE_GLOBAL_CACHE").GetString());
            Assert.Equal(Path.Combine(build, ".phase1cb-cache", "yarn"), environment.GetProperty("YARN_CACHE_FOLDER").GetString());
            Assert.Equal(Path.Combine(build, ".phase1cb-cache", "nx"), environment.GetProperty("NX_CACHE_DIRECTORY").GetString());
            Assert.Equal(Path.Combine(build, ".phase1cb-cache", "nx-workspace"), environment.GetProperty("NX_WORKSPACE_DATA_DIRECTORY").GetString());
            Assert.Equal(
                index is 0 or 10,
                environment.TryGetProperty("YARN_ENABLE_IMMUTABLE_INSTALLS", out var immutable));
            if (index is 0 or 10)
            {
                Assert.Equal("true", immutable.GetString());
            }
            Assert.False(environment.TryGetProperty("YARN_RC_FILENAME", out _));
            Assert.False(environment.TryGetProperty("NX_TASKS_RUNNER", out _));
        }
    }

    [Fact]
    public void ProcessRunnerRejectsReviewedCacheEnvironmentOutsideValidatedWorkingRoot()
    {
        Directory.CreateDirectory(_root);
        var node = FindNodeExecutable();
        var probe = Path.Combine(_root, "must-not-launch.mjs");
        File.WriteAllText(probe, "throw new Error('must-not-launch');", new UTF8Encoding(false));
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = Path.GetDirectoryName(node)!,
            ["NX_DAEMON"] = "false",
            ["YARN_ENABLE_GLOBAL_CACHE"] = "false",
            ["YARN_CACHE_FOLDER"] = Path.Combine(Path.GetPathRoot(_root)!, "outside-yarn-cache"),
            ["NX_CACHE_DIRECTORY"] = Path.Combine(_root, ".phase1cb-cache", "nx"),
            ["NX_WORKSPACE_DATA_DIRECTORY"] = Path.Combine(_root, ".phase1cb-cache", "nx-workspace"),
        };
        var specification = new ProductionPayloadProcessSpec(
            node,
            [probe],
            _root,
            UseShellExecute: false,
            environment)
        {
            RemovedEnvironmentVariables = ["NODE_OPTIONS", "NODE_PATH"],
            RemovedEnvironmentVariablePrefixes = ["npm_", "YARN_", "COREPACK_"],
        };

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadProcessRunner().Run(specification));

        Assert.Equal("production-build-process-spec-invalid", error.ReasonCode);
    }

    [Fact]
    public void ProcessRunnerUsesExplicitIsolatedEnvironmentWithoutAmbientCredentials()
    {
        var node = FindNodeExecutable();
        Directory.CreateDirectory(_root);
        var script = Path.Combine(_root, "explicit-environment-probe.mjs");
        var output = Path.Combine(_root, "explicit-environment.json");
        File.WriteAllText(
            script,
            """
            import { writeFileSync } from 'node:fs';
            const names = [
              'AWS_SECRET_ACCESS_KEY', 'GH_TOKEN', 'NPM_TOKEN',
              'SystemRoot', 'WINDIR', 'COMSPEC', 'PATHEXT',
              'HOME', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP'
            ];
            writeFileSync(process.argv[2], JSON.stringify(
              Object.fromEntries(names.filter((name) => process.env[name] !== undefined)
                .map((name) => [name, process.env[name]]))));
            """,
            new UTF8Encoding(false));
        var original = new Dictionary<string, string?>
        {
            ["AWS_SECRET_ACCESS_KEY"] = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"),
            ["GH_TOKEN"] = Environment.GetEnvironmentVariable("GH_TOKEN"),
            ["NPM_TOKEN"] = Environment.GetEnvironmentVariable("NPM_TOKEN"),
        };
        try
        {
            foreach (var name in original.Keys)
            {
                Environment.SetEnvironmentVariable(name, "ambient-secret-canary");
            }
            new ProductionPayloadProcessRunner().Run(new ProductionPayloadProcessSpec(
                node,
                [script, output],
                _root,
                UseShellExecute: false,
                Environment: null)
            {
                Timeout = TimeSpan.FromSeconds(10),
            });
        }
        finally
        {
            foreach (var pair in original)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        using var environment = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(output));
        var root = environment.RootElement;
        Assert.False(root.TryGetProperty("AWS_SECRET_ACCESS_KEY", out _));
        Assert.False(root.TryGetProperty("GH_TOKEN", out _));
        Assert.False(root.TryGetProperty("NPM_TOKEN", out _));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), root.GetProperty("SystemRoot").GetString());
        Assert.Equal(root.GetProperty("SystemRoot").GetString(), root.GetProperty("WINDIR").GetString());
        Assert.Equal(Path.Combine(root.GetProperty("SystemRoot").GetString()!, "System32", "cmd.exe"), root.GetProperty("COMSPEC").GetString());
        Assert.Equal(root.GetProperty("HOME").GetString(), root.GetProperty("USERPROFILE").GetString());
        Assert.NotEqual(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), root.GetProperty("USERPROFILE").GetString());
        Assert.Equal(root.GetProperty("TEMP").GetString(), root.GetProperty("TMP").GetString());
    }

    [Fact]
    public void ManifestInventoryRejectsExcessiveTreeDepth()
    {
        var fixture = CreateClosureFixture();
        var payload = Path.Combine(_root, "manifest-depth-budget");
        var current = payload;
        Directory.CreateDirectory(current);
        for (var index = 0; index < 140; index++)
        {
            current = Path.Combine(current, "d");
            Directory.CreateDirectory(current);
        }
        File.WriteAllText(Path.Combine(current, "file.txt"), "bounded");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.CreateManifest(payload, fixture.Header));

        Assert.Equal("production-build-traversal-budget", error.ReasonCode);
    }

    [Fact]
    public void PayloadParentReparseCannotRedirectMaterializationWrites()
    {
        var fixture = CreateClosureFixture();
        var external = Path.Combine(_root, "external-payload-parent");
        var parentLink = Path.Combine(_root, "payload-parent-link");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "sentinel.txt"), "untouched");
        Directory.CreateSymbolicLink(parentLink, external);
        var redirectedPayload = Path.Combine(parentLink, "payload");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                redirectedPayload,
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-build-path-invalid", error.ReasonCode);
        Assert.False(Directory.Exists(Path.Combine(external, "payload")));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(external, "sentinel.txt")));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("node")]
    [InlineData("inventory")]
    public void IntermediateInputReparseAncestorsAreRejected(string input)
    {
        var fixture = CreateClosureFixture();
        var buildRoot = fixture.BuildRoot;
        var node = fixture.NodeExecutable;
        var inventory = fixture.InventoryPath;
        if (input == "build")
        {
            var realParent = Directory.GetParent(buildRoot)!.FullName;
            var alias = Path.Combine(_root, "build-parent-alias");
            Directory.CreateSymbolicLink(alias, realParent);
            buildRoot = Path.Combine(alias, Path.GetFileName(buildRoot));
            node = Path.Combine(buildRoot, Path.GetRelativePath(fixture.BuildRoot, node));
            inventory = Path.Combine(buildRoot, Path.GetRelativePath(fixture.BuildRoot, inventory));
        }
        else
        {
            var target = Path.Combine(fixture.BuildRoot, $"{input}-target");
            var alias = Path.Combine(fixture.BuildRoot, $"{input}-alias");
            Directory.CreateDirectory(target);
            var source = input == "node" ? node : inventory;
            var moved = Path.Combine(target, Path.GetFileName(source));
            File.Move(source, moved);
            Directory.CreateSymbolicLink(alias, target);
            if (input == "node")
            {
                node = Path.Combine(alias, Path.GetFileName(moved));
            }
            else
            {
                inventory = Path.Combine(alias, Path.GetFileName(moved));
            }
        }
        var payload = Path.Combine(_root, $"reparse-input-{input}-payload");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                buildRoot,
                payload,
                node,
                inventory,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-build-path-invalid", error.ReasonCode);
        Assert.False(Directory.Exists(payload));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("executable")]
    [InlineData("subject")]
    [InlineData("chain")]
    public void AnyPinnedNodeIdentityMismatchFailsBeforeTheRunner(string mutation)
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "archive.zip");
        var node = Path.Combine(_root, "node.exe");
        File.WriteAllText(archive, "archive");
        File.WriteAllText(node, "node");
        var runner = new RecordingRunner();
        var trust = new MutatedNodeTrust(mutation);
        var builder = new ProductionPayloadBuilder(runner, trust);

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.VerifyPinnedNode(archive, node));

        Assert.Equal("production-node-identity-invalid", error.ReasonCode);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void TrustedMaterializationRequiresExactNodeArchiveTrustContext()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        var payload = Path.Combine(_root, "trusted-without-node-archive");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                payload,
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: false,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-node-identity-invalid", error.ReasonCode);
        Assert.False(Directory.Exists(payload));
    }

    [Fact]
    public void MaterializationProducesPhysicalRuntimeClosureAndCanonicalManifestEquality()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        var payload = Path.Combine(_root, "payload");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var result = builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: false,
            fixture.CompiledDesktopNodeRoot,
            NodeArchivePath: Path.Combine(_root, "trusted-node.zip")));

        Assert.Equal("trusted-production-payload", result.Classification);
        Assert.NotNull(result.Manifest);
        Assert.NotNull(result.ReleaseCatalog);
        Assert.True(File.Exists(Path.Combine(payload, "node.exe")));
        Assert.True(File.Exists(Path.Combine(payload, "package.json")));
        Assert.True(File.Exists(Path.Combine(payload, "node_modules", "runtime", "index.js")));
        Assert.True(File.Exists(Path.Combine(payload, "packages", "twenty-server", "dist", "main.js")));
        Assert.True(File.Exists(Path.Combine(payload, "packages", "twenty-shared", "dist", "index.js")));
        Assert.False(Directory.Exists(Path.Combine(payload, ".yarn")));
        Assert.False(File.Exists(Path.Combine(payload, "yarn.lock")));
        Assert.All(
            new[] { "twenty-server", "twenty-emails", "twenty-shared", "twenty-client-sdk" },
            workspace => Assert.False(new DirectoryInfo(Path.Combine(payload, "packages", workspace))
                .Attributes.HasFlag(FileAttributes.ReparsePoint)));

        var bytes = File.ReadAllBytes(Path.Combine(
            payload,
            ProductionPayloadValidator.ManifestFileName));
        var parsed = ProductionPayloadManifestV2.Parse(bytes);
        Assert.Equal(result.Manifest!.CanonicalDigest, parsed.CanonicalDigest);
        Assert.Equal(
            parsed.CanonicalDigest,
            ProductionPayloadCanonicalizer.ComputeDigest(parsed.Header, parsed.Files));
        Assert.Equal(parsed.CanonicalDigest, result.ReleaseCatalog!.ManifestDigest);
        Assert.Equal(
            parsed.Files.Select(file => file.RelativePath),
            EnumeratePayloadFiles(payload));
    }

    [Fact]
    public void MaterializationCopiesOnlyDeclaredCompiledWrappersAndBindsTrustedHeader()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        Write(fixture.CompiledDesktopNodeRoot, "production/undeclared.js", "export {};");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var extra = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "compiled-extra"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: false,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-entrypoint-inventory-mismatch", extra.ReasonCode);
        File.Delete(Path.Combine(fixture.CompiledDesktopNodeRoot, "production", "undeclared.js"));
        var payload = Path.Combine(_root, "compiled-declared");
        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: false,
            fixture.CompiledDesktopNodeRoot,
            NodeArchivePath: Path.Combine(_root, "trusted-node.zip")));
        Assert.True(File.Exists(Path.Combine(
            payload,
            "desktop",
            "windows",
            "node",
            "production",
            "twenty-server-role.js")));
        Assert.False(Directory.Exists(Path.Combine(
            payload,
            "desktop",
            "windows",
            "node",
            "src")));

        var mismatched = fixture.Header with { WorkerEntrypoint = fixture.Header.ServerEntrypoint };
        var headerError = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "header-mismatch"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                mismatched,
                ClosureOnly: false,
                fixture.CompiledDesktopNodeRoot)));
        Assert.Equal("production-entrypoint-inventory-mismatch", headerError.ReasonCode);
    }

    [Fact]
    public void MaterializationCopiesOnlyTheReachableCompiledRelativeEsmClosure()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        Write(
            fixture.CompiledDesktopNodeRoot,
            "production/twenty-server-role.js",
            "import '../control/runtime-control.js';\n");
        Write(
            fixture.CompiledDesktopNodeRoot,
            "control/runtime-control.js",
            "export { protocol } from '../protocol/runtime-protocol.js';\n");
        Write(
            fixture.CompiledDesktopNodeRoot,
            "protocol/runtime-protocol.js",
            "export const protocol = true;\n");
        Write(
            fixture.CompiledDesktopNodeRoot,
            "probes/unreachable-probe.js",
            "throw new Error('must not ship');\n");
        var payload = Path.Combine(_root, "compiled-relative-closure");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true,
            fixture.CompiledDesktopNodeRoot));

        Assert.True(File.Exists(Path.Combine(
            payload,
            "desktop", "windows", "node", "control", "runtime-control.js")));
        Assert.True(File.Exists(Path.Combine(
            payload,
            "desktop", "windows", "node", "protocol", "runtime-protocol.js")));
        Assert.False(File.Exists(Path.Combine(
            payload,
            "desktop", "windows", "node", "probes", "unreachable-probe.js")));
    }

    [Fact]
    public void ReachableSiblingSupportInsideCompiledProductionDirectoryIsAccepted()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        Write(
            fixture.CompiledDesktopNodeRoot,
            "production/twenty-server-role.js",
            "import './twenty-server-support.js';\n");
        Write(
            fixture.CompiledDesktopNodeRoot,
            "production/twenty-server-support.js",
            "export const support = true;\n");
        var payload = Path.Combine(_root, "compiled-production-support");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true,
            fixture.CompiledDesktopNodeRoot));

        Assert.True(File.Exists(Path.Combine(
            payload,
            "desktop", "windows", "node", "production", "twenty-server-support.js")));
    }

    [Theory]
    [InlineData("escape")]
    [InlineData("nonliteral")]
    [InlineData("missing")]
    [InlineData("reparse")]
    [InlineData("alternate-data-stream")]
    [InlineData("alias")]
    [InlineData("cycle")]
    [InlineData("absolute-url")]
    public void CompiledRelativeEsmClosureFailsClosedOnAmbiguousOrUnsafeDependencies(
        string mutation)
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        var wrapper = Path.Combine(
            fixture.CompiledDesktopNodeRoot,
            "production",
            "twenty-server-role.js");
        var dependency = Path.Combine(
            fixture.CompiledDesktopNodeRoot,
            "control",
            "runtime-control.js");
        Directory.CreateDirectory(Path.GetDirectoryName(dependency)!);
        switch (mutation)
        {
            case "escape":
                Write(
                    fixture.BuildRoot,
                    "desktop/windows/node/outside-compiled-root.js",
                    "export {};\n");
                File.WriteAllText(
                    wrapper,
                    "import '../../outside-compiled-root.js';\n",
                    new UTF8Encoding(false));
                break;
            case "nonliteral":
                File.WriteAllText(dependency, "export {};\n", new UTF8Encoding(false));
                File.WriteAllText(
                    wrapper,
                    "const dependency = '../control/runtime-control.js';\nawait import(dependency);\n",
                    new UTF8Encoding(false));
                break;
            case "missing":
                File.WriteAllText(
                    wrapper,
                    "import '../control/missing.js';\n",
                    new UTF8Encoding(false));
                break;
            case "reparse":
                var target = Path.Combine(fixture.BuildRoot, "reparse-target.js");
                File.WriteAllText(target, "export {};\n", new UTF8Encoding(false));
                File.CreateSymbolicLink(dependency, target);
                File.WriteAllText(
                    wrapper,
                    "import '../control/runtime-control.js';\n",
                    new UTF8Encoding(false));
                break;
            case "alternate-data-stream":
                File.WriteAllText(dependency, "export {};\n", new UTF8Encoding(false));
                File.WriteAllText(
                    dependency + ":phase1cb-hidden",
                    "hidden",
                    new UTF8Encoding(false));
                File.WriteAllText(
                    wrapper,
                    "import '../control/runtime-control.js';\n",
                    new UTF8Encoding(false));
                break;
            case "alias":
                File.WriteAllText(dependency, "export {};\n", new UTF8Encoding(false));
                File.WriteAllText(
                    wrapper,
                    "import '../control/../control/runtime-control.js';\n",
                    new UTF8Encoding(false));
                break;
            case "cycle":
                File.WriteAllText(
                    dependency,
                    "export { server } from '../production/twenty-server-role.js';\n",
                    new UTF8Encoding(false));
                File.WriteAllText(
                    wrapper,
                    "export { control } from '../control/runtime-control.js';\n",
                    new UTF8Encoding(false));
                break;
            case "absolute-url":
                File.WriteAllText(
                    wrapper,
                    "import 'file:///C:/outside-compiled-root.js';\n",
                    new UTF8Encoding(false));
                break;
            default:
                throw new InvalidOperationException(mutation);
        }
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "compiled-dependency-" + mutation),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-compiled-dependency-invalid", error.ReasonCode);
    }

    [Theory]
    [InlineData("commented-absolute-dynamic")]
    [InlineData("commented-nonliteral-dynamic")]
    [InlineData("commented-missing-static")]
    public void CompiledEsmImportsSeparatedByCommentsStillFailClosed(string mutation)
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        var wrapper = Path.Combine(
            fixture.CompiledDesktopNodeRoot,
            "production",
            "twenty-server-role.js");
        switch (mutation)
        {
            case "commented-absolute-dynamic":
                File.WriteAllText(
                    wrapper,
                    "await import /* boundary */ ('file:///C:/outside.js');\n",
                    new UTF8Encoding(false));
                break;
            case "commented-nonliteral-dynamic":
                File.WriteAllText(
                    wrapper,
                    "const specifier = '../control/runtime-control.js';\n" +
                    "await import/* boundary */(specifier);\n",
                    new UTF8Encoding(false));
                break;
            case "commented-missing-static":
                File.WriteAllText(
                    wrapper,
                    "import /* boundary */ '../control/missing.js';\n",
                    new UTF8Encoding(false));
                break;
            default:
                throw new InvalidOperationException(mutation);
        }
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "compiled-comment-boundary-" + mutation),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-compiled-dependency-invalid", error.ReasonCode);
    }

    [Fact]
    public void CompiledDependencyGraphRejectsExcessiveTraversalDepth()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        Write(
            fixture.CompiledDesktopNodeRoot,
            "production/twenty-server-role.js",
            "import '../deep/dependency-000.js';\n");
        for (var index = 0; index < 140; index++)
        {
            var next = index == 139
                ? "export {};\n"
                : $"import './dependency-{index + 1:000}.js';\n";
            Write(
                fixture.CompiledDesktopNodeRoot,
                $"deep/dependency-{index:000}.js",
                next);
        }
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "compiled-depth-budget"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-compiled-dependency-invalid", error.ReasonCode);
    }

    [Fact]
    public void CommentSeparatedLiteralDynamicImportJoinsCompiledClosure()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        Write(
            fixture.CompiledDesktopNodeRoot,
            "production/twenty-server-role.js",
            "await import/* boundary */('../control/runtime-control.js');\n");
        Write(
            fixture.CompiledDesktopNodeRoot,
            "control/runtime-control.js",
            "export const control = true;\n");
        var payload = Path.Combine(_root, "compiled-comment-literal");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true,
            fixture.CompiledDesktopNodeRoot));

        Assert.True(File.Exists(Path.Combine(
            payload,
            "desktop", "windows", "node", "control", "runtime-control.js")));
    }

    [Fact]
    public void CompiledImportTextInNonCodeLexicalFormsDoesNotJoinClosure()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        Write(
            fixture.CompiledDesktopNodeRoot,
            "production/twenty-server-role.js",
            "// import '../probes/line-comment.js';\n" +
            "/* import '../probes/block-comment.js'; */\n" +
            "const ordinary = \"import '../probes/string.js'\";\n" +
            "const template = `import '../probes/template.js'`;\n" +
            "const pattern = /import '\\.\\.\\/probes\\/regex\\.js'/;\n" +
            "const object = { import() {} };\n" +
            "object.import('../probes/property.js');\n" +
            "export { ordinary, template, pattern, object };\n");
        foreach (var name in new[]
                 {
                     "line-comment", "block-comment", "string", "template", "regex", "property",
                 })
        {
            Write(
                fixture.CompiledDesktopNodeRoot,
                $"probes/{name}.js",
                "throw new Error('must not ship');\n");
        }
        var payload = Path.Combine(_root, "compiled-lexical-decoys");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true,
            fixture.CompiledDesktopNodeRoot));

        Assert.False(Directory.Exists(Path.Combine(
            payload,
            "desktop", "windows", "node", "probes")));
    }

    [Fact]
    public void RegexLiteralsAfterControlHeadersAndBlocksDoNotJoinCompiledClosure()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        Write(
            fixture.CompiledDesktopNodeRoot,
            "production/twenty-server-role.js",
            "const ready = true;\n" +
            "const text = '';\n" +
            "if (ready) /import\\('\\.\\.\\/probes\\/control-decoy\\.js'\\)/.test(text);\n" +
            "if (ready) { text.trim(); } /import\\('\\.\\.\\/probes\\/block-decoy\\.js'\\)/.test(text);\n" +
            "const total = 4;\n" +
            "const count = 2;\n" +
            "const ratio = total / count;\n" +
            "const object = { if(value) { return value; } };\n" +
            "const contextualDivision = object.if(total) / count;\n" +
            "export { ratio, contextualDivision };\n");
        Write(
            fixture.CompiledDesktopNodeRoot,
            "probes/control-decoy.js",
            "throw new Error('must not ship');\n");
        Write(
            fixture.CompiledDesktopNodeRoot,
            "probes/block-decoy.js",
            "throw new Error('must not ship');\n");
        var payload = Path.Combine(_root, "compiled-regex-goals");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true,
            fixture.CompiledDesktopNodeRoot));

        Assert.False(Directory.Exists(Path.Combine(
            payload,
            "desktop", "windows", "node", "probes")));
    }

    [Theory]
    [InlineData("unterminated-block-comment", "export {}; /*")]
    [InlineData("unterminated-string", "const value = 'unterminated;")]
    [InlineData("unterminated-template", "const value = `unterminated;")]
    [InlineData("invalid-regex-flag", "const value = /safe/q;")]
    [InlineData(
        "import-attributes",
        "import '../control/runtime-control.js' with { type: 'json' };")]
    public void CompiledEsmLexingRejectsMalformedOrUnsupportedSyntax(
        string name,
        string source)
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        Write(
            fixture.CompiledDesktopNodeRoot,
            "control/runtime-control.js",
            "export const control = true;\n");
        Write(
            fixture.CompiledDesktopNodeRoot,
            "production/twenty-server-role.js",
            source);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "compiled-malformed-" + name),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-compiled-dependency-invalid", error.ReasonCode);
    }

    [Fact]
    public void TrustedImportRootMustBeOwnedByAndReachableFromItsLaunchSource()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        const string importSource = "packages/twenty-server/src/desktop-import-root.ts";
        const string importEmitted = "packages/twenty-server/dist/desktop-import-root.js";
        Write(fixture.BuildRoot, importSource, "export {};\n");
        Write(fixture.BuildRoot, importEmitted, "export {};\n");
        var launchSource = Path.Combine(
            fixture.BuildRoot,
            "desktop",
            "windows",
            "node",
            "src",
            "production",
            "twenty-server-role.ts");
        var relativeImport = Path.GetRelativePath(
                Path.GetDirectoryName(launchSource)!,
                Path.Combine(fixture.BuildRoot, importSource[..^3]))
            .Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(
            launchSource,
            $"import '{relativeImport}';\n",
            new UTF8Encoding(false));
        var inventory = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(fixture.InventoryPath))!.AsObject();
        inventory["records"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "importRootJs",
            ["ownerRole"] = "server",
            ["sourcePath"] = importSource,
            ["emittedPath"] = importEmitted,
            ["classification"] = "sideEffectFreeImport",
        });
        File.WriteAllText(
            fixture.InventoryPath,
            inventory.ToJsonString(),
            new UTF8Encoding(false));
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var result = builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            Path.Combine(_root, "reachable-import"),
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: false,
            fixture.CompiledDesktopNodeRoot,
            NodeArchivePath: Path.Combine(_root, "trusted-node.zip")));

        Assert.Equal("trusted-production-payload", result.Classification);

        File.WriteAllText(launchSource, "export {};\n", new UTF8Encoding(false));
        var unreachable = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "unreachable-import"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: false,
                fixture.CompiledDesktopNodeRoot)));
        Assert.Equal("production-entrypoint-inventory-mismatch", unreachable.ReasonCode);
    }

    [Theory]
    [InlineData("line-comment")]
    [InlineData("block-comment")]
    [InlineData("ordinary-string")]
    [InlineData("template")]
    [InlineData("regex")]
    [InlineData("nonliteral-dynamic-import")]
    [InlineData("property-dynamic-import")]
    [InlineData("import-meta")]
    public void ImportRootReachabilityIgnoresNonStaticLexicalFalseEdges(string form)
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        const string importSource = "packages/twenty-server/src/lexical-import-root.ts";
        const string importEmitted = "packages/twenty-server/dist/lexical-import-root.js";
        Write(fixture.BuildRoot, importSource, "export {};\n");
        Write(fixture.BuildRoot, importEmitted, "export {};\n");
        var launchSource = Path.Combine(
            fixture.BuildRoot,
            "desktop",
            "windows",
            "node",
            "src",
            "production",
            "twenty-server-role.ts");
        var relativeImport = Path.GetRelativePath(
                Path.GetDirectoryName(launchSource)!,
                Path.Combine(fixture.BuildRoot, importSource[..^3]))
            .Replace(Path.DirectorySeparatorChar, '/');
        var source = form switch
        {
            "line-comment" => $"// import '{relativeImport}';\nexport {{}};\n",
            "block-comment" => $"/* import '{relativeImport}'; */\nexport {{}};\n",
            "ordinary-string" => $"const text = \"import '{relativeImport}'\";\nexport {{ text }};\n",
            "template" => $"const text = `import '{relativeImport}'`;\nexport {{ text }};\n",
            "regex" => $"const pattern = /import '{relativeImport.Replace("/", "\\/")}'/;\nexport {{ pattern }};\n",
            "nonliteral-dynamic-import" =>
                $"const specifier = '{relativeImport}';\nawait import(specifier);\nexport {{}};\n",
            "property-dynamic-import" =>
                $"const loader = {{ import() {{}} }};\nloader.import('{relativeImport}');\nexport {{ loader }};\n",
            "import-meta" =>
                $"import.meta.resolve('{relativeImport}');\nexport {{}};\n",
            _ => throw new InvalidOperationException(form),
        };
        File.WriteAllText(launchSource, source, new UTF8Encoding(false));
        AddImportRootRecord(
            fixture.InventoryPath,
            importSource,
            importEmitted);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "source-lexical-false-edge-" + form),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: false,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-entrypoint-inventory-mismatch", error.ReasonCode);
    }

    [Fact]
    public void LiteralDynamicTypeScriptImportEstablishesReachability()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        const string importSource = "packages/twenty-server/src/dynamic-import-root.ts";
        const string importEmitted = "packages/twenty-server/dist/dynamic-import-root.js";
        Write(fixture.BuildRoot, importSource, "export {};\n");
        Write(fixture.BuildRoot, importEmitted, "export {};\n");
        var launchSource = Path.Combine(
            fixture.BuildRoot,
            "desktop",
            "windows",
            "node",
            "src",
            "production",
            "twenty-server-role.ts");
        var relativeImport = Path.GetRelativePath(
                Path.GetDirectoryName(launchSource)!,
                Path.Combine(fixture.BuildRoot, importSource[..^3]))
            .Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(
            launchSource,
            $"await import /* trusted boundary */ ('{relativeImport}');\n",
            new UTF8Encoding(false));
        AddImportRootRecord(
            fixture.InventoryPath,
            importSource,
            importEmitted);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var result = builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            Path.Combine(_root, "source-literal-dynamic-edge"),
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: false,
            fixture.CompiledDesktopNodeRoot,
            NodeArchivePath: Path.Combine(_root, "trusted-node.zip")));

        Assert.Equal("trusted-production-payload", result.Classification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NodeNextJavaScriptSpecifierResolvesExactTypeScriptSource(
        bool dynamicImport)
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        const string importSource = "packages/twenty-server/src/nodenext-import-root.ts";
        const string importEmitted = "packages/twenty-server/dist/nodenext-import-root.js";
        Write(fixture.BuildRoot, importSource, "export {};\n");
        Write(fixture.BuildRoot, importEmitted, "export {};\n");
        var launchSource = Path.Combine(
            fixture.BuildRoot,
            "desktop",
            "windows",
            "node",
            "src",
            "production",
            "twenty-server-role.ts");
        var bridgeSource = Path.Combine(Path.GetDirectoryName(launchSource)!, "bridge.ts");
        var relativeTarget = Path.GetRelativePath(
                Path.GetDirectoryName(bridgeSource)!,
                Path.Combine(fixture.BuildRoot, importSource[..^3]))
            .Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(
            bridgeSource,
            $"import '{relativeTarget}';\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            launchSource,
            dynamicImport
                ? "await import /* trusted boundary */ ('./bridge.js');\n"
                : "import /* trusted boundary */ './bridge.js';\n",
            new UTF8Encoding(false));
        AddImportRootRecord(
            fixture.InventoryPath,
            importSource,
            importEmitted);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var result = builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            Path.Combine(_root, "source-nodenext-edge-" + dynamicImport),
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: false,
            fixture.CompiledDesktopNodeRoot,
            NodeArchivePath: Path.Combine(_root, "trusted-node.zip")));

        Assert.Equal("trusted-production-payload", result.Classification);
    }

    [Fact]
    public void NodeNextSourceResolutionRejectsAmbiguousJavaScriptAndTypeScriptFiles()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        const string importSource = "packages/twenty-server/src/ambiguous-import-root.ts";
        const string importEmitted = "packages/twenty-server/dist/ambiguous-import-root.js";
        Write(fixture.BuildRoot, importSource, "export {};\n");
        Write(fixture.BuildRoot, importEmitted, "export {};\n");
        var launchSource = Path.Combine(
            fixture.BuildRoot,
            "desktop",
            "windows",
            "node",
            "src",
            "production",
            "twenty-server-role.ts");
        var relativeTarget = Path.GetRelativePath(
                Path.GetDirectoryName(launchSource)!,
                Path.Combine(fixture.BuildRoot, importSource[..^3]))
            .Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(launchSource)!, "bridge.js"),
            $"import '{relativeTarget}';\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(launchSource)!, "bridge.ts"),
            $"import '{relativeTarget}';\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            launchSource,
            "import './bridge.js';\n",
            new UTF8Encoding(false));
        AddImportRootRecord(
            fixture.InventoryPath,
            importSource,
            importEmitted);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "source-nodenext-ambiguous"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: false,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-entrypoint-inventory-mismatch", error.ReasonCode);
    }

    [Fact]
    public void ImportRootReachabilityRejectsAnIntermediateDirectoryReparsePoint()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        const string importSource = "packages/twenty-server/src/reparse-import-root.ts";
        const string importEmitted = "packages/twenty-server/dist/reparse-import-root.js";
        Write(fixture.BuildRoot, importSource, "export {};\n");
        Write(fixture.BuildRoot, importEmitted, "export {};\n");
        var launchSource = Path.Combine(
            fixture.BuildRoot,
            "desktop",
            "windows",
            "node",
            "src",
            "production",
            "twenty-server-role.ts");
        var externalBridge = Path.Combine(_root, "external-source-bridge");
        Directory.CreateDirectory(externalBridge);
        var bridgeAlias = Path.Combine(
            Path.GetDirectoryName(launchSource)!,
            "external-bridge");
        var relativeTarget = Path.GetRelativePath(
                bridgeAlias,
                Path.Combine(fixture.BuildRoot, importSource[..^3]))
            .Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(
            Path.Combine(externalBridge, "bridge.ts"),
            $"import '{relativeTarget}';\n",
            new UTF8Encoding(false));
        Directory.CreateSymbolicLink(bridgeAlias, externalBridge);
        File.WriteAllText(
            launchSource,
            "import './external-bridge/bridge';\n",
            new UTF8Encoding(false));
        AddImportRootRecord(
            fixture.InventoryPath,
            importSource,
            importEmitted);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "source-reparse-edge"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: false,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-entrypoint-inventory-mismatch", error.ReasonCode);
    }

    [Fact]
    public void CommentSeparatedStaticTypeScriptImportEstablishesReachability()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        const string importSource = "packages/twenty-server/src/comment-import-root.ts";
        const string importEmitted = "packages/twenty-server/dist/comment-import-root.js";
        Write(fixture.BuildRoot, importSource, "export {};\n");
        Write(fixture.BuildRoot, importEmitted, "export {};\n");
        var launchSource = Path.Combine(
            fixture.BuildRoot,
            "desktop",
            "windows",
            "node",
            "src",
            "production",
            "twenty-server-role.ts");
        var relativeImport = Path.GetRelativePath(
                Path.GetDirectoryName(launchSource)!,
                Path.Combine(fixture.BuildRoot, importSource[..^3]))
            .Replace(Path.DirectorySeparatorChar, '/');
        File.WriteAllText(
            launchSource,
            $"import /* trusted boundary */ '{relativeImport}';\n",
            new UTF8Encoding(false));
        AddImportRootRecord(
            fixture.InventoryPath,
            importSource,
            importEmitted);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var result = builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            Path.Combine(_root, "source-comment-static-edge"),
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: false,
            fixture.CompiledDesktopNodeRoot,
            NodeArchivePath: Path.Combine(_root, "trusted-node.zip")));

        Assert.Equal("trusted-production-payload", result.Classification);
    }

    [Fact]
    public void InventoryRejectsAnOnDiskCaseAliasForADeclaredSource()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        var declared = Path.Combine(
            fixture.BuildRoot,
            "desktop",
            "windows",
            "node",
            "src",
            "production",
            "twenty-server-role.ts");
        var temporary = declared + ".temporary";
        var aliased = Path.Combine(Path.GetDirectoryName(declared)!, "Twenty-Server-Role.ts");
        File.Move(declared, temporary);
        File.Move(temporary, aliased);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "case-alias-source"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: false,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-entrypoint-inventory-mismatch", error.ReasonCode);
    }

    [Fact]
    public void ClosureOnlyStopsExplicitlyUntrustedBeforeManifestOrCatalog()
    {
        var fixture = CreateClosureFixture();
        var payload = Path.Combine(_root, "closure-only");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var result = builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true));

        Assert.Equal("untrusted-build-closure", result.Classification);
        Assert.Null(result.Manifest);
        Assert.Null(result.ReleaseCatalog);
        Assert.False(File.Exists(Path.Combine(payload, ProductionPayloadValidator.ManifestFileName)));
    }

    [Fact]
    public void ClosureOnlyStillBindsTheCurrentFrontendRecordToTheHeader()
    {
        var fixture = CreateClosureFixture();
        var redirected = fixture.Header with
        {
            FrontendEntrypoint = "packages/twenty-server/dist/main.js",
        };
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "closure-frontend-redirect"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                redirected,
                ClosureOnly: true)));

        Assert.Equal("production-entrypoint-inventory-mismatch", error.ReasonCode);
    }

    [Fact]
    public void ClosureOnlyStillBindsEveryCurrentLaunchRecordToItsHeaderRole()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        var redirected = fixture.Header with
        {
            ServerEntrypoint = fixture.Header.WorkerEntrypoint,
        };
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "closure-launch-redirect"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                redirected,
                ClosureOnly: true,
                fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-entrypoint-inventory-mismatch", error.ReasonCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosureOnlyStillRequiresCurrentImportRootOwnershipAndReachability(
        bool declareOwningLaunch)
    {
        var fixture = CreateClosureFixture(completeInventory: declareOwningLaunch);
        const string importSource = "packages/twenty-server/src/closure-import-root.ts";
        const string importEmitted = "packages/twenty-server/dist/closure-import-root.js";
        Write(fixture.BuildRoot, importSource, "export {};\n");
        Write(fixture.BuildRoot, importEmitted, "export {};\n");
        var inventory = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(fixture.InventoryPath))!.AsObject();
        inventory["records"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "importRootJs",
            ["ownerRole"] = "server",
            ["sourcePath"] = importSource,
            ["emittedPath"] = importEmitted,
            ["classification"] = "sideEffectFreeImport",
        });
        File.WriteAllText(
            fixture.InventoryPath,
            inventory.ToJsonString(),
            new UTF8Encoding(false));
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "closure-import-" + declareOwningLaunch),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true,
                declareOwningLaunch ? fixture.CompiledDesktopNodeRoot : null)));

        Assert.Equal("production-entrypoint-inventory-mismatch", error.ReasonCode);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("package-manager")]
    [InlineData("frontend-marker")]
    [InlineData("source-node-modules")]
    public void UnsafeRuntimeClosureFailsClosed(string mutation)
    {
        var fixture = CreateClosureFixture();
        switch (mutation)
        {
            case "native":
                Write(fixture.BuildRoot, "node_modules/runtime/addon.node", "not-pe");
                break;
            case "package-manager":
                Write(fixture.BuildRoot, "node_modules/.bin/npm.cmd", "npm");
                break;
            case "frontend-marker":
                Write(fixture.BuildRoot, "packages/twenty-server/dist/front/index.html", "<html></html>");
                break;
            case "source-node-modules":
                Write(fixture.BuildRoot, ".source-node-modules-sentinel", "forbidden");
                break;
        }
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "bad-" + mutation),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));
    }

    [Theory]
    [InlineData(".BIN/npm.cmd")]
    [InlineData(".Bin/YARN.EXE")]
    [InlineData(".bIn/CorePack")]
    public void MixedCasePackageManagerBinAliasesFailClosedOnWindows(string alias)
    {
        var fixture = CreateClosureFixture();
        Write(fixture.BuildRoot, "node_modules/" + alias, "forbidden");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "mixed-bin-alias-" + alias.Replace('/', '-')),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-runtime-package-manager-present", error.ReasonCode);
    }

    [Fact]
    public void FinalIdentityScanRejectsDirectoryCaseAliasesNotOnlyFileAliases()
    {
        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            ProductionPayloadBuilder.ValidateCaseInsensitiveEntryIdentities(
                ["packages/Runtime", "packages/runtime", "packages/runtime/index.js"]));

        Assert.Equal("production-build-case-collision", error.ReasonCode);
    }

    [Fact]
    public void StagedClosureAlternateDataStreamFailsBeforeCopy()
    {
        var fixture = CreateClosureFixture();
        var streamed = Path.Combine(fixture.BuildRoot, "node_modules", "runtime", "index.js");
        File.WriteAllText(streamed + ":phase1cb-hidden", "hidden", new UTF8Encoding(false));
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "ads-payload"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-build-alternate-data-stream", error.ReasonCode);
    }

    [Fact]
    public void StagedClosureLongPathStillReceivesAlternateDataStreamValidation()
    {
        var fixture = CreateClosureFixture();
        var longSegment = new string('a', 220);
        var stagedLongPath = Path.Combine(
            fixture.BuildRoot,
            "node_modules",
            "runtime",
            longSegment,
            "index.js");
        Assert.True(stagedLongPath.Length > 260);
        Write(
            fixture.BuildRoot,
            $"node_modules/runtime/{longSegment}/index.js",
            "export const longPath = true;");
        var payload = Path.Combine(_root, "long-path-payload");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var result = builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true));

        Assert.Equal("untrusted-build-closure", result.Classification);
        Assert.True(File.Exists(Path.Combine(
            payload,
            "node_modules",
            "runtime",
            longSegment,
            "index.js")));
    }

    [Fact]
    public void AlternateDataStreamNativePathUsesExtendedLengthSyntax()
    {
        var longPath = Path.Combine(_root, new string('b', 220), "index.js");
        Assert.True(longPath.Length > 260);

        Assert.Equal(
            @"\\?\" + longPath,
            ProductionPayloadBuilder.ToNativeExtendedLengthPath(longPath));
    }

    [Fact]
    public void NativeAddonUsesOneBoundedExactPayloadNodeLoadProbe()
    {
        var fixture = CreateClosureFixture();
        WritePeX64(fixture.BuildRoot, "node_modules/runtime/addon.node");
        var payload = Path.Combine(_root, "native-probe-payload");
        var runner = new RecordingRunner();
        var builder = new ProductionPayloadBuilder(
            runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
            new AcceptedMinGitProbe());

        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true));

        var probe = Assert.Single(runner.Invocations);
        Assert.Equal(Path.Combine(payload, "node.exe"), probe.FileName);
        Assert.Equal(payload, probe.WorkingDirectory);
        Assert.False(probe.UseShellExecute);
        Assert.Equal(3, probe.Arguments.Count);
        Assert.Equal(Path.Combine(payload, ".phase1cb-native-addon-probe.mjs"), probe.Arguments[0]);
        Assert.Equal(payload, probe.Arguments[1]);
        Assert.Equal(Path.Combine(payload, "node_modules", "runtime", "addon.node"), probe.Arguments[2]);
        Assert.Equal(TimeSpan.FromSeconds(30), probe.Timeout);
        Assert.Equal(64 * 1024, probe.MaximumOutputBytes);
        Assert.Equal(
            payload + Path.PathSeparator +
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
            probe.Environment!["PATH"]);
        Assert.Equal("false", probe.Environment["NX_DAEMON"]);
        Assert.Equal(
            ProductionPayloadProcessPurpose.NativeAddonProbe,
            probe.Purpose);
        Assert.Contains("NODE_OPTIONS", probe.RemovedEnvironmentVariables);
        Assert.Contains("NODE_PATH", probe.RemovedEnvironmentVariables);
        Assert.Contains("npm_execpath", probe.RemovedEnvironmentVariables);
        Assert.Contains("YARN_IGNORE_PATH", probe.RemovedEnvironmentVariables);
        Assert.Contains("COREPACK_HOME", probe.RemovedEnvironmentVariables);
        Assert.False(File.Exists(probe.Arguments[0]));
    }

    [Fact]
    public void NativeAddonProbeRunsThroughRealInstalledLikeProcessPolicy()
    {
        var payload = Path.Combine(_root, "real-native-probe-policy");
        Directory.CreateDirectory(payload);
        var node = Path.Combine(payload, "node.exe");
        CreateTestHardLink(node, FindNodeExecutable());
        var probe = Path.Combine(payload, ".phase1cb-native-addon-probe.mjs");
        var addon = Path.Combine(payload, "runtime.node");
        File.WriteAllText(
            probe,
            "if (process.argv[2] !== process.cwd()) process.exit(91);",
            new UTF8Encoding(false));
        File.WriteAllBytes(addon, [0]);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = payload + Path.PathSeparator + Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32"),
            ["NX_DAEMON"] = "false",
        };

        new ProductionPayloadProcessRunner().Run(new ProductionPayloadProcessSpec(
            node,
            [probe, payload, addon],
            payload,
            UseShellExecute: false,
            environment)
        {
            Purpose = ProductionPayloadProcessPurpose.NativeAddonProbe,
            Timeout = TimeSpan.FromSeconds(30),
            MaximumOutputBytes = 64 * 1024,
            RemovedEnvironmentVariables = ["NODE_OPTIONS", "NODE_PATH"],
            RemovedEnvironmentVariablePrefixes = ["npm_", "YARN_", "COREPACK_"],
        });
    }

    [Fact]
    public void UnknownNonTargetPlatformNativePathsFailInsteadOfSubstringPruning()
    {
        var fixture = CreateClosureFixture();
        WriteElfX64(
            fixture.BuildRoot,
            "node_modules/runtime/prebuilds/linux-x64/addon.node");
        WritePe(
            fixture.BuildRoot,
            "node_modules/runtime/prebuilds/win32-arm64/addon.node",
            0xAA64);
        var payload = Path.Combine(_root, "native-nontarget-payload");
        var runner = new RecordingRunner();
        var builder = new ProductionPayloadBuilder(runner, new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                payload,
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-native-pruning-inventory-invalid", error.ReasonCode);
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("missing")]
    [InlineData("extra")]
    public void BcryptNativePruningRequiresExactPackageVersionLayoutAndCount(string mutation)
    {
        var fixture = CreateClosureFixture();
        var bcryptRoot = "node_modules/bcrypt";
        Write(
            fixture.BuildRoot,
            $"{bcryptRoot}/package.json",
            mutation == "version"
                ? "{\"name\":\"bcrypt\",\"version\":\"6.0.1\"}"
                : "{\"name\":\"bcrypt\",\"version\":\"6.0.0\"}");
        var paths = BcryptNativeInventory().ToList();
        if (mutation == "missing")
        {
            paths.RemoveAt(0);
        }
        foreach (var path in paths)
        {
            if (path == "prebuilds/win32-x64/bcrypt.node")
            {
                WritePeX64(fixture.BuildRoot, $"{bcryptRoot}/{path}");
            }
            else
            {
                WriteElfX64(fixture.BuildRoot, $"{bcryptRoot}/{path}");
            }
        }
        if (mutation == "extra")
        {
            WriteElfX64(fixture.BuildRoot, $"{bcryptRoot}/prebuilds/freebsd-x64/bcrypt.node");
        }
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "bcrypt-" + mutation),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-native-pruning-inventory-invalid", error.ReasonCode);
    }

    [Fact]
    public void ExactBcryptInventoryPrunesOnlyForeignVariantsAndProbesRetainedPeX64()
    {
        var fixture = CreateClosureFixture();
        const string bcryptRoot = "node_modules/bcrypt";
        Write(
            fixture.BuildRoot,
            $"{bcryptRoot}/package.json",
            "{\"name\":\"bcrypt\",\"version\":\"6.0.0\"}");
        foreach (var path in BcryptNativeInventory())
        {
            if (path == "prebuilds/win32-x64/bcrypt.node")
            {
                WritePeX64(fixture.BuildRoot, $"{bcryptRoot}/{path}");
            }
            else
            {
                WriteElfX64(fixture.BuildRoot, $"{bcryptRoot}/{path}");
            }
        }
        var payload = Path.Combine(_root, "bcrypt-exact");
        var runner = new RecordingRunner();
        var builder = new ProductionPayloadBuilder(runner, new AcceptedNodeTrust());

        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true));

        var retained = Assert.Single(Directory.EnumerateFiles(
            payload,
            "*.node",
            SearchOption.AllDirectories));
        Assert.EndsWith(
            Path.Combine("prebuilds", "win32-x64", "bcrypt.node"),
            retained,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public void ExactSentryProfilerInventoryPrunesThirtyAndProbesFiveRetainedPeX64()
    {
        var fixture = CreateClosureFixture();
        const string profilerRoot = "node_modules/@sentry/node-cpu-profiler";
        Write(
            fixture.BuildRoot,
            $"{profilerRoot}/package.json",
            "{\"name\":\"@sentry/node-cpu-profiler\",\"version\":\"2.4.2\"}");
        foreach (var path in SentryNativeInventory())
        {
            if (path.Contains("-win32-x64-", StringComparison.Ordinal))
            {
                WritePeX64(fixture.BuildRoot, $"{profilerRoot}/{path}");
            }
            else
            {
                WriteElfX64(fixture.BuildRoot, $"{profilerRoot}/{path}");
            }
        }
        var payload = Path.Combine(_root, "sentry-exact");
        var runner = new RecordingRunner();
        var builder = new ProductionPayloadBuilder(runner, new AcceptedNodeTrust());

        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true));

        var retained = Directory.EnumerateFiles(payload, "*.node", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(5, retained.Length);
        Assert.All(retained, path => Assert.Contains("-win32-x64-", path, StringComparison.Ordinal));
        Assert.Equal(5, runner.Invocations.Count);
    }

    [Fact]
    public void NativeAddonLoadProbeFailureHasAStableFailClosedReason()
    {
        var fixture = CreateClosureFixture();
        WritePeX64(fixture.BuildRoot, "node_modules/runtime/addon.node");
        var builder = new ProductionPayloadBuilder(
            new RejectingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "native-probe-rejected"),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-native-addon-load-failed", error.ReasonCode);
    }

    [Fact]
    public void NativeAddonProbeMutationFailsTheFinalClosureEqualityScan()
    {
        var fixture = CreateClosureFixture();
        WritePeX64(fixture.BuildRoot, "node_modules/runtime/addon.node");
        var payload = Path.Combine(_root, "native-probe-mutated");
        var builder = new ProductionPayloadBuilder(
            new MutatingProbeRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                payload,
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-build-closure-mutated", error.ReasonCode);
        Assert.False(Directory.Exists(payload));
    }

    [Fact]
    public void PublicBuildNormalizesRunsPlacesFrontendAndMaterializesClosure()
    {
        var fixture = CreateClosureFixture();
        var existingFront = Path.Combine(
            fixture.BuildRoot,
            "packages",
            "twenty-server",
            "dist",
            "front");
        Directory.Delete(existingFront, recursive: true);
        Write(
            fixture.BuildRoot,
            "packages/twenty-front/build/index.html",
            """
            <script id="twenty-env-config">
              window._env_ = {
                // This will be overwritten
              };
            </script>
            """);
        var repository = FindRepositoryRoot();
        var sourceProject = Path.Combine(repository, "packages", "twenty-sdk", "project.json");
        var stagedProject = Path.Combine(fixture.BuildRoot, "packages", "twenty-sdk", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedProject)!);
        File.Copy(sourceProject, stagedProject);
        var payload = Path.Combine(_root, "public-build-payload");
        var archive = Path.Combine(_root, "public-build-archive.zip");
        File.WriteAllText(archive, "archive");
        var runner = new RecordingRunner();
        var builder = new ProductionPayloadBuilder(
            runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
            new AcceptedMinGitProbe());

        var result = builder.Build(new ProductionPayloadBuildRequest(
            archive,
            fixture.NodeExecutable,
            fixture.BuildRoot,
            payload,
            fixture.InventoryPath,
            Path.Combine(fixture.BuildRoot, "desktop", "windows", "node", "publish"),
            sourceProject,
            fixture.Header,
            ClosureOnly: true));

        Assert.Equal("untrusted-build-closure", result.Classification);
        Assert.Equal(11, runner.Invocations.Count);
        Assert.True(File.Exists(Path.Combine(
            fixture.BuildRoot,
            "packages",
            "twenty-server",
            "dist",
            "front",
            "index.html")));
        Assert.True(File.Exists(Path.Combine(
            payload,
            "packages",
            "twenty-server",
            "dist",
            "front",
            "index.html")));
        Assert.Equal(
            "142C0322F8C626656B242FC35A32807F50B59C0C2B039F391813C9248E80AC28",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(stagedProject))));
    }

    [Fact]
    public void PublicBuildRechecksPinnedNodeAfterCommandsAndBeforeProductionCopy()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        var repository = FindRepositoryRoot();
        var sourceProject = Path.Combine(repository, "packages", "twenty-sdk", "project.json");
        var stagedProject = Path.Combine(fixture.BuildRoot, "packages", "twenty-sdk", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedProject)!);
        File.Copy(sourceProject, stagedProject, overwrite: true);
        var payload = Path.Combine(_root, "mutated-node-payload");
        var archive = Path.Combine(_root, "mutated-node-archive.zip");
        File.WriteAllText(archive, "archive");
        var runner = new NodeMutatingBuildRunner(fixture.NodeExecutable);
        var trust = new ContentAwareNodeTrust();
        var builder = new ProductionPayloadBuilder(
            runner, trust, new AcceptedMinGitTrust(), new AcceptedMinGitProbe());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Build(new ProductionPayloadBuildRequest(
                archive,
                fixture.NodeExecutable,
                fixture.BuildRoot,
                payload,
                fixture.InventoryPath,
                fixture.CompiledDesktopNodeRoot,
                sourceProject,
                fixture.Header,
                ClosureOnly: false)));

        Assert.Equal("production-node-identity-invalid", error.ReasonCode);
        Assert.True(trust.InspectionCount >= 2);
        Assert.False(Directory.Exists(payload));
    }

    [Fact]
    public void PublicBuildRejectsNodeReparseAncestorBeforeTrustInspectionOrRunnerLaunch()
    {
        var fixture = CreateClosureFixture();
        var nodeTarget = Path.Combine(fixture.BuildRoot, "node-target");
        Directory.CreateDirectory(nodeTarget);
        var movedNode = Path.Combine(nodeTarget, Path.GetFileName(fixture.NodeExecutable));
        File.Move(fixture.NodeExecutable, movedNode);
        var nodeAlias = Path.Combine(fixture.BuildRoot, "node-alias");
        Directory.CreateSymbolicLink(nodeAlias, nodeTarget);
        var nodeThroughAlias = Path.Combine(nodeAlias, Path.GetFileName(movedNode));
        var archive = Path.Combine(_root, "public-build-node-archive.zip");
        File.WriteAllText(archive, "archive");
        var repository = FindRepositoryRoot();
        var sourceProject = Path.Combine(repository, "packages", "twenty-sdk", "project.json");
        var runner = new RecordingRunner();
        var trust = new CountingAcceptedNodeTrust();
        var builder = new ProductionPayloadBuilder(runner, trust);

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Build(new ProductionPayloadBuildRequest(
                archive,
                nodeThroughAlias,
                fixture.BuildRoot,
                Path.Combine(_root, "public-build-input-reparse-payload"),
                fixture.InventoryPath,
                fixture.CompiledDesktopNodeRoot,
                sourceProject,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-build-path-invalid", error.ReasonCode);
        Assert.Equal(0, trust.InspectionCount);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void FrontendPlacementRejectsDestinationJunctionBeforeAnyExternalWrite()
    {
        var fixture = CreateClosureFixture();
        var serverDist = Path.Combine(fixture.BuildRoot, "packages", "twenty-server", "dist");
        Directory.Delete(serverDist, recursive: true);
        var external = Path.Combine(_root, "frontend-placement-external");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        Directory.CreateSymbolicLink(serverDist, external);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.PlaceBuiltFrontend(fixture.BuildRoot));

        Assert.Equal("production-build-path-invalid", error.ReasonCode);
        Assert.False(Directory.Exists(Path.Combine(external, "front")));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    [Fact]
    public void FrontendPlacementRejectsExcessiveTreeDepthBeforeUnboundedCopying()
    {
        var build = Path.Combine(_root, "frontend-depth-build");
        var source = Path.Combine(build, "packages", "twenty-front", "build");
        var current = source;
        Directory.CreateDirectory(current);
        for (var index = 0; index < 140; index++)
        {
            current = Path.Combine(current, "d");
            Directory.CreateDirectory(current);
        }
        File.WriteAllText(Path.Combine(current, "asset.txt"), "bounded");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.PlaceBuiltFrontend(build));

        Assert.Equal("production-build-traversal-budget", error.ReasonCode);
    }

    [Fact]
    public void InstalledDescriptorIsProtectedPrivilegedOwnedAndRuntimeReadExecuteOnly()
    {
        var runtime = new SecurityIdentifier("S-1-5-21-1000-1001-1002-1003");

        var descriptor = ProductionPayloadBuilder.CreateInstalledReadExecuteDescriptor(
            runtime,
            directory: true);

        TrustedApplicationRootSecurity.Validate(descriptor);
        Assert.Equal(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            descriptor.Owner);
        var runtimeAce = Assert.Single(
            descriptor.DiscretionaryAcl!.OfType<CommonAce>(),
            ace => runtime.Equals(ace.SecurityIdentifier));
        Assert.Equal(AceQualifier.AccessAllowed, runtimeAce.AceQualifier);
        Assert.Equal(
            0,
            runtimeAce.AccessMask & (int)(
                FileSystemRights.WriteData |
                FileSystemRights.AppendData |
                FileSystemRights.Delete |
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership));
        foreach (var interactiveGroup in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                     new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
                 })
        {
            var ace = Assert.Single(
                descriptor.DiscretionaryAcl!.OfType<CommonAce>(),
                candidate => interactiveGroup.Equals(candidate.SecurityIdentifier));
            Assert.Equal((int)FileSystemRights.ReadAndExecute, ace.AccessMask);
        }
    }

    [Fact]
    public void InstalledAclTraversalRejectsReparseBeforeAnyExternalMutation()
    {
        var root = Path.Combine(_root, "installed-acl-root");
        var external = Path.Combine(_root, "installed-acl-external");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, "outside.txt");
        File.WriteAllText(externalFile, "outside");
        var before = new FileInfo(externalFile).GetAccessControl().GetSecurityDescriptorBinaryForm();
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), external);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.ApplyAndVerifyInstalledReadExecuteAcl(root));

        Assert.Equal("production-build-reparse-point", error.ReasonCode);
        Assert.Equal(
            before,
            new FileInfo(externalFile).GetAccessControl().GetSecurityDescriptorBinaryForm());
    }

    [Fact]
    public void DefaultPublishContainsNoUnreachableUntrustedPublishPipeline()
    {
        var repository = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));

        Assert.DoesNotContain("'manifest', 'create'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'installation', 'secure'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'manifest', 'verify'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'publish', 'desktop/windows/src/HowardLab.EbayCrm.AppHost/", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPublishFailsClosedBeforePublishUntilTask6ATrustValidatorExists()
    {
        var repository = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));
        var guard = script.IndexOf(
            "phase1cb-candidate-validator-task6a-required",
            StringComparison.Ordinal);
        var publish = script.IndexOf(
            "'publish', 'desktop/windows/src/HowardLab.EbayCrm.AppHost/",
            StringComparison.Ordinal);

        Assert.True(guard >= 0);
        Assert.True(publish < 0);
        Assert.DoesNotContain("canonicalDigest) -notmatch", script, StringComparison.Ordinal);
        Assert.Contains("if (-not $ClosureOnly)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPublishGuardNeedsNoRuntimeToolResolutionEvenWithUnusablePath()
    {
        var repository = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1");
        var script = File.ReadAllText(scriptPath);
        var guard = script.IndexOf(
            "phase1cb-candidate-validator-task6a-required",
            StringComparison.Ordinal);
        var dotnetResolution = script.IndexOf(
            "$dotnetCommand = Get-Command dotnet",
            StringComparison.Ordinal);
        Assert.True(guard >= 0);
        Assert.Equal(-1, dotnetResolution);
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "default-publish-must-not-create");
        var harness = Path.Combine(_root, "default-publish-ordering-harness.ps1");
        File.WriteAllText(
            harness,
            $"[Environment]::SetEnvironmentVariable('PATH', (Join-Path $env:WINDIR 'System32'), 'Process')" + Environment.NewLine +
            "try {" + Environment.NewLine +
            $"  & '{PowerShellLiteral(scriptPath)}' -RepositoryRoot '{PowerShellLiteral(repository)}' -OutputRoot '{PowerShellLiteral(output)}'" + Environment.NewLine +
            "  exit 0" + Environment.NewLine +
            "} catch {" + Environment.NewLine +
            "  [Console]::Error.WriteLine($_.Exception.Message)" + Environment.NewLine +
            "  exit 1" + Environment.NewLine +
            "}" + Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "phase1cb-candidate-validator-task6a-required",
            result.StandardError + result.StandardOutput,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void PayloadToolUsesCheckedInLockAndScriptNeverInvokesRuntimeRestoreOrMsBuild()
    {
        var repository = FindRepositoryRoot();
        var toolRoot = Path.Combine(
            repository,
            "desktop",
            "windows",
            "tools",
            "HowardLab.EbayCrm.PayloadTool");
        var project = File.ReadAllText(Path.Combine(
            toolRoot,
            "HowardLab.EbayCrm.PayloadTool.csproj"));
        var lockPath = Path.Combine(toolRoot, "packages.lock.json");
        var script = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));

        Assert.True(File.Exists(lockPath));
        using var locked = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(lockPath));
        Assert.Equal(1, locked.RootElement.GetProperty("version").GetInt32());
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", project, StringComparison.Ordinal);
        Assert.Contains("<RestoreLockedMode>true</RestoreLockedMode>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("'restore', $payloadToolProject", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'run', '--no-restore'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'exec',", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-BoundedPayloadTool $payloadToolExe", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptedClosureMaterializationCarriesPinnedNodeArchiveTrustContext()
    {
        var repository = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));
        var program = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "tools",
            "HowardLab.EbayCrm.PayloadTool",
            "Program.cs"));

        Assert.Contains("'--node-archive', $archive", script, StringComparison.Ordinal);
        Assert.Contains("\"node-archive\"", program, StringComparison.Ordinal);
        Assert.Contains(
            "NodeArchivePath: Required(options, \"node-archive\")",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellFrontendPlacementUsesHardenedPayloadToolAndRejectsDestinationJunction()
    {
        var fixture = CreateClosureFixture();
        var serverDist = Path.Combine(fixture.BuildRoot, "packages", "twenty-server", "dist");
        Directory.Delete(serverDist, recursive: true);
        var external = Path.Combine(_root, "powershell-frontend-external");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        Directory.CreateSymbolicLink(serverDist, external);
        var repository = FindRepositoryRoot();
        var scriptPath = Path.Combine(repository, "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1");
        var script = File.ReadAllText(scriptPath);
        Assert.DoesNotContain(
            "Copy-Item -LiteralPath (Join-Path $buildRoot 'packages\\twenty-front\\build')",
            script,
            StringComparison.Ordinal);
        Assert.Contains("'build', 'execute'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-NativeChecked $pinnedNode", script, StringComparison.Ordinal);
        var dotnetExecutable = FindExecutable("dotnet.exe");
        var payloadTool = Path.Combine(AppContext.BaseDirectory, "HowardLab.EbayCrm.PayloadTool.dll");
        Assert.True(File.Exists(payloadTool));
        var harness = Path.Combine(_root, "frontend-placement-harness.ps1");
        File.WriteAllText(
            harness,
            $"& '{PowerShellLiteral(dotnetExecutable)}' '{PowerShellLiteral(payloadTool)}' frontend place --build-root '{PowerShellLiteral(fixture.BuildRoot)}'" + Environment.NewLine +
            "exit $LASTEXITCODE" + Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "production-build-path-invalid",
            result.StandardError + result.StandardOutput,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(external, "front")));
        Assert.Equal("untouched", File.ReadAllText(sentinel));
    }

    [Fact]
    public void PowerShellBuildUsesExactSingleFileToolPathWhileBuildPathIsConstrained()
    {
        var repository = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            repository,
            "desktop",
            "windows",
            "scripts",
            "Build-Phase1CBPayload.ps1"));

        Assert.DoesNotContain("$dotnetExecutable = Get-CanonicalPath", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-BoundedPayloadTool $payloadToolExe @(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-NativeChecked", script, StringComparison.Ordinal);
        Assert.Contains("$environmentPairs", script, StringComparison.Ordinal);
        Assert.Contains("LaunchSuspendedAssigned", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$start.EnvironmentVariables.Clear()", script, StringComparison.Ordinal);
        Assert.Contains("phase1cb-dotnet-timeout", script, StringComparison.Ordinal);
        Assert.Contains("phase1cb-dotnet-output-limit", script, StringComparison.Ordinal);
        Assert.Contains("phase1cb-dotnet-inherited-pipe", script, StringComparison.Ordinal);
        Directory.CreateDirectory(_root);
        var harness = Path.Combine(_root, "constrained-dotnet-path-harness.ps1");
        File.WriteAllText(
            harness,
            """
            $dotnetExecutable = (Get-Command dotnet -CommandType Application).Source
            [Environment]::SetEnvironmentVariable('PATH', (Join-Path $env:WINDIR 'System32'), 'Process')
            & $dotnetExecutable --version | Out-Null
            exit $LASTEXITCODE
            """,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
    }

    [Fact]
    public void PowerShellDotnetBootstrapBoundsRealOutputFloodAndClearsAmbientCredentials()
    {
        var repository = FindRepositoryRoot();
        var sourceScript = File.ReadAllText(Path.Combine(
            repository,
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        Assert.DoesNotContain(
            "function Resolve-ExactGitExecutable",
            sourceScript,
            StringComparison.Ordinal);
        var boundary = sourceScript.IndexOf("function Get-SourceHead", StringComparison.Ordinal);
        Assert.True(boundary > 0);
        Directory.CreateDirectory(_root);
        var probe = Path.Combine(_root, "bootstrap-output-probe.mjs");
        File.WriteAllText(
            probe,
            "if (process.env.AWS_SECRET_ACCESS_KEY) process.exit(91);\n" +
            "setInterval(() => process.stdout.write('x'.repeat(8192)), 0);\n",
            new UTF8Encoding(false));
        var harness = Path.Combine(_root, "bootstrap-output-harness.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..boundary] + Environment.NewLine +
            "$env:AWS_SECRET_ACCESS_KEY = 'ambient-canary'" + Environment.NewLine +
            "try {" + Environment.NewLine +
            $"  Invoke-BoundedDotnet -FilePath '{PowerShellLiteral(FindNodeExecutable())}' -Arguments @('{PowerShellLiteral(probe)}') -ProfileRoot '{PowerShellLiteral(Path.Combine(_root, "bootstrap-profile"))}' -TimeoutSeconds 10 -MaximumOutputBytes 65536" + Environment.NewLine +
            "  exit 0" + Environment.NewLine +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }" + Environment.NewLine,
            new UTF8Encoding(false));
        var watch = Stopwatch.StartNew();

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        watch.Stop();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("phase1cb-dotnet-output-limit", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("output", "phase1cb-dotnet-output-limit")]
    [InlineData("timeout", "phase1cb-dotnet-timeout")]
    [InlineData("parent-exit", "phase1cb-dotnet-inherited-pipe")]
    public void PowerShellDotnetJobTerminatesAndVerifiesEveryDescendant(
        string mode,
        string reason)
    {
        var repository = FindRepositoryRoot();
        var sourceScript = File.ReadAllText(Path.Combine(
            repository,
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        var boundary = sourceScript.IndexOf("function Get-SourceHead", StringComparison.Ordinal);
        Assert.True(boundary > 0);
        Directory.CreateDirectory(_root);
        var fixture = Path.Combine(_root, $"outer-job-{mode}.mjs");
        var childPid = Path.Combine(_root, $"outer-job-{mode}.pid");
        var executable = mode == "parent-exit"
            ? Path.Combine(AppContext.BaseDirectory, "HowardLab.EbayCrm.AppHost.Fixture.exe")
            : FindNodeExecutable();
        var invocationArguments = mode == "parent-exit"
            ? $"@('orphan-output-handles','{PowerShellLiteral(childPid)}')"
            : $"@('{PowerShellLiteral(fixture)}','{mode}','{PowerShellLiteral(childPid)}')";
        if (mode != "parent-exit")
        {
            File.WriteAllText(
                fixture,
                "import { spawn } from 'node:child_process';\n" +
                "import { writeFileSync } from 'node:fs';\n" +
                "if (process.argv[2] === 'child') setInterval(() => {}, 1000);\n" +
                "else { const c=spawn(process.execPath,[process.argv[1],'child'],{stdio:'ignore'});" +
                "writeFileSync(process.argv[3],String(c.pid));" +
                "if(process.argv[2]==='output')setInterval(()=>process.stdout.write('x'.repeat(8192)),0);" +
                "else setInterval(()=>{},1000); }\n",
                new UTF8Encoding(false));
        }
        var harness = Path.Combine(_root, $"outer-job-{mode}.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..boundary] + Environment.NewLine +
            "try {" + Environment.NewLine +
            $" Invoke-BoundedDotnet -FilePath '{PowerShellLiteral(executable)}' -Arguments {invocationArguments} -ProfileRoot '{PowerShellLiteral(Path.Combine(_root, "outer-job-profile-" + mode))}' -TimeoutSeconds {(mode == "timeout" ? 1 : 10)} -MaximumOutputBytes 65536" + Environment.NewLine +
            " exit 0" + Environment.NewLine +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }" + Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(reason, result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(childPid));
        if (mode == "parent-exit")
        {
            using var identity = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(childPid));
            AssertExactProcessExited(
                identity.RootElement.GetProperty("ProcessId").GetInt32(),
                identity.RootElement.GetProperty("CreationTimeUtcTicks").GetInt64());
        }
        else
        {
            AssertProcessExited(int.Parse(File.ReadAllText(childPid)));
        }
    }

    [Fact]
    public void MaterializationWithArchiveContextRechecksCopiedNodeSignatureIdentity()
    {
        var fixture = CreateClosureFixture();
        File.WriteAllText(Path.Combine(_root, "pinned-node-archive.zip"), "archive");
        var payload = Path.Combine(_root, "post-copy-signature-recheck-payload");
        var trust = new RejectSecondNodeInspection();
        var builder = new ProductionPayloadBuilder(new RecordingRunner(), trust);

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                payload,
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true,
                NodeArchivePath: Path.Combine(_root, "pinned-node-archive.zip"))));

        Assert.Equal("production-node-identity-invalid", error.ReasonCode);
        Assert.Equal(2, trust.InspectedExecutables.Count);
        Assert.Equal(fixture.NodeExecutable, trust.InspectedExecutables[0]);
        Assert.Equal(Path.Combine(payload, "node.exe"), trust.InspectedExecutables[1]);
        Assert.False(Directory.Exists(payload));
    }

    [Theory]
    [InlineData("output")]
    [InlineData("timeout")]
    public void RealProcessRunnerBoundsOutputAndTimeoutAndKillsDescendantTree(string mode)
    {
        var node = FindNodeExecutable();
        Directory.CreateDirectory(_root);
        var fixture = Path.Combine(_root, "runner-fixture.mjs");
        var childPidPath = Path.Combine(_root, $"runner-{mode}-child.pid");
        File.WriteAllText(
            fixture,
            """
            import { spawn } from 'node:child_process';
            import { writeFileSync } from 'node:fs';
            import { fileURLToPath } from 'node:url';
            if (process.argv[2] === 'child') {
              setInterval(() => {}, 1000);
            } else {
              const child = spawn(process.execPath, [fileURLToPath(import.meta.url), 'child'], { stdio: 'ignore' });
              writeFileSync(process.argv[3], String(child.pid));
              if (process.argv[2] === 'output') {
                setInterval(() => process.stdout.write('x'.repeat(8192)), 0);
              } else {
                setInterval(() => {}, 1000);
              }
            }
            """,
            new UTF8Encoding(false));
        var runner = new ProductionPayloadProcessRunner();
        var watch = Stopwatch.StartNew();

        var error = Assert.Throws<ProductionPayloadBuildException>(() => runner.Run(
            new ProductionPayloadProcessSpec(
                node,
                [fixture, mode, childPidPath],
                _root,
                UseShellExecute: false,
                Environment: null)
            {
                Timeout = mode == "timeout" ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(10),
                MaximumOutputBytes = 64 * 1024,
            }));

        watch.Stop();
        Assert.Equal(
            mode == "timeout" ? "production-build-process-timeout" : "production-build-process-failed",
            error.ReasonCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(childPidPath));
        AssertProcessExited(int.Parse(File.ReadAllText(childPidPath)));
    }

    [Fact]
    public async Task ProcessRunnerAppliesOneDeadlineWhenAnExitedParentLeavesInheritedPipesOpen()
    {
        Directory.CreateDirectory(_root);
        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "HowardLab.EbayCrm.AppHost.Fixture.exe");
        var childIdentityPath = Path.Combine(_root, "runner-inherited-pipes-child.json");
        Assert.True(File.Exists(fixture));
        var runner = new ProductionPayloadProcessRunner();
        var watch = Stopwatch.StartNew();
        var invocation = Task.Run(() => Record.Exception(() => runner.Run(
            new ProductionPayloadProcessSpec(
                fixture,
                ["orphan-output-handles", childIdentityPath],
                _root,
                UseShellExecute: false,
                Environment: null)
            {
                Timeout = TimeSpan.FromMilliseconds(1_500),
                MaximumOutputBytes = 64 * 1024,
            })));

        var completedWithinBound = await Task.WhenAny(
            invocation,
            Task.Delay(TimeSpan.FromSeconds(3))) == invocation;
        if (!completedWithinBound && File.Exists(childIdentityPath))
        {
            using var identity = System.Text.Json.JsonDocument.Parse(
                File.ReadAllBytes(childIdentityPath));
            KillTestProcess(identity.RootElement.GetProperty("ProcessId").GetInt32());
            _ = await Task.WhenAny(invocation, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        watch.Stop();

        Assert.True(completedWithinBound, "runner exceeded its total command deadline");
        var error = Assert.IsType<ProductionPayloadBuildException>(await invocation);
        Assert.Equal("production-build-process-timeout", error.ReasonCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(File.Exists(childIdentityPath));
        using var childIdentity = System.Text.Json.JsonDocument.Parse(
            File.ReadAllBytes(childIdentityPath));
        AssertExactProcessExited(
            childIdentity.RootElement.GetProperty("ProcessId").GetInt32(),
            childIdentity.RootElement.GetProperty("CreationTimeUtcTicks").GetInt64());
    }

    [Fact]
    public void ManifestVerifyUsesUniqueOwnedProfileAndLeavesZeroResidueOnFailure()
    {
        var root = Path.Combine(_root, "invalid-manifest-root");
        Directory.CreateDirectory(root);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        Assert.Throws<ProductionPayloadValidationException>(() =>
            builder.VerifyManifestWithTemporaryProfile(
                root,
                new ProductionReleaseCatalog(
                    true,
                    new string('a', 64),
                    "test")));
        Assert.Empty(Directory.EnumerateDirectories(
            _root,
            "payload-tool-profile-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void WorkspaceReparseMaterializesOnlyWhenItsCanonicalTargetStaysInsideBuildRoot()
    {
        var build = Path.Combine(_root, "links", "build");
        var inside = Path.Combine(build, "packages", "workspace");
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(external);
        Write(inside, "package.json", "{\"name\":\"workspace\"}");
        Write(inside, "dist/index.js", "export {};");
        Write(inside, "src/secret.ts", "do not copy");
        var internalLink = Path.Combine(build, "internal-link");
        var externalLink = Path.Combine(build, "external-link");
        Directory.CreateSymbolicLink(internalLink, inside);
        Directory.CreateSymbolicLink(externalLink, external);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var destination = Path.Combine(_root, "materialized");
        builder.MaterializeInternalLink(build, internalLink, destination);
        Assert.True(File.Exists(Path.Combine(destination, "package.json")));
        Assert.True(File.Exists(Path.Combine(destination, "dist", "index.js")));
        Assert.False(Directory.Exists(Path.Combine(destination, "src")));
        Assert.False(new DirectoryInfo(destination).Attributes.HasFlag(FileAttributes.ReparsePoint));
        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.MaterializeInternalLink(build, externalLink, Path.Combine(_root, "escaped")));
        Assert.Equal("production-build-link-outside-root", error.ReasonCode);
    }

    [Fact]
    public void NestedWorkspaceDirectoryReparseIsRejectedEvenWhenTargetStaysInsideBuildRoot()
    {
        var fixture = CreateClosureFixture();
        Write(fixture.BuildRoot, "packages/workspace-support/index.js", "do not project");
        Directory.CreateSymbolicLink(
            Path.Combine(fixture.BuildRoot, "packages", "twenty-shared", "dist", "nested-link"),
            Path.Combine(fixture.BuildRoot, "packages", "workspace-support"));
        var payload = Path.Combine(_root, "nested-workspace-link-payload");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                payload,
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-build-reparse-point", error.ReasonCode);
        Assert.False(Directory.Exists(payload));
    }

    [Fact]
    public void NestedRuntimeDependencyReparseCannotMasqueradeAsApprovedWorkspaceAlias()
    {
        var fixture = CreateClosureFixture();
        Directory.CreateSymbolicLink(
            Path.Combine(fixture.BuildRoot, "node_modules", "runtime", "nested-link"),
            Path.Combine(fixture.BuildRoot, "packages", "twenty-shared"));
        var payload = Path.Combine(_root, "nested-runtime-dependency-link-payload");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                payload,
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-build-reparse-point", error.ReasonCode);
        Assert.False(Directory.Exists(payload));
    }

    [Fact]
    public void RuntimeDependencyCopyRejectsExcessiveTreeDepth()
    {
        var fixture = CreateClosureFixture();
        var current = Path.Combine(fixture.BuildRoot, "node_modules", "runtime");
        for (var index = 0; index < 140; index++)
        {
            current = Path.Combine(current, "d");
            Directory.CreateDirectory(current);
        }
        File.WriteAllText(Path.Combine(current, "index.js"), "module.exports = {};\n");
        var payload = Path.Combine(_root, "runtime-depth-budget-payload");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                payload,
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-build-traversal-budget", error.ReasonCode);
        Assert.False(Directory.Exists(payload));
    }

    [Fact]
    public void WorkspaceLinksBecomePackageAndDistOnlyAtBothRuntimeLocations()
    {
        var fixture = CreateClosureFixture();
        Write(fixture.BuildRoot, "packages/twenty-shared/src/secret.ts", "do not copy");
        Write(fixture.BuildRoot, "packages/twenty-shared/test/secret.test.ts", "do not copy");
        Write(fixture.BuildRoot, "packages/twenty-shared/.env", "SECRET=forbidden");
        Directory.CreateSymbolicLink(
            Path.Combine(fixture.BuildRoot, "node_modules", "twenty-shared"),
            Path.Combine(fixture.BuildRoot, "packages", "twenty-shared"));
        var payload = Path.Combine(_root, "workspace-projection");
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true));

        foreach (var root in new[]
                 {
                     Path.Combine(payload, "packages", "twenty-shared"),
                     Path.Combine(payload, "node_modules", "twenty-shared"),
                 })
        {
            Assert.True(File.Exists(Path.Combine(root, "package.json")));
            Assert.True(File.Exists(Path.Combine(root, "dist", "index.js")));
            Assert.False(Directory.Exists(Path.Combine(root, "src")));
            Assert.False(Directory.Exists(Path.Combine(root, "test")));
            Assert.False(File.Exists(Path.Combine(root, ".env")));
            Assert.False(new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
    }

    [Theory]
    [InlineData("twenty-server")]
    [InlineData("twenty-emails")]
    [InlineData("twenty-shared")]
    [InlineData("twenty-client-sdk")]
    public void PhysicalRuntimeWorkspaceAliasesProjectOnlyPackageAndDist(string workspace)
    {
        var fixture = CreateClosureFixture();
        var runtimeWorkspace = Path.Combine(
            fixture.BuildRoot,
            "node_modules",
            workspace);
        Write(runtimeWorkspace, "package.json", $"{{\"name\":\"{workspace}\"}}");
        if (StringComparer.Ordinal.Equals(workspace, "twenty-server"))
        {
            Write(runtimeWorkspace, "dist/main.js", "module.exports = {};");
            Write(
                runtimeWorkspace,
                "dist/front/index.html",
                """
                <script id="twenty-env-config">
                  window._env_ = {
                    // This will be overwritten
                  };
                </script>
                """);
        }
        else
        {
            Write(runtimeWorkspace, "dist/index.js", "module.exports = {};");
        }
        Write(runtimeWorkspace, "src/secret.ts", "do not copy");
        Write(runtimeWorkspace, "tests/secret.test.ts", "do not copy");
        Write(runtimeWorkspace, ".env", "SECRET=forbidden");
        var payload = Path.Combine(_root, "physical-workspace-" + workspace);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        builder.Materialize(new ProductionPayloadMaterializationRequest(
            fixture.BuildRoot,
            payload,
            fixture.NodeExecutable,
            fixture.InventoryPath,
            fixture.Header,
            ClosureOnly: true));

        foreach (var root in new[]
                 {
                     Path.Combine(payload, "packages", workspace),
                     Path.Combine(payload, "node_modules", workspace),
                 })
        {
            Assert.True(File.Exists(Path.Combine(root, "package.json")));
            Assert.True(Directory.Exists(Path.Combine(root, "dist")));
            Assert.False(Directory.Exists(Path.Combine(root, "src")));
            Assert.False(Directory.Exists(Path.Combine(root, "tests")));
            Assert.False(File.Exists(Path.Combine(root, ".env")));
            Assert.False(new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
    }

    [Theory]
    [InlineData("missing-package")]
    [InlineData("missing-dist")]
    [InlineData("case-alias")]
    public void PhysicalRuntimeWorkspaceAliasRequiresExactProjectionIdentity(string mutation)
    {
        var fixture = CreateClosureFixture();
        var directoryName = StringComparer.Ordinal.Equals(mutation, "case-alias")
            ? "Twenty-Shared"
            : "twenty-shared";
        var runtimeWorkspace = Path.Combine(
            fixture.BuildRoot,
            "node_modules",
            directoryName);
        Directory.CreateDirectory(runtimeWorkspace);
        if (!StringComparer.Ordinal.Equals(mutation, "missing-package"))
        {
            Write(runtimeWorkspace, "package.json", "{\"name\":\"twenty-shared\"}");
        }
        if (!StringComparer.Ordinal.Equals(mutation, "missing-dist"))
        {
            Write(runtimeWorkspace, "dist/index.js", "module.exports = {};");
        }
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.Materialize(new ProductionPayloadMaterializationRequest(
                fixture.BuildRoot,
                Path.Combine(_root, "physical-workspace-invalid-" + mutation),
                fixture.NodeExecutable,
                fixture.InventoryPath,
                fixture.Header,
                ClosureOnly: true)));

        Assert.Equal("production-workspace-projection-invalid", error.ReasonCode);
    }

    [Fact]
    public void StagedTwentySdkNormalizationIsExactAtomicAndLeavesSourceBytesUnchanged()
    {
        var repository = FindRepositoryRoot();
        var source = Path.Combine(repository, "packages", "twenty-sdk", "project.json");
        var sourceBefore = File.ReadAllBytes(source);
        var sourceDigest = Convert.ToHexString(SHA256.HashData(sourceBefore));
        var build = Path.Combine(_root, "sdk-normalization", "build");
        var staged = Path.Combine(build, "packages", "twenty-sdk", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllBytes(staged, sourceBefore);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var result = builder.NormalizeStagedTwentySdkProject(build, source);

        Assert.Equal(sourceDigest, result.PreimageSha256);
        Assert.Equal(
            "142C0322F8C626656B242FC35A32807F50B59C0C2B039F391813C9248E80AC28",
            result.OutputSha256);
        Assert.Equal(sourceBefore, File.ReadAllBytes(source));
        using var changed = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(staged));
        var buildCommand = changed.RootElement.GetProperty("targets").GetProperty("build")
            .GetProperty("options").GetProperty("commands")[2].GetString();
        var devCommand = changed.RootElement.GetProperty("targets").GetProperty("dev")
            .GetProperty("options").GetProperty("command").GetString();
        Assert.Equal(
            @"..\..\.phase1cb-toolchain\node.exe ..\..\.phase1cb-toolchain\promote-sdk-declarations.mjs",
            buildCommand);
        Assert.Contains("'dist/sdk'", devCommand, StringComparison.Ordinal);
        Assert.Equal(2, result.ReplacedCommandCount);
        Assert.Equal(1, result.AddedDependencyCount);
        Assert.Equal(
            "C145FB95A0EAD566C1F6FDC3406B04EFB538D398B09382F5B391DA93D1ADE199",
            result.HelperSha256);
        var helper = Path.Combine(
            build,
            ".phase1cb-toolchain",
            "promote-sdk-declarations.mjs");
        Assert.True(File.Exists(helper));
        Assert.DoesNotContain(build, File.ReadAllText(helper), StringComparison.OrdinalIgnoreCase);
        var buildSdk = changed.RootElement.GetProperty("targets").GetProperty("build:sdk");
        Assert.Equal(
            new[] { "^build", "build" },
            buildSdk.GetProperty("dependsOn").EnumerateArray().Select(item => item.GetString()));
        var buildSdkCommand = buildSdk.GetProperty("options").GetProperty("command").GetString();
        Assert.Equal(
            "npx vite build -c vite.config.define.ts && npx vite build -c vite.config.billing.ts && npx vite build -c vite.config.front-component.ts && npx vite build -c vite.config.logic-function.ts && npx vite build -c vite.config.utils.ts",
            buildSdkCommand);
        Assert.DoesNotContain("rollup", buildSdkCommand, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(staged)!,
            "*.tmp-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void StagedTwentySdkDeclarationHelperPromotesAllFiveTreesAndPreservesViteOutputs()
    {
        var repository = FindRepositoryRoot();
        var source = Path.Combine(repository, "packages", "twenty-sdk", "project.json");
        var build = Path.Combine(_root, "sdk-promotion", "build");
        var staged = Path.Combine(build, "packages", "twenty-sdk", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(source, staged);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());
        builder.NormalizeStagedTwentySdkProject(build, source);

        var sdkRoot = Path.Combine(build, "packages", "twenty-sdk");
        foreach (var publicExport in new[]
                 {
                     "billing", "define", "front-component", "logic-function", "utils",
                 })
        {
            Write(sdkRoot, $"dist/{publicExport}/index.mjs", $"export const name = '{publicExport}';");
            Write(sdkRoot, $"dist/sdk/{publicExport}/index.d.ts", "export type Marker = string;");
            Write(sdkRoot, $"dist/sdk/{publicExport}/nested/value.d.ts", "export type Value = number;");
        }
        Write(
            sdkRoot,
            "dist/sdk/front-component/index.d.ts",
            "export type { OpenCommandConfirmationModalFunction } from './globals/frontComponentHostCommunicationApi';");
        Write(
            sdkRoot,
            "dist/sdk/front-component/globals/frontComponentHostCommunicationApi.d.ts",
            "export type OpenCommandConfirmationModalFunction = (params: { title: string }) => Promise<'confirm' | 'cancel'>;");

        var result = RunNode(
            Path.Combine(build, ".phase1cb-toolchain", "promote-sdk-declarations.mjs"),
            sdkRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(sdkRoot, "dist", "sdk")));
        foreach (var publicExport in new[]
                 {
                     "billing", "define", "front-component", "logic-function", "utils",
                 })
        {
            Assert.True(File.Exists(Path.Combine(sdkRoot, "dist", publicExport, "index.mjs")));
            Assert.True(File.Exists(Path.Combine(sdkRoot, "dist", publicExport, "index.d.ts")));
            Assert.True(File.Exists(Path.Combine(sdkRoot, "dist", publicExport, "nested", "value.d.ts")));
        }
        var frontIndex = Path.Combine(sdkRoot, "dist", "front-component", "index.d.ts");
        var relativeContract = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(frontIndex)!,
            "globals",
            "frontComponentHostCommunicationApi.d.ts"));
        Assert.True(File.Exists(relativeContract));
        Assert.Contains("OpenCommandConfirmationModalFunction", File.ReadAllText(relativeContract));
        Assert.DoesNotContain(build, File.ReadAllText(frontIndex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StagedTwentySdkDeclarationHelperRejectsReparseTraversalBeforeWriting()
    {
        var repository = FindRepositoryRoot();
        var source = Path.Combine(repository, "packages", "twenty-sdk", "project.json");
        var build = Path.Combine(_root, "sdk-promotion-reparse", "build");
        var staged = Path.Combine(build, "packages", "twenty-sdk", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(source, staged);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());
        builder.NormalizeStagedTwentySdkProject(build, source);

        var sdkRoot = Path.Combine(build, "packages", "twenty-sdk");
        foreach (var publicExport in new[]
                 {
                     "billing", "define", "front-component", "logic-function", "utils",
                 })
        {
            Write(sdkRoot, $"dist/sdk/{publicExport}/index.d.ts", "export type Marker = string;");
            Write(sdkRoot, $"dist/{publicExport}/index.mjs", "export {};\n");
        }
        var external = Path.Combine(_root, "external-sdk-declarations");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "escaped.d.ts"), "export type Escaped = true;");
        Directory.CreateSymbolicLink(
            Path.Combine(sdkRoot, "dist", "sdk", "front-component", "escaped"),
            external);

        var result = RunNode(
            Path.Combine(build, ".phase1cb-toolchain", "promote-sdk-declarations.mjs"),
            sdkRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("phase1cb-sdk-declarations-reparse", result.StandardError);
        Assert.True(Directory.Exists(Path.Combine(sdkRoot, "dist", "sdk")));
        Assert.Equal("export {};\n", File.ReadAllText(Path.Combine(
            sdkRoot,
            "dist",
            "front-component",
            "index.mjs")));
        Assert.False(File.Exists(Path.Combine(
            sdkRoot,
            "dist",
            "front-component",
            "escaped",
            "escaped.d.ts")));
    }

    [Fact]
    public void StagedTwentySdkDeclarationHelperRejectsStalePublicDeclarationsBeforeWriting()
    {
        var repository = FindRepositoryRoot();
        var source = Path.Combine(repository, "packages", "twenty-sdk", "project.json");
        var build = Path.Combine(_root, "sdk-promotion-stale", "build");
        var staged = Path.Combine(build, "packages", "twenty-sdk", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(source, staged);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());
        builder.NormalizeStagedTwentySdkProject(build, source);

        var sdkRoot = Path.Combine(build, "packages", "twenty-sdk");
        foreach (var publicExport in new[]
                 {
                     "billing", "define", "front-component", "logic-function", "utils",
                 })
        {
            Write(sdkRoot, $"dist/sdk/{publicExport}/index.d.ts", "export type Marker = string;");
            Write(sdkRoot, $"dist/{publicExport}/index.mjs", "export {};\n");
        }
        Write(
            sdkRoot,
            "dist/front-component/stale.d.ts",
            "export type Stale = true;");

        var result = RunNode(
            Path.Combine(build, ".phase1cb-toolchain", "promote-sdk-declarations.mjs"),
            sdkRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("phase1cb-sdk-declarations-destination-not-clean", result.StandardError);
        Assert.True(Directory.Exists(Path.Combine(sdkRoot, "dist", "sdk")));
        Assert.False(File.Exists(Path.Combine(
            sdkRoot,
            "dist",
            "billing",
            "index.d.ts")));
    }

    [Fact]
    public void StagedTwentySdkNormalizationRejectsSourceOrCommandDriftWithoutWriting()
    {
        var repository = FindRepositoryRoot();
        var source = Path.Combine(repository, "packages", "twenty-sdk", "project.json");
        var build = Path.Combine(_root, "sdk-drift", "build");
        var staged = Path.Combine(build, "packages", "twenty-sdk", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        var drifted = File.ReadAllText(source).Replace("'dist/sdk'", "'dist/other'", StringComparison.Ordinal);
        File.WriteAllText(staged, drifted, new UTF8Encoding(false));
        var before = File.ReadAllBytes(staged);
        var builder = new ProductionPayloadBuilder(
            new RecordingRunner(),
            new AcceptedNodeTrust());

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            builder.NormalizeStagedTwentySdkProject(build, source));

        Assert.Equal("production-windows-build-normalization-drift", error.ReasonCode);
        Assert.Equal(before, File.ReadAllBytes(staged));
    }

    [Fact]
    public void PowerShellSourceInventoryIsNullDelimitedStreamingAndBoundedBeforeCopy()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));

        Assert.DoesNotContain("$paths = & git", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$status = @(& git", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-StreamingGitInventory", script, StringComparison.Ordinal);
        Assert.Contains("ls-files", script, StringComparison.Ordinal);
        Assert.Contains("'status', '--porcelain=v2', '-z', '--untracked-files=all'", script, StringComparison.Ordinal);
        Assert.Contains("'-z'", script, StringComparison.Ordinal);
        Assert.Contains("250000", script, StringComparison.Ordinal);
        Assert.Contains("17179869184", script, StringComparison.Ordinal);
        Assert.Contains("relativeDepth -gt 64", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellGitIsNeutralJobSupervisedAndUsesBatchBlobVerification()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));

        Assert.DoesNotContain("& git", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEndAsync", script, StringComparison.Ordinal);
        Assert.DoesNotContain("rev-parse \"$ExpectedCommit`:$relative\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("hash-object --path=$relative", script, StringComparison.Ordinal);
        Assert.Contains("LaunchSuspendedAssigned", script, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_NOSYSTEM=1", script, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_GLOBAL=NUL", script, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_COUNT=0", script, StringComparison.Ordinal);
        Assert.Contains("'ls-files', '--stage', '-z'", script, StringComparison.Ordinal);
        Assert.Contains("Get-GitBlobSha1", script, StringComparison.Ordinal);
        Assert.Contains("phase1cb-git-stderr-limit", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellGitIgnoresAmbientCanaries()
    {
        var repository = FindRepositoryRoot();
        var sourceScript = File.ReadAllText(Path.Combine(
            repository,
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        var boundary = sourceScript.IndexOf(
            "try {\n    Clear-BuildInjectionEnvironment",
            StringComparison.Ordinal);
        Assert.True(boundary > 0);
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "neutral-git-head.txt");
        var config = Path.Combine(_root, "hostile-git.config");
        var fsmonitor = Path.Combine(_root, "fsmonitor-canary.cmd");
        File.WriteAllText(config, "[core]\n\tfsmonitor = " + fsmonitor.Replace('\\', '/') + "\n");
        File.WriteAllText(fsmonitor, "@echo hostile>\"%~dp0fsmonitor-ran\"\r\n", new UTF8Encoding(false));
        var fakeBin = Path.Combine(_root, "fake-git-bin");
        Directory.CreateDirectory(fakeBin);
        File.WriteAllText(
            Path.Combine(fakeBin, "git.cmd"),
            "@echo ambient-git-ran>\"%~dp0ambient-git-ran\"\r\n@exit /b 91\r\n",
            new UTF8Encoding(false));
        var exactGit = FindExecutable("git.exe");
        var harness = Path.Combine(_root, "neutral-git-harness.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..boundary] + Environment.NewLine +
            "$env:GIT_DIR='Z:\\ambient-invalid-git-dir'" + Environment.NewLine +
            "$env:GIT_WORK_TREE='Z:\\ambient-invalid-work-tree'" + Environment.NewLine +
            "$env:GIT_INDEX_FILE='Z:\\ambient-invalid-index'" + Environment.NewLine +
            "$env:GIT_OBJECT_DIRECTORY='Z:\\ambient-invalid-objects'" + Environment.NewLine +
            "$env:GIT_ALTERNATE_OBJECT_DIRECTORIES='Z:\\ambient-invalid-alternates'" + Environment.NewLine +
            $"$env:GIT_CONFIG_GLOBAL='{PowerShellLiteral(config)}'" + Environment.NewLine +
            "$env:GIT_CONFIG_COUNT='1'" + Environment.NewLine +
            "$env:GIT_CONFIG_KEY_0='core.fsmonitor'" + Environment.NewLine +
            $"$env:GIT_CONFIG_VALUE_0='{PowerShellLiteral(fsmonitor)}'" + Environment.NewLine +
            $"$env:PATH='{PowerShellLiteral(fakeBin)};' + $env:PATH" + Environment.NewLine +
            $"[IO.File]::WriteAllText('{PowerShellLiteral(output)}',(Get-SourceHead -SourceRoot '{PowerShellLiteral(repository)}' -GitExecutable '{PowerShellLiteral(exactGit)}'))" + Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            RunProcess(FindExecutable("git.exe"), repository, ["rev-parse", "--verify", "HEAD"]).StandardOutput.Trim(),
            File.ReadAllText(output));
        Assert.False(File.Exists(Path.Combine(_root, "fsmonitor-ran")));
        Assert.False(File.Exists(Path.Combine(fakeBin, "ambient-git-ran")));
    }

    [Fact]
    public void PowerShellGitCapsConcurrentStderrStreaming()
    {
        var sourceScript = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        var boundary = sourceScript.IndexOf(
            "try {\n    Clear-BuildInjectionEnvironment",
            StringComparison.Ordinal);
        Assert.True(boundary > 0);
        Directory.CreateDirectory(_root);
        var harness = Path.Combine(_root, "git-stderr-flood.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..boundary] + Environment.NewLine +
            "try {" + Environment.NewLine +
            $" Invoke-BoundedGitRecords -SourceRoot '{PowerShellLiteral(_root)}' -GitExecutable '{PowerShellLiteral(FindExecutable("powershell.exe"))}' -RawArguments -GitArguments @('-NoProfile','-NonInteractive','-Command','[Console]::Error.Write((''x''*70000))') -OnRecord {{}} -TimeoutSeconds 10" + Environment.NewLine +
            " exit 0" + Environment.NewLine +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }" + Environment.NewLine,
            new UTF8Encoding(false));

        var watch = Stopwatch.StartNew();
        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);
        watch.Stop();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("phase1cb-git-stderr-limit", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void PowerShellGitJobTerminatesExactDescendant()
    {
        var sourceScript = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        var boundary = sourceScript.IndexOf(
            "try {\n    Clear-BuildInjectionEnvironment",
            StringComparison.Ordinal);
        Assert.True(boundary > 0);
        Directory.CreateDirectory(_root);
        var identityPath = Path.Combine(_root, "git-descendant-identity.json");
        var fixture = Path.Combine(AppContext.BaseDirectory, "HowardLab.EbayCrm.AppHost.Fixture.exe");
        var harness = Path.Combine(_root, "git-descendant-job.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..boundary] + Environment.NewLine +
            "try {" + Environment.NewLine +
            $" Invoke-BoundedGitRecords -SourceRoot '{PowerShellLiteral(_root)}' -GitExecutable '{PowerShellLiteral(fixture)}' -RawArguments -GitArguments @('orphan-output-handles','{PowerShellLiteral(identityPath)}') -OnRecord {{}} -TimeoutSeconds 1" + Environment.NewLine +
            " exit 0" + Environment.NewLine +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }" + Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("phase1cb-source-inventory-timeout", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(identityPath));
        using var identity = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(identityPath));
        AssertExactProcessExited(
            identity.RootElement.GetProperty("ProcessId").GetInt32(),
            identity.RootElement.GetProperty("CreationTimeUtcTicks").GetInt64());
    }

    [Fact]
    public void PowerShellLauncherUsesOnlyExplicitStandardHandleList()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));

        Assert.Contains("StartupInfoEx", script, StringComparison.Ordinal);
        Assert.Contains("ProcThreadAttributeHandleList", script, StringComparison.Ordinal);
        Assert.Contains("ExtendedStartupInfoPresent", script, StringComparison.Ordinal);
        Assert.Contains("InitializeProcThreadAttributeList", script, StringComparison.Ordinal);
        Assert.Contains("UpdateProcThreadAttribute", script, StringComparison.Ordinal);
        Assert.Contains("DeleteProcThreadAttributeList", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellLauncherDoesNotLeakUnrelatedInheritableHandle()
    {
        var sourceScript = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        var boundary = sourceScript.IndexOf(
            "try {\n    Clear-BuildInjectionEnvironment",
            StringComparison.Ordinal);
        Assert.True(boundary > 0);
        Directory.CreateDirectory(_root);
        var marker = Path.Combine(_root, "inherited-handle-marker");
        var fixture = Path.Combine(AppContext.BaseDirectory, "HowardLab.EbayCrm.AppHost.Fixture.exe");
        var harness = Path.Combine(_root, "explicit-handle-list.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..boundary] + Environment.NewLine +
            "Add-Type -TypeDefinition @'" + Environment.NewLine +
            "using System; using System.Runtime.InteropServices;" + Environment.NewLine +
            "public static class InheritableSentinel {" + Environment.NewLine +
            " [StructLayout(LayoutKind.Sequential)] struct SA { public int n; public IntPtr d; [MarshalAs(UnmanagedType.Bool)] public bool i; }" + Environment.NewLine +
            " [DllImport(\"kernel32.dll\",SetLastError=true)] static extern IntPtr CreateEvent(ref SA a,bool m,bool s,string n);" + Environment.NewLine +
            " [DllImport(\"kernel32.dll\")] public static extern bool CloseHandle(IntPtr h);" + Environment.NewLine +
            " public static IntPtr Create(){var a=new SA{n=Marshal.SizeOf(typeof(SA)),i=true};return CreateEvent(ref a,false,false,null);}" + Environment.NewLine +
            "}" + Environment.NewLine +
            "'@" + Environment.NewLine +
            "$sentinel=[InheritableSentinel]::Create()" + Environment.NewLine +
            "try {" + Environment.NewLine +
            $" Invoke-BoundedGitRecords -SourceRoot '{PowerShellLiteral(_root)}' -GitExecutable '{PowerShellLiteral(fixture)}' -RawArguments -GitArguments @('probe-inherited-handle',([long]$sentinel).ToString(),'{PowerShellLiteral(marker)}') -OnRecord {{}} -TimeoutSeconds 10" + Environment.NewLine +
            "} finally { [InheritableSentinel]::CloseHandle($sentinel) | Out-Null }" + Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void BuildCommandsPinAllDependencyCacheAndOutputRootsInsideTheStagingRoot()
    {
        var build = Path.Combine(_root, "resolution-ledger-build");
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var runner = new RecordingRunner();

        new ProductionPayloadBuilder(
                runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
                new AcceptedMinGitProbe())
            .RunBuildCommands(node, minGit, build, compiled);

        Assert.All(runner.Invocations, invocation =>
        {
            Assert.Equal(Path.Combine(build, ".phase1cb-cache", "yarn"), invocation.Environment!["YARN_CACHE_FOLDER"]);
            Assert.Equal(Path.Combine(build, ".phase1cb-cache", "nx"), invocation.Environment["NX_CACHE_DIRECTORY"]);
            Assert.Equal(Path.Combine(build, ".phase1cb-cache", "nx-workspace"), invocation.Environment["NX_WORKSPACE_DATA_DIRECTORY"]);
            Assert.Equal("false", invocation.Environment["YARN_ENABLE_GLOBAL_CACHE"]);
        });
        var ledger = Path.Combine(build, ".phase1cb-resolution-ledger-v1.json");
        Assert.True(File.Exists(ledger));
        var text = File.ReadAllText(ledger);
        Assert.Equal(
            "{\"schemaVersion\":1," +
            "\"ancestorNodeModules\":\"rejected\"," +
            "\"dependencyRoot\":\"node_modules\"," +
            "\"initialInstall\":\"full-non-production\"," +
            "\"immutableInstalls\":true," +
            "\"buildOnlyMinGit\":{" +
            "\"version\":\"2.55.0.windows.2\"," +
            "\"archiveSha256\":\"E3EA2944CEA4B3FABCD69C7C1669EF69B1B66C05AC7806D81224D0ABAD2DEC31\"," +
            "\"executableSha256\":\"22FEAD8244EF3A7225FB800099A4E43ECA8BCEC0466774917669599C2F19A05A\"," +
            "\"executablePath\":\".phase1cb-toolchain/mingit/cmd/git.exe\"," +
            "\"locatorUrl\":\"https://github.com/electron/node-gyp.git\"," +
            "\"locatorCommit\":\"06b29aafb7708acef8b3669835c8a7857ebc92d2\"," +
            "\"lockBinding\":\"exact-yarn-lock-entry\"," +
            "\"canary\":\"supervised-depth-1-exact-commit-fetch\"," +
            "\"excludedLocatorCachePath\":\".phase1cb-cache/yarn/@electron-node-gyp-https-d0f303c37e-e8c97bb534.zip\"," +
            "\"excludedLocatorCacheDisposition\":\"must-be-absent\"," +
            "\"payloadDisposition\":\"excluded\"}," +
            "\"yarnCacheRoot\":\".phase1cb-cache/yarn\"," +
            "\"nxCacheRoot\":\".phase1cb-cache/nx\"," +
            "\"nxWorkspaceDataRoot\":\".phase1cb-cache/nx-workspace\"," +
            "\"gitTraceRoot\":\".phase1cb-cache/git-trace\"," +
            "\"gitProbeRoot\":\".phase1cb-cache/git-probe\"," +
            "\"compiledOutputRoot\":\"desktop/windows/node/publish\"}",
            text);
        Assert.DoesNotContain(FindRepositoryRoot(), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCommandsRejectAncestorNodeModulesBeforeAnyRunnerInvocation()
    {
        var ancestor = Path.Combine(_root, "ancestor-resolution");
        var build = Path.Combine(ancestor, "staging", "build");
        var node = Path.Combine(build, ".phase1cb-toolchain", "node.exe");
        Directory.CreateDirectory(Path.Combine(ancestor, "node_modules"));
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        File.WriteAllText(node, "node");
        var minGit = CreateFakeMinGit(build);
        var runner = new RecordingRunner();

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(
                    runner, new AcceptedNodeTrust(), new AcceptedMinGitTrust(),
                    new AcceptedMinGitProbe()).RunBuildCommands(
                node,
                minGit,
                build,
                Path.Combine(build, "desktop", "windows", "node", "publish")));

        Assert.Equal("production-build-ancestor-node-modules", error.ReasonCode);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void PowerShellBuildsOutsideCheckoutThenRelocatesExactFinalLayoutAndCleansStaging()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));

        Assert.Contains("Join-Path ([IO.Path]::GetPathRoot($repository)) 'HowardLabPhase1CBStaging'", script, StringComparison.Ordinal);
        Assert.Contains("Test-StagingParentWritableAndIsolated", script, StringComparison.Ordinal);
        Assert.Contains(".phase1cb-write-probe-", script, StringComparison.Ordinal);
        Assert.Contains("$stream.Flush($true)", script, StringComparison.Ordinal);
        Assert.Contains("$finalBuildRoot = Join-Path $output 'build'", script, StringComparison.Ordinal);
        Assert.Contains("$payloadRoot = Join-Path $output 'payload'", script, StringComparison.Ordinal);
        Assert.Contains("$catalogRoot = Join-Path $output 'catalog'", script, StringComparison.Ordinal);
        Assert.Contains("$appHostRoot = Join-Path $output 'apphost'", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($buildRoot, $finalBuildRoot)", script, StringComparison.Ordinal);
        Assert.Contains("Remove-OwnedOutputRoot -Path $stagingRoot -AllowedRoot $stagingParent", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellUsesExternallyHashBoundSingleFilePayloadToolOnly()
    {
        var repository = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            repository,
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        var project = File.ReadAllText(Path.Combine(
            repository,
            "desktop", "windows", "tools", "HowardLab.EbayCrm.PayloadTool",
            "HowardLab.EbayCrm.PayloadTool.csproj"));

        Assert.Contains("$PayloadToolExePath", script, StringComparison.Ordinal);
        Assert.Contains("$PayloadToolSha256", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$PayloadToolDllPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet exec", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'exec',", script, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", project, StringComparison.Ordinal);
        Assert.Contains("<SelfContained>false</SelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", project, StringComparison.Ordinal);
        Assert.Contains("<DebugSymbols>false</DebugSymbols>", project, StringComparison.Ordinal);
        Assert.Contains("<DebugType>None</DebugType>", project, StringComparison.Ordinal);
        Assert.Contains("<RestoreLockedMode>true</RestoreLockedMode>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("'restore', $payloadToolProject", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'run', '--no-restore'", script, StringComparison.Ordinal);
        Assert.Contains("CreateKillOnClose", script, StringComparison.Ordinal);
        Assert.Contains("AssignAndVerify", script, StringComparison.Ordinal);
        Assert.Contains("TerminateAndVerifyEmpty", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadToolProjectPublishesExactlyOneFrameworkDependentSingleFileExecutable()
    {
        var repository = FindRepositoryRoot();
        var publishScript = File.ReadAllText(Path.Combine(
            repository,
            "desktop", "windows", "scripts", "Publish-Phase1CBPayloadTool.ps1"));

        Assert.Contains("publish $project", publishScript, StringComparison.Ordinal);
        Assert.Contains("-c Release", publishScript, StringComparison.Ordinal);
        Assert.Contains("-r win-x64", publishScript, StringComparison.Ordinal);
        Assert.Contains("--self-contained false", publishScript, StringComparison.Ordinal);
        Assert.Contains("--no-restore", publishScript, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=true", publishScript, StringComparison.Ordinal);
        Assert.Contains("-p:DebugSymbols=false", publishScript, StringComparison.Ordinal);
        Assert.Contains("-p:DebugType=None", publishScript, StringComparison.Ordinal);
        Assert.Contains("$entries.Count -ne 1", publishScript, StringComparison.Ordinal);
        Assert.Contains("HowardLab.EbayCrm.PayloadTool.exe", publishScript, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain(".dll'", publishScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".deps.json", publishScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".runtimeconfig.json", publishScript, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PowerShellPayloadToolArtifactRejectsMutationBeforeEveryLaunch(int mutationLaunch)
    {
        var repository = FindRepositoryRoot();
        var sourceScript = File.ReadAllText(Path.Combine(
            repository,
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        var boundary = sourceScript.IndexOf(
            "try {\n    Clear-BuildInjectionEnvironment",
            StringComparison.Ordinal);
        Assert.True(boundary > 0);
        var source = Path.Combine(_root, "payload-tool-source", "HowardLab.EbayCrm.PayloadTool.exe");
        var staged = Path.Combine(_root, "payload-tool-private");
        var stagedMarker = Path.Combine(_root, $"payload-tool-staged-{mutationLaunch}");
        var launchMarker = Path.Combine(_root, $"payload-tool-launch-{mutationLaunch}");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "trusted-single-file-bundle");
        foreach (var formerLoadInput in new[]
                 {
                     "HowardLab.EbayCrm.PayloadTool.deps.json",
                     "HowardLab.EbayCrm.PayloadTool.runtimeconfig.json",
                     "HowardLab.EbayCrm.AppHost.Windows.dll",
                 })
        {
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(source)!, formerLoadInput),
                "mutated-before-stage");
        }
        var mainHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var harness = Path.Combine(_root, "payload-tool-artifact-mutation.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..boundary] + Environment.NewLine +
            $"$tool = New-PayloadToolArtifact -SourceExe '{PowerShellLiteral(source)}' -ExpectedExeSha256 '{mainHash}' -DestinationRoot '{PowerShellLiteral(staged)}'" + Environment.NewLine +
            $"[IO.File]::WriteAllText('{PowerShellLiteral(stagedMarker)}','staged')" + Environment.NewLine +
            "try {" + Environment.NewLine +
            " for ($launch = 1; $launch -le 3; $launch++) {" + Environment.NewLine +
            $"  if ($launch -eq {mutationLaunch}) {{ [IO.File]::WriteAllText($tool,'mutated') }}" + Environment.NewLine +
            "  Assert-PayloadToolArtifact -Path $tool -ExpectedSha256 '" + mainHash + "'" + Environment.NewLine +
            $"  if ($launch -eq {mutationLaunch}) {{ [IO.File]::WriteAllText('{PowerShellLiteral(launchMarker)}','launched') }}" + Environment.NewLine +
            " }" + Environment.NewLine +
            " exit 0" + Environment.NewLine +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }" + Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("phase1cb-payload-tool-artifact-invalid", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(stagedMarker));
        Assert.False(File.Exists(launchMarker));
    }

    [Fact]
    public void PowerShellRevalidatesPrivatePayloadToolArtifactBeforeEveryLaunch()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));

        Assert.Contains("New-PayloadToolArtifact", script, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(script, "Assert-PayloadToolArtifact -Path \\$payloadToolExe").Count);
        Assert.Equal(3, Regex.Matches(script, "Invoke-BoundedPayloadTool \\$payloadToolExe").Count);
        Assert.DoesNotContain(".deps.json", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".runtimeconfig.json", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload-tool-closure", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PayloadToolCatalogOutputRejectsRelativeAndReparseAncestorPaths()
    {
        Assert.Throws<InvalidDataException>(() =>
            PayloadToolPathPolicy.ValidateAvailableFile("relative-catalog.json"));
        var external = Path.Combine(_root, "catalog-external");
        var alias = Path.Combine(_root, "catalog-alias");
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(_root);
        Directory.CreateSymbolicLink(alias, external);

        Assert.Throws<InvalidDataException>(() =>
            PayloadToolPathPolicy.ValidateAvailableFile(Path.Combine(alias, "catalog.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(external));
    }

    [Fact]
    public void PayloadToolAtomicWriteCreatesEverySegmentWithoutFollowingReparsePoints()
    {
        var output = Path.Combine(_root, "catalog", "nested", "catalog.json");
        PayloadToolPathPolicy.WriteAtomic(output, Encoding.UTF8.GetBytes("trusted"));
        Assert.Equal("trusted", File.ReadAllText(output));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(output)!, "*.tmp-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void PayloadToolManifestCreateRejectsCatalogOverlapBeforeWriting()
    {
        var fixture = CreateClosureFixture();
        var root = Path.Combine(_root, "manifest-overlap-root");
        Directory.CreateDirectory(root);
        WritePayloadToolHeader(root, fixture.Header);
        File.WriteAllText(Path.Combine(root, "payload.txt"), "payload");
        var catalog = Path.Combine(root, "catalog.json");

        var result = RunPayloadTool(
            "manifest", "create", "--root", root, "--catalog-output", catalog);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(root, ProductionPayloadValidator.ManifestFileName)));
        Assert.False(File.Exists(catalog));
    }

    [Fact]
    public void PayloadToolManifestCreateRejectsHeaderJunctionBeforeWriting()
    {
        var fixture = CreateClosureFixture();
        var root = Path.Combine(_root, "manifest-header-junction-root");
        var external = Path.Combine(_root, "manifest-header-external.json");
        var catalog = Path.Combine(_root, "manifest-header-catalog.json");
        Directory.CreateDirectory(root);
        WritePayloadToolHeader(_root, fixture.Header, Path.GetFileName(external));
        File.CreateSymbolicLink(
            Path.Combine(root, "production-payload-header-v2.json"),
            external);
        File.WriteAllText(Path.Combine(root, "payload.txt"), "payload");

        var result = RunPayloadTool(
            "manifest", "create", "--root", root, "--catalog-output", catalog);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(root, ProductionPayloadValidator.ManifestFileName)));
        Assert.False(File.Exists(catalog));
    }

    [Fact]
    public void PayloadToolManifestCreateRejectsManifestReparseBeforeWriting()
    {
        var fixture = CreateClosureFixture();
        var root = Path.Combine(_root, "manifest-path-reparse-root");
        var external = Path.Combine(_root, "manifest-path-external.json");
        var catalog = Path.Combine(_root, "manifest-path-catalog.json");
        Directory.CreateDirectory(root);
        WritePayloadToolHeader(root, fixture.Header);
        File.WriteAllText(Path.Combine(root, "payload.txt"), "payload");
        File.WriteAllText(external, "sentinel");
        File.CreateSymbolicLink(
            Path.Combine(root, ProductionPayloadValidator.ManifestFileName),
            external);

        var result = RunPayloadTool(
            "manifest", "create", "--root", root, "--catalog-output", catalog);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("sentinel", File.ReadAllText(external));
        Assert.False(File.Exists(catalog));
    }

    [Fact]
    public void PayloadToolManifestCreateRollsBackWhenCatalogCommitFails()
    {
        var fixture = CreateClosureFixture();
        var root = Path.Combine(_root, "manifest-atomic-root");
        var catalog = Path.Combine(_root, "manifest-atomic-catalog.json");
        Directory.CreateDirectory(root);
        WritePayloadToolHeader(root, fixture.Header);
        File.WriteAllText(Path.Combine(root, "payload.txt"), "payload");
        File.WriteAllText(catalog, "original-catalog");
        (int ExitCode, string StandardOutput, string StandardError) result;
        using (File.Open(catalog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = RunPayloadTool(
                "manifest", "create", "--root", root, "--catalog-output", catalog);
        }

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(root, ProductionPayloadValidator.ManifestFileName)));
        Assert.Equal("original-catalog", File.ReadAllText(catalog));
    }

    [Fact]
    public void PowerShellTrackedSourceCopyRejectsIntermediateReparseBeforeDestinationWrite()
    {
        var repository = FindRepositoryRoot();
        var sourceScript = File.ReadAllText(Path.Combine(
            repository,
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        var boundary = sourceScript.IndexOf(
            "try {\n    Clear-BuildInjectionEnvironment",
            StringComparison.Ordinal);
        Assert.True(boundary > 0);
        var source = Path.Combine(_root, "copy-source");
        var destination = Path.Combine(_root, "copy-destination");
        var external = Path.Combine(_root, "copy-external");
        Directory.CreateDirectory(Path.Combine(source, "linked"));
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(source, "linked", "tracked.txt"), "trusted");
        Assert.Equal(0, RunProcess(FindExecutable("git.exe"), source, ["init"]).ExitCode);
        Assert.Equal(0, RunProcess(FindExecutable("git.exe"), source, ["add", "linked/tracked.txt"]).ExitCode);
        Directory.Delete(Path.Combine(source, "linked"), recursive: true);
        File.WriteAllText(Path.Combine(external, "tracked.txt"), "external");
        Directory.CreateSymbolicLink(Path.Combine(source, "linked"), external);
        var harness = Path.Combine(_root, "copy-tracked-reparse.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..boundary] + Environment.NewLine +
            "try {" + Environment.NewLine +
            $"Copy-TrackedSource -SourceRoot '{PowerShellLiteral(source)}' -DestinationRoot '{PowerShellLiteral(destination)}' -ExpectedCommit '{new string('0', 40)}' -GitExecutable '{PowerShellLiteral(FindExecutable("git.exe"))}' -AllowUntrustedSource" + Environment.NewLine +
            "exit 0" + Environment.NewLine +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }" + Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);
        Directory.Delete(Path.Combine(source, "linked"));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("phase1cb-reparse-point", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.Equal("external", File.ReadAllText(Path.Combine(external, "tracked.txt")));
    }

    [Fact]
    public void PowerShellTrustedSourceCopyUsesIndexBlobBatch()
    {
        var repository = FindRepositoryRoot();
        var sourceScript = File.ReadAllText(Path.Combine(
            repository,
            "desktop", "windows", "scripts", "Build-Phase1CBPayload.ps1"));
        var boundary = sourceScript.IndexOf(
            "try {\n    Clear-BuildInjectionEnvironment",
            StringComparison.Ordinal);
        Assert.True(boundary > 0);
        var source = Path.Combine(_root, "trusted-copy-source");
        var destination = Path.Combine(_root, "trusted-copy-destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "tracked.txt"), "trusted-bytes");
        Assert.Equal(0, RunProcess(FindExecutable("git.exe"), source, ["init"]).ExitCode);
        Assert.Equal(0, RunProcess(FindExecutable("git.exe"), source, ["config", "user.name", "HowardLab Test"]).ExitCode);
        Assert.Equal(0, RunProcess(FindExecutable("git.exe"), source, ["config", "user.email", "test@howardlab.invalid"]).ExitCode);
        Assert.Equal(0, RunProcess(FindExecutable("git.exe"), source, ["add", "tracked.txt"]).ExitCode);
        Assert.Equal(0, RunProcess(FindExecutable("git.exe"), source, ["commit", "-m", "fixture"]).ExitCode);
        var head = RunProcess(FindExecutable("git.exe"), source, ["rev-parse", "HEAD"]).StandardOutput.Trim();
        var harness = Path.Combine(_root, "trusted-copy-batch.ps1");
        File.WriteAllText(
            harness,
            sourceScript[..boundary] + Environment.NewLine +
            $"Copy-TrackedSource -SourceRoot '{PowerShellLiteral(source)}' -DestinationRoot '{PowerShellLiteral(destination)}' -ExpectedCommit '{head}' -GitExecutable '{PowerShellLiteral(FindExecutable("git.exe"))}'" + Environment.NewLine,
            new UTF8Encoding(false));

        var result = RunProcess(
            FindExecutable("powershell.exe"),
            _root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness]);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
        Assert.Equal("trusted-bytes", File.ReadAllText(Path.Combine(destination, "tracked.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".git")));
    }

    [Fact]
    public void ProcessRunnerFailureUnlinksProfileReparseAndPreservesExternalSentinel()
    {
        Directory.CreateDirectory(_root);
        var external = Path.Combine(_root, "profile-cleanup-external");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var probe = Path.Combine(_root, "profile-cleanup-probe.mjs");
        File.WriteAllText(
            probe,
            "import { symlinkSync } from 'node:fs';\n" +
            "import path from 'node:path';\n" +
            "symlinkSync(process.argv[2],path.join(process.env.USERPROFILE,'escape'),'junction');\n" +
            "process.exit(27);\n",
            new UTF8Encoding(false));

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadProcessRunner().Run(new ProductionPayloadProcessSpec(
                FindNodeExecutable(),
                [probe, external],
                _root,
                UseShellExecute: false,
                Environment: null)
            {
                Timeout = TimeSpan.FromSeconds(10),
            }));

        Assert.Equal("production-build-process-failed", error.ReasonCode);
        Assert.Equal("untouched", File.ReadAllText(sentinel));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.GetTempPath(),
            "howardlab-phase1cb-child-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void ProcessRunnerCountsProfileReparsesBeforeCleanupClassification()
    {
        Directory.CreateDirectory(_root);
        var profilesBefore = Directory.EnumerateDirectories(
                Path.GetTempPath(),
                "howardlab-phase1cb-child-*",
                SearchOption.TopDirectoryOnly)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var external = Path.Combine(_root, "profile-budget-external");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var probe = Path.Combine(_root, "profile-budget-probe.mjs");
        File.WriteAllText(
            probe,
            "import { symlinkSync } from 'node:fs';\n" +
            "import path from 'node:path';\n" +
            "for(let i=0;i<4;i++)symlinkSync(process.argv[2]," +
            "path.join(process.env.USERPROFILE,'escape-'+i),'junction');\n",
            new UTF8Encoding(false));

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadProcessRunner().Run(new ProductionPayloadProcessSpec(
                FindNodeExecutable(),
                [probe, external],
                _root,
                UseShellExecute: false,
                Environment: null)
            {
                Timeout = TimeSpan.FromSeconds(10),
                MaximumProfileCleanupEntries = 3,
            }));

        Assert.Equal("production-build-profile-cleanup-failed", error.ReasonCode);
        Assert.Equal("untouched", File.ReadAllText(sentinel));
        foreach (var profile in Directory.EnumerateDirectories(
                     Path.GetTempPath(),
                     "howardlab-phase1cb-child-*",
                     SearchOption.TopDirectoryOnly)
                 .Where(profile => !profilesBefore.Contains(profile)))
        {
            foreach (var entry in new DirectoryInfo(profile).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    entry.Delete();
                }
            }
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public void SourceImportGraphRejectsOversizedLaunchModuleBeforeFollowingImports()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        var launch = Path.Combine(
            fixture.BuildRoot,
            "desktop", "windows", "node", "src", "production", "twenty-server-role.ts");
        File.WriteAllBytes(launch, Enumerable.Repeat((byte)' ', 16 * 1024 * 1024 + 1).ToArray());
        AddImportRootRecord(
            fixture.InventoryPath,
            "packages/twenty-server/src/engine/core-modules/serverless/serverless.module.ts",
            "packages/twenty-server/dist/engine/core-modules/serverless/serverless.module.js");
        Write(
            fixture.BuildRoot,
            "packages/twenty-server/src/engine/core-modules/serverless/serverless.module.ts",
            "export {};\n");

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(new RecordingRunner(), new AcceptedNodeTrust()).Materialize(
                new ProductionPayloadMaterializationRequest(
                    fixture.BuildRoot,
                    Path.Combine(_root, "oversized-source-payload"),
                    fixture.NodeExecutable,
                    fixture.InventoryPath,
                    fixture.Header,
                    ClosureOnly: true,
                    fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-entrypoint-inventory-mismatch", error.ReasonCode);
    }

    [Fact]
    public void ClosureOnlyRejectsMultiGigabyteSparseInventoryBeforeAllocationAndCleansPayload()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        SetSparseLength(fixture.InventoryPath, 3L * 1024 * 1024 * 1024);
        var payload = Path.Combine(_root, "oversized-inventory-payload");

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(new RecordingRunner(), new AcceptedNodeTrust()).Materialize(
                new ProductionPayloadMaterializationRequest(
                    fixture.BuildRoot,
                    payload,
                    fixture.NodeExecutable,
                    fixture.InventoryPath,
                    fixture.Header,
                    ClosureOnly: true,
                    fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-entrypoint-inventory-mismatch", error.ReasonCode);
        Assert.False(Directory.Exists(payload));
    }

    [Fact]
    public void ClosureOnlyRejectsOversizedSparseFrontendBeforeTextAllocationAndCleansPayload()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        SetSparseLength(
            Path.Combine(fixture.BuildRoot, "packages", "twenty-server", "dist", "front", "index.html"),
            16L * 1024 * 1024 + 1);
        var payload = Path.Combine(_root, "oversized-frontend-payload");

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(new RecordingRunner(), new AcceptedNodeTrust()).Materialize(
                new ProductionPayloadMaterializationRequest(
                    fixture.BuildRoot,
                    payload,
                    fixture.NodeExecutable,
                    fixture.InventoryPath,
                    fixture.Header,
                    ClosureOnly: true,
                    fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-frontend-marker-invalid", error.ReasonCode);
        Assert.False(Directory.Exists(payload));
    }

    [Fact]
    public void ClosureOnlyRejectsOversizedSparsePackageJsonBeforeJsonAllocationAndCleansPayload()
    {
        var fixture = CreateClosureFixture(completeInventory: true);
        SetSparseLength(
            Path.Combine(fixture.BuildRoot, "packages", "twenty-server", "package.json"),
            1024L * 1024 + 1);
        var payload = Path.Combine(_root, "oversized-package-payload");

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(new RecordingRunner(), new AcceptedNodeTrust()).Materialize(
                new ProductionPayloadMaterializationRequest(
                    fixture.BuildRoot,
                    payload,
                    fixture.NodeExecutable,
                    fixture.InventoryPath,
                    fixture.Header,
                    ClosureOnly: true,
                    fixture.CompiledDesktopNodeRoot)));

        Assert.Equal("production-workspace-projection-invalid", error.ReasonCode);
        Assert.False(Directory.Exists(payload));
    }

    [Fact]
    public void SdkNormalizationRejectsOversizedSparseJsonBeforeAllocation()
    {
        var source = Path.Combine(_root, "oversized-sdk-source.json");
        var build = Path.Combine(_root, "oversized-sdk-build");
        var staged = Path.Combine(build, "packages", "twenty-sdk", "project.json");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        SetSparseLength(source, 1024L * 1024 + 1);
        SetSparseLength(staged, 1024L * 1024 + 1);

        var error = Assert.Throws<ProductionPayloadBuildException>(() =>
            new ProductionPayloadBuilder(new RecordingRunner(), new AcceptedNodeTrust())
                .NormalizeStagedTwentySdkProject(build, source));

        Assert.Equal("production-windows-build-normalization-drift", error.ReasonCode);
        Assert.Equal(1024L * 1024 + 1, new FileInfo(source).Length);
        Assert.Equal(1024L * 1024 + 1, new FileInfo(staged).Length);
    }

    [Fact]
    public void NodeTrustVerifierStreamsExactPinnedArchiveAndExecutableLengths()
    {
        var archive = Path.Combine(_root, "sparse-node-archive.zip");
        var executable = Path.Combine(_root, "sparse-node.exe");
        SetSparseLength(archive, ProductionPayloadBuilder.NodeArchiveLength);
        SetSparseLength(executable, ProductionPayloadBuilder.NodeExecutableLength);

        var trust = new ProductionNodeTrustVerifier().Inspect(archive, executable);

        Assert.Equal(64, trust.ArchiveSha256.Length);
        Assert.Equal(64, trust.ExecutableSha256.Length);
        Assert.False(trust.ChainTrusted);
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop", "windows", "src", "HowardLab.EbayCrm.AppHost.Windows",
            "Payload", "ProductionPayloadBuilder.cs"));
        Assert.DoesNotContain("SHA256.HashData(File.ReadAllBytes", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedSdkPromoterRejectsHostileDepthBeforePublishingDeclarations()
    {
        var repository = FindRepositoryRoot();
        var sourceProject = Path.Combine(repository, "packages", "twenty-sdk", "project.json");
        var build = Path.Combine(_root, "promoter-depth-build");
        var sdk = Path.Combine(build, "packages", "twenty-sdk");
        Directory.CreateDirectory(sdk);
        File.Copy(sourceProject, Path.Combine(sdk, "project.json"));
        var builder = new ProductionPayloadBuilder(new RecordingRunner(), new AcceptedNodeTrust());
        builder.NormalizeStagedTwentySdkProject(build, sourceProject);
        foreach (var name in new[] { "billing", "define", "front-component", "logic-function", "utils" })
        {
            Write(build, $"packages/twenty-sdk/dist/sdk/{name}/index.d.ts", "export {};\n");
            Write(build, $"packages/twenty-sdk/dist/{name}/index.js", "export {};\n");
        }
        var current = Path.Combine(sdk, "dist", "sdk", "define");
        for (var depth = 0; depth < 66; depth++)
        {
            current = Path.Combine(current, "d");
            Directory.CreateDirectory(current);
        }
        File.WriteAllText(Path.Combine(current, "hostile.d.ts"), "export {};\n");

        var result = RunNode(
            Path.Combine(build, ".phase1cb-toolchain", "promote-sdk-declarations.mjs"),
            sdk);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("phase1cb-sdk-declarations-traversal-budget", result.StandardError, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(sdk, "dist", "sdk")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private ClosureFixture CreateClosureFixture(bool completeInventory = false)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "trusted-node.zip"), "archive");
        var build = Path.Combine(_root, "fixture", Guid.NewGuid().ToString("N"), "build");
        Directory.CreateDirectory(build);
        Write(build, "package.json", "{\"packageManager\":\"yarn@4.13.0\"}");
        Write(build, "node_modules/runtime/index.js", "module.exports = true;");
        Write(build, "packages/twenty-server/package.json", "{\"name\":\"twenty-server\"}");
        Write(build, "packages/twenty-server/dist/main.js", "module.exports = {};");
        Write(
            build,
            "packages/twenty-server/dist/front/index.html",
            """
            <script id="twenty-env-config">
              window._env_ = {
                // This will be overwritten
              };
            </script>
            """);
        Write(
            build,
            "packages/twenty-front/build/index.html",
            """
            <script id="twenty-env-config">
              window._env_ = {
                // This will be overwritten
              };
            </script>
            """);
        foreach (var workspace in new[] { "twenty-emails", "twenty-shared", "twenty-client-sdk" })
        {
            Write(build, $"packages/{workspace}/package.json", $"{{\"name\":\"{workspace}\"}}");
            Write(build, $"packages/{workspace}/dist/index.js", "module.exports = {};");
        }
        var header = Header();
        var compiled = Path.Combine(build, "desktop", "windows", "node", "publish");
        var records = new List<object>
        {
            new
            {
                kind = "frontendAsset",
                role = "frontend",
                sourcePath = "packages/twenty-front/build/index.html",
                emittedPath = "packages/twenty-server/dist/front/index.html",
                buildProvenance = "vite-empty-runtime-config-v1",
            },
        };
        if (completeInventory)
        {
            foreach (var entrypoint in HeaderEntrypoints(header))
            {
                var fileName = Path.GetFileName(entrypoint.EmittedPath);
                var sourcePath = $"desktop/windows/node/src/production/{Path.ChangeExtension(fileName, ".ts")}";
                Write(build, sourcePath, "export {};\n");
                Write(compiled, $"production/{fileName}", "export {};\n");
                records.Add(new
                {
                    kind = "launchExecutableJs",
                    role = entrypoint.Role,
                    sourcePath,
                    emittedPath = entrypoint.EmittedPath,
                    classification = "sideEffectFreeImport",
                });
            }
        }
        var inventory = Path.Combine(build, "production-entrypoints-v1.json");
        File.WriteAllText(inventory, System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            records,
        }), new UTF8Encoding(false));
        var node = Path.Combine(build, "trusted-node.exe");
        File.WriteAllText(node, "node");
        CreateFakeMinGit(build);
        return new ClosureFixture(build, node, inventory, header, compiled);
    }

    private static ProductionPayloadHeader Header() => new(
        2,
        new string('a', 40),
        "phase-1c-b/test-build",
        "24.18.0",
        "4.13.0",
        "win-x64",
        "apphost-control-v1",
        "role-generation-v1",
        "node.exe",
        "desktop/windows/node/production/twenty-server-role.js",
        "desktop/windows/node/production/twenty-worker-role.js",
        "desktop/windows/node/production/setup-database-role.js",
        "desktop/windows/node/production/run-instance-commands-role.js",
        "desktop/windows/node/production/acceptance-orchestrator.js",
        "desktop/windows/node/production/acceptance-cleanup-role.js",
        "desktop/windows/node/production/bullmq-compatibility-preflight.js",
        "packages/twenty-server/dist/front/index.html",
        new string('d', 64),
        new string('f', 64),
        new());

    private static IEnumerable<(string Role, string EmittedPath)> HeaderEntrypoints(
        ProductionPayloadHeader header) =>
    [
        ("server", header.ServerEntrypoint),
        ("worker", header.WorkerEntrypoint),
        ("setup", header.SetupEntrypoint),
        ("instance-command", header.InstanceCommandEntrypoint),
        ("compatibility-preflight", header.CompatibilityPreflightEntrypoint),
        ("acceptance", header.AcceptanceEntrypoint),
        ("acceptance-cleanup", header.AcceptanceCleanupEntrypoint),
    ];

    private static IReadOnlyList<ProductionPayloadProcessSpec> ExpectedCommands(
        string node,
        string build,
        string compiled) =>
    [
        new(
            node,
            [
                ".yarn/releases/yarn-4.13.0.cjs",
                "workspaces",
                "focus",
                "twenty",
                "twenty-server",
                "twenty-front",
                "twenty-emails",
            ],
            build,
            false,
            ImmutableYarnEnvironment(build)),
        Command(node, build, ".yarn/releases/yarn-4.13.0.cjs", "nx", "run", "twenty-server:lingui:extract", "--outputStyle=stream", "--verbose", "--parallel=1"),
        Command(node, build, ".yarn/releases/yarn-4.13.0.cjs", "nx", "run", "twenty-server:lingui:compile"),
        Command(node, build, ".yarn/releases/yarn-4.13.0.cjs", "nx", "run", "twenty-emails:lingui:extract"),
        Command(node, build, ".yarn/releases/yarn-4.13.0.cjs", "nx", "run", "twenty-emails:lingui:compile"),
        Command(node, build, ".yarn/releases/yarn-4.13.0.cjs", "nx", "run", "twenty-server:build"),
        Command(node, build, ".yarn/releases/yarn-4.13.0.cjs", "nx", "run", "twenty-front:lingui:extract"),
        Command(node, build, ".yarn/releases/yarn-4.13.0.cjs", "nx", "run", "twenty-front:lingui:compile"),
        Command(node, build, ".yarn/releases/yarn-4.13.0.cjs", "nx", "build", "twenty-front", environment: new Dictionary<string, string> { ["NODE_OPTIONS"] = "--max-old-space-size=8192" }),
        Command(node, build, ".yarn/releases/yarn-4.13.0.cjs", "exec", "tsc", "--project", "desktop/windows/node/tsconfig.publish.json", "--outDir", compiled),
        new(
            node,
            [
                ".yarn/releases/yarn-4.13.0.cjs",
                "workspaces",
                "focus",
                "--production",
                "twenty-emails",
                "twenty-shared",
                "twenty-client-sdk",
                "twenty-server",
            ],
            build,
            false,
            ImmutableYarnEnvironment(build)),
    ];

    private static ProductionPayloadProcessSpec Command(
        string file,
        string workingDirectory,
        params string[] arguments) => new(
            file,
            arguments,
            workingDirectory,
            false,
            BaseEnvironment(workingDirectory));

    private static ProductionPayloadProcessSpec Command(
        string file,
        string workingDirectory,
        string a1,
        string a2,
        string a3,
        string a4,
        IReadOnlyDictionary<string, string> environment)
    {
        var merged = BaseEnvironment(workingDirectory);
        foreach (var pair in environment)
        {
            merged[pair.Key] = pair.Value;
        }
        return new(file, [a1, a2, a3, a4], workingDirectory, false, merged);
    }

    private static Dictionary<string, string> BaseEnvironment(string build) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = Path.Combine(build, ".phase1cb-toolchain") + Path.PathSeparator +
                Path.Combine(build, ".phase1cb-toolchain", "mingit", "cmd") + Path.PathSeparator +
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"),
            ["NX_DAEMON"] = "false",
            ["YARN_ENABLE_GLOBAL_CACHE"] = "false",
            ["YARN_CACHE_FOLDER"] = Path.Combine(build, ".phase1cb-cache", "yarn"),
            ["NX_CACHE_DIRECTORY"] = Path.Combine(build, ".phase1cb-cache", "nx"),
            ["NX_WORKSPACE_DATA_DIRECTORY"] = Path.Combine(build, ".phase1cb-cache", "nx-workspace"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_SYSTEM"] = "NUL",
            ["GIT_CONFIG_GLOBAL"] = "NUL",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "Never",
            ["GIT_TRACE2_EVENT"] = Path.Combine(
                build,
                ".phase1cb-cache",
                "git-trace",
                "event.jsonl"),
        };

    private static Dictionary<string, string> ImmutableYarnEnvironment(string build)
    {
        var environment = BaseEnvironment(build);
        environment["YARN_ENABLE_IMMUTABLE_INSTALLS"] = "true";
        return environment;
    }

    private static string CreateFakeMinGit(string build)
    {
        var root = Path.Combine(build, ".phase1cb-toolchain", "mingit");
        Directory.CreateDirectory(Path.Combine(root, "cmd"));
        File.WriteAllText(Path.Combine(root, "cmd", "git.exe"), "git");
        File.Copy(
            Path.Combine(FindRepositoryRoot(), "yarn.lock"),
            Path.Combine(build, "yarn.lock"),
            overwrite: true);
        return root;
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static void AddImportRootRecord(
        string inventoryPath,
        string sourcePath,
        string emittedPath)
    {
        var inventory = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(inventoryPath))!.AsObject();
        inventory["records"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "importRootJs",
            ["ownerRole"] = "server",
            ["sourcePath"] = sourcePath,
            ["emittedPath"] = emittedPath,
            ["classification"] = "sideEffectFreeImport",
        });
        File.WriteAllText(
            inventoryPath,
            inventory.ToJsonString(),
            new UTF8Encoding(false));
    }

    private static void WritePeX64(string root, string relative)
        => WritePe(root, relative, 0x8664);

    private static void WritePe(string root, string relative, ushort machine)
    {
        var bytes = new byte[128];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(bytes, 0x3c);
        bytes[64] = (byte)'P';
        bytes[65] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 68);
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteElfX64(string root, string relative)
    {
        var bytes = new byte[64];
        bytes[0] = 0x7f;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        BitConverter.GetBytes((ushort)0x3e).CopyTo(bytes, 18);
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string[] BcryptNativeInventory() =>
    [
        "prebuilds/darwin-arm64/bcrypt.node",
        "prebuilds/darwin-x64/bcrypt.node",
        "prebuilds/linux-arm/bcrypt.glibc.node",
        "prebuilds/linux-arm/bcrypt.musl.node",
        "prebuilds/linux-arm64/bcrypt.glibc.node",
        "prebuilds/linux-arm64/bcrypt.musl.node",
        "prebuilds/linux-x64/bcrypt.glibc.node",
        "prebuilds/linux-x64/bcrypt.musl.node",
        "prebuilds/win32-arm64/bcrypt.node",
        "prebuilds/win32-x64/bcrypt.node",
    ];

    private static string[] SentryNativeInventory()
    {
        var abis = new[] { "108", "115", "127", "137", "147" };
        var platforms = new[]
        {
            "darwin-arm64",
            "darwin-x64",
            "linux-arm64-glibc",
            "linux-arm64-musl",
            "linux-x64-glibc",
            "linux-x64-musl",
            "win32-x64",
        };
        return platforms
            .SelectMany(platform => abis.Select(abi =>
                $"lib/sentry_cpu_profiler-{platform}-{abi}.node"))
            .ToArray();
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunNode(
        string script,
        string workingDirectory)
    {
        var node = FindNodeExecutable();
        return RunProcess(node, workingDirectory, [script]);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunProcess(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        return (process.ExitCode, standardOutput, standardError);
    }

    private static string FindNodeExecutable() => FindExecutable("node.exe");

    private static string FindExecutable(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(path => Path.Combine(path, name))
        .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException($"{name}-not-found");

    private static void AssertProcessExited(int processId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            Thread.Sleep(50);
        }
        Assert.Fail($"process-still-running:{processId}");
    }

    private static void AssertExactProcessExited(int processId, long creationTimeUtcTicks)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != creationTimeUtcTicks)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            Thread.Sleep(50);
        }
        Assert.Fail($"exact-process-still-running:{processId}");
    }

    private static void KillTestProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(2_000);
        }
        catch (ArgumentException)
        {
        }
    }

    private static string PowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void SetSparseLength(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    private static void CreateTestHardLink(string path, string existingPath)
    {
        Assert.True(
            CreateHardLink(path, existingPath, IntPtr.Zero),
            $"hard-link-failed:{Marshal.GetLastWin32Error()}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static void WritePayloadToolHeader(
        string root,
        ProductionPayloadHeader header,
        string fileName = "production-payload-header-v2.json")
    {
        Directory.CreateDirectory(root);
        File.WriteAllBytes(
            Path.Combine(root, fileName),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                header,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                }));
    }

    private (int ExitCode, string StandardOutput, string StandardError) RunPayloadTool(
        params string[] arguments)
    {
        var payloadTool = Path.Combine(AppContext.BaseDirectory, "HowardLab.EbayCrm.PayloadTool.dll");
        Assert.True(File.Exists(payloadTool));
        return RunProcess(
            FindExecutable("dotnet.exe"),
            _root,
            ["exec", payloadTool, .. arguments]);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "packages", "twenty-sdk", "project.json")))
            {
                return current.FullName;
            }
        }
        throw new InvalidOperationException("repository-root-not-found");
    }

    private static string ElectronLocatorCachePath(string buildRoot) => Path.Combine(
        buildRoot,
        ".phase1cb-cache",
        "yarn",
        ProductionPayloadBuilder.MinGitLocatorCacheFileName);

    private static string[] EnumeratePayloadFiles(string payload) => Directory
        .EnumerateFiles(payload, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(payload, path).Replace(Path.DirectorySeparatorChar, '/'))
        .Where(path => path != ProductionPayloadValidator.ManifestFileName)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private sealed record ClosureFixture(
        string BuildRoot,
        string NodeExecutable,
        string InventoryPath,
        ProductionPayloadHeader Header,
        string CompiledDesktopNodeRoot);

    private sealed class RecordingRunner : IProductionPayloadProcessRunner
    {
        internal List<ProductionPayloadProcessSpec> Invocations { get; } = [];
        public void Run(ProductionPayloadProcessSpec process)
        {
            Invocations.Add(process);
            WriteAcceptedMinGitTrace(process);
        }
    }

    private sealed class ProbeRecordingRunner : IProductionPayloadProcessRunner
    {
        internal List<ProductionPayloadProcessSpec> Invocations { get; } = [];

        public void Run(ProductionPayloadProcessSpec process)
        {
            Invocations.Add(process);
            var repository = Path.Combine(
                process.WorkingDirectory,
                ".phase1cb-cache",
                "git-probe",
                "repository.git");
            Directory.CreateDirectory(repository);
            if (Invocations.Count != 2)
            {
                return;
            }
            File.WriteAllText(
                Path.Combine(repository, "FETCH_HEAD"),
                ProductionPayloadBuilder.MinGitLocatorCommit + "\t\t'" +
                ProductionPayloadBuilder.MinGitLocatorCommit + "' of " +
                ProductionPayloadBuilder.MinGitLocatorUrl + "\n",
                new UTF8Encoding(false));
            WriteAcceptedMinGitTrace(process);
        }
    }

    private sealed class RejectingRunner : IProductionPayloadProcessRunner
    {
        public void Run(ProductionPayloadProcessSpec process) =>
            throw new ProductionPayloadBuildException("production-build-process-failed");
    }

    private sealed class OrdinalRejectingRunner(int failingOrdinal, Exception failure)
        : IProductionPayloadProcessRunner
    {
        internal int InvocationCount { get; private set; }

        public void Run(ProductionPayloadProcessSpec process)
        {
            InvocationCount++;
            if (InvocationCount == failingOrdinal)
            {
                throw failure;
            }
            WriteAcceptedMinGitTrace(process);
        }
    }

    private sealed class MutatingProbeRunner : IProductionPayloadProcessRunner
    {
        public void Run(ProductionPayloadProcessSpec process) => File.WriteAllText(
            Path.Combine(process.Arguments[1], "mutated-during-probe.txt"),
            "mutated");
    }

    private sealed class NodeMutatingBuildRunner(string nodeExecutable) : IProductionPayloadProcessRunner
    {
        private int _invocationCount;

        public void Run(ProductionPayloadProcessSpec process)
        {
            if (Interlocked.Increment(ref _invocationCount) == 1)
            {
                WriteAcceptedMinGitTrace(process);
                File.WriteAllText(nodeExecutable, "tampered-after-initial-verification");
            }
        }
    }

    private static void WriteAcceptedMinGitTrace(ProductionPayloadProcessSpec process)
    {
        if (process.Environment is null ||
            !process.Environment.TryGetValue("GIT_TRACE2_EVENT", out var path) ||
            File.Exists(path))
        {
            return;
        }
        File.WriteAllText(
            path,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "version",
                exe = "2.55.0.windows.2",
            }) + "\n" +
            System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "start",
                argv = new[]
                {
                    Path.Combine(
                        process.WorkingDirectory,
                        ".phase1cb-toolchain",
                        "mingit",
                        "cmd",
                        "git.exe"),
                    "fetch",
                    "https://github.com/electron/node-gyp.git",
                    "06b29aafb7708acef8b3669835c8a7857ebc92d2",
                },
            }) + "\n",
            new UTF8Encoding(false));
    }

    private sealed class GitTraceMutationRunner(string mutation) : IProductionPayloadProcessRunner
    {
        internal int InvocationCount { get; private set; }

        public void Run(ProductionPayloadProcessSpec process)
        {
            InvocationCount++;
            if (InvocationCount != 1 || mutation == "missing")
            {
                return;
            }
            var path = process.Environment!["GIT_TRACE2_EVENT"];
            var version = mutation == "version" ? "0.0.0.windows.0" : "2.55.0.windows.2";
            var locator = mutation == "locator"
                ? "https://example.invalid/node-gyp.git"
                : "https://github.com/electron/node-gyp.git";
            var commit = mutation == "commit"
                ? new string('0', 40)
                : "06b29aafb7708acef8b3669835c8a7857ebc92d2";
            File.WriteAllText(
                path,
                mutation == "malformed"
                    ? "{"
                    : System.Text.Json.JsonSerializer.Serialize(new { @event = "version", exe = version }) + "\n" +
                      System.Text.Json.JsonSerializer.Serialize(new
                      {
                          @event = "start",
                          argv = new[] { "git.exe", "fetch", locator, commit },
                      }) + "\n",
                new UTF8Encoding(false));
        }
    }

    private sealed class ContentAwareNodeTrust : IProductionNodeTrustVerifier
    {
        internal int InspectionCount { get; private set; }

        public ProductionNodeTrust Inspect(string archivePath, string executablePath)
        {
            InspectionCount++;
            var accepted = File.ReadAllText(executablePath) == "node";
            return new ProductionNodeTrust(
                ProductionPayloadBuilder.NodeArchiveSha256,
                accepted
                    ? ProductionPayloadBuilder.NodeExecutableSha256
                    : new string('0', 64),
                ProductionPayloadBuilder.NodeAuthenticodeSubject,
                ChainTrusted: true);
        }
    }

    private sealed class RejectSecondNodeInspection : IProductionNodeTrustVerifier
    {
        internal List<string> InspectedExecutables { get; } = [];

        public ProductionNodeTrust Inspect(string archivePath, string executablePath)
        {
            InspectedExecutables.Add(executablePath);
            return new ProductionNodeTrust(
                ProductionPayloadBuilder.NodeArchiveSha256,
                ProductionPayloadBuilder.NodeExecutableSha256,
                ProductionPayloadBuilder.NodeAuthenticodeSubject,
                ChainTrusted: InspectedExecutables.Count == 1);
        }
    }

    private sealed class CountingAcceptedNodeTrust : IProductionNodeTrustVerifier
    {
        internal int InspectionCount { get; private set; }

        public ProductionNodeTrust Inspect(string archivePath, string executablePath)
        {
            InspectionCount++;
            return new ProductionNodeTrust(
                ProductionPayloadBuilder.NodeArchiveSha256,
                ProductionPayloadBuilder.NodeExecutableSha256,
                ProductionPayloadBuilder.NodeAuthenticodeSubject,
                ChainTrusted: true);
        }
    }

    private sealed class AcceptedNodeTrust : IProductionNodeTrustVerifier
    {
        public ProductionNodeTrust Inspect(string archivePath, string executablePath) => new(
            ProductionPayloadBuilder.NodeArchiveSha256,
            ProductionPayloadBuilder.NodeExecutableSha256,
            ProductionPayloadBuilder.NodeAuthenticodeSubject,
            ChainTrusted: true);
    }

    private sealed class AcceptedMinGitTrust : IProductionMinGitTrustVerifier
    {
        public ProductionMinGitTrust Inspect(string executablePath) => new(
            ProductionPayloadBuilder.MinGitExecutableLength,
            ProductionPayloadBuilder.MinGitExecutableSha256,
            ProductionPayloadBuilder.MinGitAuthenticodeSubject,
            ChainTrusted: true);
    }

    private sealed class AcceptedMinGitProbe : IProductionMinGitProbe
    {
        public void Verify(string executablePath, string buildRoot, string minGitRoot)
        {
            var trace = Path.Combine(
                buildRoot,
                ".phase1cb-cache",
                "git-trace",
                "event.jsonl");
            File.WriteAllText(
                trace,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    @event = "version",
                    exe = ProductionPayloadBuilder.MinGitVersion,
                }) + "\n" +
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    @event = "start",
                    argv = new[]
                    {
                        executablePath,
                        "fetch",
                        ProductionPayloadBuilder.MinGitLocatorUrl,
                        ProductionPayloadBuilder.MinGitLocatorCommit,
                    },
                }) + "\n",
                new UTF8Encoding(false));
        }

        public void VerifyLocatorCacheIsAbsent(string buildRoot)
        {
        }
    }

    private sealed class MutatedMinGitProbe(string mutation) : IProductionMinGitProbe
    {
        public void Verify(string executablePath, string buildRoot, string minGitRoot)
        {
            if (mutation == "missing")
            {
                return;
            }
            var path = Path.Combine(
                buildRoot,
                ".phase1cb-cache",
                "git-trace",
                "event.jsonl");
            var version = mutation == "version"
                ? "0.0.0.windows.0"
                : ProductionPayloadBuilder.MinGitVersion;
            var locator = mutation == "locator"
                ? "https://example.invalid/node-gyp.git"
                : ProductionPayloadBuilder.MinGitLocatorUrl;
            var commit = mutation == "commit"
                ? new string('0', 40)
                : ProductionPayloadBuilder.MinGitLocatorCommit;
            File.WriteAllText(
                path,
                mutation == "malformed"
                    ? "{"
                    : System.Text.Json.JsonSerializer.Serialize(new
                      {
                          @event = "version",
                          exe = version,
                      }) + "\n" +
                      System.Text.Json.JsonSerializer.Serialize(new
                      {
                          @event = "start",
                          argv = new[] { "git.exe", "fetch", locator, commit },
                      }) + "\n",
                new UTF8Encoding(false));
        }

        public void VerifyLocatorCacheIsAbsent(string buildRoot)
        {
        }
    }

    private sealed class MutatedMinGitTrust(string mutation) : IProductionMinGitTrustVerifier
    {
        public ProductionMinGitTrust Inspect(string executablePath) => new(
            mutation == "length" ? 1 : ProductionPayloadBuilder.MinGitExecutableLength,
            mutation == "hash" ? new string('0', 64) : ProductionPayloadBuilder.MinGitExecutableSha256,
            mutation == "subject" ? "CN=Other" : ProductionPayloadBuilder.MinGitAuthenticodeSubject,
            ChainTrusted: mutation != "chain");
    }

    private sealed class MutatedNodeTrust(string mutation) : IProductionNodeTrustVerifier
    {
        public ProductionNodeTrust Inspect(string archivePath, string executablePath) => new(
            mutation == "archive" ? new string('0', 64) : ProductionPayloadBuilder.NodeArchiveSha256,
            mutation == "executable" ? new string('1', 64) : ProductionPayloadBuilder.NodeExecutableSha256,
            mutation == "subject" ? "CN=Other" : ProductionPayloadBuilder.NodeAuthenticodeSubject,
            ChainTrusted: mutation != "chain");
    }
}
