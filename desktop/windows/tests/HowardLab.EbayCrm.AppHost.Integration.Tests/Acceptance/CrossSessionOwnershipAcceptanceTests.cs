using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Globalization;
using System.Runtime.InteropServices;
using HowardLab.EbayCrm.AppHost.Core.Lifecycle;
using HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;
using HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Acceptance;

[Collection("Cross-session ownership")]
public sealed class CrossSessionOwnershipAcceptanceTests
{
    [PostgresFact, Trait("Category", "ReleaseAcceptance")]
    public async Task SameUserDifferentSession_LoserCannotMutateOwnedProfile()
    {
        using var published = await PublishedAcceptanceLayout.CreateAsync();
        using var layout = TestLayout.CreateReal("ebaycrm-task4-cross-session");
        using var owner = StartOwner(published.AppHostPath, published.FixturePath, layout);
        try
        {
            await WaitForReadyAsync(owner).WaitAsync(TimeSpan.FromMinutes(2));
            var ownerSid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("current-user-sid-unavailable");
            var ownerSessionId = owner.SessionId;
            var logs = Path.Combine(layout.ProfileRoot, "logs");
            await WaitUntilAsync(() => Directory.Exists(logs) && Directory.EnumerateFiles(logs).Any());
            var before = CaptureSegments(logs);

            S4uBrokerResult result;
            try
            {
                result = await TaskSchedulerS4uRunner.RunAsync(new S4uRunRequest(
                    published.BrokerPath,
                    published.AppHostPath,
                    published.AppHostDirectory,
                    layout.ProfileRoot,
                    layout.PostgresBin,
                    published.FixturePath,
                    layout.Port,
                    ownerSid,
                    ownerSessionId), CancellationToken.None);
            }
            catch (S4uEnvironmentUnavailableException error) when (!IsReleaseGate())
            {
                throw Xunit.Sdk.SkipException.ForSkip(error.ReasonCode);
            }

            Assert.Equal(ownerSid, result.UserSid);
            Assert.NotEqual(ownerSessionId, result.BrokerSessionId);
            Assert.Equal(result.BrokerSessionId, result.ContenderSessionId);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("profile-already-owned", result.StandardError.Trim());
            Assert.Equal(
                string.Join('\n',
                    RuntimeState.AcquiringInstance,
                    RuntimeState.Stopping,
                    RuntimeState.Stopped),
                result.StandardOutput.Trim());
            Assert.Equal(1u, result.TotalProcesses);
            await AssertOwnerRemainsAliveForAsync(owner, TimeSpan.FromMilliseconds(1250));
            Assert.Equal(before, CaptureSegments(logs));
        }
        finally
        {
            await StopAsync(owner);
            await WaitForExclusiveFilesAsync(layout.ProfileRoot);
        }
    }

    [Theory]
    [InlineData(0, "result-nonce-mismatch")]
    [InlineData(1, "result-stale")]
    [InlineData(2, "result-session-not-isolated")]
    [InlineData(3, "result-malformed")]
    public async Task TamperedResult_IsRejectedAndAllArtifactsAreCleaned(
        int faultValue,
        string reasonCode)
    {
        var fault = (ResultFault)faultValue;
        await using var harness = await S4uRunnerHarness.CreateAsync(fault);

        var error = await Assert.ThrowsAsync<S4uResultValidationException>(() => harness.RunAsync());

        Assert.Equal(reasonCode, error.ReasonCode);
        harness.AssertRunnerOwnedArtifactsClean();
        harness.AssertClean();
    }

    [Theory]
    [InlineData(4, "task-timeout")]
    [InlineData(5, "broker-crash")]
    public async Task IncompleteTask_IsBoundedAndAllArtifactsAreCleaned(
        int faultValue,
        string reasonCode)
    {
        var fault = (ResultFault)faultValue;
        await using var harness = await S4uRunnerHarness.CreateAsync(fault);

        var error = await Assert.ThrowsAsync<S4uExecutionException>(() => harness.RunAsync());

        Assert.Equal(reasonCode, error.ReasonCode);
        harness.AssertRunnerOwnedArtifactsClean();
        harness.AssertClean();
    }

