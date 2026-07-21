using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Protocol.Control;
using HowardLab.EbayCrm.AppHost.Windows.Processes;

namespace HowardLab.EbayCrm.AppHost.Windows.Payload;

public sealed class ProductionPayloadBuildException : Exception
{
    internal ProductionPayloadBuildException(string reasonCode, string? diagnostic = null)
        : base(reasonCode)
    {
        ReasonCode = reasonCode;
        Diagnostic = diagnostic;
    }

    public string ReasonCode { get; }
    public string? Diagnostic { get; }
}

public sealed record ProductionNodeTrust(
    string ArchiveSha256,
    string ExecutableSha256,
    string AuthenticodeSubject,
    bool ChainTrusted);

public interface IProductionNodeTrustVerifier
{
    ProductionNodeTrust Inspect(string archivePath, string executablePath);
}

public sealed record ProductionMinGitTrust(
    long ExecutableLength,
    string ExecutableSha256,
    string AuthenticodeSubject,
    bool ChainTrusted);

public interface IProductionMinGitTrustVerifier
{
    ProductionMinGitTrust Inspect(string executablePath);
}

public interface IProductionMinGitProbe
{
    void Verify(string executablePath, string buildRoot, string minGitRoot);
    void VerifyLocatorCacheIsAbsent(string buildRoot);
}

public enum ProductionPayloadProcessPurpose
{
    Build,
    NativeAddonProbe,
}

public sealed record ProductionPayloadProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    bool UseShellExecute,
    IReadOnlyDictionary<string, string>? Environment)
{
    public ProductionPayloadProcessPurpose Purpose { get; init; } =
        ProductionPayloadProcessPurpose.Build;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public int MaximumOutputBytes { get; init; } = 16 * 1024 * 1024;
    internal int MaximumProfileCleanupEntries { get; init; } = 250_000;
    public IReadOnlyList<string> RemovedEnvironmentVariables { get; init; } = [];
    public IReadOnlyList<string> RemovedEnvironmentVariablePrefixes { get; init; } = [];
}

public interface IProductionPayloadProcessRunner
{
    void Run(ProductionPayloadProcessSpec process);
}

public sealed record ProductionPayloadMaterializationRequest(
    string BuildRoot,
    string PayloadRoot,
    string NodeExecutable,
    string EntrypointInventoryPath,
    ProductionPayloadHeader Header,
    bool ClosureOnly,
    string? CompiledDesktopNodeRoot = null,
    string? NodeArchivePath = null);

public sealed record ProductionPayloadBuildRequest(
    string NodeArchivePath,
    string NodeExecutable,
    string BuildRoot,
    string PayloadRoot,
    string EntrypointInventoryPath,
    string CompiledDesktopNodeRoot,
    string SourceTwentySdkProjectPath,
    ProductionPayloadHeader Header,
    bool ClosureOnly);

public sealed record ProductionPayloadBuildResult(
    string Classification,
    string PayloadRoot,
    ProductionPayloadManifestV2? Manifest,
    ProductionReleaseCatalog? ReleaseCatalog);

public sealed record ProductionWindowsBuildNormalizationResult(
    string PreimageSha256,
    string OutputSha256,
    int ReplacedCommandCount,
    int AddedDependencyCount,
    string HelperSha256);

public sealed class ProductionPayloadBuilder
{
    public const string NodeVersion = "24.18.0";
    public const string YarnVersion = "4.13.0";
    public const string NodeArchiveSha256 =
        "0AE68406B42D7725661DA979B1403EC9926DA205C6770827F33AAC9D8F26E821";
    public const string NodeExecutableSha256 =
        "9A4EB5F1C29C6A2E93852EAD46B999E284A6A5CA8BAB4D4E241D587D025A52DE";
    public const long NodeArchiveLength = 37_176_245;
    public const long NodeExecutableLength = 92_534_088;
    public const string MinGitVersion = "2.55.0.windows.2";
    public const string MinGitArchiveSha256 =
        "E3EA2944CEA4B3FABCD69C7C1669EF69B1B66C05AC7806D81224D0ABAD2DEC31";
    public const long MinGitArchiveLength = 38_839_825;
    public const string MinGitExecutableSha256 =
        "22FEAD8244EF3A7225FB800099A4E43ECA8BCEC0466774917669599C2F19A05A";
    public const long MinGitExecutableLength = 46_936;
    public const string MinGitAuthenticodeSubject =
        "CN=Johannes Schindelin, O=Johannes Schindelin, L=Bruehl, C=DE";
    public const string MinGitLocatorUrl =
        "https://github.com/electron/node-gyp.git";
    public const string MinGitLocatorCommit =
        "06b29aafb7708acef8b3669835c8a7857ebc92d2";
    public const string MinGitLocatorCacheFileName =
        "@electron-node-gyp-https-d0f303c37e-e8c97bb534.zip";
    private const int MaximumEntrypointInventoryBytes = 16 * 1024 * 1024;
    private const int MaximumFrontendMarkerBytes = 16 * 1024 * 1024;
    private const int MaximumPackageJsonBytes = 1024 * 1024;
    private const int MaximumNormalizationJsonBytes = 1024 * 1024;
    private const int MaximumNormalizationHelperBytes = 256 * 1024;
    public const string NodeAuthenticodeSubject =
        "CN=OpenJS Foundation, O=OpenJS Foundation, L=San Francisco, S=California, C=US";
    private const string TwentySdkWindowsBuildPreimage =
        "npx rimraf 'dist/sdk' 'dist/define/**/*.d.ts' 'dist/define/**/*.d.ts.map' 'dist/billing/**/*.d.ts' 'dist/billing/**/*.d.ts.map' 'dist/front-component/**/*.d.ts' 'dist/front-component/**/*.d.ts.map' 'dist/logic-function/**/*.d.ts' 'dist/logic-function/**/*.d.ts.map' 'dist/utils/**/*.d.ts' 'dist/utils/**/*.d.ts.map' && npx rollup -c rollup.config.sdk-dts.mjs";
    private const string TwentySdkProjectPreimageSha256 =
        "4A1B573A2C6B95927A7CAF293A25DA0F1BF0A23ED5E204026400C6F95EFFD369";
    private const string TwentySdkWindowsBuildCommand =
        @"..\..\.phase1cb-toolchain\node.exe ..\..\.phase1cb-toolchain\promote-sdk-declarations.mjs";
    private const string TwentySdkWindowsBuildCommandJson =
        @"..\\..\\.phase1cb-toolchain\\node.exe ..\\..\\.phase1cb-toolchain\\promote-sdk-declarations.mjs";
    private const string TwentySdkBuildSdkCommandPreimage =
        "npx vite build -c vite.config.define.ts && npx vite build -c vite.config.billing.ts && npx vite build -c vite.config.front-component.ts && npx vite build -c vite.config.logic-function.ts && npx vite build -c vite.config.utils.ts && npx rollup -c rollup.config.sdk-dts.mjs";
    private const string TwentySdkBuildSdkCommand =
        "npx vite build -c vite.config.define.ts && npx vite build -c vite.config.billing.ts && npx vite build -c vite.config.front-component.ts && npx vite build -c vite.config.logic-function.ts && npx vite build -c vite.config.utils.ts";
    private const string TwentySdkBuildSdkTargetMarker = "\"build:sdk\": {";
    private const string TwentySdkBuildSdkDependsOnPreimage =
        "      \"dependsOn\": [\"^build\"],";
    private const string TwentySdkBuildSdkDependsOn =
        "      \"dependsOn\": [\"^build\", \"build\"],";
    private const string TwentySdkDeclarationPromotionHelper =
        "promote-sdk-declarations.mjs";
    internal const string NativeAddonProbeName = ".phase1cb-native-addon-probe.mjs";
    private const string NativeAddonProbe =
        """
        import { realpathSync } from 'node:fs';
        import path from 'node:path';

        const root = realpathSync.native(process.argv[2]);
        const addon = realpathSync.native(process.argv[3]);
        const relative = path.relative(root, addon);
        if (relative === '' || relative === '..' || relative.startsWith(`..${path.sep}`) ||
            path.isAbsolute(relative) || !addon.toLowerCase().endsWith('.node')) {
          throw new Error('phase1cb-native-addon-path');
        }
        const nativeModule = { exports: {} };
        process.dlopen(nativeModule, addon);
        """;
    private const string TwentySdkDeclarationPromotionScript =
        """
        import {
          copyFileSync,
          lstatSync,
          mkdirSync,
          readdirSync,
          realpathSync,
          rmdirSync,
          unlinkSync,
        } from 'node:fs';
        import path from 'node:path';
        import { fileURLToPath } from 'node:url';

        const PUBLIC_EXPORTS = Object.freeze([
          'billing',
          'define',
          'front-component',
          'logic-function',
          'utils',
        ]);

        const fail = (reason) => {
          throw new Error(reason);
        };

        const createTraversalBudget = () => ({ entries: 0, bytes: 0 });
        const observeTraversal = (budget, metadata, depth) => {
          const bytes = metadata.isFile() ? metadata.size : 0;
          if (depth > 64 || budget.entries >= 250000 || bytes < 0 ||
              budget.bytes > 16 * 1024 * 1024 * 1024 - bytes) {
            fail('phase1cb-sdk-declarations-traversal-budget');
          }
          budget.entries += 1;
          budget.bytes += bytes;
        };

        const samePath = (left, right) =>
          process.platform === 'win32'
            ? left.toLowerCase() === right.toLowerCase()
            : left === right;

        const assertContained = (candidate, root) => {
          const relative = path.relative(root, candidate);
          if (relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative))) {
            return;
          }
          fail('phase1cb-sdk-declarations-path');
        };

        const assertOrdinaryTree = (tree, budget = createTraversalBudget(), depth = 0) => {
          const metadata = lstatSync(tree, { throwIfNoEntry: false });
          if (metadata === undefined || metadata.isSymbolicLink()) {
            fail('phase1cb-sdk-declarations-reparse');
          }
          if (!metadata.isDirectory()) {
            fail('phase1cb-sdk-declarations-type');
          }
          observeTraversal(budget, metadata, depth);
          for (const entry of readdirSync(tree, { withFileTypes: true })) {
            const child = path.join(tree, entry.name);
            const childMetadata = lstatSync(child);
            if (childMetadata.isSymbolicLink()) {
              fail('phase1cb-sdk-declarations-reparse');
            }
            if (childMetadata.isDirectory()) {
              assertOrdinaryTree(child, budget, depth + 1);
            } else if (!childMetadata.isFile()) {
              fail('phase1cb-sdk-declarations-type');
            } else {
              observeTraversal(budget, childMetadata, depth + 1);
            }
          }
        };

        const copyOrdinaryTree = (source, destination, budget = createTraversalBudget(), depth = 0) => {
          observeTraversal(budget, lstatSync(source), depth);
          mkdirSync(destination, { recursive: true });
          for (const entry of readdirSync(source, { withFileTypes: true })) {
            const sourceChild = path.join(source, entry.name);
            const destinationChild = path.join(destination, entry.name);
            if (entry.isDirectory()) {
              copyOrdinaryTree(sourceChild, destinationChild, budget, depth + 1);
            } else if (entry.isFile()) {
              observeTraversal(budget, lstatSync(sourceChild), depth + 1);
              copyFileSync(sourceChild, destinationChild);
            } else {
              fail('phase1cb-sdk-declarations-reparse');
            }
          }
        };

        const assertNoDeclarations = (tree, budget = createTraversalBudget(), depth = 0) => {
          observeTraversal(budget, lstatSync(tree), depth);
          for (const entry of readdirSync(tree, { withFileTypes: true })) {
            const child = path.join(tree, entry.name);
            if (entry.isDirectory()) {
              assertNoDeclarations(child, budget, depth + 1);
            } else if (entry.name.endsWith('.d.ts') || entry.name.endsWith('.d.ts.map')) {
              fail('phase1cb-sdk-declarations-destination-not-clean');
            } else {
              observeTraversal(budget, lstatSync(child), depth + 1);
            }
          }
        };

        const removeOrdinaryTree = (root) => {
          const budget = createTraversalBudget();
          const stack = [{ pathname: root, depth: 0, visited: false }];
          while (stack.length !== 0) {
            const item = stack.pop();
            const metadata = lstatSync(item.pathname);
            if (metadata.isSymbolicLink()) fail('phase1cb-sdk-declarations-reparse');
            if (item.visited) {
              rmdirSync(item.pathname);
              continue;
            }
            observeTraversal(budget, metadata, item.depth);
            if (metadata.isFile()) {
              unlinkSync(item.pathname);
              continue;
            }
            if (!metadata.isDirectory()) fail('phase1cb-sdk-declarations-type');
            stack.push({ ...item, visited: true });
            for (const entry of readdirSync(item.pathname, { withFileTypes: true })) {
              stack.push({ pathname: path.join(item.pathname, entry.name), depth: item.depth + 1, visited: false });
            }
          }
        };

        const scriptPath = path.resolve(fileURLToPath(import.meta.url));
        const toolchainRoot = path.dirname(scriptPath);
        const buildRoot = path.dirname(toolchainRoot);
        const sdkRoot = path.join(buildRoot, 'packages', 'twenty-sdk');
        const literalCwd = path.resolve(process.cwd());
        if (!samePath(literalCwd, sdkRoot) ||
            !samePath(realpathSync.native(literalCwd), realpathSync.native(sdkRoot))) {
          fail('phase1cb-sdk-declarations-identity');
        }
        for (const identity of [buildRoot, toolchainRoot, sdkRoot]) {
          const metadata = lstatSync(identity, { throwIfNoEntry: false });
          if (metadata === undefined || !metadata.isDirectory() || metadata.isSymbolicLink()) {
            fail('phase1cb-sdk-declarations-reparse');
          }
        }
        const expectedScript = path.join(toolchainRoot, 'promote-sdk-declarations.mjs');
        if (!samePath(scriptPath, expectedScript)) {
          fail('phase1cb-sdk-declarations-identity');
        }

        const distRoot = path.join(sdkRoot, 'dist');
        const generatedRoot = path.join(distRoot, 'sdk');
        assertContained(distRoot, sdkRoot);
        assertContained(generatedRoot, distRoot);
        assertOrdinaryTree(generatedRoot);
        for (const publicExport of PUBLIC_EXPORTS) {
          const source = path.join(generatedRoot, publicExport);
          const destination = path.join(distRoot, publicExport);
          assertContained(source, generatedRoot);
          assertContained(destination, distRoot);
          assertOrdinaryTree(source);
          assertOrdinaryTree(destination);
          assertNoDeclarations(destination);
        }

        for (const publicExport of PUBLIC_EXPORTS) {
          copyOrdinaryTree(
            path.join(generatedRoot, publicExport),
            path.join(distRoot, publicExport),
          );
        }
        assertOrdinaryTree(generatedRoot);
        removeOrdinaryTree(generatedRoot);
        """;

    private static readonly string[] RuntimeWorkspaces =
    [
        "twenty-server",
        "twenty-emails",
        "twenty-shared",
        "twenty-client-sdk",
    ];

    private sealed record NativePruningInventory(
        string PackagePath,
        string PackageName,
        string Version,
        IReadOnlySet<string> AllNativePaths,
        IReadOnlySet<string> PrunedNativePaths);

    private sealed class TreeTraversalBudget(string reason, int maximumDepth = 64)
    {
        private const int MaximumEntries = 250_000;
        private const long MaximumAggregateBytes = 16L * 1024 * 1024 * 1024;
        private readonly int _maximumDepth = maximumDepth;
        private int _entries;
        private long _aggregateBytes;

        internal void ObserveDirectory(int depth) => Observe(depth, 0);

        internal void ObserveFile(FileInfo file, int depth)
        {
            long length;
            try
            {
                length = file.Length;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw Failure(reason);
            }
            Observe(depth, length);
        }

        internal void ObserveEntry(int depth) => Observe(depth, 0);

        private void Observe(int depth, long bytes)
        {
            if (depth < 0 || depth > _maximumDepth || bytes < 0 ||
                _entries == MaximumEntries ||
                _aggregateBytes > MaximumAggregateBytes - bytes)
            {
                throw Failure(reason);
            }
            _entries++;
            _aggregateBytes += bytes;
        }
    }

    private static readonly IReadOnlyList<NativePruningInventory> NativePruningInventories =
        CreateNativePruningInventories();

    private readonly IProductionPayloadProcessRunner _runner;
    private readonly IProductionNodeTrustVerifier _nodeTrust;
    private readonly IProductionMinGitTrustVerifier _minGitTrust;
    private readonly IProductionMinGitProbe _minGitProbe;

    public ProductionPayloadBuilder()
        : this(
            new ProductionPayloadProcessRunner(),
            new ProductionNodeTrustVerifier(),
            new ProductionMinGitTrustVerifier(),
            new ProductionMinGitProbe())
    {
    }

    public ProductionPayloadBuilder(
        IProductionPayloadProcessRunner runner,
        IProductionNodeTrustVerifier nodeTrust)
        : this(
            runner,
            nodeTrust,
            new ProductionMinGitTrustVerifier(),
            new ProductionMinGitProbe())
    {
    }

    internal ProductionPayloadBuilder(
        IProductionPayloadProcessRunner runner,
        IProductionNodeTrustVerifier nodeTrust,
        IProductionMinGitTrustVerifier minGitTrust)
        : this(runner, nodeTrust, minGitTrust, new ProductionMinGitProbe())
    {
    }

