using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;

public sealed class PostgresRuntimeTests
{
    [PostgresFact, Trait("Category", "Postgres")]
    public async Task StartsVerifiedPostmaster_ProbesAuthenticatedSql_AndStopsFast()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();

        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;

        Assert.Equal(PostgreSqlOperationOutcome.Completed, started.Outcome);
        Assert.Equal(cluster.Paths.DataDirectory, identity.CanonicalDataDirectory, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(cluster.Layout.PostgresExe, identity.VerifiedImagePath, StringComparer.OrdinalIgnoreCase);
        Assert.True(identity.VerifiedJobMembership);
        Assert.False(identity.PostmasterHandle.IsInvalid);
        Assert.Equal(cluster.Port, identity.LoopbackPort);

        var probe = await cluster.Runtime.ProbeAsync(identity);
        Assert.Equal("1", probe.SelectOne);
        Assert.Equal(cluster.Paths.DataDirectory, probe.ReportedDataDirectory, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(PostgreSqlOperationOutcome.Completed, await cluster.Runtime.StopFastAsync(identity));
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped, await cluster.Runtime.ReconcileStopAsync(identity));
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task Initialize_RefusesNonemptyUnrecognizedDataDirectory()
    {
        await using var cluster = await PostgresTestCluster.CreateUninitializedAsync();
        Directory.CreateDirectory(cluster.Paths.DataDirectory);
        await File.WriteAllTextAsync(Path.Combine(cluster.Paths.DataDirectory, "seller-data.txt"), "keep");

        var error = await Assert.ThrowsAsync<PostgresClusterRepairRequiredException>(async () =>
            await cluster.Runtime.InitializeAsync());

        Assert.Equal("postgres-data-directory-unrecognized", error.ReasonCode);
        Assert.True(File.Exists(Path.Combine(cluster.Paths.DataDirectory, "seller-data.txt")));
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task Initialize_RefusesPartialPgVersionDirectory()
    {
        await using var cluster = await PostgresTestCluster.CreateUninitializedAsync();
        Directory.CreateDirectory(cluster.Paths.DataDirectory);
        await File.WriteAllTextAsync(Path.Combine(cluster.Paths.DataDirectory, "PG_VERSION"), "16");

        await Assert.ThrowsAsync<PostgresClusterRepairRequiredException>(async () =>
            await cluster.Runtime.InitializeAsync());
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task LateStart_IsIndeterminate_BlocksSecondStart_AndReconcilesByIdentityAndSql()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync(
            startDeadline: TimeSpan.FromMilliseconds(1),
            reconciliationDeadline: TimeSpan.FromSeconds(30));

        var started = await cluster.Runtime.StartAsync();
        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate, started.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cluster.Runtime.StartAsync());

        await PostgresTestCluster.WaitForFileAsync(cluster.Paths.PostmasterPidFile, TimeSpan.FromSeconds(30));
        var reconciled = await cluster.Runtime.ReconcileStartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(reconciled.Identity);
        cluster.Identity = identity;

        Assert.Equal(PostgreSqlOperationOutcome.ReconciledRunning, reconciled.Outcome);
        Assert.Equal("1", (await cluster.Runtime.ProbeAsync(identity)).SelectOne);
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task LateFastStop_RemainsIndeterminateUntilRetainedHandleSignals()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync(stopDeadline: TimeSpan.FromMilliseconds(1));
        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;

        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate, await cluster.Runtime.StopFastAsync(identity));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cluster.Runtime.StopFastAsync(identity));
        await identity.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped, await cluster.Runtime.ReconcileStopAsync(identity));
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task OccupiedPort_FailsWithoutDisturbingListenerOrCreatingAnIdentity()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var cluster = await PostgresTestCluster.CreateAsync(port: port);

        var started = await cluster.Runtime.StartAsync();

        Assert.NotEqual(PostgreSqlOperationOutcome.Completed, started.Outcome);
        Assert.Null(started.Identity);
        Assert.True(listener.Server.IsBound);
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task UnavailableLogPath_FailsWithoutLeavingPostmaster()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        Directory.CreateDirectory(cluster.Paths.LogFile);

        var started = await cluster.Runtime.StartAsync();

        Assert.NotEqual(PostgreSqlOperationOutcome.Completed, started.Outcome);
        Assert.Null(started.Identity);
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task ReadOnlyDataDirectory_DoesNotStartOrProduceAnIdentity()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync(reconciliationDeadline: TimeSpan.FromSeconds(2));
        var directory = new DirectoryInfo(cluster.Paths.DataDirectory);
        var original = directory.GetAccessControl(AccessControlSections.All);
        var denied = new DirectorySecurity();
        denied.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm());
        var sid = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User!;
        denied.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.WriteData | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories |
            FileSystemRights.AppendData | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny));
        directory.SetAccessControl(denied);
        try
        {
            var started = await cluster.Runtime.StartAsync();
            Assert.NotEqual(PostgreSqlOperationOutcome.Completed, started.Outcome);
            Assert.Null(started.Identity);
        }
        finally
        {
            directory.SetAccessControl(original);
            RestoreDisposableTreeAccess(cluster.Paths.DataDirectory, sid);
        }
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task UnrelatedProcessPidFile_IsNeverAcceptedAsPostmasterIdentity()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        var current = System.Diagnostics.Process.GetCurrentProcess();
        var seconds = new DateTimeOffset(current.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
        await File.WriteAllTextAsync(cluster.Paths.PostmasterPidFile,
            $"{Environment.ProcessId}\n{cluster.Paths.DataDirectory}\n{seconds}\n{cluster.Port}\n\n127.0.0.1\n0 0\nready\n");

        var result = await cluster.Runtime.StartAsync();

        Assert.Equal(PostgreSqlOperationOutcome.Failed, result.Outcome);
        Assert.Null(result.Identity);
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task CorruptAndStalePidFiles_AreRejectedBeforePgCtlCanReplaceThem()
    {
        await using var corrupt = await PostgresTestCluster.CreateAsync();
        await File.WriteAllTextAsync(corrupt.Paths.PostmasterPidFile, "not-a-pid-file");
        var corruptResult = await corrupt.Runtime.StartAsync();
        Assert.Equal(PostgreSqlOperationOutcome.Failed, corruptResult.Outcome);
        Assert.Equal("postmaster-pid-malformed", corruptResult.ReasonCode);

        await using var stale = await PostgresTestCluster.CreateAsync();
        await File.WriteAllTextAsync(stale.Paths.PostmasterPidFile,
            $"2147483647\n{stale.Paths.DataDirectory}\n{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n{stale.Port}\n\n127.0.0.1\n0 0\nready\n");
        var staleResult = await stale.Runtime.StartAsync();
        Assert.Equal(PostgreSqlOperationOutcome.Failed, staleResult.Outcome);
        Assert.Equal("postmaster-pid-stale", staleResult.ReasonCode);
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task WrongPostgresClusterOnTargetPort_IsNotAcceptedAndIsNotDisturbed()
    {
        await using var owner = await PostgresTestCluster.CreateAsync();
        var ownerStart = await owner.Runtime.StartAsync();
        var ownerIdentity = Assert.IsType<PostgresInstanceIdentity>(ownerStart.Identity);
        owner.Identity = ownerIdentity;

        await using var contender = await PostgresTestCluster.CreateAsync(port: owner.Port);
        var contenderStart = await contender.Runtime.StartAsync();

        Assert.NotEqual(PostgreSqlOperationOutcome.Completed, contenderStart.Outcome);
        Assert.Null(contenderStart.Identity);
        Assert.Equal("1", (await owner.Runtime.ProbeAsync(ownerIdentity)).SelectOne);
        Assert.NotEqual(owner.Paths.DataDirectory, contender.Paths.DataDirectory);
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task JobClosure_CrashesOwnedPostmaster_ThenSameClusterRecoversUnderNewJob()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        var firstStart = await cluster.Runtime.StartAsync();
        using var firstIdentity = Assert.IsType<PostgresInstanceIdentity>(firstStart.Identity);
        cluster.Identity = firstIdentity;

        var recovered = await cluster.CrashJobAndRestartAsync();
        var recoveredIdentity = Assert.IsType<PostgresInstanceIdentity>(recovered.Identity);
        cluster.Identity = recoveredIdentity;

        Assert.True(firstIdentity.HasExited);
        Assert.Equal(PostgreSqlOperationOutcome.Completed, recovered.Outcome);
        Assert.NotEqual(firstIdentity.ProcessId, recoveredIdentity.ProcessId);
        Assert.Equal(cluster.Paths.DataDirectory, (await cluster.Runtime.ProbeAsync(recoveredIdentity)).ReportedDataDirectory,
            StringComparer.OrdinalIgnoreCase);
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task FailedAuthenticatedReadiness_RemainsIndeterminateAndCannotBypassReconciliation()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        await cluster.ReplaceRuntimePasswordAsync(new HowardLab.EbayCrm.AppHost.Core.Diagnostics.SecretValue(
            $"wrong-{Guid.NewGuid():N}-Aa1!"));

        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;

        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate, started.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cluster.Runtime.StartAsync());
        var reconciled = await cluster.Runtime.ReconcileStartAsync();
        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate, reconciled.Outcome);
        Assert.Same(identity, reconciled.Identity);
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task StopLaunchFailure_PreservesOwnedRunningIdentityForRetry()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;
        cluster.Launcher.FailNextStopLaunch = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await cluster.Runtime.StopFastAsync(identity));

        Assert.Equal("1", (await cluster.Runtime.ProbeAsync(identity)).SelectOne);
        Assert.Equal(PostgreSqlOperationOutcome.Completed, await cluster.Runtime.StopFastAsync(identity));
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped,
            await cluster.Runtime.ReconcileStopAsync(identity));
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task StopCancellationBeforeLaunch_PreservesOwnedRunningIdentityForRetry()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;
        cluster.Launcher.DelayNextStopLaunch = TimeSpan.FromSeconds(30);
        using var cancellation = new CancellationTokenSource();

        var stopping = cluster.Runtime.StopFastAsync(identity, cancellation.Token);
        await cluster.Launcher.StopLaunchObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stopping);

        Assert.Equal("1", (await cluster.Runtime.ProbeAsync(identity)).SelectOne);
        Assert.Equal(PostgreSqlOperationOutcome.Completed, await cluster.Runtime.StopFastAsync(identity));
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped,
            await cluster.Runtime.ReconcileStopAsync(identity));
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task NonzeroCompletedStop_ReconcilesRunningAndAllowsRetry()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;
        cluster.Launcher.CompleteNextStopWithExitCode = 1;

        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate,
            await cluster.Runtime.StopFastAsync(identity));
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledRunning,
            await cluster.Runtime.ReconcileStopAsync(identity));

        Assert.Equal(PostgreSqlOperationOutcome.Completed, await cluster.Runtime.StopFastAsync(identity));
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped,
            await cluster.Runtime.ReconcileStopAsync(identity));
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task StopDeadline_IsSharedAcrossLaunchAndCompletionWait()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync(stopDeadline: TimeSpan.FromSeconds(2));
        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;
        cluster.Launcher.DelayNextStopLaunch = TimeSpan.FromMilliseconds(1100);
        cluster.Launcher.LeaveNextStopPending = true;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate,
            await cluster.Runtime.StopFastAsync(identity));

        timer.Stop();
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(1.8), TimeSpan.FromMilliseconds(2700));
        Assert.Equal(2, cluster.Launcher.LastStopTimeoutSeconds);
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task ReconcileIndeterminateExitedIdentity_CleansItsVerifiedStalePidFile()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        await cluster.ReplaceRuntimePasswordAsync(new HowardLab.EbayCrm.AppHost.Core.Diagnostics.SecretValue(
            $"wrong-{Guid.NewGuid():N}-Aa1!"));
        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;
        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate, started.Outcome);

        cluster.Job.Dispose();
        await identity.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(cluster.Paths.PostmasterPidFile));

        var reconciled = await cluster.Runtime.ReconcileStartAsync();

        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped, reconciled.Outcome);
        Assert.False(File.Exists(cluster.Paths.PostmasterPidFile));
        cluster.Identity = null;
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task FastShutdown_WhileRealStandbyRecoveryIsActive()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        cluster.ConfigureAsDisconnectedStandby();

        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;

        Assert.Equal(PostgreSqlOperationOutcome.Completed, started.Outcome);
        Assert.Equal("t", await cluster.ExecuteSqlScalarAsync("SELECT pg_is_in_recovery();"));
        Assert.Equal(PostgreSqlOperationOutcome.Completed, await cluster.Runtime.StopFastAsync(identity));
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped,
            await cluster.Runtime.ReconcileStopAsync(identity));
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task ReconcileRunningExit_CleansPidAndTransitionsStopped()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        var started = await cluster.Runtime.StartAsync();
        using var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;

        cluster.Job.Dispose();
        await identity.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var reconciled = await cluster.Runtime.ReconcileStartAsync();

        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped, reconciled.Outcome);
        Assert.False(File.Exists(cluster.Paths.PostmasterPidFile));
        cluster.Identity = null;
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task TimedOutSqlHelper_CannotTerminatePostmasterJob()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        await cluster.ReplaceRuntimePasswordAsync(cluster.Password, TimeSpan.FromTicks(1));

        var started = await cluster.Runtime.StartAsync();
        var identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = identity;

        Assert.Equal(PostgreSqlOperationOutcome.TimedOutIndeterminate, started.Outcome);
        Assert.False(identity.HasExited);
        Assert.True(identity.VerifiedJobMembership);
    }

    [PostgresFact, Trait("Category", "Postgres")]
    public async Task ServerLogs_UsePostgresNativeCollectorWithCyclicRuntimeFiles()
    {
        await using var cluster = await PostgresTestCluster.CreateAsync();
        Directory.CreateDirectory(cluster.Paths.ServerLogDirectory);
        for (var index = 0; index < 12; index++)
        {
            var oldLog = Path.Combine(cluster.Paths.ServerLogDirectory, $"postgres-old-{index:D2}.log");
            await File.WriteAllBytesAsync(oldLog, new byte[(2 * 1024 * 1024) + index]);
            File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddMinutes(-index - 1));
        }
        var started = await cluster.Runtime.StartAsync();
        var firstIdentity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        cluster.Identity = firstIdentity;
        var currentLogFiles = Path.Combine(cluster.Paths.DataDirectory, "current_logfiles");

        await PostgresTestCluster.WaitForFileAsync(currentLogFiles, TimeSpan.FromSeconds(10));
        var current = await File.ReadAllTextAsync(currentLogFiles);

        Assert.Contains("../runtime/postgres-logs/postgres-", current, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(cluster.Paths.ServerLogDirectory));
        var boundedLogs = Directory.GetFiles(cluster.Paths.ServerLogDirectory, "postgres-*.log");
        Assert.InRange(boundedLogs.Length, 1, 8);
        Assert.All(boundedLogs, path => Assert.InRange(new FileInfo(path).Length, 0, 1024 * 1024));

        await cluster.ExecuteSqlAsync("DO $$ BEGIN FOR i IN 1..4096 LOOP RAISE LOG '%', repeat('x', 1024); END LOOP; END $$;");
        await WaitUntilAsync(
            () => Directory.GetFiles(cluster.Paths.ServerLogDirectory, "postgres-*.log")
                .Any(path => new FileInfo(path).Length > 1024 * 1024),
            TimeSpan.FromSeconds(10));

        _ = await cluster.Runtime.StopFastAsync(firstIdentity);
        Assert.Equal(PostgreSqlOperationOutcome.ReconciledStopped,
            await cluster.Runtime.ReconcileStopAsync(firstIdentity));
        firstIdentity.Dispose();
        cluster.Identity = null;
        AssertLifecycleLogBounds(cluster.Paths.ServerLogDirectory);

        var restarted = await cluster.Runtime.StartAsync();
        var secondIdentity = Assert.IsType<PostgresInstanceIdentity>(restarted.Identity);
        cluster.Identity = secondIdentity;
        await cluster.ExecuteSqlAsync("DO $$ BEGIN FOR i IN 1..4096 LOOP RAISE LOG '%', repeat('y', 1024); END LOOP; END $$;");
        await WaitUntilAsync(
            () => Directory.GetFiles(cluster.Paths.ServerLogDirectory, "postgres-*.log")
                .Any(path => new FileInfo(path).Length > 1024 * 1024),
            TimeSpan.FromSeconds(10));

        cluster.Job.Dispose();
        await secondIdentity.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await cluster.DisposeRuntimeAsync();
        AssertLifecycleLogBounds(cluster.Paths.ServerLogDirectory);
    }

    private static void AssertLifecycleLogBounds(string serverLogDirectory)
    {
        var logs = Directory.GetFiles(serverLogDirectory, "postgres-*.log");
        Assert.InRange(logs.Length, 1, 8);
        Assert.All(logs, path => Assert.InRange(new FileInfo(path).Length, 0, 1024 * 1024));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The expected condition was not reached.");
            await Task.Delay(50);
        }
    }

    private static void RestoreDisposableTreeAccess(string root, SecurityIdentifier sid)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(value => value.Length))
        {
            if (Directory.Exists(path))
            {
                var security = new DirectorySecurity();
                security.SetOwner(sid);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(security);
            }
            else
            {
                var security = new FileSecurity();
                security.SetOwner(sid);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(security);
            }
        }
    }
}

internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Real PostgreSQL supervision requires Windows.";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EBAYCRM_POSTGRES_BIN")))
        {
            Skip = "Set EBAYCRM_POSTGRES_BIN to the PostgreSQL 16 bin directory.";
        }
    }
}