    [Fact]
    public async Task CleanupFailure_IsReportedAfterEveryCleanupActionWasAttempted()
    {
        await using var harness = await S4uRunnerHarness.CreateAsync(ResultFault.CleanupFailure);

        var error = await Assert.ThrowsAsync<S4uCleanupException>(() => harness.RunAsync());

        Assert.Equal("task-cleanup-failed", error.ReasonCode);
        harness.AssertCleanupAttempted();
        harness.AssertRunnerOwnedArtifactsClean();
        harness.AssertClean();
    }

    [Fact]
    public async Task PrimaryAndCleanupFailures_AreBothReportedWithoutLosingPrimaryContext()
    {
        await using var harness = await S4uRunnerHarness.CreateAsync(
            ResultFault.TimeoutWithCleanupFailure);

        var error = await Assert.ThrowsAsync<S4uCleanupException>(() => harness.RunAsync());

        Assert.Equal("task-cleanup-failed", error.ReasonCode);
        Assert.Equal(1, error.FailureCount);
        var combined = Assert.IsType<AggregateException>(error.InnerException);
        var primary = Assert.IsType<S4uExecutionException>(combined.InnerExceptions[0]);
        Assert.Equal("task-timeout", primary.ReasonCode);
        Assert.IsType<IOException>(combined.InnerExceptions[1]);
        harness.AssertCleanupAttempted();
        harness.AssertRunnerOwnedArtifactsClean();
        harness.AssertClean();
    }

    [Fact]
    public async Task HarnessDispose_RemovesUnexpectedRunnerArtifactsAfterAnEarlierTestFailure()
    {
        var harness = await S4uRunnerHarness.CreateAsync(ResultFault.Timeout);
        Directory.CreateDirectory(harness.RunnerRoot);
        File.WriteAllText(Path.Combine(harness.RunnerRoot, "unexpected.txt"), "leftover");
        try
        {
            await harness.DisposeAsync();

            Assert.False(Directory.Exists(harness.RunnerRoot));
        }
        finally
        {
            if (Directory.Exists(harness.RunnerRoot))
                Directory.Delete(harness.RunnerRoot, recursive: true);
        }
    }