    internal ProductionPayloadBuilder(
        IProductionPayloadProcessRunner runner,
        IProductionNodeTrustVerifier nodeTrust,
        IProductionMinGitTrustVerifier minGitTrust,
        IProductionMinGitProbe minGitProbe)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _nodeTrust = nodeTrust ?? throw new ArgumentNullException(nameof(nodeTrust));
        _minGitTrust = minGitTrust ?? throw new ArgumentNullException(nameof(minGitTrust));
        _minGitProbe = minGitProbe ?? throw new ArgumentNullException(nameof(minGitProbe));
    }

    public ProductionPayloadBuildResult Build(ProductionPayloadBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ValidateBuildRequest(request);
        VerifyPinnedNode(request.NodeArchivePath, request.NodeExecutable);
        NormalizeStagedTwentySdkProject(
            request.BuildRoot,
            request.SourceTwentySdkProjectPath);
        RunBuildCommands(
            request.NodeExecutable,
            Path.Combine(request.BuildRoot, ".phase1cb-toolchain", "mingit"),
            request.BuildRoot,
            request.CompiledDesktopNodeRoot);
        VerifyPinnedNode(request.NodeArchivePath, request.NodeExecutable);
        PlaceBuiltFrontend(request.BuildRoot);
        return Materialize(new ProductionPayloadMaterializationRequest(
            request.BuildRoot,
            request.PayloadRoot,
            request.NodeExecutable,
            request.EntrypointInventoryPath,
            request.Header,
            request.ClosureOnly,
            request.CompiledDesktopNodeRoot,
            request.NodeArchivePath));
    }

    private static ProductionPayloadBuildRequest ValidateBuildRequest(
        ProductionPayloadBuildRequest request)
    {
        var buildRoot = CanonicalExistingDirectory(request.BuildRoot);
        var archive = CanonicalExistingFile(request.NodeArchivePath);
        var node = CanonicalExistingFile(request.NodeExecutable);
        var inventory = CanonicalExistingFile(request.EntrypointInventoryPath);
        var sourceProject = CanonicalExistingFile(request.SourceTwentySdkProjectPath);
        var payload = ValidateAvailableDestination(request.PayloadRoot);
        var compiled = ValidateExistingOrAvailableDirectory(request.CompiledDesktopNodeRoot);
        if (!IsSameOrUnder(node, buildRoot) ||
            !IsSameOrUnder(inventory, buildRoot) ||
            !IsSameOrUnder(compiled, buildRoot) ||
            StringComparer.OrdinalIgnoreCase.Equals(compiled, buildRoot) ||
            IsSameOrUnder(payload, buildRoot) ||
            IsSameOrUnder(buildRoot, payload))
        {
            throw Failure("production-build-path-invalid");
        }
        return request with
        {
            NodeArchivePath = archive,
            NodeExecutable = node,
            BuildRoot = buildRoot,
            PayloadRoot = payload,
            EntrypointInventoryPath = inventory,
            CompiledDesktopNodeRoot = compiled,
            SourceTwentySdkProjectPath = sourceProject,
        };
    }

    public void VerifyPinnedNode(string archivePath, string executablePath)
    {
        if (!Path.IsPathFullyQualified(archivePath) ||
            !Path.IsPathFullyQualified(executablePath))
        {
            throw Failure("production-build-path-invalid");
        }
        var archive = CanonicalExistingFile(archivePath);
        var executable = CanonicalExistingFile(executablePath);
        var trust = _nodeTrust.Inspect(archive, executable);
        if (!StringComparer.Ordinal.Equals(trust.ArchiveSha256, NodeArchiveSha256) ||
            !StringComparer.Ordinal.Equals(trust.ExecutableSha256, NodeExecutableSha256) ||
            !StringComparer.Ordinal.Equals(trust.AuthenticodeSubject, NodeAuthenticodeSubject) ||
            !trust.ChainTrusted)
        {
            throw Failure("production-node-identity-invalid");
        }
    }

    public void RunBuildCommands(
        string nodeExecutable,
        string minGitRoot,
        string buildRoot,
        string compiledDesktopNodeRoot)
    {
        var node = CanonicalExistingFile(nodeExecutable);
        var minGit = CanonicalExistingDirectory(minGitRoot);
        var git = CanonicalExistingFile(Path.Combine(minGit, "cmd", "git.exe"));
        var root = CanonicalExistingDirectory(buildRoot);
        var compiled = ValidateExistingOrAvailableDirectory(compiledDesktopNodeRoot);
        if (!IsSameOrUnder(node, root) ||
            !StringComparer.Ordinal.Equals(
                minGit,
                Path.Combine(root, ".phase1cb-toolchain", "mingit")) ||
            !StringComparer.Ordinal.Equals(git, Path.Combine(minGit, "cmd", "git.exe")) ||
            !IsSameOrUnder(compiled, root) ||
            StringComparer.OrdinalIgnoreCase.Equals(compiled, root))
        {
            throw Failure("production-build-path-invalid");
        }
        VerifyPinnedMinGit(git);
        ValidatePinnedMinGitLocatorLock(root);
        WriteResolutionLedger(root, minGit, compiled);
        _minGitProbe.Verify(git, root, minGit);
        VerifyPinnedMinGit(git);
        ValidateMinGitTrace(root, minGit);
        var commands = CommandPlan(node, minGit, root, compiled);
        for (var index = 0; index < commands.Count; index++)
        {
            var ordinal = index + 1;
            try
            {
                _runner.Run(commands[index]);
                if (index == 0)
                {
                    VerifyPinnedMinGit(git);
                    ValidatePinnedMinGitLocatorLock(root);
                    _minGitProbe.VerifyLocatorCacheIsAbsent(root);
                    try
                    {
                        ValidateMinGitTrace(root, minGit);
                    }
                    catch (ProductionPayloadBuildException error)
                        when (!StringComparer.Ordinal.Equals(
                            error.ReasonCode,
                            "production-build-git-trace-invalid"))
                    {
                        throw Failure("production-build-git-trace-invalid");
                    }
                }
            }
            catch (ProductionPayloadBuildException error)
                when (StringComparer.Ordinal.Equals(
                    error.ReasonCode,
                    "production-build-process-start-failed"))
            {
                throw Failure($"production-build-command-{ordinal}-start");
            }
            catch (ProductionPayloadBuildException error)
                when (StringComparer.Ordinal.Equals(
                    error.ReasonCode,
                    "production-build-process-timeout"))
            {
                throw Failure($"production-build-command-{ordinal}-timeout");
            }
            catch (ProductionPayloadBuildException error)
                when (StringComparer.Ordinal.Equals(
                          error.ReasonCode,
                          "production-build-git-trace-invalid") ||
                      StringComparer.Ordinal.Equals(
                          error.ReasonCode,
                          "production-build-git-lock-invalid") ||
                      StringComparer.Ordinal.Equals(
                          error.ReasonCode,
                          "production-build-git-cache-invalid") ||
                      StringComparer.Ordinal.Equals(
                          error.ReasonCode,
                          "production-mingit-identity-invalid"))
            {
                throw;
            }
            catch (ProductionPayloadBuildException error)
                when (StringComparer.Ordinal.Equals(
                    error.ReasonCode,
                    "production-build-process-failed"))
            {
                throw Failure($"production-build-command-{ordinal}-failed", error.Diagnostic);
            }
            catch
            {
                throw Failure($"production-build-command-{ordinal}-failed");
            }
        }
        VerifyPinnedMinGit(git);
    }

    private static void ValidatePinnedMinGitLocatorLock(string buildRoot)
    {
        var text = ReadBoundedUtf8Text(
            Path.Combine(buildRoot, "yarn.lock"),
            16 * 1024 * 1024,
            "production-build-git-lock-invalid");
        var key = "\"@electron/node-gyp@git+" + MinGitLocatorUrl + "#" +
            MinGitLocatorCommit + "\":";
        var resolution = "  resolution: \"@electron/node-gyp@" + MinGitLocatorUrl +
            "#commit=" + MinGitLocatorCommit + "\"";
        var dependency = "    \"@electron/node-gyp\": \"git+" + MinGitLocatorUrl +
            "#" + MinGitLocatorCommit + "\"";
        var keyCount = 0;
        var resolutionCount = 0;
        var dependencyCount = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            keyCount += StringComparer.Ordinal.Equals(line, key) ? 1 : 0;
            resolutionCount += StringComparer.Ordinal.Equals(line, resolution) ? 1 : 0;
            dependencyCount += StringComparer.Ordinal.Equals(line, dependency) ? 1 : 0;
        }
        if (keyCount != 1 || resolutionCount != 1 || dependencyCount != 1)
        {
            throw Failure("production-build-git-lock-invalid");
        }
    }

    internal static void ValidatePinnedMinGitLocatorCacheIsAbsent(string buildRoot)
    {
        var root = CanonicalExistingDirectory(buildRoot);
        var cache = Path.Combine(
            root,
            ".phase1cb-cache",
            "yarn",
            MinGitLocatorCacheFileName);
        try
        {
            _ = File.GetAttributes(cache);
            throw Failure("production-build-git-cache-invalid");
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (ProductionPayloadBuildException)
        {
            throw;
        }
        catch
        {
            throw Failure("production-build-git-cache-invalid");
        }
    }

    private void VerifyPinnedMinGit(string executablePath)
    {
        var executable = CanonicalExistingFile(executablePath);
        var trust = _minGitTrust.Inspect(executable);
        if (trust.ExecutableLength != MinGitExecutableLength ||
            !StringComparer.Ordinal.Equals(
                trust.ExecutableSha256,
                MinGitExecutableSha256) ||
            !StringComparer.Ordinal.Equals(
                trust.AuthenticodeSubject,
                MinGitAuthenticodeSubject) ||
            !trust.ChainTrusted)
        {
            throw Failure("production-mingit-identity-invalid");
        }
    }

    private static void ValidateMinGitTrace(string buildRoot, string minGitRoot)
    {
        const int maximumTraceBytes = 8 * 1024 * 1024;
        const int maximumTraceRecords = 50_000;
        const int maximumArgumentCount = 256;
        const int maximumArgumentBytes = 16 * 1024;
        string expectedGit;
        string trace;
        try
        {
            expectedGit = CanonicalExistingFile(Path.Combine(minGitRoot, "cmd", "git.exe"));
            trace = CanonicalExistingFile(Path.Combine(
                buildRoot,
                ".phase1cb-cache",
                "git-trace",
                "event.jsonl"));
        }
        catch (ProductionPayloadBuildException)
        {
            throw Failure("production-build-git-trace-invalid");
        }
        if (!StringComparer.Ordinal.Equals(
                expectedGit,
                Path.Combine(buildRoot, ".phase1cb-toolchain", "mingit", "cmd", "git.exe")))
        {
            throw Failure("production-build-git-trace-invalid");
        }
        var text = ReadBoundedUtf8Text(
            trace,
            maximumTraceBytes,
            "production-build-git-trace-invalid");
        var versionObserved = false;
        var locatorObserved = false;
        var commitObserved = false;
        var recordCount = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
            {
                continue;
            }
            if (++recordCount > maximumTraceRecords ||
                Encoding.UTF8.GetByteCount(line) > 64 * 1024)
            {
                throw Failure("production-build-git-trace-invalid");
            }
            try
            {
                using var document = JsonDocument.Parse(
                    line,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 16,
                    });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw Failure("production-build-git-trace-invalid");
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw Failure("production-build-git-trace-invalid");
                    }
                }
                if (!root.TryGetProperty("event", out var eventProperty) ||
                    eventProperty.ValueKind != JsonValueKind.String)
                {
                    throw Failure("production-build-git-trace-invalid");
                }
                var eventName = eventProperty.GetString();
                if (StringComparer.Ordinal.Equals(eventName, "version"))
                {
                    versionObserved |= root.TryGetProperty("exe", out var executableVersion) &&
                        executableVersion.ValueKind == JsonValueKind.String &&
                        StringComparer.Ordinal.Equals(
                            executableVersion.GetString(),
                            MinGitVersion);
                    continue;
                }
                if (!StringComparer.Ordinal.Equals(eventName, "start"))
                {
                    continue;
                }
                if (!root.TryGetProperty("argv", out var arguments) ||
                    arguments.ValueKind != JsonValueKind.Array ||
                    arguments.GetArrayLength() == 0 ||
                    arguments.GetArrayLength() > maximumArgumentCount)
                {
                    throw Failure("production-build-git-trace-invalid");
                }
                var argumentValues = new List<string>();
                foreach (var argument in arguments.EnumerateArray())
                {
                    if (argument.ValueKind != JsonValueKind.String ||
                        argument.GetString() is not { } value ||
                        Encoding.UTF8.GetByteCount(value) > maximumArgumentBytes)
                    {
                        throw Failure("production-build-git-trace-invalid");
                    }
                    argumentValues.Add(value);
                }
                var executableLeaf = Path.GetFileName(argumentValues[0]);
                if (!StringComparer.OrdinalIgnoreCase.Equals(executableLeaf, "git.exe") &&
                    !StringComparer.OrdinalIgnoreCase.Equals(executableLeaf, "git"))
                {
                    continue;
                }
                locatorObserved |= argumentValues.Contains(
                    MinGitLocatorUrl,
                    StringComparer.Ordinal);
                commitObserved |= argumentValues.Contains(
                    MinGitLocatorCommit,
                    StringComparer.Ordinal);
            }
            catch (ProductionPayloadBuildException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException or OverflowException)
            {
                throw Failure("production-build-git-trace-invalid");
            }
        }
        if (recordCount == 0 || !versionObserved || !locatorObserved || !commitObserved)
        {
            throw Failure("production-build-git-trace-invalid");
        }
    }

    private static void WriteResolutionLedger(
        string buildRoot,
        string minGitRoot,
        string compiledDesktopNodeRoot)
    {
        for (var ancestor = Directory.GetParent(buildRoot);
             ancestor is not null;
             ancestor = ancestor.Parent)
        {
            var candidate = Path.Combine(ancestor.FullName, "node_modules");
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                throw Failure("production-build-ancestor-node-modules");
            }
        }
        var cacheRoot = Path.Combine(buildRoot, ".phase1cb-cache");
        var yarnCache = Path.Combine(cacheRoot, "yarn");
        var nxCache = Path.Combine(cacheRoot, "nx");
        var nxWorkspace = Path.Combine(cacheRoot, "nx-workspace");
        var gitTrace = Path.Combine(cacheRoot, "git-trace");
        foreach (var directory in new[] { yarnCache, nxCache, nxWorkspace, gitTrace })
        {
            CreateValidatedDirectoryTree(directory);
        }
        if (!IsSameOrUnder(compiledDesktopNodeRoot, buildRoot))
        {
            throw Failure("production-build-path-invalid");
        }
        var bytes = new UTF8Encoding(false).GetBytes(
            "{\"schemaVersion\":1," +
            "\"ancestorNodeModules\":\"rejected\"," +
            "\"dependencyRoot\":\"node_modules\"," +
            "\"initialInstall\":\"full-non-production\"," +
            "\"immutableInstalls\":true," +
            "\"buildOnlyMinGit\":{" +
            "\"version\":\"" + MinGitVersion + "\"," +
            "\"archiveSha256\":\"" + MinGitArchiveSha256 + "\"," +
            "\"executableSha256\":\"" + MinGitExecutableSha256 + "\"," +
            "\"executablePath\":\"" +
            Path.GetRelativePath(buildRoot, Path.Combine(minGitRoot, "cmd", "git.exe"))
                .Replace(Path.DirectorySeparatorChar, '/') + "\"," +
            "\"locatorUrl\":\"" + MinGitLocatorUrl + "\"," +
            "\"locatorCommit\":\"" + MinGitLocatorCommit + "\"," +
            "\"lockBinding\":\"exact-yarn-lock-entry\"," +
            "\"canary\":\"supervised-depth-1-exact-commit-fetch\"," +
            "\"excludedLocatorCachePath\":\".phase1cb-cache/yarn/" +
            MinGitLocatorCacheFileName + "\"," +
            "\"excludedLocatorCacheDisposition\":\"must-be-absent\"," +
            "\"payloadDisposition\":\"excluded\"}," +
            "\"yarnCacheRoot\":\".phase1cb-cache/yarn\"," +
            "\"nxCacheRoot\":\".phase1cb-cache/nx\"," +
            "\"nxWorkspaceDataRoot\":\".phase1cb-cache/nx-workspace\"," +
            "\"gitTraceRoot\":\".phase1cb-cache/git-trace\"," +
            "\"gitProbeRoot\":\".phase1cb-cache/git-probe\"," +
            "\"compiledOutputRoot\":\"" +
            Path.GetRelativePath(buildRoot, compiledDesktopNodeRoot)
                .Replace(Path.DirectorySeparatorChar, '/') + "\"}");
        WriteAtomic(Path.Combine(buildRoot, ".phase1cb-resolution-ledger-v1.json"), bytes);
    }

    public ProductionPayloadBuildResult Materialize(
        ProductionPayloadMaterializationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buildRoot = CanonicalExistingDirectory(request.BuildRoot);
        var payloadRoot = ValidateAvailableDestination(request.PayloadRoot);
        var nodeExecutable = CanonicalExistingFile(request.NodeExecutable);
        var inventoryPath = CanonicalExistingFile(request.EntrypointInventoryPath);
        if (!IsSameOrUnder(nodeExecutable, buildRoot) ||
            !IsSameOrUnder(inventoryPath, buildRoot) ||
            IsSameOrUnder(payloadRoot, buildRoot) ||
            IsSameOrUnder(buildRoot, payloadRoot))
        {
            throw Failure("production-build-path-invalid");
        }
        if (File.Exists(Path.Combine(buildRoot, ".source-node-modules-sentinel")))
        {
            throw Failure("production-build-source-node-modules");
        }
        var inventory = ProductionEntrypointInventoryV1.Parse(ReadBoundedUtf8Bytes(
            inventoryPath,
            MaximumEntrypointInventoryBytes,
            "production-entrypoint-inventory-mismatch"));
        try
        {
            CreateValidatedDirectoryTree(payloadRoot);
            if (request.NodeArchivePath is not null)
            {
                VerifyPinnedNode(request.NodeArchivePath, nodeExecutable);
            }
            var sourceNodeHash = HashFileSha256(nodeExecutable);
            var payloadNode = Path.Combine(payloadRoot, "node.exe");
            CopyOrdinaryFile(nodeExecutable, payloadNode);
            if (!sourceNodeHash.AsSpan().SequenceEqual(HashFileSha256(payloadNode)))
            {
                throw Failure("production-node-identity-invalid");
            }
            if (request.NodeArchivePath is not null)
            {
                VerifyPinnedNode(request.NodeArchivePath, payloadNode);
            }
            CopyRequiredFile(buildRoot, payloadRoot, "package.json");
            foreach (var workspace in RuntimeWorkspaces)
            {
                CopyWorkspaceProjection(
                    Path.Combine(buildRoot, "packages", workspace),
                    Path.Combine(payloadRoot, "packages", workspace),
                    buildRoot);
            }
            CopyProductionNodeModules(buildRoot, payloadRoot);

            MaterializeDeclaredLaunchWrappers(
                inventory,
                buildRoot,
                payloadRoot,
                request.CompiledDesktopNodeRoot);
            ValidateInventory(
                inventory,
                buildRoot,
                payloadRoot,
                request.Header,
                request.ClosureOnly);
            ValidateRuntimeClosure(payloadRoot, Path.Combine(payloadRoot, "node.exe"));

            if (!request.ClosureOnly && string.IsNullOrWhiteSpace(request.NodeArchivePath))
            {
                throw Failure("production-node-identity-invalid");
            }

            if (request.ClosureOnly)
            {
                return new ProductionPayloadBuildResult(
                    "untrusted-build-closure",
                    payloadRoot,
                    null,
                    null);
            }

            var manifest = CreateManifest(payloadRoot, request.Header);
            var catalog = new ProductionReleaseCatalog(
                true,
                manifest.CanonicalDigest,
                request.Header.BuildIdentity);
            WriteAtomic(
                Path.Combine(payloadRoot, ProductionPayloadValidator.ManifestFileName),
                manifest.Serialize());
            return new ProductionPayloadBuildResult(
                "trusted-production-payload",
                payloadRoot,
                manifest,
                catalog);
        }
        catch
        {
            if (Directory.Exists(payloadRoot))
            {
                DeleteValidatedTreeNoFollow(payloadRoot);
            }
            throw;
        }
    }

    public ProductionPayloadManifestV2 CreateManifest(
        string payloadRoot,
        ProductionPayloadHeader header)
    {
        var root = CanonicalExistingDirectory(payloadRoot);
        var records = EnumerateOrdinaryFiles(root)
            .Where(path => !StringComparer.Ordinal.Equals(
                path,
                ProductionPayloadValidator.ManifestFileName))
            .Select((relative, ordinal) =>
            {
                var absolute = Path.Combine(
                    root,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                using var stream = new FileStream(
                    absolute,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.SequentialScan);
                return new ProductionPayloadFileRecord(
                    ordinal,
                    relative,
                    stream.Length,
                    Convert.ToHexString(SHA256.HashData(stream)));
            })
            .ToArray();
        var digest = ProductionPayloadCanonicalizer.ComputeDigest(header, records);
        return new ProductionPayloadManifestV2(header, Array.AsReadOnly(records), digest);
    }

    public void VerifyManifestWithTemporaryProfile(
        string payloadRoot,
        ProductionReleaseCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var root = CanonicalExistingDirectory(payloadRoot);
        var parent = CanonicalExistingDirectory(Path.GetDirectoryName(root)!);
        var profile = Path.Combine(
            parent,
            "payload-tool-profile-" + Guid.NewGuid().ToString("N"));
        CreateValidatedDirectoryTree(profile);
        try
        {
            using var payload = new ProductionPayloadValidator().Validate(
                root,
                profile,
                catalog);
            payload.VerifyClosure();
        }
        finally
        {
            DeleteOwnedTemporaryProfile(profile, parent);
        }
    }

    public void MaterializeInternalLink(
        string buildRoot,
        string linkPath,
        string destinationPath)
    {
        var root = CanonicalExistingDirectory(buildRoot);
        var link = CanonicalAbsolute(linkPath);
        var metadata = new DirectoryInfo(link);
        if (!metadata.Exists || !metadata.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Failure("production-build-link-invalid");
        }
        var target = metadata.ResolveLinkTarget(returnFinalTarget: true);
        if (target is null)
        {
            throw Failure("production-build-link-invalid");
        }
        var canonicalTarget = CanonicalExistingDirectory(target.FullName);
        if (!IsSameOrUnder(canonicalTarget, root))
        {
            throw Failure("production-build-link-outside-root");
        }
        var destination = ValidateAvailableDestination(destinationPath);
        CopyWorkspaceProjection(canonicalTarget, destination, root);
    }

    public void PlaceBuiltFrontend(string buildRoot)
    {
        var root = CanonicalExistingDirectory(buildRoot);
        var source = CanonicalExistingDirectory(Path.Combine(
            root,
            "packages",
            "twenty-front",
            "build"));
        var destination = CanonicalAbsolute(Path.Combine(
            root,
            "packages",
            "twenty-server",
            "dist",
            "front"));
        if (!IsSameOrUnder(source, root) ||
            !IsSameOrUnder(destination, root))
        {
            throw Failure("production-frontend-placement-invalid");
        }
        destination = ValidateAvailableDestination(destination);
        CopyTree(source, destination, root);
    }

    public void ApplyAndVerifyInstalledReadExecuteAcl(string payloadRoot)
    {
        var root = CanonicalExistingDirectory(payloadRoot);
        _ = EnumerateOrdinaryFiles(root);
        var runtime = WindowsIdentity.GetCurrent().User ??
            throw Failure("production-installed-acl-invalid");
        var entries = EnumerateOrdinaryEntries(root)
            .OrderByDescending(entry => entry.FullName.Length)
            .Append(new DirectoryInfo(root));
        foreach (var entry in entries)
        {
            var canonical = CanonicalAbsolute(entry.FullName);
            if (!IsSameOrUnder(canonical, root) ||
                entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Failure("production-build-reparse-point");
            }
            var directory = entry is DirectoryInfo;
            var descriptor = CreateInstalledReadExecuteDescriptor(runtime, directory);
            try
            {
                if (directory)
                {
                    var security = new DirectorySecurity();
                    security.SetSecurityDescriptorBinaryForm(ToBytes(descriptor));
                    ((DirectoryInfo)entry).SetAccessControl(security);
                    VerifyInstalledAcl(((DirectoryInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
                }
                else
                {
                    var security = new FileSecurity();
                    security.SetSecurityDescriptorBinaryForm(ToBytes(descriptor));
                    ((FileInfo)entry).SetAccessControl(security);
                    VerifyInstalledAcl(((FileInfo)entry).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
                }
            }
            catch (ProductionPayloadBuildException)
            {
                throw;
            }
            catch
            {
                throw Failure("production-installed-acl-invalid");
            }
        }
    }

    internal static RawSecurityDescriptor CreateInstalledReadExecuteDescriptor(
        SecurityIdentifier runtime,
        bool directory)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        var system = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            domainSid: null);
        var users = new SecurityIdentifier(
            WellKnownSidType.BuiltinUsersSid,
            domainSid: null);
        var interactive = new SecurityIdentifier(
            WellKnownSidType.InteractiveSid,
            domainSid: null);
        var inheritance = directory
            ? AceFlags.ContainerInherit | AceFlags.ObjectInherit
            : AceFlags.None;
        var dacl = new RawAcl(GenericAcl.AclRevision, capacity: 5);
        dacl.InsertAce(0, new CommonAce(
            inheritance,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            administrators,
            isCallback: false,
            opaque: null));
        dacl.InsertAce(1, new CommonAce(
            inheritance,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            system,
            isCallback: false,
            opaque: null));
        dacl.InsertAce(2, new CommonAce(
            inheritance,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.ReadAndExecute,
            runtime,
            isCallback: false,
            opaque: null));
        dacl.InsertAce(3, new CommonAce(
            inheritance,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.ReadAndExecute,
            users,
            isCallback: false,
            opaque: null));
        dacl.InsertAce(4, new CommonAce(
            inheritance,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.ReadAndExecute,
            interactive,
            isCallback: false,
            opaque: null));
        return new RawSecurityDescriptor(
            ControlFlags.SelfRelative |
            ControlFlags.DiscretionaryAclPresent |
            ControlFlags.DiscretionaryAclProtected,
            administrators,
            administrators,
            systemAcl: null,
            discretionaryAcl: dacl);
    }

    public ProductionWindowsBuildNormalizationResult NormalizeStagedTwentySdkProject(
        string buildRoot,
        string sourceProjectPath)
    {
        var root = CanonicalExistingDirectory(buildRoot);
        var source = CanonicalExistingFile(sourceProjectPath);
        var staged = CanonicalExistingFile(Path.Combine(
            root,
            "packages",
            "twenty-sdk",
            "project.json"));
        if (!IsSameOrUnder(staged, root) || IsSameOrUnder(source, root))
        {
            throw Failure("production-windows-build-normalization-path-invalid");
        }
        var sourceBytes = ReadBoundedUtf8Bytes(
            source,
            MaximumNormalizationJsonBytes,
            "production-windows-build-normalization-drift");
        var stagedBytes = ReadBoundedUtf8Bytes(
            staged,
            MaximumNormalizationJsonBytes,
            "production-windows-build-normalization-drift");
        var sourceDigest = Convert.ToHexString(SHA256.HashData(sourceBytes));
        if (!StringComparer.Ordinal.Equals(sourceDigest, TwentySdkProjectPreimageSha256) ||
            !sourceBytes.AsSpan().SequenceEqual(stagedBytes))
        {
            throw Failure("production-windows-build-normalization-drift");
        }
        var text = new UTF8Encoding(false, true).GetString(stagedBytes);
        using (var document = JsonDocument.Parse(stagedBytes))
        {
            var commands = document.RootElement
                .GetProperty("targets")
                .GetProperty("build")
                .GetProperty("options")
                .GetProperty("commands");
            if (commands.ValueKind != JsonValueKind.Array ||
                commands.GetArrayLength() != 3 ||
                !StringComparer.Ordinal.Equals(
                    commands[2].GetString(),
                    TwentySdkWindowsBuildPreimage))
            {
                throw Failure("production-windows-build-normalization-drift");
            }
            var buildSdk = document.RootElement
                .GetProperty("targets")
                .GetProperty("build:sdk");
            var buildSdkDependencies = buildSdk.GetProperty("dependsOn");
            if (buildSdkDependencies.ValueKind != JsonValueKind.Array ||
                buildSdkDependencies.GetArrayLength() != 1 ||
                !StringComparer.Ordinal.Equals(
                    buildSdkDependencies[0].GetString(),
                    "^build") ||
                !StringComparer.Ordinal.Equals(
                    buildSdk.GetProperty("options").GetProperty("command").GetString(),
                    TwentySdkBuildSdkCommandPreimage))
            {
                throw Failure("production-windows-build-normalization-drift");
            }
        }
        if (CountOccurrences(text, TwentySdkWindowsBuildPreimage) != 2 ||
            TwentySdkWindowsBuildPreimage.Count(character => character == '\'') != 22 ||
            CountOccurrences(text, TwentySdkBuildSdkCommandPreimage) != 1 ||
            CountOccurrences(text, TwentySdkBuildSdkTargetMarker) != 1)
        {
            throw Failure("production-windows-build-normalization-drift");
        }
        var commandOffset = text.IndexOf(TwentySdkWindowsBuildPreimage, StringComparison.Ordinal);
        var buildSdkTargetOffset = text.IndexOf(TwentySdkBuildSdkTargetMarker, StringComparison.Ordinal);
        var buildSdkCommandOffset = text.IndexOf(
            TwentySdkBuildSdkCommandPreimage,
            buildSdkTargetOffset,
            StringComparison.Ordinal);
        var buildSdkDependsOnOffset = text.IndexOf(
            TwentySdkBuildSdkDependsOnPreimage,
            buildSdkTargetOffset,
            StringComparison.Ordinal);
        if (commandOffset < 0 ||
            buildSdkTargetOffset < 0 ||
            buildSdkCommandOffset < buildSdkTargetOffset ||
            buildSdkDependsOnOffset < buildSdkTargetOffset ||
            buildSdkDependsOnOffset > buildSdkCommandOffset)
        {
            throw Failure("production-windows-build-normalization-drift");
        }
        var normalizedText = text;
        foreach (var replacement in new[]
                 {
                     (buildSdkCommandOffset, TwentySdkBuildSdkCommandPreimage.Length, TwentySdkBuildSdkCommand),
                     (buildSdkDependsOnOffset, TwentySdkBuildSdkDependsOnPreimage.Length, TwentySdkBuildSdkDependsOn),
                     (commandOffset, TwentySdkWindowsBuildPreimage.Length, TwentySdkWindowsBuildCommandJson),
                 }.OrderByDescending(item => item.Item1))
        {
            normalizedText = normalizedText[..replacement.Item1] + replacement.Item3 +
                normalizedText[(replacement.Item1 + replacement.Item2)..];
        }
        var outputBytes = new UTF8Encoding(false).GetBytes(normalizedText);
        var toolchainRoot = Path.Combine(root, ".phase1cb-toolchain");
        if (File.Exists(toolchainRoot))
        {
            throw Failure("production-windows-build-normalization-path-invalid");
        }
        if (Directory.Exists(toolchainRoot))
        {
            var toolchainMetadata = new DirectoryInfo(toolchainRoot);
            if (toolchainMetadata.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Failure("production-windows-build-normalization-path-invalid");
            }
        }
        else
        {
            CreateValidatedDirectoryTree(toolchainRoot);
        }
        var helper = Path.Combine(toolchainRoot, TwentySdkDeclarationPromotionHelper);
        var helperBytes = new UTF8Encoding(false).GetBytes(TwentySdkDeclarationPromotionScript);
        if (File.Exists(helper))
        {
            var helperMetadata = new FileInfo(helper);
            if (helperMetadata.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !helperBytes.AsSpan().SequenceEqual(ReadBoundedUtf8Bytes(
                    helper,
                    MaximumNormalizationHelperBytes,
                    "production-windows-build-normalization-drift")))
            {
                throw Failure("production-windows-build-normalization-drift");
            }
        }
        else
        {
            WriteAtomic(helper, helperBytes);
        }
        var temporary = staged + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, outputBytes);
            using (var document = JsonDocument.Parse(outputBytes))
            {
                var command = document.RootElement
                    .GetProperty("targets")
                    .GetProperty("build")
                    .GetProperty("options")
                    .GetProperty("commands")[2]
                    .GetString();
                if (!StringComparer.Ordinal.Equals(command, TwentySdkWindowsBuildCommand))
                {
                    throw Failure("production-windows-build-normalization-drift");
                }
                var buildSdk = document.RootElement
                    .GetProperty("targets")
                    .GetProperty("build:sdk");
                if (!buildSdk.GetProperty("dependsOn")
                        .EnumerateArray()
                        .Select(item => item.GetString())
                        .SequenceEqual(new[] { "^build", "build" }, StringComparer.Ordinal) ||
                    !StringComparer.Ordinal.Equals(
                        buildSdk.GetProperty("options").GetProperty("command").GetString(),
                        TwentySdkBuildSdkCommand))
                {
                    throw Failure("production-windows-build-normalization-drift");
                }
            }
            File.Move(temporary, staged, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        if (new FileInfo(source).Length != sourceBytes.Length ||
            !SHA256.HashData(sourceBytes).AsSpan().SequenceEqual(HashFileSha256(source)))
        {
            throw Failure("production-windows-build-source-mutated");
        }
        return new ProductionWindowsBuildNormalizationResult(
            sourceDigest,
            Convert.ToHexString(SHA256.HashData(outputBytes)),
            2,
            1,
            Convert.ToHexString(SHA256.HashData(helperBytes)));
    }

    private static IReadOnlyList<ProductionPayloadProcessSpec> CommandPlan(
        string node,
        string minGitRoot,
        string buildRoot,
        string compiledDesktopNodeRoot)
    {
        const string yarn = ".yarn/releases/yarn-4.13.0.cjs";
        var constrainedPath = Path.Combine(buildRoot, ".phase1cb-toolchain") +
            Path.PathSeparator +
            Path.Combine(minGitRoot, "cmd") +
            Path.PathSeparator +
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32");
        IReadOnlyDictionary<string, string> EnvironmentFor(
            string? nodeOptions = null,
            bool immutableInstalls = false)
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = constrainedPath,
                ["NX_DAEMON"] = "false",
                ["YARN_ENABLE_GLOBAL_CACHE"] = "false",
                ["YARN_CACHE_FOLDER"] = Path.Combine(buildRoot, ".phase1cb-cache", "yarn"),
                ["NX_CACHE_DIRECTORY"] = Path.Combine(buildRoot, ".phase1cb-cache", "nx"),
                ["NX_WORKSPACE_DATA_DIRECTORY"] = Path.Combine(
                    buildRoot,
                    ".phase1cb-cache",
                    "nx-workspace"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_SYSTEM"] = "NUL",
                ["GIT_CONFIG_GLOBAL"] = "NUL",
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "Never",
                ["GIT_TRACE2_EVENT"] = Path.Combine(
                    buildRoot,
                    ".phase1cb-cache",
                    "git-trace",
                    "event.jsonl"),
            };
            if (nodeOptions is not null)
            {
                environment["NODE_OPTIONS"] = nodeOptions;
            }
            if (immutableInstalls)
            {
                environment["YARN_ENABLE_IMMUTABLE_INSTALLS"] = "true";
            }
            return environment;
        }
        ProductionPayloadProcessSpec Command(
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment = null) =>
            new(node, arguments, buildRoot, false, environment ?? EnvironmentFor())
            {
                RemovedEnvironmentVariables = ["NODE_OPTIONS", "NODE_PATH"],
                RemovedEnvironmentVariablePrefixes = ["npm_", "YARN_", "COREPACK_"],
            };
        return
        [
            Command(
                [
                    yarn,
                    "workspaces",
                    "focus",
                    "twenty",
                    "twenty-server",
                    "twenty-front",
                    "twenty-emails",
                ],
                EnvironmentFor(immutableInstalls: true)),
            Command([
                yarn, "nx", "run", "twenty-server:lingui:extract",
                "--outputStyle=stream", "--verbose", "--parallel=1",
            ]),
            Command([yarn, "nx", "run", "twenty-server:lingui:compile"]),
            Command([yarn, "nx", "run", "twenty-emails:lingui:extract"]),
            Command([yarn, "nx", "run", "twenty-emails:lingui:compile"]),
            Command([yarn, "nx", "run", "twenty-server:build"]),
            Command([yarn, "nx", "run", "twenty-front:lingui:extract"]),
            Command([yarn, "nx", "run", "twenty-front:lingui:compile"]),
            Command(
                [yarn, "nx", "build", "twenty-front"],
                EnvironmentFor("--max-old-space-size=8192")),
            Command([
                yarn, "exec", "tsc", "--project",
                "desktop/windows/node/tsconfig.publish.json",
                "--outDir", compiledDesktopNodeRoot,
            ]),
            Command([
                yarn, "workspaces", "focus", "--production",
                "twenty-emails", "twenty-shared", "twenty-client-sdk", "twenty-server",
            ], EnvironmentFor(immutableInstalls: true)),
        ];
    }

    private static void MaterializeDeclaredLaunchWrappers(
        ProductionEntrypointInventoryV1 inventory,
        string buildRoot,
        string payloadRoot,
        string? compiledDesktopNodeRoot)
    {
        var launchRecords = inventory.Records
            .OfType<ProductionLaunchExecutableEntrypointV1>()
            .ToArray();
        if (launchRecords.Length == 0)
        {
            return;
        }
        if (compiledDesktopNodeRoot is null)
        {
            throw Failure("production-compiled-entrypoint-root-required");
        }
        var compiledRoot = CanonicalExistingDirectory(compiledDesktopNodeRoot);
        if (!IsSameOrUnder(compiledRoot, buildRoot))
        {
            throw Failure("production-compiled-entrypoint-root-invalid");
        }
        const string sourcePrefix = "desktop/windows/node/src/";
        const string emittedPrefix = "desktop/windows/node/";
        var declaredCompiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var launchRoots = new List<string>();
        foreach (var record in launchRecords)
        {
            if (!record.SourcePath.StartsWith(sourcePrefix, StringComparison.Ordinal) ||
                !record.EmittedPath.StartsWith(emittedPrefix, StringComparison.Ordinal) ||
                !StringComparer.Ordinal.Equals(
                    emittedPrefix + record.SourcePath[sourcePrefix.Length..^3] + ".js",
                    record.EmittedPath))
            {
                throw Failure("production-entrypoint-inventory-mismatch");
            }
            var compiledRelative = record.EmittedPath[emittedPrefix.Length..];
            if (!declaredCompiled.Add(compiledRelative))
            {
                throw Failure("production-entrypoint-inventory-mismatch");
            }
            _ = ExactOrdinaryFile(
                compiledRoot,
                compiledRelative,
                "production-entrypoint-inventory-mismatch");
            launchRoots.Add(compiledRelative);
        }
        var closure = ResolveCompiledJavaScriptClosure(compiledRoot, launchRoots);
        var productionRoot = Path.Combine(compiledRoot, "production");
        if (Directory.Exists(productionRoot))
        {
            var actual = EnumerateOrdinaryFiles(productionRoot)
                .Select(relative => "production/" + relative)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reachableProduction = closure
                .Where(relative => relative.StartsWith("production/", StringComparison.Ordinal))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actual.SetEquals(reachableProduction))
            {
                throw Failure("production-entrypoint-inventory-mismatch");
            }
        }
        foreach (var relative in closure)
        {
            CopyOrdinaryFile(
                Path.Combine(
                    compiledRoot,
                    relative.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(
                    payloadRoot,
                    emittedPrefix.Replace('/', Path.DirectorySeparatorChar),
                    relative.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    private static IReadOnlyList<string> ResolveCompiledJavaScriptClosure(
        string compiledRoot,
        IReadOnlyList<string> launchRoots)
    {
        const string reason = "production-compiled-dependency-invalid";
        var states = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var budget = new TreeTraversalBudget(reason);

        void Visit(string relative, int depth)
        {
            if (states.TryGetValue(relative, out var state))
            {
                if (state == 1)
                {
                    throw Failure(reason);
                }
                return;
            }
            states.Add(relative, 1);
            string file;
            try
            {
                file = ExactOrdinaryFile(compiledRoot, relative, reason);
                budget.ObserveFile(new FileInfo(file), depth);
                EnsureNoAlternateDataStreams(compiledRoot);
                var current = compiledRoot;
                foreach (var segment in relative.Split('/'))
                {
                    current = Path.Combine(current, segment);
                    EnsureNoAlternateDataStreams(current);
                }
            }
            catch (ProductionPayloadBuildException)
            {
                throw Failure(reason);
            }
            foreach (var specifier in ReadCompiledEsmSpecifiers(file))
            {
                var dependency = ResolveCompiledRelativeSpecifier(
                    compiledRoot,
                    relative,
                    specifier);
                if (dependency is not null)
                {
                    Visit(dependency, depth + 1);
                }
            }
            states[relative] = 2;
            closure.Add(relative);
        }

        foreach (var launchRoot in launchRoots.Order(StringComparer.Ordinal))
        {
            Visit(launchRoot, 0);
        }
        return Array.AsReadOnly(closure.Order(StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<string> ReadCompiledEsmSpecifiers(string file)
    {
        const string reason = "production-compiled-dependency-invalid";
        return ReadModuleSpecifiers(
            file,
            reason,
            includeDynamicImports: true,
            sourceSyntax: false);
    }

    private static IReadOnlyList<string> ReadModuleSpecifiers(
        string file,
        string reason,
        bool includeDynamicImports,
        bool sourceSyntax)
    {
        string text;
        try
        {
            text = ReadBoundedUtf8Text(file, 16 * 1024 * 1024, reason);
        }
        catch (Exception exception) when (
            exception is DecoderFallbackException or IOException or UnauthorizedAccessException)
        {
            throw Failure(reason);
        }
        var tokens = new BoundedJavaScriptLexer(text, reason).Tokenize();
        return ParseModuleSpecifiers(
            tokens,
            reason,
            includeDynamicImports,
            sourceSyntax);
    }

    private static IReadOnlyList<string> ParseModuleSpecifiers(
        IReadOnlyList<JavaScriptToken> tokens,
        string reason,
        bool includeDynamicImports,
        bool sourceSyntax)
    {
        var specifiers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind != JavaScriptTokenKind.Identifier)
            {
                continue;
            }
            if (StringComparer.Ordinal.Equals(token.Text, "require") &&
                !IsPropertyToken(tokens, index) &&
                TokenIs(tokens, index + 1, "("))
            {
                throw Failure(reason);
            }
            if (StringComparer.Ordinal.Equals(token.Text, "import"))
            {
                if (IsPropertyToken(tokens, index) ||
                    TokenIs(tokens, index + 1, ":") ||
                    IsObjectMethodNamedImport(tokens, index))
                {
                    continue;
                }
                if (TokenIs(tokens, index + 1, ".") &&
                    TokenIs(tokens, index + 2, "meta"))
                {
                    index += 2;
                    continue;
                }
                if (TokenIs(tokens, index + 1, "("))
                {
                    if (index + 3 >= tokens.Count ||
                        tokens[index + 2].Kind != JavaScriptTokenKind.String ||
                        tokens[index + 2].HasEscape ||
                        !TokenIs(tokens, index + 3, ")"))
                    {
                        throw Failure(reason);
                    }
                    if (includeDynamicImports)
                    {
                        specifiers.Add(tokens[index + 2].Text);
                    }
                    index += 3;
                    continue;
                }
                if (index + 1 >= tokens.Count)
                {
                    throw Failure(reason);
                }
                if (tokens[index + 1].Kind == JavaScriptTokenKind.String)
                {
                    AddStaticModuleSpecifier(tokens[index + 1], specifiers, reason);
                    EnsureModuleDeclarationEnds(tokens, index + 2, reason);
                    index++;
                    continue;
                }
                var typeOnly = TokenIs(tokens, index + 1, "type");
                var from = FindTopLevelFrom(tokens, index + 1, reason);
                if (from + 1 >= tokens.Count ||
                    tokens[from + 1].Kind != JavaScriptTokenKind.String)
                {
                    throw Failure(reason);
                }
                if (sourceSyntax &&
                    !typeOnly &&
                    ContainsUnsupportedTypeOnlyBinding(tokens, index + 1, from))
                {
                    throw Failure(reason);
                }
                if (!typeOnly)
                {
                    AddStaticModuleSpecifier(tokens[from + 1], specifiers, reason);
                }
                EnsureModuleDeclarationEnds(tokens, from + 2, reason);
                index = from + 1;
                continue;
            }
            if (!StringComparer.Ordinal.Equals(token.Text, "export") ||
                IsPropertyToken(tokens, index) ||
                index + 1 >= tokens.Count)
            {
                continue;
            }
            if (TokenIs(tokens, index + 1, "type"))
            {
                continue;
            }
            if (TokenIs(tokens, index + 1, "*"))
            {
                var from = index + 2;
                if (TokenIs(tokens, from, "as"))
                {
                    if (from + 1 >= tokens.Count ||
                        tokens[from + 1].Kind != JavaScriptTokenKind.Identifier)
                    {
                        throw Failure(reason);
                    }
                    from += 2;
                }
                if (!TokenIs(tokens, from, "from") ||
                    from + 1 >= tokens.Count ||
                    tokens[from + 1].Kind != JavaScriptTokenKind.String)
                {
                    throw Failure(reason);
                }
                AddStaticModuleSpecifier(tokens[from + 1], specifiers, reason);
                EnsureModuleDeclarationEnds(tokens, from + 2, reason);
                index = from + 1;
                continue;
            }
            if (!TokenIs(tokens, index + 1, "{"))
            {
                continue;
            }
            var close = FindMatchingToken(tokens, index + 1, "{", "}", reason);
            if (!TokenIs(tokens, close + 1, "from"))
            {
                index = close;
                continue;
            }
            if (sourceSyntax &&
                ContainsUnsupportedTypeOnlyBinding(tokens, index + 2, close))
            {
                throw Failure(reason);
            }
            if (close + 2 >= tokens.Count ||
                tokens[close + 2].Kind != JavaScriptTokenKind.String)
            {
                throw Failure(reason);
            }
            AddStaticModuleSpecifier(tokens[close + 2], specifiers, reason);
            EnsureModuleDeclarationEnds(tokens, close + 3, reason);
            index = close + 2;
        }
        return Array.AsReadOnly(specifiers.Order(StringComparer.Ordinal).ToArray());
    }

    private static bool IsPropertyToken(IReadOnlyList<JavaScriptToken> tokens, int index) =>
        index > 0 && (TokenIs(tokens, index - 1, ".") || TokenIs(tokens, index - 1, "?."));

    private static bool IsObjectMethodNamedImport(
        IReadOnlyList<JavaScriptToken> tokens,
        int index) =>
        TokenIs(tokens, index + 1, "(") &&
        TokenIs(tokens, index + 2, ")") &&
        TokenIs(tokens, index + 3, "{");

    private static bool TokenIs(
        IReadOnlyList<JavaScriptToken> tokens,
        int index,
        string value) =>
        index >= 0 && index < tokens.Count &&
        StringComparer.Ordinal.Equals(tokens[index].Text, value);

    private static void AddStaticModuleSpecifier(
        JavaScriptToken token,
        ISet<string> specifiers,
        string reason)
    {
        if (token.HasEscape || token.Text.Length == 0)
        {
            throw Failure(reason);
        }
        specifiers.Add(token.Text);
    }

    private static int FindTopLevelFrom(
        IReadOnlyList<JavaScriptToken> tokens,
        int start,
        string reason)
    {
        var depth = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            if (TokenIs(tokens, index, "{") ||
                TokenIs(tokens, index, "[") ||
                TokenIs(tokens, index, "("))
            {
                depth++;
            }
            else if (TokenIs(tokens, index, "}") ||
                     TokenIs(tokens, index, "]") ||
                     TokenIs(tokens, index, ")"))
            {
                depth--;
                if (depth < 0)
                {
                    throw Failure(reason);
                }
            }
            else if (depth == 0 && TokenIs(tokens, index, "from"))
            {
                return index;
            }
            else if (depth == 0 && TokenIs(tokens, index, ";"))
            {
                break;
            }
        }
        throw Failure(reason);
    }

    private static int FindMatchingToken(
        IReadOnlyList<JavaScriptToken> tokens,
        int start,
        string open,
        string close,
        string reason)
    {
        var depth = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            if (TokenIs(tokens, index, open))
            {
                depth++;
            }
            else if (TokenIs(tokens, index, close) && --depth == 0)
            {
                return index;
            }
        }
        throw Failure(reason);
    }

    private static bool ContainsUnsupportedTypeOnlyBinding(
        IReadOnlyList<JavaScriptToken> tokens,
        int start,
        int end)
    {
        for (var index = start; index < end; index++)
        {
            if (TokenIs(tokens, index, "type"))
            {
                return true;
            }
        }
        return false;
    }

    private static void EnsureModuleDeclarationEnds(
        IReadOnlyList<JavaScriptToken> tokens,
        int index,
        string reason)
    {
        if (index >= tokens.Count || TokenIs(tokens, index, ";"))
        {
            return;
        }
        if (TokenIs(tokens, index, "with") || TokenIs(tokens, index, "assert") ||
            !tokens[index].LineBreakBefore)
        {
            throw Failure(reason);
        }
    }

    private enum JavaScriptTokenKind
    {
        Identifier,
        String,
        Number,
        RegularExpression,
        Template,
        Punctuator,
    }

    private sealed record JavaScriptToken(
        JavaScriptTokenKind Kind,
        string Text,
        bool HasEscape,
        bool LineBreakBefore,
        bool StatementStartBefore);

    private sealed record JavaScriptDelimiter(
        char Open,
        bool IsControlHeader,
        bool IsStatementBlock);

    private sealed class BoundedJavaScriptLexer
    {
        private const int MaximumTokens = 1_000_000;
        private const int MaximumDelimiterDepth = 256;
        private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(5);
        private static readonly string[] MultiCharacterPunctuators =
        [
            ">>>=", "===", "!==", "**=", "&&=", "||=", "??=", "...", ">>>",
            "=>", "==", "!=", "<=", ">=", "++", "--", "&&", "||", "??", "?.",
            "**", "<<", ">>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
        ];
        private readonly string _text;
        private readonly string _reason;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly List<JavaScriptToken> _tokens = [];
        private readonly Stack<JavaScriptDelimiter> _delimiters = [];
        private int _index;
        private bool _lineBreakBefore;
        private bool _regexAllowed = true;
        private bool _statementStart = true;

        internal BoundedJavaScriptLexer(string text, string reason)
        {
            _text = text;
            _reason = reason;
        }

        internal IReadOnlyList<JavaScriptToken> Tokenize()
        {
            while (_index < _text.Length)
            {
                CheckBounds();
                SkipTrivia();
                if (_index >= _text.Length)
                {
                    break;
                }
                if (_text[_index] == '`')
                {
                    ScanTemplateLiteral();
                }
                else
                {
                    AddToken(ReadOrdinaryToken());
                }
            }
            if (_delimiters.Count != 0)
            {
                Fail();
            }
            return Array.AsReadOnly(_tokens.ToArray());
        }

        private void SkipTrivia()
        {
            while (_index < _text.Length)
            {
                CheckBounds();
                var current = _text[_index];
                if (char.IsWhiteSpace(current))
                {
                    _lineBreakBefore |= IsLineTerminator(current);
                    _index++;
                    continue;
                }
                if (_index == 0 && current == '#' && Peek(1) == '!')
                {
                    _index += 2;
                    SkipLineComment();
                    continue;
                }
                if (current != '/' || _index + 1 >= _text.Length)
                {
                    return;
                }
                if (Peek(1) == '/')
                {
                    _index += 2;
                    SkipLineComment();
                    continue;
                }
                if (Peek(1) != '*')
                {
                    return;
                }
                _index += 2;
                var closed = false;
                while (_index < _text.Length)
                {
                    CheckBounds();
                    _lineBreakBefore |= IsLineTerminator(_text[_index]);
                    if (_text[_index] == '*' && Peek(1) == '/')
                    {
                        _index += 2;
                        closed = true;
                        break;
                    }
                    _index++;
                }
                if (!closed)
                {
                    Fail();
                }
            }
        }

        private void SkipLineComment()
        {
            while (_index < _text.Length && !IsLineTerminator(_text[_index]))
            {
                CheckBounds();
                _index++;
            }
        }

        private JavaScriptToken ReadOrdinaryToken()
        {
            CheckBounds();
            var current = _text[_index];
            if (IsIdentifierStart(current))
            {
                return ReadIdentifier();
            }
            if (char.IsDigit(current))
            {
                return ReadNumber();
            }
            if (current is '\'' or '"')
            {
                return ReadString();
            }
            if (current == '/')
            {
                return _regexAllowed ? ReadRegularExpression() : ReadPunctuator();
            }
            if (current == '\0' || current == '\\' ||
                "{}()[];:,.?~+-*%&|^!<>=#".IndexOf(current) < 0)
            {
                Fail();
            }
            return ReadPunctuator();
        }

        private JavaScriptToken ReadIdentifier()
        {
            var start = _index++;
            while (_index < _text.Length && IsIdentifierPart(_text[_index]))
            {
                _index++;
            }
            return Token(JavaScriptTokenKind.Identifier, _text[start.._index]);
        }

        private JavaScriptToken ReadNumber()
        {
            var start = _index++;
            while (_index < _text.Length &&
                   (char.IsLetterOrDigit(_text[_index]) ||
                    _text[_index] is '_' or '.'))
            {
                _index++;
            }
            return Token(JavaScriptTokenKind.Number, _text[start.._index]);
        }

        private JavaScriptToken ReadString()
        {
            var quote = _text[_index++];
            var value = new StringBuilder();
            var escaped = false;
            while (_index < _text.Length)
            {
                CheckBounds();
                var current = _text[_index++];
                if (current == quote)
                {
                    return Token(JavaScriptTokenKind.String, value.ToString(), escaped);
                }
                if (IsLineTerminator(current))
                {
                    Fail();
                }
                if (current != '\\')
                {
                    value.Append(current);
                    continue;
                }
                escaped = true;
                ReadStringEscape(value);
            }
            Fail();
            return null!;
        }

        private void ReadStringEscape(StringBuilder value)
        {
            if (_index >= _text.Length)
            {
                Fail();
            }
            var escaped = _text[_index++];
            if (IsLineTerminator(escaped))
            {
                if (escaped == '\r' && _index < _text.Length && _text[_index] == '\n')
                {
                    _index++;
                }
                return;
            }
            if (escaped == 'x')
            {
                ReadFixedHexDigits(2);
                value.Append('?');
                return;
            }
            if (escaped == 'u')
            {
                if (_index < _text.Length && _text[_index] == '{')
                {
                    _index++;
                    var digits = 0;
                    var scalar = 0;
                    while (_index < _text.Length && _text[_index] != '}')
                    {
                        if (!IsHex(_text[_index]) || digits == 6)
                        {
                            Fail();
                        }
                        scalar = checked((scalar * 16) + HexValue(_text[_index++]));
                        digits++;
                    }
                    if (digits == 0 || _index >= _text.Length || scalar > 0x10ffff)
                    {
                        Fail();
                    }
                    _index++;
                }
                else
                {
                    ReadFixedHexDigits(4);
                }
                value.Append('?');
                return;
            }
            if (char.IsDigit(escaped) && escaped != '0')
            {
                Fail();
            }
            value.Append(escaped);
        }

        private void ReadFixedHexDigits(int count)
        {
            for (var index = 0; index < count; index++)
            {
                if (_index >= _text.Length || !IsHex(_text[_index]))
                {
                    Fail();
                }
                _index++;
            }
        }

        private JavaScriptToken ReadRegularExpression()
        {
            var start = _index++;
            var inClass = false;
            var closed = false;
            while (_index < _text.Length)
            {
                CheckBounds();
                var current = _text[_index++];
                if (IsLineTerminator(current))
                {
                    Fail();
                }
                if (current == '\\')
                {
                    if (_index >= _text.Length || IsLineTerminator(_text[_index]))
                    {
                        Fail();
                    }
                    _index++;
                    continue;
                }
                if (current == '[')
                {
                    inClass = true;
                }
                else if (current == ']' && inClass)
                {
                    inClass = false;
                }
                else if (current == '/' && !inClass)
                {
                    closed = true;
                    break;
                }
            }
            if (!closed || inClass)
            {
                Fail();
            }
            var flags = new HashSet<char>();
            while (_index < _text.Length && IsIdentifierPart(_text[_index]))
            {
                var flag = _text[_index];
                if ("dgimsuvy".IndexOf(flag) < 0 ||
                    !flags.Add(flag) ||
                    (flag == 'u' && flags.Contains('v')) ||
                    (flag == 'v' && flags.Contains('u')))
                {
                    Fail();
                }
                _index++;
            }
            return Token(
                JavaScriptTokenKind.RegularExpression,
                _text[start.._index]);
        }

        private void ScanTemplateLiteral()
        {
            _index++;
            while (_index < _text.Length)
            {
                CheckBounds();
                var current = _text[_index++];
                if (current == '`')
                {
                    AddToken(Token(JavaScriptTokenKind.Template, "`"));
                    return;
                }
                if (current == '\\')
                {
                    if (_index >= _text.Length)
                    {
                        Fail();
                    }
                    if (_text[_index] == '\r' && Peek(1) == '\n')
                    {
                        _index += 2;
                    }
                    else
                    {
                        _index++;
                    }
                    continue;
                }
                if (current == '$' && _index < _text.Length && _text[_index] == '{')
                {
                    _index++;
                    var delimiterCount = _delimiters.Count;
                    _regexAllowed = true;
                    ScanTemplateExpression(delimiterCount);
                }
            }
            Fail();
        }

        private void ScanTemplateExpression(int delimiterCount)
        {
            while (_index < _text.Length)
            {
                CheckBounds();
                SkipTrivia();
                if (_index >= _text.Length)
                {
                    Fail();
                }
                if (_text[_index] == '}' && _delimiters.Count == delimiterCount)
                {
                    _index++;
                    return;
                }
                if (_text[_index] == '`')
                {
                    ScanTemplateLiteral();
                }
                else
                {
                    AddToken(ReadOrdinaryToken());
                    if (_delimiters.Count < delimiterCount)
                    {
                        Fail();
                    }
                }
            }
            Fail();
        }

        private JavaScriptToken ReadPunctuator()
        {
            foreach (var punctuator in MultiCharacterPunctuators)
            {
                if (_text.AsSpan(_index).StartsWith(punctuator, StringComparison.Ordinal))
                {
                    _index += punctuator.Length;
                    return Token(JavaScriptTokenKind.Punctuator, punctuator);
                }
            }
            return Token(
                JavaScriptTokenKind.Punctuator,
                _text[_index++].ToString());
        }

        private JavaScriptToken Token(
            JavaScriptTokenKind kind,
            string text,
            bool hasEscape = false) =>
            new(kind, text, hasEscape, _lineBreakBefore, _statementStart);

        private void AddToken(JavaScriptToken token)
        {
            if (_tokens.Count == MaximumTokens)
            {
                Fail();
            }
            bool? regexAllowedAfterClose = null;
            var statementBlockOpened = false;
            if (token.Text.Length == 1)
            {
                var current = token.Text[0];
                if (current is '(' or '[' or '{')
                {
                    if (_delimiters.Count == MaximumDelimiterDepth)
                    {
                        Fail();
                    }
                    var controlHeader = current == '(' && IsControlHeaderStart();
                    statementBlockOpened = current == '{' && IsStatementBlockStart();
                    _delimiters.Push(new JavaScriptDelimiter(
                        current,
                        controlHeader,
                        statementBlockOpened));
                }
                else if (current is ')' or ']' or '}')
                {
                    if (_delimiters.Count == 0)
                    {
                        Fail();
                    }
                    var delimiter = _delimiters.Pop();
                    if (!DelimitersMatch(delimiter.Open, current))
                    {
                        Fail();
                    }
                    regexAllowedAfterClose = delimiter.IsControlHeader ||
                        delimiter.IsStatementBlock;
                }
            }
            _tokens.Add(token);
            _lineBreakBefore = false;
            _regexAllowed = regexAllowedAfterClose ?? RegexCanFollow(token);
            _statementStart = statementBlockOpened ||
                regexAllowedAfterClose == true ||
                TokenStartsStatement(token);
        }

        private bool IsControlHeaderStart()
        {
            if (_tokens.Count == 0)
            {
                return false;
            }
            var previous = _tokens[^1];
            if (previous.Kind == JavaScriptTokenKind.Identifier &&
                previous.StatementStartBefore &&
                previous.Text is "if" or "while" or "for" or "with" or
                    "switch" or "catch")
            {
                return true;
            }
            return StringComparer.Ordinal.Equals(previous.Text, "await") &&
                _tokens.Count > 1 &&
                StringComparer.Ordinal.Equals(_tokens[^2].Text, "for") &&
                _tokens[^2].StatementStartBefore;
        }

        private bool IsStatementBlockStart()
        {
            if (_statementStart || _tokens.Count == 0)
            {
                return true;
            }
            var previous = _tokens[^1];
            return previous.Kind == JavaScriptTokenKind.Identifier &&
                previous.Text is "else" or "do" or "try" or "finally";
        }

        private static bool TokenStartsStatement(JavaScriptToken token) =>
            StringComparer.Ordinal.Equals(token.Text, ";") ||
            token.Kind == JavaScriptTokenKind.Identifier &&
            token.Text is "else" or "do" or "try" or "finally";

        private static bool RegexCanFollow(JavaScriptToken token)
        {
            if (token.Kind == JavaScriptTokenKind.Identifier)
            {
                return token.Text is
                    "return" or "throw" or "case" or "delete" or "void" or "typeof" or
                    "instanceof" or "in" or "of" or "new" or "yield" or "await" or
                    "else" or "do";
            }
            if (token.Kind is JavaScriptTokenKind.String or
                JavaScriptTokenKind.Number or
                JavaScriptTokenKind.RegularExpression or
                JavaScriptTokenKind.Template)
            {
                return false;
            }
            return token.Text is not ")" and not "]" and not "}" and
                not "++" and not "--" and not "." and not "?.";
        }

        private void CheckBounds()
        {
            if (_stopwatch.Elapsed > MaximumDuration)
            {
                Fail();
            }
        }

        private char Peek(int offset) =>
            _index + offset < _text.Length ? _text[_index + offset] : '\0';

        private static bool DelimitersMatch(char open, char close) =>
            (open == '(' && close == ')') ||
            (open == '[' && close == ']') ||
            (open == '{' && close == '}');

        private static bool IsLineTerminator(char value) =>
            value is '\r' or '\n' or '\u2028' or '\u2029';

        private static bool IsIdentifierStart(char value) =>
            value is '_' or '$' || char.IsLetter(value) ||
            char.GetUnicodeCategory(value) ==
            System.Globalization.UnicodeCategory.LetterNumber;

        private static bool IsIdentifierPart(char value)
        {
            if (IsIdentifierStart(value) || char.IsDigit(value))
            {
                return true;
            }
            var category = char.GetUnicodeCategory(value);
            return category is
                System.Globalization.UnicodeCategory.NonSpacingMark or
                System.Globalization.UnicodeCategory.SpacingCombiningMark or
                System.Globalization.UnicodeCategory.ConnectorPunctuation;
        }

        private static bool IsHex(char value) =>
            value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

        private static int HexValue(char value) => value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            _ => value - 'A' + 10,
        };

        [DoesNotReturn]
        private void Fail() => throw Failure(_reason);
    }

    private static string? ResolveCompiledRelativeSpecifier(
        string compiledRoot,
        string importerRelative,
        string specifier)
    {
        const string reason = "production-compiled-dependency-invalid";
        var relative = specifier.StartsWith("./", StringComparison.Ordinal) ||
            specifier.StartsWith("../", StringComparison.Ordinal);
        if (!relative)
        {
            if (specifier.StartsWith(".", StringComparison.Ordinal) ||
                specifier.StartsWith("/", StringComparison.Ordinal) ||
                specifier.StartsWith("\\", StringComparison.Ordinal) ||
                !IsAllowedCompiledBareSpecifier(specifier))
            {
                throw Failure(reason);
            }
            return null;
        }
        if (!specifier.EndsWith(".js", StringComparison.Ordinal) ||
            specifier.Contains('\\', StringComparison.Ordinal) ||
            specifier.Contains(':', StringComparison.Ordinal) ||
            specifier.Contains('?', StringComparison.Ordinal) ||
            specifier.Contains('#', StringComparison.Ordinal) ||
            specifier.Contains('%', StringComparison.Ordinal) ||
            specifier.Contains("//", StringComparison.Ordinal))
        {
            throw Failure(reason);
        }
        var importer = Path.Combine(
            compiledRoot,
            importerRelative.Replace('/', Path.DirectorySeparatorChar));
        var candidate = CanonicalAbsolute(Path.Combine(
            Path.GetDirectoryName(importer)!,
            specifier.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrUnder(candidate, compiledRoot))
        {
            throw Failure(reason);
        }
        var canonicalSpecifier = Path.GetRelativePath(
                Path.GetDirectoryName(importer)!,
                candidate)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!canonicalSpecifier.StartsWith(".", StringComparison.Ordinal))
        {
            canonicalSpecifier = "./" + canonicalSpecifier;
        }
        if (!StringComparer.Ordinal.Equals(canonicalSpecifier, specifier))
        {
            throw Failure(reason);
        }
        var dependencyRelative = Path.GetRelativePath(compiledRoot, candidate)
            .Replace(Path.DirectorySeparatorChar, '/');
        try
        {
            _ = ExactOrdinaryFile(compiledRoot, dependencyRelative, reason);
        }
        catch (ProductionPayloadBuildException)
        {
            throw Failure(reason);
        }
        return dependencyRelative;
    }

    private static bool IsAllowedCompiledBareSpecifier(string specifier)
    {
        if (specifier.StartsWith("node:", StringComparison.Ordinal))
        {
            return Regex.IsMatch(
                specifier,
                """\Anode:[A-Za-z0-9_./-]+\z""",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        var segments = specifier.Split('/');
        var minimumSegments = specifier.StartsWith('@') ? 2 : 1;
        if (segments.Length < minimumSegments ||
            segments.Any(segment =>
                string.IsNullOrEmpty(segment) ||
                StringComparer.Ordinal.Equals(segment, ".") ||
                StringComparer.Ordinal.Equals(segment, "..")))
        {
            return false;
        }
        return segments.All(segment => Regex.IsMatch(
            segment,
            """\A@?[A-Za-z0-9_][A-Za-z0-9_.-]*\z""",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)));
    }

    private static void ValidateInventory(
        ProductionEntrypointInventoryV1 inventory,
        string buildRoot,
        string payloadRoot,
        ProductionPayloadHeader header,
        bool closureOnly)
    {
        foreach (var record in inventory.Records)
        {
            _ = ExactOrdinaryFile(
                buildRoot,
                record.SourcePath,
                "production-entrypoint-inventory-mismatch");
            _ = ExactOrdinaryFile(
                payloadRoot,
                record.EmittedPath,
                "production-entrypoint-inventory-mismatch");
        }
        var launches = inventory.Records
            .OfType<ProductionLaunchExecutableEntrypointV1>()
            .ToDictionary(record => record.Role, record => record.EmittedPath, StringComparer.Ordinal);
        var imports = inventory.Records.OfType<ProductionImportRootEntrypointV1>().ToArray();
        var knownRoles = HeaderEntrypoints(header);
        if (launches.Keys.Any(role => !knownRoles.ContainsKey(role)) ||
            imports.Any(record => !knownRoles.ContainsKey(record.OwnerRole)))
        {
            throw Failure("production-entrypoint-inventory-mismatch");
        }
        var frontend = inventory.Records.OfType<ProductionFrontendAssetEntrypointV1>().ToArray();
        if (frontend.Length != 1 ||
            !StringComparer.Ordinal.Equals(frontend[0].EmittedPath, header.FrontendEntrypoint) ||
            launches.Any(pair => !StringComparer.Ordinal.Equals(knownRoles[pair.Key], pair.Value)))
        {
            throw Failure("production-entrypoint-inventory-mismatch");
        }
        foreach (var import in imports)
        {
            if (!launches.TryGetValue(import.OwnerRole, out _))
            {
                throw Failure("production-entrypoint-inventory-mismatch");
            }
            var launch = inventory.Records
                .OfType<ProductionLaunchExecutableEntrypointV1>()
                .Single(record => StringComparer.Ordinal.Equals(record.Role, import.OwnerRole));
            if (!SourceImportGraphReaches(buildRoot, launch.SourcePath, import.SourcePath))
            {
                throw Failure("production-entrypoint-inventory-mismatch");
            }
        }
        if (!closureOnly && launches.Count != knownRoles.Count)
        {
            throw Failure("production-entrypoint-inventory-mismatch");
        }
    }

    private static IReadOnlyDictionary<string, string> HeaderEntrypoints(
        ProductionPayloadHeader header) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["server"] = header.ServerEntrypoint,
            ["worker"] = header.WorkerEntrypoint,
            ["setup"] = header.SetupEntrypoint,
            ["instance-command"] = header.InstanceCommandEntrypoint,
            ["compatibility-preflight"] = header.CompatibilityPreflightEntrypoint,
            ["acceptance"] = header.AcceptanceEntrypoint,
            ["acceptance-cleanup"] = header.AcceptanceCleanupEntrypoint,
        };

    private static string ExactOrdinaryFile(
        string root,
        string canonicalRelativePath,
        string reason)
    {
        var current = CanonicalExistingDirectory(root);
        var segments = canonicalRelativePath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var matches = new DirectoryInfo(current)
                .EnumerateFileSystemInfos()
                .Where(entry => StringComparer.OrdinalIgnoreCase.Equals(
                    entry.Name,
                    segments[index]))
                .ToArray();
            if (matches.Length != 1 ||
                !StringComparer.Ordinal.Equals(matches[0].Name, segments[index]) ||
                matches[0].Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Failure(reason);
            }
            if (index == segments.Length - 1)
            {
                if (matches[0] is not FileInfo file)
                {
                    throw Failure(reason);
                }
                return CanonicalExistingFile(file.FullName);
            }
            if (matches[0] is not DirectoryInfo directory)
            {
                throw Failure(reason);
            }
            current = directory.FullName;
        }
        throw Failure(reason);
    }

    private static bool SourceImportGraphReaches(
        string buildRoot,
        string launchSource,
        string targetSource)
    {
        const string reason = "production-entrypoint-inventory-mismatch";
        var root = CanonicalExistingDirectory(buildRoot);
        var target = ExactOrdinarySourceFile(root, targetSource, reason);
        var budget = new TreeTraversalBudget(reason);
        var pending = new Stack<(string Path, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push((ExactOrdinarySourceFile(root, launchSource, reason), 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }
            budget.ObserveFile(new FileInfo(current), depth);
            if (StringComparer.OrdinalIgnoreCase.Equals(current, target))
            {
                return true;
            }
            foreach (var value in ReadModuleSpecifiers(
                         current,
                         reason,
                         includeDynamicImports: true,
                         sourceSyntax: true))
            {
                var relative = value.StartsWith("./", StringComparison.Ordinal) ||
                    value.StartsWith("../", StringComparison.Ordinal);
                if (!relative)
                {
                    if (value.StartsWith(".", StringComparison.Ordinal) ||
                        value.StartsWith("/", StringComparison.Ordinal) ||
                        value.StartsWith("\\", StringComparison.Ordinal) ||
                        Path.IsPathFullyQualified(value))
                    {
                        throw Failure(reason);
                    }
                    continue;
                }
                var currentDirectory = Path.GetDirectoryName(current)!;
                string candidate;
                try
                {
                    candidate = CanonicalAbsolute(Path.Combine(
                        currentDirectory,
                        value.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (Exception exception) when (
                    exception is ArgumentException or NotSupportedException or
                        PathTooLongException or ProductionPayloadBuildException)
                {
                    throw Failure(reason);
                }
                if (!IsSameOrUnder(candidate, root))
                {
                    throw Failure(reason);
                }
                var canonicalSpecifier = Path.GetRelativePath(currentDirectory, candidate)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!canonicalSpecifier.StartsWith(".", StringComparison.Ordinal))
                {
                    canonicalSpecifier = "./" + canonicalSpecifier;
                }
                if (!StringComparer.Ordinal.Equals(canonicalSpecifier, value))
                {
                    throw Failure(reason);
                }
                var resolvedFile = ResolveExactSourceImport(
                    root,
                    candidate,
                    value,
                    reason);
                if (resolvedFile is not null)
                {
                    pending.Push((resolvedFile, depth + 1));
                }
            }
        }
        return false;
    }

    private static string? ResolveExactSourceImport(
        string buildRoot,
        string candidate,
        string specifier,
        string reason)
    {
        if (specifier.EndsWith(".js", StringComparison.Ordinal))
        {
            var exactJavaScript = TryExactOrdinarySourceFile(
                buildRoot,
                candidate,
                reason);
            var mappedTypeScript = TryExactOrdinarySourceFile(
                buildRoot,
                candidate[..^3] + ".ts",
                reason);
            if (exactJavaScript is not null && mappedTypeScript is not null)
            {
                throw Failure(reason);
            }
            if (exactJavaScript is not null)
            {
                throw Failure(reason);
            }
            return mappedTypeScript;
        }
        var matches = new[]
            {
                candidate,
                candidate + ".ts",
                Path.Combine(candidate, "index.ts"),
            }
            .Select(path => TryExactOrdinarySourceFile(buildRoot, path, reason))
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw Failure(reason),
        };
    }

    private static string ExactOrdinarySourceFile(
        string buildRoot,
        string canonicalRelativePath,
        string reason)
    {
        if (string.IsNullOrEmpty(canonicalRelativePath) ||
            canonicalRelativePath.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(canonicalRelativePath))
        {
            throw Failure(reason);
        }
        string candidate;
        try
        {
            candidate = CanonicalAbsolute(Path.Combine(
                buildRoot,
                canonicalRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
                PathTooLongException or ProductionPayloadBuildException)
        {
            throw Failure(reason);
        }
        if (!IsSameOrUnder(candidate, buildRoot) ||
            !StringComparer.Ordinal.Equals(
                Path.GetRelativePath(buildRoot, candidate)
                    .Replace(Path.DirectorySeparatorChar, '/'),
                canonicalRelativePath))
        {
            throw Failure(reason);
        }
        return TryExactOrdinarySourceFile(buildRoot, candidate, reason) ??
            throw Failure(reason);
    }

    private static string? TryExactOrdinarySourceFile(
        string buildRoot,
        string candidatePath,
        string reason)
    {
        string candidate;
        try
        {
            candidate = CanonicalAbsolute(candidatePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
                PathTooLongException or ProductionPayloadBuildException)
        {
            throw Failure(reason);
        }
        if (!IsSameOrUnder(candidate, buildRoot) ||
            StringComparer.OrdinalIgnoreCase.Equals(candidate, buildRoot))
        {
            throw Failure(reason);
        }
        var relative = Path.GetRelativePath(buildRoot, candidate);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        if (segments.Length == 0 || segments.Any(segment =>
                string.IsNullOrEmpty(segment) ||
                StringComparer.Ordinal.Equals(segment, ".") ||
                StringComparer.Ordinal.Equals(segment, "..")))
        {
            throw Failure(reason);
        }
        var current = buildRoot;
        try
        {
            EnsureNoAlternateDataStreams(current);
            for (var index = 0; index < segments.Length; index++)
            {
                var matches = new DirectoryInfo(current)
                    .EnumerateFileSystemInfos()
                    .Where(entry => StringComparer.OrdinalIgnoreCase.Equals(
                        entry.Name,
                        segments[index]))
                    .ToArray();
                if (matches.Length == 0)
                {
                    return null;
                }
                if (matches.Length != 1 ||
                    !StringComparer.Ordinal.Equals(matches[0].Name, segments[index]) ||
                    matches[0].Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Failure(reason);
                }
                EnsureNoAlternateDataStreams(matches[0].FullName);
                if (index == segments.Length - 1)
                {
                    return matches[0] is FileInfo file
                        ? CanonicalAbsolute(file.FullName)
                        : null;
                }
                if (matches[0] is not DirectoryInfo directory)
                {
                    return null;
                }
                current = directory.FullName;
            }
        }
        catch (ProductionPayloadBuildException)
        {
            throw Failure(reason);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw Failure(reason);
        }
        throw Failure(reason);
    }

    private void ValidateRuntimeClosure(string payloadRoot, string nodeExecutable)
    {
        var frontend = Path.Combine(
            payloadRoot,
            "packages", "twenty-server", "dist", "front", "index.html");
        var frontendText = ReadBoundedUtf8Text(
            frontend,
            MaximumFrontendMarkerBytes,
            "production-frontend-marker-invalid");
        if (!Regex.IsMatch(
                frontendText,
                @"<script\s+id=""twenty-env-config"">\s*" +
                @"window\._env_\s*=\s*\{\s*" +
                @"// This will be overwritten\s*\}\s*;\s*</script>",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)))
        {
            throw Failure("production-frontend-marker-invalid");
        }
        var forbidden = new HashSet<string>(
            ["yarn", "yarn.cmd", "yarn.exe", "npm", "npm.cmd", "npm.exe",
             "npx", "npx.cmd", "npx.exe", "corepack", "corepack.cmd", "corepack.exe"],
            StringComparer.OrdinalIgnoreCase);
        var beforeProbe = CaptureClosureSnapshot(payloadRoot);
        var nativeAddons = new List<string>();
        foreach (var snapshot in beforeProbe)
        {
            var relative = snapshot.RelativePath;
            var fileName = Path.GetFileName(relative);
            if (forbidden.Contains(fileName) &&
                HasPathSegment(relative, ".bin"))
            {
                throw Failure("production-runtime-package-manager-present");
            }
            if (relative.EndsWith(".node", StringComparison.OrdinalIgnoreCase))
            {
                var addon = Path.Combine(
                    payloadRoot,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                ValidatePeX64(addon);
                nativeAddons.Add(addon);
            }
        }
        foreach (var workspace in RuntimeWorkspaces)
        {
            var directory = new DirectoryInfo(Path.Combine(payloadRoot, "packages", workspace));
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Failure("production-workspace-not-physical");
            }
        }
        ProbeNativeAddons(payloadRoot, nodeExecutable, nativeAddons);
        var afterProbe = CaptureClosureSnapshot(payloadRoot);
        if (!beforeProbe.SequenceEqual(afterProbe))
        {
            throw Failure("production-build-closure-mutated");
        }
        foreach (var snapshot in afterProbe.Where(item =>
                     item.RelativePath.EndsWith(".node", StringComparison.OrdinalIgnoreCase)))
        {
            ValidatePeX64(Path.Combine(
                payloadRoot,
                snapshot.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    private static bool HasPathSegment(string canonicalRelativePath, string segment) =>
        canonicalRelativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate, segment));

    private sealed record ClosureSnapshot(
        string RelativePath,
        long Length,
        string Sha256);

    private static IReadOnlyList<ClosureSnapshot> CaptureClosureSnapshot(string root) =>
        EnumerateOrdinaryFiles(root)
            .Select(relative =>
            {
                var path = Path.Combine(
                    root,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.SequentialScan);
                return new ClosureSnapshot(
                    relative,
                    stream.Length,
                    Convert.ToHexString(SHA256.HashData(stream)));
            })
            .ToArray();

    private static IReadOnlyList<string> EnumerateOrdinaryFiles(string root)
    {
        var files = EnumerateOrdinaryEntries(root)
            .OfType<FileInfo>()
            .Select(file => Path.GetRelativePath(root, file.FullName)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return Array.AsReadOnly(files);
    }

    private static IReadOnlyList<FileSystemInfo> EnumerateOrdinaryEntries(string root)
    {
        var canonicalRoot = CanonicalExistingDirectory(root);
        var budget = new TreeTraversalBudget("production-build-traversal-budget");
        budget.ObserveDirectory(0);
        var entries = new List<FileSystemInfo>();
        var identities = new List<string>();
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((canonicalRoot, 0));
        while (stack.Count > 0)
        {
            var (directory, depth) = stack.Pop();
            var directoryInfo = new DirectoryInfo(directory);
            if (directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Failure("production-build-reparse-point");
            }
            EnsureNoAlternateDataStreams(directoryInfo.FullName);
            foreach (var entry in directoryInfo.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Failure("production-build-reparse-point");
                }
                EnsureNoAlternateDataStreams(entry.FullName);
                var canonical = CanonicalAbsolute(entry.FullName);
                if (!IsSameOrUnder(canonical, canonicalRoot))
                {
                    throw Failure("production-build-path-invalid");
                }
                identities.Add(Path.GetRelativePath(canonicalRoot, canonical)
                    .Replace(Path.DirectorySeparatorChar, '/'));
                entries.Add(entry);
                if (entry is DirectoryInfo child)
                {
                    budget.ObserveDirectory(depth + 1);
                    stack.Push((child.FullName, depth + 1));
                }
                else if (entry is FileInfo file)
                {
                    budget.ObserveFile(file, depth + 1);
                }
                else
                {
                    throw Failure("production-build-nonordinary-input");
                }
            }
        }
        ValidateCaseInsensitiveEntryIdentities(identities);
        return Array.AsReadOnly(entries.ToArray());
    }

    internal static void ValidateCaseInsensitiveEntryIdentities(
        IEnumerable<string> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var caseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity) || !caseInsensitive.Add(identity))
            {
                throw Failure("production-build-case-collision");
            }
        }
    }

    private static void CopyRequiredFile(string sourceRoot, string targetRoot, string relative)
    {
        var source = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(source))
        {
            throw Failure("production-build-input-missing");
        }
        CopyOrdinaryFile(source, Path.Combine(
            targetRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void CopyProductionNodeModules(string buildRoot, string payloadRoot)
    {
        var source = Path.Combine(buildRoot, "node_modules");
        if (!Directory.Exists(source))
        {
            throw Failure("production-build-input-missing");
        }
        ValidateNativePruningInventories(source);
        CopyRuntimeDependencyTree(
            source,
            Path.Combine(payloadRoot, "node_modules"),
            buildRoot,
            payloadRoot);
    }

    private static void CopyRuntimeDependencyTree(
        string source,
        string destination,
        string buildRoot,
        string payloadRoot)
    {
        var budget = new TreeTraversalBudget("production-build-traversal-budget");
        CopyRuntimeDependencyTreeCore(
            source,
            destination,
            buildRoot,
            payloadRoot,
            budget,
            0);
    }

    private static void CopyRuntimeDependencyTreeCore(
        string source,
        string destination,
        string buildRoot,
        string payloadRoot,
        TreeTraversalBudget budget,
        int depth)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists)
        {
            throw Failure("production-build-input-missing");
        }
        budget.ObserveDirectory(depth);
        var runtimeWorkspace = GetRuntimeWorkspaceAliasName(sourceInfo, buildRoot);
        if (sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (runtimeWorkspace is null)
            {
                throw Failure("production-build-reparse-point");
            }
            var target = sourceInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not DirectoryInfo targetDirectory)
            {
                throw Failure("production-build-reparse-point");
            }
            var workspaceName = GetWorkspaceProjectionName(targetDirectory.FullName, buildRoot);
            if (!StringComparer.Ordinal.Equals(runtimeWorkspace, workspaceName))
            {
                throw Failure("production-workspace-projection-invalid");
            }
            CopyWorkspaceProjection(targetDirectory.FullName, destination, buildRoot);
            var packageDestination = Path.Combine(payloadRoot, "packages", workspaceName);
            if (!Directory.Exists(packageDestination))
            {
                CopyWorkspaceProjection(targetDirectory.FullName, packageDestination, buildRoot);
            }
            return;
        }
        if (runtimeWorkspace is not null)
        {
            CopyPhysicalRuntimeWorkspaceProjection(
                sourceInfo.FullName,
                destination,
                buildRoot,
                runtimeWorkspace);
            return;
        }
        EnsureNoAlternateDataStreams(sourceInfo.FullName);
        CreateValidatedDirectoryTree(destination);
        foreach (var entry in sourceInfo.EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                CopyRuntimeDependencyTreeCore(
                    directory.FullName,
                    target,
                    buildRoot,
                    payloadRoot,
                    budget,
                    depth + 1);
            }
            else if (entry is FileInfo file &&
                     !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                budget.ObserveFile(file, depth + 1);
                if (!IsKnownNonTargetNativeAddon(file.FullName, buildRoot))
                {
                    CopyOrdinaryFile(file.FullName, target);
                }
            }
            else
            {
                throw Failure("production-build-reparse-point");
            }
        }
    }

    private static string? GetRuntimeWorkspaceAliasName(
        DirectoryInfo source,
        string buildRoot)
    {
        var nodeModulesRoot = CanonicalAbsolute(Path.Combine(buildRoot, "node_modules"));
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                CanonicalAbsolute(source.Parent?.FullName ?? string.Empty),
                nodeModulesRoot))
        {
            return null;
        }
        var match = RuntimeWorkspaces.SingleOrDefault(workspace =>
            StringComparer.OrdinalIgnoreCase.Equals(workspace, source.Name));
        if (match is null)
        {
            return null;
        }
        if (!StringComparer.Ordinal.Equals(match, source.Name))
        {
            throw Failure("production-workspace-projection-invalid");
        }
        return match;
    }

    private static void CopyPhysicalRuntimeWorkspaceProjection(
        string workspaceRoot,
        string destination,
        string buildRoot,
        string expectedWorkspace)
    {
        var canonical = CanonicalExistingDirectory(workspaceRoot);
        if (!IsSameOrUnder(canonical, buildRoot))
        {
            throw Failure("production-workspace-projection-invalid");
        }
        CopyWorkspaceProjectionFiles(
            canonical,
            destination,
            buildRoot,
            expectedWorkspace);
    }

    private static string GetWorkspaceProjectionName(string workspaceRoot, string buildRoot)
    {
        var canonical = CanonicalExistingDirectory(workspaceRoot);
        var packagesRoot = CanonicalExistingDirectory(Path.Combine(buildRoot, "packages"));
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(canonical), packagesRoot))
        {
            throw Failure("production-build-link-outside-root");
        }
        var name = Path.GetFileName(canonical);
        using var document = JsonDocument.Parse(ReadBoundedUtf8Bytes(
            Path.Combine(canonical, "package.json"),
            MaximumPackageJsonBytes,
            "production-workspace-projection-invalid"));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("name", out var packageName) ||
            packageName.ValueKind != JsonValueKind.String ||
            !StringComparer.Ordinal.Equals(packageName.GetString(), name))
        {
            throw Failure("production-workspace-projection-invalid");
        }
        return name;
    }

    private static void CopyWorkspaceProjection(
        string workspaceRoot,
        string destination,
        string buildRoot)
    {
        var workspaceName = GetWorkspaceProjectionName(workspaceRoot, buildRoot);
        CopyWorkspaceProjectionFiles(
            workspaceRoot,
            destination,
            buildRoot,
            workspaceName);
    }

    private static void CopyWorkspaceProjectionFiles(
        string workspaceRoot,
        string destination,
        string buildRoot,
        string expectedWorkspace)
    {
        const string reason = "production-workspace-projection-invalid";
        var root = CanonicalExistingDirectory(workspaceRoot);
        if (!IsSameOrUnder(root, buildRoot))
        {
            throw Failure(reason);
        }
        string packageJson;
        string dist;
        try
        {
            packageJson = ExactOrdinaryFile(root, "package.json", reason);
            dist = ExactOrdinaryDirectory(root, "dist", reason);
            EnsureNoAlternateDataStreams(root);
            EnsureNoAlternateDataStreams(packageJson);
            EnsureNoAlternateDataStreams(dist);
            using var document = JsonDocument.Parse(ReadBoundedUtf8Bytes(
                packageJson,
                MaximumPackageJsonBytes,
                reason));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("name", out var packageName) ||
                packageName.ValueKind != JsonValueKind.String ||
                !StringComparer.Ordinal.Equals(packageName.GetString(), expectedWorkspace))
            {
                throw Failure(reason);
            }
        }
        catch (ProductionPayloadBuildException)
        {
            throw Failure(reason);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw Failure(reason);
        }
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw Failure("production-build-output-not-clean");
        }
        CreateValidatedDirectoryTree(destination);
        CopyOrdinaryFile(
            packageJson,
            Path.Combine(destination, "package.json"));
        CopyTree(
            dist,
            Path.Combine(destination, "dist"),
            buildRoot);
    }

    private static string ExactOrdinaryDirectory(
        string root,
        string canonicalRelativePath,
        string reason)
    {
        var current = CanonicalExistingDirectory(root);
        var segments = canonicalRelativePath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var matches = new DirectoryInfo(current)
                .EnumerateFileSystemInfos()
                .Where(entry => StringComparer.OrdinalIgnoreCase.Equals(
                    entry.Name,
                    segments[index]))
                .ToArray();
            if (matches.Length != 1 ||
                !StringComparer.Ordinal.Equals(matches[0].Name, segments[index]) ||
                matches[0].Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                matches[0] is not DirectoryInfo directory)
            {
                throw Failure(reason);
            }
            current = directory.FullName;
        }
        return CanonicalExistingDirectory(current);
    }

    private static void CopyTree(string source, string destination, string allowedRoot)
    {
        var budget = new TreeTraversalBudget("production-build-traversal-budget");
        CopyTreeCore(source, destination, allowedRoot, budget, 0);
    }

    private static void CopyTreeCore(
        string source,
        string destination,
        string allowedRoot,
        TreeTraversalBudget budget,
        int depth)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Failure("production-build-reparse-point");
        }
        budget.ObserveDirectory(depth);
        EnsureNoAlternateDataStreams(sourceInfo.FullName);
        CreateValidatedDirectoryTree(destination);
        foreach (var entry in sourceInfo.EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                CopyTreeCore(
                    directory.FullName,
                    target,
                    allowedRoot,
                    budget,
                    depth + 1);
            }
            else if (entry is FileInfo file &&
                     !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                budget.ObserveFile(file, depth + 1);
                if (!IsKnownNonTargetNativeAddon(file.FullName, allowedRoot))
                {
                    CopyOrdinaryFile(file.FullName, target);
                }
            }
            else
            {
                throw Failure("production-build-reparse-point");
            }
        }
    }

    private static bool IsKnownNonTargetNativeAddon(string path, string allowedRoot)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(path), ".node"))
        {
            return false;
        }
        var canonical = CanonicalAbsolute(path);
        var root = CanonicalAbsolute(allowedRoot);
        if (!IsSameOrUnder(canonical, root))
        {
            throw Failure("production-build-path-invalid");
        }
        var relative = Path.GetRelativePath(root, canonical)
            .Replace(Path.DirectorySeparatorChar, '/');
        var pruned = NativePruningInventories.Any(inventory =>
            inventory.PrunedNativePaths.Contains(relative));
        if (pruned)
        {
            return true;
        }
        if (LooksLikeForeignNativePath("/" + relative + "/"))
        {
            throw Failure("production-native-pruning-inventory-invalid");
        }
        return false;
    }

    private static IReadOnlyList<NativePruningInventory> CreateNativePruningInventories()
    {
        var sentryAbis = new[] { "108", "115", "127", "137", "147" };
        var sentryPlatforms = new[]
        {
            "darwin-arm64",
            "darwin-x64",
            "linux-arm64-glibc",
            "linux-arm64-musl",
            "linux-x64-glibc",
            "linux-x64-musl",
            "win32-x64",
        };
        var sentry = sentryPlatforms
            .SelectMany(platform => sentryAbis.Select(abi =>
                $"lib/sentry_cpu_profiler-{platform}-{abi}.node"))
            .ToHashSet(StringComparer.Ordinal);
        var sentryPruned = sentry
            .Where(path => !path.Contains("-win32-x64-", StringComparison.Ordinal))
            .Select(path => "node_modules/@sentry/node-cpu-profiler/" + path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bcrypt = new HashSet<string>(StringComparer.Ordinal)
        {
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
        };
        var bcryptPruned = bcrypt
            .Where(path => !StringComparer.Ordinal.Equals(
                path,
                "prebuilds/win32-x64/bcrypt.node"))
            .Select(path => "node_modules/bcrypt/" + path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            new NativePruningInventory(
                "@sentry/node-cpu-profiler",
                "@sentry/node-cpu-profiler",
                "2.4.2",
                sentry,
                sentryPruned),
            new NativePruningInventory(
                "bcrypt",
                "bcrypt",
                "6.0.0",
                bcrypt,
                bcryptPruned),
        ];
    }

    private static void ValidateNativePruningInventories(string nodeModulesRoot)
    {
        foreach (var inventory in NativePruningInventories)
        {
            var packageRoot = Path.Combine(
                nodeModulesRoot,
                inventory.PackagePath.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(packageRoot))
            {
                continue;
            }
            using var document = JsonDocument.Parse(ReadBoundedUtf8Bytes(
                Path.Combine(packageRoot, "package.json"),
                MaximumPackageJsonBytes,
                "production-native-pruning-inventory-invalid"));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("name", out var name) ||
                !root.TryGetProperty("version", out var version) ||
                name.ValueKind != JsonValueKind.String ||
                version.ValueKind != JsonValueKind.String ||
                !StringComparer.Ordinal.Equals(name.GetString(), inventory.PackageName) ||
                !StringComparer.Ordinal.Equals(version.GetString(), inventory.Version))
            {
                throw Failure("production-native-pruning-inventory-invalid");
            }
            var actual = EnumerateOrdinaryFiles(packageRoot)
                .Where(path => path.EndsWith(".node", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.Ordinal);
            if (!actual.SetEquals(inventory.AllNativePaths))
            {
                throw Failure("production-native-pruning-inventory-invalid");
            }
        }
    }

    private static bool LooksLikeForeignNativePath(string relative) =>
        new[]
        {
            "/darwin-arm64/",
            "/darwin-x64/",
            "/linux-arm/",
            "/linux-arm64/",
            "/linux-x64/",
            "/win32-arm64/",
            "-darwin-arm64-",
            "-darwin-x64-",
            "-linux-arm-",
            "-linux-arm64-",
            "-linux-x64-",
            "-win32-arm64-",
        }.Any(marker => relative.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static void CopyOrdinaryFile(string source, string destination)
    {
        var sourceInfo = new FileInfo(source);
        if (!sourceInfo.Exists || sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Failure("production-build-nonordinary-input");
        }
        EnsureNoAlternateDataStreams(sourceInfo.FullName);
        CreateValidatedDirectoryTree(Path.GetDirectoryName(destination)!);
        sourceInfo.CopyTo(destination, overwrite: false);
    }

    private void ProbeNativeAddons(
        string payloadRoot,
        string nodeExecutable,
        IReadOnlyList<string> nativeAddons)
    {
        if (nativeAddons.Count == 0)
        {
            return;
        }
        var probePath = Path.Combine(payloadRoot, NativeAddonProbeName);
        if (File.Exists(probePath) || Directory.Exists(probePath))
        {
            throw Failure("production-native-addon-load-failed");
        }
        WriteAtomic(probePath, new UTF8Encoding(false).GetBytes(NativeAddonProbe));
        try
        {
            foreach (var addon in nativeAddons.Order(StringComparer.Ordinal))
            {
                var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PATH"] = payloadRoot + Path.PathSeparator + Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "System32"),
                    ["NX_DAEMON"] = "false",
                };
                try
                {
                    _runner.Run(new ProductionPayloadProcessSpec(
                        nodeExecutable,
                        [probePath, payloadRoot, addon],
                        payloadRoot,
                        UseShellExecute: false,
                        environment)
                    {
                        Purpose = ProductionPayloadProcessPurpose.NativeAddonProbe,
                        Timeout = TimeSpan.FromSeconds(30),
                        MaximumOutputBytes = 64 * 1024,
                        RemovedEnvironmentVariables =
                        [
                            "NODE_OPTIONS",
                            "NODE_PATH",
                            "npm_config_user_agent",
                            "npm_execpath",
                            "npm_node_execpath",
                            "YARN_IGNORE_PATH",
                            "YARN_WRAP_OUTPUT",
                            "COREPACK_HOME",
                            "COREPACK_DEFAULT_TO_LATEST",
                            "COREPACK_ENABLE_PROJECT_SPEC",
                            "COREPACK_INTEGRITY_KEYS",
                        ],
                        RemovedEnvironmentVariablePrefixes =
                        [
                            "npm_",
                            "YARN_",
                            "COREPACK_",
                        ],
                    });
                }
                catch
                {
                    throw Failure("production-native-addon-load-failed");
                }
            }
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    private static void ValidatePeX64(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> dos = stackalloc byte[64];
        if (stream.Read(dos) != dos.Length || dos[0] != 'M' || dos[1] != 'Z')
        {
            throw Failure("production-native-addon-invalid");
        }
        var offset = BitConverter.ToInt32(dos[0x3c..]);
        if (offset < 64 || offset > stream.Length - 6 || offset > 1024 * 1024)
        {
            throw Failure("production-native-addon-invalid");
        }
        stream.Position = offset;
        Span<byte> header = stackalloc byte[6];
        if (stream.Read(header) != header.Length ||
            header[0] != 'P' || header[1] != 'E' || header[2] != 0 || header[3] != 0 ||
            BitConverter.ToUInt16(header[4..]) != 0x8664)
        {
            throw Failure("production-native-addon-invalid");
        }
    }

    internal static void EnsureNoAlternateDataStreams(string path)
    {
        var handle = FindFirstStream(ToNativeExtendedLengthPath(path), 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 38)
            {
                return;
            }
            throw Failure("production-build-alternate-data-stream-scan-failed");
        }
        try
        {
            while (true)
            {
                if (!StringComparer.Ordinal.Equals(data.StreamName, "::$DATA"))
                {
                    throw Failure("production-build-alternate-data-stream");
                }
                if (!FindNextStream(handle, out data))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 38)
                    {
                        break;
                    }
                    throw Failure("production-build-alternate-data-stream-scan-failed");
                }
            }
        }
        finally
        {
            _ = FindClose(handle);
        }
    }

    internal static string ToNativeExtendedLengthPath(string path) =>
        @"\\?\" + CanonicalAbsolute(path);

    private static byte[] ToBytes(RawSecurityDescriptor descriptor)
    {
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void VerifyInstalledAcl(FileSystemSecurity security)
    {
        var bytes = security.GetSecurityDescriptorBinaryForm();
        try
        {
            TrustedApplicationRootSecurity.Validate(new RawSecurityDescriptor(bytes, 0));
        }
        catch
        {
            throw Failure("production-installed-acl-invalid");
        }
    }

    private static void DeleteOwnedTemporaryProfile(string profilePath, string parentPath)
    {
        var parent = CanonicalExistingDirectory(parentPath);
        var profile = CanonicalAbsolute(profilePath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(profile), parent) ||
            !Regex.IsMatch(
                Path.GetFileName(profile),
                "^payload-tool-profile-[0-9a-f]{32}$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)))
        {
            throw Failure("production-profile-cleanup-invalid");
        }
        if (!Directory.Exists(profile))
        {
            return;
        }
        var root = new DirectoryInfo(profile);
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw Failure("production-profile-cleanup-invalid");
        }
        var entries = EnumerateOrdinaryEntries(profile);
        foreach (var file in entries.OfType<FileInfo>())
        {
            file.Delete();
        }
        foreach (var directory in entries.OfType<DirectoryInfo>()
                     .OrderByDescending(item => item.FullName.Length))
        {
            directory.Delete();
        }
        root.Delete();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        internal long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string StreamName;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr FindFirstStream(
        string fileName,
        int infoLevel,
        out Win32FindStreamData data,
        int flags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(IntPtr findHandle, out Win32FindStreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findHandle);

    private static void WriteAtomic(string target, byte[] bytes)
    {
        _ = ValidateExactExistingPath(Path.GetDirectoryName(target)!, expectDirectory: true);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, target);
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }
        return count;
    }

    internal static string CanonicalExistingDirectory(string path)
    {
        return ValidateExactExistingPath(path, expectDirectory: true);
    }

    internal static string CanonicalExistingFile(string path)
    {
        return ValidateExactExistingPath(path, expectDirectory: false);
    }

    private static string ValidateExactExistingPath(string path, bool expectDirectory)
    {
        var canonical = CanonicalAbsolute(path);
        var root = Path.GetPathRoot(canonical) ?? throw Failure("production-build-path-invalid");
        var segments = canonical[root.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            if (expectDirectory && Directory.Exists(root))
            {
                return Path.TrimEndingDirectorySeparator(root);
            }
            throw Failure("production-build-path-invalid");
        }
        var current = root;
        try
        {
            for (var index = 0; index < segments.Length; index++)
            {
                EnsureNoAlternateDataStreams(current);
                var matches = new DirectoryInfo(current)
                    .EnumerateFileSystemInfos()
                    .Where(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.Name, segments[index]))
                    .ToArray();
                if (matches.Length != 1 ||
                    !StringComparer.Ordinal.Equals(matches[0].Name, segments[index]) ||
                    matches[0].Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Failure("production-build-path-invalid");
                }
                EnsureNoAlternateDataStreams(matches[0].FullName);
                var final = index == segments.Length - 1;
                if ((!final || expectDirectory) && matches[0] is not DirectoryInfo)
                {
                    throw Failure("production-build-path-invalid");
                }
                if (final && !expectDirectory && matches[0] is not FileInfo)
                {
                    throw Failure("production-build-path-invalid");
                }
                current = matches[0].FullName;
            }
            return CanonicalAbsolute(current);
        }
        catch (ProductionPayloadBuildException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure("production-build-path-invalid");
        }
    }

    private static string ValidateAvailableDestination(string path)
    {
        var canonical = CanonicalAbsolute(path);
        var root = Path.GetPathRoot(canonical) ?? throw Failure("production-build-path-invalid");
        var segments = canonical[root.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw Failure("production-build-path-invalid");
        }
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            EnsureNoAlternateDataStreams(current);
            var matches = new DirectoryInfo(current)
                .EnumerateFileSystemInfos()
                .Where(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.Name, segments[index]))
                .ToArray();
            if (matches.Length == 0)
            {
                return canonical;
            }
            if (matches.Length != 1 ||
                !StringComparer.Ordinal.Equals(matches[0].Name, segments[index]) ||
                matches[0] is not DirectoryInfo directory ||
                matches[0].Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw Failure("production-build-path-invalid");
            }
            EnsureNoAlternateDataStreams(directory.FullName);
            current = directory.FullName;
            if (index == segments.Length - 1)
            {
                throw Failure("production-build-output-not-clean");
            }
        }
        throw Failure("production-build-output-not-clean");
    }

    private static string ValidateExistingOrAvailableDirectory(string path)
    {
        var canonical = CanonicalAbsolute(path);
        if (Directory.Exists(canonical) || File.Exists(canonical))
        {
            return ValidateExactExistingPath(canonical, expectDirectory: true);
        }
        _ = ValidateAvailableDestination(canonical);
        return canonical;
    }

    internal static void CreateValidatedDirectoryTree(string path)
    {
        var canonical = CanonicalAbsolute(path);
        var root = Path.GetPathRoot(canonical) ?? throw Failure("production-build-path-invalid");
        var current = root;
        foreach (var segment in canonical[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            if (!Directory.Exists(next) && !File.Exists(next))
            {
                Directory.CreateDirectory(next);
            }
            current = ValidateExactExistingPath(next, expectDirectory: true);
        }
        if (!StringComparer.Ordinal.Equals(current, canonical))
        {
            throw Failure("production-build-path-invalid");
        }
    }

    private static void DeleteValidatedTreeNoFollow(string root)
    {
        var canonical = ValidateExactExistingPath(root, expectDirectory: true);
        var budget = new TreeTraversalBudget("production-build-traversal-budget", 128);
        budget.ObserveDirectory(0);
        static void DeleteDirectory(
            string directory,
            string ownedRoot,
            TreeTraversalBudget budget,
            int depth)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var child = CanonicalAbsolute(entry.FullName);
                if (!IsSameOrUnder(child, ownedRoot) || StringComparer.OrdinalIgnoreCase.Equals(child, ownedRoot))
                {
                    throw Failure("production-build-path-invalid");
                }
                EnsureNoAlternateDataStreams(entry.FullName);
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    budget.ObserveEntry(depth + 1);
                    if (entry is DirectoryInfo)
                    {
                        Directory.Delete(entry.FullName);
                    }
                    else
                    {
                        File.Delete(entry.FullName);
                    }
                }
                else if (entry is DirectoryInfo childDirectory)
                {
                    budget.ObserveDirectory(depth + 1);
                    DeleteDirectory(
                        childDirectory.FullName,
                        ownedRoot,
                        budget,
                        depth + 1);
                    childDirectory.Delete();
                }
                else if (entry is FileInfo childFile)
                {
                    budget.ObserveFile(childFile, depth + 1);
                    childFile.Delete();
                }
                else
                {
                    throw Failure("production-build-path-invalid");
                }
            }
        }
        DeleteDirectory(canonical, canonical, budget, 0);
        Directory.Delete(canonical);
    }

    private static byte[] ReadBoundedUtf8Bytes(
        string path,
        int maximumBytes,
        string reason)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            if (maximumBytes <= 0 || stream.Length < 0 || stream.Length > maximumBytes)
            {
                throw Failure(reason);
            }
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (bytes.Length >= 3 &&
                bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                throw Failure(reason);
            }
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return bytes;
        }
        catch (ProductionPayloadBuildException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            DecoderFallbackException or OverflowException)
        {
            throw Failure(reason);
        }
    }

    private static string ReadBoundedUtf8Text(
        string path,
        int maximumBytes,
        string reason) =>
        new UTF8Encoding(false, true).GetString(
            ReadBoundedUtf8Bytes(path, maximumBytes, reason));

    private static byte[] HashFileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    private static string CanonicalAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            path.Contains('\0') ||
            path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw Failure("production-build-path-invalid");
        }
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(canonical) ?? throw Failure("production-build-path-invalid");
        if (canonical.IndexOf(':', root.Length) >= 0)
        {
            throw Failure("production-build-path-invalid");
        }
        return canonical;
    }

    private static bool IsSameOrUnder(string candidate, string root) =>
        StringComparer.OrdinalIgnoreCase.Equals(candidate, root) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static ProductionPayloadBuildException Failure(string reason, string? diagnostic = null) =>
        new(reason, diagnostic);
}