    [Fact]
    public void RunnerRoot_IsCreatedWithProtectedCurrentUserOnlyAcl()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-s4u-acl-{Guid.NewGuid():N}");
        try
        {
            TaskSchedulerS4uRunner.CreateCurrentUserOnlyDirectory(root);

            var sid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("current-user-sid-unavailable");
            var security = FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(root),
                AccessControlSections.Access | AccessControlSections.Owner);
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(sid, security.GetOwner(typeof(SecurityIdentifier)));
            var rule = Assert.Single(security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>());
            Assert.False(rule.IsInherited);
            Assert.Equal(sid, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                rule.InheritanceFlags);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void RegistrationPolicyFailure_RemovesOnlyFolderCreatedByThisAttempt(
        bool folderCreated,
        int expectedDeleteCount)
    {
        var deleteCount = 0;

        var error = Assert.Throws<COMException>(() =>
            RealS4uTaskSession.CompleteAfterFolderAcquisition(
                () => throw new COMException(
                    "policy-blocked",
                    unchecked((int)0x80070005)),
                folderCreated,
                () => deleteCount++));

        Assert.Equal(unchecked((int)0x80070005), error.HResult);
        Assert.Equal(expectedDeleteCount, deleteCount);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void SuccessfulSessionCleanup_RemovesOnlyFolderCreatedByThisSession(
        bool folderCreated,
        int expectedDeleteCount)
    {
        var deleteCount = 0;

        RealS4uTaskSession.DeleteFolderIfOwned(folderCreated, () => deleteCount++);

        Assert.Equal(expectedDeleteCount, deleteCount);
    }

    [Fact]
    public void ConfigurationFailureAfterFolderCreation_RemovesFolderBeforeRegistration()
    {
        var deleteCount = 0;
        var registrationAttempted = false;

        var error = Assert.Throws<InvalidOperationException>(() =>
            RealS4uTaskSession.CompleteAfterFolderAcquisition(
                () =>
                {
                    InjectSettingsFailure();
                    registrationAttempted = true;
                    return new object();
                },
                folderCreated: true,
                () => deleteCount++));

        Assert.Equal("injected-settings-failure", error.Message);
        Assert.False(registrationAttempted);
        Assert.Equal(1, deleteCount);

        static void InjectSettingsFailure() =>
            throw new InvalidOperationException("injected-settings-failure");
    }

    [Fact]
    public void RegistrationAndFolderCleanupFailures_AreBothReportedWithoutPolicyMisclassification()
    {
        var primary = new COMException(
            "policy-blocked",
            unchecked((int)0x80070005));
        var cleanup = new IOException("injected-folder-cleanup-failure");

        var error = Assert.Throws<S4uCleanupException>(() =>
            RealS4uTaskSession.CompleteAfterFolderAcquisition(
                () => throw primary,
                folderCreated: true,
                () => throw cleanup));

        Assert.Equal("task-cleanup-failed", error.ReasonCode);
        Assert.Equal(1, error.FailureCount);
        var combined = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Same(primary, combined.InnerExceptions[0]);
        Assert.Same(cleanup, combined.InnerExceptions[1]);
        Assert.NotEqual(primary.HResult, error.HResult);
    }

    private static Process StartOwner(string appHostPath, string fixturePath, TestLayout layout)
    {
        var startInfo = new ProcessStartInfo(appHostPath)
        {
            WorkingDirectory = Path.GetDirectoryName(appHostPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "--profile-root", layout.ProfileRoot,
            "--postgres-bin", layout.PostgresBin,
            "--fixture-path", fixturePath,
            "--port", layout.Port.ToString(CultureInfo.InvariantCulture),
            "--mode", "run",
            "--runtime-backend", "redis",
            "--role-target", "controlled-fixture",
        }) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("owner-start-failed");
    }

    private static async Task WaitForReadyAsync(Process process)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (StringComparer.Ordinal.Equals(line, RuntimeState.Ready.ToString())) return;
        }

        throw new InvalidOperationException("owner-exited-before-ready: " +
            await process.StandardError.ReadToEndAsync());
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: false);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task WaitForExclusiveFilesAsync(string root)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }

                return;
            }
            catch (IOException)
            {
                await Task.Delay(50);
            }
        }

        throw new IOException("profile-files-remained-open-after-owner-stop");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("owner-log-not-created");
            await Task.Delay(20);
        }
    }

    private static async Task AssertOwnerRemainsAliveForAsync(
        Process owner,
        TimeSpan observationWindow)
    {
        await Task.Delay(observationWindow);
        Assert.False(owner.HasExited);
    }

    private static IReadOnlyList<SegmentSnapshot> CaptureSegments(string logs) =>
        Directory.EnumerateFiles(logs, "apphost-*.jsonl")
            .Order(StringComparer.Ordinal)
            .Select(path => new SegmentSnapshot(
                Path.GetFileName(path),
                File.GetLastWriteTimeUtc(path),
                Convert.ToHexString(SHA256.HashData(ReadShared(path)))))
            .ToArray();

    private static byte[] ReadShared(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static bool IsReleaseGate() =>
        StringComparer.Ordinal.Equals(
            Environment.GetEnvironmentVariable("EBAYCRM_RELEASE_ACCEPTANCE"), "1");

    private sealed record SegmentSnapshot(string Name, DateTime LastWriteUtc, string Sha256);
}

[CollectionDefinition("Cross-session ownership", DisableParallelization = true)]
public sealed class CrossSessionOwnershipCollection;