public sealed class ProductionNodeTrustVerifier : IProductionNodeTrustVerifier
{
    public ProductionNodeTrust Inspect(string archivePath, string executablePath)
    {
        var archive = HashPinnedFile(
            archivePath,
            ProductionPayloadBuilder.NodeArchiveLength);
        var executable = HashPinnedFile(
            executablePath,
            ProductionPayloadBuilder.NodeExecutableLength);
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(executablePath));
#pragma warning restore SYSLIB0057
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            return new ProductionNodeTrust(
                archive,
                executable,
                certificate.Subject,
                chain.Build(certificate));
        }
        catch (CryptographicException)
        {
            return new ProductionNodeTrust(archive, executable, string.Empty, false);
        }
    }

    private static string HashPinnedFile(string path, long expectedLength)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length != expectedLength)
            {
                throw new ProductionPayloadBuildException("production-node-identity-invalid");
            }
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (ProductionPayloadBuildException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ProductionPayloadBuildException("production-node-identity-invalid");
        }
    }
}

public sealed class ProductionMinGitTrustVerifier : IProductionMinGitTrustVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint Size;
        internal string FilePath;
        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal uint Size;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr window,
        [In] ref Guid action,
        [In] ref WinTrustData data);

    public ProductionMinGitTrust Inspect(string executablePath)
    {
        try
        {
            using var stream = new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            var length = stream.Length;
            var digest = Convert.ToHexString(SHA256.HashData(stream));
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(
                X509Certificate.CreateFromSignedFile(executablePath));
#pragma warning restore SYSLIB0057
            return new ProductionMinGitTrust(
                length,
                digest,
                certificate.Subject,
                VerifyAuthenticode(executablePath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new ProductionPayloadBuildException("production-mingit-identity-invalid");
        }
    }

    private static bool VerifyAuthenticode(string executablePath)
    {
        var file = new WinTrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = executablePath,
            FileHandle = IntPtr.Zero,
            KnownSubject = IntPtr.Zero,
        };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 1,
                UnionChoice = 1,
                FileInfo = filePointer,
                ProviderFlags = 0x00000080,
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }
}

public sealed class ProductionMinGitProbe : IProductionMinGitProbe
{
    private readonly IProductionPayloadProcessRunner _runner;

    public ProductionMinGitProbe()
        : this(new ProductionPayloadProcessRunner())
    {
    }

    internal ProductionMinGitProbe(IProductionPayloadProcessRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public void VerifyLocatorCacheIsAbsent(string buildRoot)
    {
        try
        {
            ProductionPayloadBuilder.ValidatePinnedMinGitLocatorCacheIsAbsent(buildRoot);
        }
        catch (ProductionPayloadBuildException error)
            when (!StringComparer.Ordinal.Equals(
                error.ReasonCode,
                "production-build-git-cache-invalid"))
        {
            throw new ProductionPayloadBuildException("production-build-git-cache-invalid");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new ProductionPayloadBuildException("production-build-git-cache-invalid");
        }
    }

    public void Verify(string executablePath, string buildRoot, string minGitRoot)
    {
        var executable = ProductionPayloadBuilder.CanonicalExistingFile(executablePath);
        var root = ProductionPayloadBuilder.CanonicalExistingDirectory(buildRoot);
        var minGit = ProductionPayloadBuilder.CanonicalExistingDirectory(minGitRoot);
        var expectedExecutable = Path.Combine(minGit, "cmd", "git.exe");
        var expectedMinGit = Path.Combine(root, ".phase1cb-toolchain", "mingit");
        var probeRoot = Path.Combine(root, ".phase1cb-cache", "git-probe");
        var repository = Path.Combine(probeRoot, "repository.git");
        var trace = Path.Combine(root, ".phase1cb-cache", "git-trace", "event.jsonl");
        if (!StringComparer.Ordinal.Equals(executable, expectedExecutable) ||
            !StringComparer.Ordinal.Equals(minGit, expectedMinGit) ||
            Directory.Exists(repository) || File.Exists(repository) ||
            Directory.Exists(trace) || File.Exists(trace))
        {
            throw new ProductionPayloadBuildException("production-build-git-trace-invalid");
        }
        ProductionPayloadBuilder.CreateValidatedDirectoryTree(probeRoot);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = Path.Combine(root, ".phase1cb-toolchain") + Path.PathSeparator +
                Path.Combine(minGit, "cmd") + Path.PathSeparator +
                Path.Combine(windows, "System32"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_SYSTEM"] = "NUL",
            ["GIT_CONFIG_GLOBAL"] = "NUL",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "Never",
            ["GIT_TRACE2_EVENT"] = trace,
        };
        ProductionPayloadProcessSpec Command(params string[] arguments) => new(
            executable,
            arguments,
            root,
            UseShellExecute: false,
            environment)
        {
            Timeout = TimeSpan.FromMinutes(5),
            MaximumOutputBytes = 1024 * 1024,
            RemovedEnvironmentVariables = ["NODE_OPTIONS", "NODE_PATH"],
            RemovedEnvironmentVariablePrefixes = ["npm_", "YARN_", "COREPACK_"],
        };
        try
        {
            _runner.Run(Command(
                "init",
                "--bare",
                Path.GetRelativePath(root, repository)));
            _runner.Run(Command(
                "--git-dir",
                Path.GetRelativePath(root, repository),
                "fetch",
                "--depth=1",
                ProductionPayloadBuilder.MinGitLocatorUrl,
                ProductionPayloadBuilder.MinGitLocatorCommit));
            var fetchHead = ProductionPayloadBuilder.CanonicalExistingFile(Path.Combine(
                repository,
                "FETCH_HEAD"));
            var length = new FileInfo(fetchHead).Length;
            if (length <= 41 || length > 4096)
            {
                throw new ProductionPayloadBuildException(
                    "production-build-git-trace-invalid");
            }
            var bytes = File.ReadAllBytes(fetchHead);
            if (bytes.Length != length ||
                bytes[40] != (byte)'\t' ||
                !new UTF8Encoding(false, true).GetString(bytes, 0, 40).Equals(
                    ProductionPayloadBuilder.MinGitLocatorCommit,
                    StringComparison.Ordinal))
            {
                throw new ProductionPayloadBuildException(
                    "production-build-git-trace-invalid");
            }
        }
        catch (ProductionPayloadBuildException error)
            when (!StringComparer.Ordinal.Equals(
                error.ReasonCode,
                "production-build-git-trace-invalid"))
        {
            throw new ProductionPayloadBuildException("production-build-git-trace-invalid");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new ProductionPayloadBuildException("production-build-git-trace-invalid");
        }
    }
}

public sealed class ProductionPayloadProcessRunner : IProductionPayloadProcessRunner
{
    private const int MaximumFailureDiagnosticBytes = 32 * 1024;
    private const int MaximumFailureDiagnosticLines = 200;
    private static readonly Regex CredentialField = new(
        """(?<![\p{L}\p{N}_])["']?(?:password|passwd|secret|token|api_key|apikey|access_key|private_key|connectionstring|connection_string)["']?\s*[:=]""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));
    private static readonly HashSet<string> AllowedCommandEnvironmentVariables = new(
        [
            "PATH", "NX_DAEMON", "NODE_OPTIONS",
            "YARN_ENABLE_GLOBAL_CACHE", "YARN_ENABLE_IMMUTABLE_INSTALLS",
            "YARN_CACHE_FOLDER",
            "NX_CACHE_DIRECTORY", "NX_WORKSPACE_DATA_DIRECTORY",
            "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_GLOBAL",
            "GIT_TERMINAL_PROMPT", "GCM_INTERACTIVE", "GIT_TRACE2_EVENT",
        ],
        StringComparer.OrdinalIgnoreCase);

    public void Run(ProductionPayloadProcessSpec process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.UseShellExecute ||
            process.Timeout <= TimeSpan.Zero ||
            process.Timeout > TimeSpan.FromMinutes(30) ||
            process.MaximumOutputBytes <= 0 ||
            process.MaximumOutputBytes > 16 * 1024 * 1024 ||
            process.MaximumProfileCleanupEntries <= 0 ||
            process.MaximumProfileCleanupEntries > 250_000)
        {
            throw new ProductionPayloadBuildException("production-build-shell-forbidden");
        }
        if (process.RemovedEnvironmentVariables.Any(name =>
                string.IsNullOrWhiteSpace(name) || name.Contains('=')) ||
            process.RemovedEnvironmentVariablePrefixes.Any(prefix =>
                string.IsNullOrWhiteSpace(prefix) || prefix.Contains('=')))
        {
            throw new ProductionPayloadBuildException("production-build-process-spec-invalid");
        }
        if (process.Environment is not null)
        {
            foreach (var pair in process.Environment)
            {
                var reviewed = process.RemovedEnvironmentVariables.Contains(
                        pair.Key,
                        StringComparer.OrdinalIgnoreCase) ||
                    process.RemovedEnvironmentVariablePrefixes.Any(prefix =>
                        pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                var intentionalFrontendMemoryLimit =
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "NODE_OPTIONS") &&
                    StringComparer.Ordinal.Equals(pair.Value, "--max-old-space-size=8192");
                var reviewedCacheName =
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "YARN_ENABLE_GLOBAL_CACHE") ||
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "YARN_ENABLE_IMMUTABLE_INSTALLS") ||
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "YARN_CACHE_FOLDER") ||
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "NX_CACHE_DIRECTORY") ||
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "NX_WORKSPACE_DATA_DIRECTORY");
                if (reviewed && !intentionalFrontendMemoryLimit && !reviewedCacheName)
                {
                    throw new ProductionPayloadBuildException("production-build-process-spec-invalid");
                }
            }
        }
        if (!Path.IsPathFullyQualified(process.FileName) ||
            !Path.IsPathFullyQualified(process.WorkingDirectory))
        {
            throw new ProductionPayloadBuildException("production-build-path-invalid");
        }
        var executable = ProductionPayloadBuilder.CanonicalExistingFile(process.FileName);
        var workingDirectory = ProductionPayloadBuilder.CanonicalExistingDirectory(
            process.WorkingDirectory);
        if (process.Purpose == ProductionPayloadProcessPurpose.NativeAddonProbe &&
            !IsExactNativeAddonProbe(process, executable, workingDirectory))
        {
            throw new ProductionPayloadBuildException("production-build-process-spec-invalid");
        }
        if (process.Environment is not null)
        {
            foreach (var pair in process.Environment)
            {
                if (!AllowedCommandEnvironmentVariables.Contains(pair.Key))
                {
                    throw new ProductionPayloadBuildException("production-build-process-spec-invalid");
                }
                if (StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "PATH") &&
                    !(process.Purpose == ProductionPayloadProcessPurpose.NativeAddonProbe
                        ? IsExactNativeAddonProbePath(pair.Value, workingDirectory)
                        : IsExactBuildPath(pair.Value, workingDirectory)))
                {
                    throw new ProductionPayloadBuildException(
                        "production-build-process-spec-invalid");
                }
                if (pair.Key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) ||
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "GCM_INTERACTIVE"))
                {
                    if (!IsExactBuildGitEnvironment(pair, workingDirectory))
                    {
                        throw new ProductionPayloadBuildException(
                            "production-build-process-spec-invalid");
                    }
                }
                var reviewed = process.RemovedEnvironmentVariables.Contains(
                        pair.Key,
                        StringComparer.OrdinalIgnoreCase) ||
                    process.RemovedEnvironmentVariablePrefixes.Any(prefix =>
                        pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                var intentionalFrontendMemoryLimit =
                    StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "NODE_OPTIONS") &&
                    StringComparer.Ordinal.Equals(pair.Value, "--max-old-space-size=8192");
                var intentionalBuildCacheValue = IsExactBuildCacheEnvironment(
                    pair,
                    workingDirectory);
                if (reviewed && !intentionalFrontendMemoryLimit && !intentionalBuildCacheValue)
                {
                    throw new ProductionPayloadBuildException("production-build-process-spec-invalid");
                }
            }
        }
        var profileParent = ProductionPayloadBuilder.CanonicalExistingDirectory(
            Path.GetTempPath());
        var isolatedProfile = Path.Combine(
            profileParent,
            "howardlab-phase1cb-child-" + Guid.NewGuid().ToString("N"));
        var roamingProfile = Path.Combine(isolatedProfile, "AppData", "Roaming");
        var localProfile = Path.Combine(isolatedProfile, "AppData", "Local");
        var isolatedTemp = Path.Combine(isolatedProfile, "Temp");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["WINDIR"] = windows,
            ["COMSPEC"] = Path.Combine(windows, "System32", "cmd.exe"),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["PATH"] = Path.Combine(windows, "System32"),
            ["HOME"] = isolatedProfile,
            ["USERPROFILE"] = isolatedProfile,
            ["APPDATA"] = roamingProfile,
            ["LOCALAPPDATA"] = localProfile,
            ["TEMP"] = isolatedTemp,
            ["TMP"] = isolatedTemp,
        };
        foreach (var name in process.RemovedEnvironmentVariables)
        {
            environment.Remove(name);
        }
        foreach (var prefix in process.RemovedEnvironmentVariablePrefixes)
        {
            foreach (var name in environment.Keys
                         .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                environment.Remove(name);
            }
        }
        if (process.Environment is not null)
        {
            foreach (var pair in process.Environment)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        try
        {
            ProductionPayloadBuilder.CreateValidatedDirectoryTree(roamingProfile);
            ProductionPayloadBuilder.CreateValidatedDirectoryTree(localProfile);
            ProductionPayloadBuilder.CreateValidatedDirectoryTree(isolatedTemp);
            var watch = Stopwatch.StartNew();
            using var job = WindowsJobObject.CreateKillOnClose();
            ISupervisedProcess launched;
            try
            {
                launched = new WindowsProcessLauncher(
                        ProductionPayloadNullDiagnosticSink.Instance,
                        maxOutputBytes: process.MaximumOutputBytes,
                        maxLineBytes: process.MaximumOutputBytes,
                        processCleanupTimeout: TimeSpan.FromSeconds(2))
                    .LaunchAsync(
                        new LaunchSpecification(
                            RuntimeRole.Server,
                            new ProcessGeneration(RuntimeRole.Server, 1, Guid.NewGuid()),
                            executable,
                            process.Arguments,
                            workingDirectory,
                            environment,
                            new Dictionary<string, SecretValue>(StringComparer.Ordinal),
                            process.Timeout + TimeSpan.FromSeconds(1)),
                        job,
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                throw new ProductionPayloadBuildException("production-build-process-start-failed");
            }

            var jobClosed = false;
            try
            {
                while (true)
                {
                    if (launched.StandardOutput.ObservedByteCount +
                        launched.StandardError.ObservedByteCount > process.MaximumOutputBytes)
                    {
                        job.Dispose();
                        jobClosed = true;
                        throw new ProductionPayloadBuildException(
                            "production-build-process-failed",
                            BuildFailureDiagnostic(
                                workingDirectory,
                                launched.StandardOutput.Snapshot(),
                                launched.StandardError.Snapshot()));
                    }
                    var remaining = process.Timeout - watch.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        job.Dispose();
                        jobClosed = true;
                        throw new ProductionPayloadBuildException("production-build-process-timeout");
                    }
                    var interval = remaining < TimeSpan.FromMilliseconds(25)
                        ? remaining
                        : TimeSpan.FromMilliseconds(25);
                    if (Task.WhenAny(launched.Completion, Task.Delay(interval))
                        .GetAwaiter().GetResult() != launched.Completion)
                    {
                        continue;
                    }
                    var exitCode = launched.Completion.GetAwaiter().GetResult();
                    if (launched.StandardOutput.ObservedByteCount +
                        launched.StandardError.ObservedByteCount > process.MaximumOutputBytes ||
                        exitCode != 0)
                    {
                        throw new ProductionPayloadBuildException(
                            "production-build-process-failed",
                            BuildFailureDiagnostic(
                                workingDirectory,
                                launched.StandardOutput.Snapshot(),
                                launched.StandardError.Snapshot()));
                    }
                    return;
                }
            }
            catch (ProductionPayloadBuildException)
            {
                throw;
            }
            catch
            {
                throw new ProductionPayloadBuildException("production-build-process-failed");
            }
            finally
            {
                if (!jobClosed && !launched.Completion.IsCompleted)
                {
                    job.Dispose();
                }
                try
                {
                    launched.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }
            }
        }
        finally
        {
            if (Directory.Exists(isolatedProfile) || File.Exists(isolatedProfile))
            {
                DeleteIsolatedProfileNoFollow(
                    isolatedProfile,
                    profileParent,
                    process.MaximumProfileCleanupEntries);
            }
        }
    }

    private static string? BuildFailureDiagnostic(
        string buildRoot,
        string standardOutputSnapshot,
        string standardErrorSnapshot)
    {
        var sanitizer = new FailureDiagnosticSanitizer(buildRoot);
        sanitizer.Append(standardOutputSnapshot);
        sanitizer.AppendSeparatorIfNeeded(standardOutputSnapshot, standardErrorSnapshot);
        sanitizer.Append(standardErrorSnapshot);
        return sanitizer.Complete();
    }

    private sealed class FailureDiagnosticSanitizer(string buildRoot)
    {
        private const string Redacted = "[REDACTED]";
        private readonly StringBuilder _output = new(MaximumFailureDiagnosticBytes);
        private readonly StringBuilder _line = new(MaximumFailureDiagnosticBytes);
        private int _bytes;
        private int _lines;
        private int _yamlRedactionIndent = -1;
        private AnsiState _ansiState;
        private bool _pendingCarriageReturn;
        private bool _complete;

        public void Append(string snapshot)
        {
            foreach (var character in snapshot)
            {
                if (_complete) return;
                Consume(character);
            }
        }

        public void AppendSeparatorIfNeeded(string standardOutput, string standardError)
        {
            if (!_complete && standardOutput.Length != 0 && standardError.Length != 0)
            {
                FinishLine();
            }
        }

        public string? Complete()
        {
            if (!_complete && _line.Length != 0)
            {
                FinishLine();
            }
            return _output.Length == 0 ? null : _output.ToString();
        }

        private void Consume(char character)
        {
            if (_ansiState != AnsiState.None)
            {
                ConsumeAnsi(character);
                return;
            }
            if (character == '\u001b') { _ansiState = AnsiState.Escape; return; }
            if (character == '\u009b') { _ansiState = AnsiState.Csi; return; }
            if (character == '\u009d') { _ansiState = AnsiState.Osc; return; }
            if (character is '\u0090' or '\u0098' or '\u009e' or '\u009f') { _ansiState = AnsiState.String; return; }
            if (character == '\r') { _pendingCarriageReturn = true; FinishLine(); return; }
            if (character == '\n') { if (!_pendingCarriageReturn) FinishLine(); _pendingCarriageReturn = false; return; }
            _pendingCarriageReturn = false;
            if (character is '\u2028' or '\u2029' || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                FinishLine();
                return;
            }
            if (character == '\t' || !char.IsControl(character))
            {
                if (_line.Length == MaximumFailureDiagnosticBytes)
                {
                    FinishLine();
                    return;
                }
                _line.Append(character);
            }
        }

        private void ConsumeAnsi(char character)
        {
            switch (_ansiState)
            {
                case AnsiState.Escape:
                    _ansiState = character switch
                    {
                        '[' => AnsiState.Csi,
                        ']' => AnsiState.Osc,
                        'P' or 'X' or '^' or '_' => AnsiState.String,
                        >= '\u0020' and <= '\u002f' => AnsiState.EscapeIntermediate,
                        _ => AnsiState.None,
                    };
                    return;
                case AnsiState.EscapeIntermediate:
                    _ansiState = character is >= '\u0020' and <= '\u002f'
                        ? AnsiState.EscapeIntermediate
                        : AnsiState.None;
                    return;
                case AnsiState.Csi:
                    if (character is >= '@' and <= '~') _ansiState = AnsiState.None;
                    return;
                case AnsiState.Osc:
                    if (character == '\a' || character == '\u009c') _ansiState = AnsiState.None;
                    else if (character == '\u001b') _ansiState = AnsiState.StringEscape;
                    return;
                case AnsiState.String:
                    if (character == '\u009c') _ansiState = AnsiState.None;
                    else if (character == '\u001b') _ansiState = AnsiState.StringEscape;
                    return;
                case AnsiState.StringEscape:
                    _ansiState = character == '\\' ? AnsiState.None : AnsiState.String;
                    return;
            }
        }

        private void FinishLine()
        {
            if (_complete) return;
            var line = _line.ToString().Replace(buildRoot, "<BUILD_ROOT>", StringComparison.OrdinalIgnoreCase);
            _line.Clear();
            var indentation = line.TakeWhile(character => character is ' ' or '\t').Count();
            if (_yamlRedactionIndent >= 0 && line.Length == 0)
            {
                // Blank lines are valid YAML block content and do not end the redaction span.
            }
            else if (_yamlRedactionIndent >= 0 && indentation > _yamlRedactionIndent)
            {
                line = Redacted;
            }
            else
            {
                _yamlRedactionIndent = -1;
                try
                {
                    var credential = CredentialField.Match(line);
                    if (credential.Success)
                    {
                        var suffix = line[(credential.Index + credential.Length)..].TrimStart();
                        _yamlRedactionIndent = suffix.StartsWith('|') || suffix.StartsWith('>')
                            ? indentation
                            : -1;
                        line = line[..(credential.Index + credential.Length)] + Redacted;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    line = Redacted;
                    _yamlRedactionIndent = indentation;
                }
            }
            if (StringComparer.Ordinal.Equals(line, "production-build-diagnostic-begin") ||
                StringComparer.Ordinal.Equals(line, "production-build-diagnostic-end"))
            {
                line = "[CHILD_DIAGNOSTIC_MARKER_REDACTED]";
            }

            foreach (var rune in line.EnumerateRunes())
            {
                var text = rune.ToString();
                var runeBytes = Encoding.UTF8.GetByteCount(text);
                if (_bytes + runeBytes > MaximumFailureDiagnosticBytes) { _complete = true; return; }
                _output.Append(text);
                _bytes += runeBytes;
            }
            if (_bytes == MaximumFailureDiagnosticBytes || ++_lines == MaximumFailureDiagnosticLines)
            {
                _complete = true;
                return;
            }
            _output.Append('\n');
            _bytes++;
        }

        private enum AnsiState { None, Escape, EscapeIntermediate, Csi, Osc, String, StringEscape }
    }

    private static bool IsExactBuildCacheEnvironment(
        KeyValuePair<string, string> pair,
        string workingDirectory)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "YARN_ENABLE_GLOBAL_CACHE"))
        {
            return StringComparer.Ordinal.Equals(pair.Value, "false");
        }
        if (StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "YARN_ENABLE_IMMUTABLE_INSTALLS"))
        {
            return StringComparer.Ordinal.Equals(pair.Value, "true");
        }
        var relative = pair.Key.ToUpperInvariant() switch
        {
            "YARN_CACHE_FOLDER" => new[] { ".phase1cb-cache", "yarn" },
            "NX_CACHE_DIRECTORY" => new[] { ".phase1cb-cache", "nx" },
            "NX_WORKSPACE_DATA_DIRECTORY" => new[] { ".phase1cb-cache", "nx-workspace" },
            _ => null,
        };
        if (relative is null || !Path.IsPathFullyQualified(pair.Value))
        {
            return false;
        }
        try
        {
            var expected = Path.Combine([workingDirectory, .. relative]);
            var actual = ProductionPayloadBuilder.CanonicalExistingDirectory(pair.Value);
            return StringComparer.Ordinal.Equals(actual, expected);
        }
        catch (ProductionPayloadBuildException)
        {
            return false;
        }
    }

    private static bool IsExactBuildGitEnvironment(
        KeyValuePair<string, string> pair,
        string workingDirectory)
    {
        var literal = pair.Key.ToUpperInvariant() switch
        {
            "GIT_CONFIG_NOSYSTEM" => "1",
            "GIT_CONFIG_SYSTEM" => "NUL",
            "GIT_CONFIG_GLOBAL" => "NUL",
            "GIT_TERMINAL_PROMPT" => "0",
            "GCM_INTERACTIVE" => "Never",
            _ => null,
        };
        if (literal is not null)
        {
            return StringComparer.Ordinal.Equals(pair.Value, literal);
        }
        if (!StringComparer.OrdinalIgnoreCase.Equals(pair.Key, "GIT_TRACE2_EVENT") ||
            !Path.IsPathFullyQualified(pair.Value))
        {
            return false;
        }
        try
        {
            var parent = ProductionPayloadBuilder.CanonicalExistingDirectory(
                Path.GetDirectoryName(pair.Value)!);
            var expectedParent = Path.Combine(
                workingDirectory,
                ".phase1cb-cache",
                "git-trace");
            return StringComparer.Ordinal.Equals(parent, expectedParent) &&
                StringComparer.Ordinal.Equals(Path.GetFileName(pair.Value), "event.jsonl");
        }
        catch (ProductionPayloadBuildException)
        {
            return false;
        }
    }

    private static bool IsExactBuildPath(string value, string workingDirectory)
    {
        try
        {
            var nodeDirectory = ProductionPayloadBuilder.CanonicalExistingDirectory(
                Path.Combine(workingDirectory, ".phase1cb-toolchain"));
            var gitDirectory = ProductionPayloadBuilder.CanonicalExistingDirectory(
                Path.Combine(nodeDirectory, "mingit", "cmd"));
            var system32 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32");
            return StringComparer.Ordinal.Equals(
                value,
                nodeDirectory + Path.PathSeparator + gitDirectory + Path.PathSeparator + system32);
        }
        catch (ProductionPayloadBuildException)
        {
            return false;
        }
    }

    private static bool IsExactNativeAddonProbe(
        ProductionPayloadProcessSpec process,
        string executable,
        string workingDirectory)
    {
        try
        {
            var addon = ProductionPayloadBuilder.CanonicalExistingFile(
                process.Arguments.Count == 3 ? process.Arguments[2] : string.Empty);
            if (!StringComparer.Ordinal.Equals(
                    executable,
                    ProductionPayloadBuilder.CanonicalExistingFile(
                        Path.Combine(workingDirectory, "node.exe"))) ||
                process.Arguments.Count != 3 ||
                !StringComparer.Ordinal.Equals(
                    ProductionPayloadBuilder.CanonicalExistingFile(process.Arguments[0]),
                    Path.Combine(
                        workingDirectory,
                        ProductionPayloadBuilder.NativeAddonProbeName)) ||
                !StringComparer.Ordinal.Equals(process.Arguments[1], workingDirectory) ||
                !StringComparer.Ordinal.Equals(Path.GetExtension(addon), ".node") ||
                !addon.StartsWith(
                    workingDirectory + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) ||
                process.Environment is null ||
                process.Environment.Count != 2 ||
                !process.Environment.TryGetValue("PATH", out var path) ||
                !IsExactNativeAddonProbePath(path, workingDirectory) ||
                !process.Environment.TryGetValue("NX_DAEMON", out var nxDaemon) ||
                !StringComparer.Ordinal.Equals(nxDaemon, "false"))
            {
                return false;
            }
            return true;
        }
        catch (ProductionPayloadBuildException)
        {
            return false;
        }
    }

    private static bool IsExactNativeAddonProbePath(
        string value,
        string workingDirectory)
    {
        var system32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32");
        return StringComparer.Ordinal.Equals(
            value,
            workingDirectory + Path.PathSeparator + system32);
    }

    private static void DeleteIsolatedProfileNoFollow(
        string path,
        string expectedParent,
        int maximumEntries)
    {
        try
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(path)),
                    expectedParent) ||
                !Regex.IsMatch(
                    Path.GetFileName(path),
                    "^howardlab-phase1cb-child-[0-9a-f]{32}$",
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)))
            {
                throw new ProductionPayloadBuildException("production-build-profile-cleanup-failed");
            }
            var canonical = ProductionPayloadBuilder.CanonicalExistingDirectory(path);
            var stack = new Stack<(DirectoryInfo Directory, int Depth, bool Visited)>();
            stack.Push((new DirectoryInfo(canonical), 0, false));
            var entries = 1;
            long bytes = 0;
            while (stack.Count > 0)
            {
                var (directory, depth, visited) = stack.Pop();
                if (visited)
                {
                    directory.Delete();
                    continue;
                }
                if (depth > 64 || entries > maximumEntries)
                {
                    throw new ProductionPayloadBuildException("production-build-profile-cleanup-failed");
                }
                ProductionPayloadBuilder.EnsureNoAlternateDataStreams(directory.FullName);
                stack.Push((directory, depth, true));
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if (entries++ >= maximumEntries)
                    {
                        throw new ProductionPayloadBuildException("production-build-profile-cleanup-failed");
                    }
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        entry.Delete();
                    }
                    else if (entry is DirectoryInfo child)
                    {
                        stack.Push((child, depth + 1, false));
                    }
                    else if (entry is FileInfo file)
                    {
                        ProductionPayloadBuilder.EnsureNoAlternateDataStreams(file.FullName);
                        if (file.Length < 0 || bytes > 16L * 1024 * 1024 * 1024 - file.Length)
                        {
                            throw new ProductionPayloadBuildException("production-build-profile-cleanup-failed");
                        }
                        bytes += file.Length;
                        file.Delete();
                    }
                    else
                    {
                        throw new ProductionPayloadBuildException("production-build-profile-cleanup-failed");
                    }
                }
            }
        }
        catch (ProductionPayloadBuildException)
        {
            throw;
        }
        catch
        {
            throw new ProductionPayloadBuildException("production-build-profile-cleanup-failed");
        }
    }

    private sealed class ProductionPayloadNullDiagnosticSink : IDiagnosticSink
    {
        internal static ProductionPayloadNullDiagnosticSink Instance { get; } = new();

        public ValueTask WriteAsync(
            DiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
