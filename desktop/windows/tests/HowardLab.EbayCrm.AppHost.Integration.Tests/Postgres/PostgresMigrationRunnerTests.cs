using HowardLab.EbayCrm.AppHost.Core.Migrations;
using HowardLab.EbayCrm.AppHost.Core.Processes;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.Postgres;

public sealed class PostgresMigrationRunnerTests
{
    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_UsesStdinTransactionAndPersistsVerifiedSuccess()
    {
        await using var cluster = await StartClusterAsync();
        var expectedClusterId = Guid.NewGuid();
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        var runner = CreateRunner(cluster, store, expectedClusterId);

        var result = await runner.RunAsync();

        Assert.True(
            result.Outcome == MigrationOutcome.Succeeded,
            $"Outcome {result.Outcome}, reason {result.ReasonCode}, exit {result.ProcessExitCode}; " +
            $"stderr: {cluster.Launcher.LastMigrationProcess?.StandardError.Snapshot()}");
        Assert.Equal("migration-target-verified", result.ReasonCode);
        var row = await cluster.ExecuteSqlScalarAsync(
            "SELECT cluster_id::text || '|' || schema_version::text || '|' || singleton_key::text FROM desktop_runtime.apphost_control;");
        Assert.Equal($"{expectedClusterId:D}|1|true", row);
        var marker = Assert.IsType<MigrationAttemptRecord>(await store.ReadAsync());
        Assert.Equal(MigrationAttemptState.Succeeded, marker.State);
        Assert.Equal(1, marker.TargetSchemaVersion);
        Assert.False(File.Exists(store.TemporaryPath));
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_ExitAfterCommitBeforeMarkerUpdate_IsRecoveredFromActualSchema()
    {
        await using var cluster = await StartClusterAsync();
        var expectedClusterId = Guid.NewGuid();
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        var crashing = CreateRunner(
            cluster,
            store,
            expectedClusterId,
            stage =>
            {
                if (stage == PostgresMigrationStage.AfterVerifiedTargetBeforeMarker)
                    throw new IOException("injected-apphost-termination");
            });

        await Assert.ThrowsAsync<IOException>(() => crashing.RunAsync());
        Assert.Equal(MigrationAttemptState.Running, (await store.ReadAsync())!.State);

        var recovered = await CreateRunner(cluster, store, expectedClusterId).RunAsync();

        Assert.Equal(MigrationOutcome.Succeeded, recovered.Outcome);
        Assert.Equal("migration-commit-recovered", recovered.ReasonCode);
        Assert.Equal(MigrationAttemptState.Succeeded, (await store.ReadAsync())!.State);
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_ExistingMismatchedClusterId_FailsClosedWithoutChangingRow()
    {
        await using var cluster = await StartClusterAsync();
        var actualClusterId = Guid.NewGuid();
        await cluster.ExecuteSqlAsync(
            $"CREATE SCHEMA desktop_runtime; CREATE TABLE desktop_runtime.apphost_control " +
            "(singleton_key boolean PRIMARY KEY CHECK (singleton_key), cluster_id uuid NOT NULL, schema_version integer NOT NULL CHECK (schema_version >= 0)); " +
            $"INSERT INTO desktop_runtime.apphost_control VALUES (true, '{actualClusterId:D}', 0);");
        var runner = CreateRunner(
            cluster,
            new AtomicMigrationAttemptStore(cluster.Root),
            Guid.NewGuid());

        var result = await runner.RunAsync();

        Assert.Equal(MigrationOutcome.RepairRequired, result.Outcome);
        Assert.Equal("migration-cluster-id-mismatch", result.ReasonCode);
        Assert.Equal(actualClusterId.ToString("D"), await cluster.ExecuteSqlScalarAsync(
            "SELECT cluster_id::text FROM desktop_runtime.apphost_control;"));
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_PreExistingUnderConstrainedControlTable_IsRejectedWithoutAdvance()
    {
        await using var cluster = await StartClusterAsync();
        var expectedClusterId = Guid.NewGuid();
        await cluster.ExecuteSqlAsync(
            "CREATE SCHEMA desktop_runtime; " +
            "CREATE TABLE desktop_runtime.apphost_control " +
            "(singleton_key boolean PRIMARY KEY, cluster_id uuid, schema_version integer); " +
            $"INSERT INTO desktop_runtime.apphost_control VALUES (true, '{expectedClusterId:D}', 0);");
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        var runner = CreateRunner(cluster, store, expectedClusterId);

        var result = await runner.RunAsync();

        Assert.Equal(MigrationOutcome.Failed, result.Outcome);
        Assert.NotEqual(0, result.ProcessExitCode);
        Assert.Equal("0", await cluster.ExecuteSqlScalarAsync(
            "SELECT schema_version::text FROM desktop_runtime.apphost_control;"));
        await cluster.ExecuteSqlAsync(
            $"INSERT INTO desktop_runtime.apphost_control VALUES (false, '{Guid.NewGuid():D}', -1);");
        Assert.Equal("2", await cluster.ExecuteSqlScalarAsync(
            "SELECT count(*)::text FROM desktop_runtime.apphost_control;"));
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_KnownSqlError_RecordsFailedOnlyAfterVerifyingStartingSchema()
    {
        await using var cluster = await StartClusterAsync();
        var badScript = Path.Combine(cluster.Root, "bad-migration.sql");
        await File.WriteAllTextAsync(badScript, "BEGIN; SELECT no_such_function(); COMMIT;");
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        var runner = new PostgresMigrationRunner(
            cluster.Runtime,
            cluster.Identity!,
            store,
            Guid.NewGuid(),
            new Version(1, 0, 0),
            startingSchemaVersion: 0,
            targetSchemaVersion: 1,
            badScript,
            TimeSpan.FromSeconds(20));

        var result = await runner.RunAsync();

        Assert.Equal(MigrationOutcome.Failed, result.Outcome);
        Assert.Equal("migration-process-failed", result.ReasonCode);
        Assert.Equal(MigrationAttemptState.Failed, (await store.ReadAsync())!.State);
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_ExitZeroWithWrongSchema_RecordsFailed()
    {
        await using var cluster = await StartClusterAsync();
        var noOpScript = Path.Combine(cluster.Root, "no-op-migration.sql");
        await File.WriteAllTextAsync(noOpScript, "SELECT 1;");
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        var runner = CreateRunnerForScript(cluster, store, Guid.NewGuid(), noOpScript);

        var result = await runner.RunAsync();

        Assert.Equal(0, result.ProcessExitCode);
        Assert.Equal(MigrationOutcome.Failed, result.Outcome);
        Assert.Equal("migration-exit-success-schema-mismatch", result.ReasonCode);
        Assert.Equal(MigrationAttemptState.Failed, (await store.ReadAsync())!.State);
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_RunningMarkerAtStartingSchema_RequiresExplicitRetryWithoutSameRunLaunch()
    {
        await using var cluster = await StartClusterAsync();
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        var started = DateTimeOffset.UtcNow;
        await store.WriteAsync(new MigrationAttemptRecord(
            Guid.NewGuid(), new Version(1, 0, 0), 0, 1,
            MigrationAttemptState.Running, started, null, "migration-running"));
        var runner = CreateRunner(cluster, store, Guid.NewGuid());

        var result = await runner.RunAsync();

        Assert.Equal(MigrationOutcome.ExplicitRetryAllowed, result.Outcome);
        Assert.Equal("migration-not-applied-retry-allowed", result.ReasonCode);
        Assert.Null(cluster.Launcher.LastMigrationProcess);
        Assert.Equal(MigrationAttemptState.Running, (await store.ReadAsync())!.State);
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_SubstitutedPidFileAfterIdentityCapture_FailsClosedWithoutSignalingOtherCluster()
    {
        await using var owner = await StartClusterAsync();
        await using var other = await StartClusterAsync();
        var retainedPidFile = await File.ReadAllBytesAsync(owner.Paths.PostmasterPidFile);
        try
        {
            File.Copy(other.Paths.PostmasterPidFile, owner.Paths.PostmasterPidFile, overwrite: true);
            var store = new AtomicMigrationAttemptStore(owner.Root);
            var runner = CreateRunner(owner, store, Guid.NewGuid());

            await Assert.ThrowsAsync<PostmasterPidFileException>(() => runner.RunAsync());

            Assert.False(other.Identity!.HasExited);
            Assert.Null(owner.Launcher.LastMigrationProcess);
            Assert.Null(await store.ReadAsync());
        }
        finally
        {
            await File.WriteAllBytesAsync(owner.Paths.PostmasterPidFile, retainedPidFile);
        }
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_PidFileSubstitutionAfterChildExit_FailsVerificationClosedThenRecoversCommit()
    {
        await using var owner = await StartClusterAsync();
        await using var other = await StartClusterAsync();
        var retainedPidFile = await File.ReadAllBytesAsync(owner.Paths.PostmasterPidFile);
        var store = new AtomicMigrationAttemptStore(owner.Root);
        var clusterId = Guid.NewGuid();
        var runner = CreateRunner(
            owner,
            store,
            clusterId,
            stage =>
            {
                if (stage == PostgresMigrationStage.AfterChildExitBeforeVerification)
                    File.Copy(other.Paths.PostmasterPidFile, owner.Paths.PostmasterPidFile, overwrite: true);
            });

        try
        {
            await Assert.ThrowsAsync<PostmasterPidFileException>(() => runner.RunAsync());

            Assert.False(other.Identity!.HasExited);
            Assert.Equal(MigrationAttemptState.Running, (await store.ReadAsync())!.State);
        }
        finally
        {
            await File.WriteAllBytesAsync(owner.Paths.PostmasterPidFile, retainedPidFile);
        }

        var recovered = await CreateRunner(owner, store, clusterId).RunAsync();
        Assert.Equal(MigrationOutcome.Succeeded, recovered.Outcome);
        Assert.Equal("migration-commit-recovered", recovered.ReasonCode);
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_CancellationAfterChildExit_StillVerifiesAndRecordsCommittedTarget()
    {
        await using var cluster = await StartClusterAsync();
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        using var cancellation = new CancellationTokenSource();
        var runner = CreateRunner(
            cluster,
            store,
            Guid.NewGuid(),
            stage =>
            {
                if (stage == PostgresMigrationStage.AfterChildExitBeforeVerification)
                    cancellation.Cancel();
            });

        var result = await runner.RunAsync(cancellation.Token);

        Assert.Equal(MigrationOutcome.Succeeded, result.Outcome);
        Assert.Equal(MigrationAttemptState.Succeeded, (await store.ReadAsync())!.State);
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_PostgresJobTerminationDuringMigration_LeavesRunningMarkerIndeterminate()
    {
        await using var cluster = await StartClusterAsync();
        var blockingScript = Path.Combine(cluster.Root, "blocking-migration.sql");
        await File.WriteAllTextAsync(blockingScript, "BEGIN; SELECT pg_sleep(300); COMMIT;");
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        var runner = CreateRunnerForScript(cluster, store, Guid.NewGuid(), blockingScript);

        var run = runner.RunAsync();
        await cluster.Launcher.MigrationLaunchObserved.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cluster.Job.Dispose();
        await cluster.Identity!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<PostgresProbeException>(() => run);
        Assert.Equal(MigrationAttemptState.Running, (await store.ReadAsync())!.State);
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_HostCancellationDuringMigration_LeavesCrashMarkerForNextLaunchClassification()
    {
        await using var cluster = await StartClusterAsync();
        var blockingScript = Path.Combine(cluster.Root, "apphost-termination-migration.sql");
        await File.WriteAllTextAsync(blockingScript, "BEGIN; SELECT pg_sleep(300); COMMIT;");
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        var clusterId = Guid.NewGuid();
        var runner = CreateRunnerForScript(cluster, store, clusterId, blockingScript);
        using var hostLifetime = new CancellationTokenSource();

        var run = runner.RunAsync(hostLifetime.Token);
        await cluster.Launcher.MigrationLaunchObserved.Task.WaitAsync(TimeSpan.FromSeconds(20));
        hostLifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(MigrationAttemptState.Running, (await store.ReadAsync())!.State);
        Assert.True(cluster.Launcher.LastMigrationProcess!.Completion.IsCompleted);
        Assert.False(File.Exists(store.TemporaryPath));

        var nextLaunch = await CreateRunner(cluster, store, clusterId).RunAsync();
        Assert.Equal(MigrationOutcome.ExplicitRetryAllowed, nextLaunch.Outcome);
        Assert.Equal("migration-not-applied-retry-allowed", nextLaunch.ReasonCode);
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task AbruptAppHostProcessTermination_ClosesJobsAndClassifiesRetainedProfileOnRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-task8-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var readyPath = Path.Combine(root, "migration-started.gate");
        var scriptPath = Path.Combine(root, "blocking-host-migration.sql");
        await File.WriteAllTextAsync(scriptPath, "BEGIN; SELECT pg_sleep(300); COMMIT;");
        var port = AllocateLoopbackPort();
        var clusterId = Guid.NewGuid();
        var password = $"task8-host-{Guid.NewGuid():N}-Aa1!";
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "HowardLab.EbayCrm.AppHost.Fixture.exe");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fixturePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "migration-host",
            Environment.GetEnvironmentVariable("EBAYCRM_POSTGRES_BIN")!,
            root,
            port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            clusterId.ToString("D"),
            Path.GetFullPath(scriptPath),
            readyPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["TASK8_PG_PASSWORD"] = password;
        using var host = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the migration-host fixture.");
        PostgresTestCluster? recoveredCluster = null;
        try
        {
            var ready = PostgresTestCluster.WaitForFileAsync(readyPath, TimeSpan.FromSeconds(60));
            var exited = host.WaitForExitAsync();
            if (await Task.WhenAny(ready, exited) == exited && !File.Exists(readyPath))
            {
                throw new InvalidOperationException(
                    $"Migration host exited before its gate. stdout: {await host.StandardOutput.ReadToEndAsync()} " +
                    $"stderr: {await host.StandardError.ReadToEndAsync()}");
            }
            await ready;
            var processIds = (await File.ReadAllTextAsync(readyPath))
                .Split('|').Select(int.Parse).ToArray();
            Assert.Equal(2, processIds.Length);
            Assert.False(System.Diagnostics.Process.GetProcessById(processIds[0]).HasExited);
            Assert.False(System.Diagnostics.Process.GetProcessById(processIds[1]).HasExited);

            host.Kill(entireProcessTree: false);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await WaitForProcessExitAsync(processIds[0]);
            await WaitForProcessExitAsync(processIds[1]);

            var store = new AtomicMigrationAttemptStore(root);
            Assert.Equal(MigrationAttemptState.Running, (await store.ReadAsync())!.State);
            Assert.False(File.Exists(store.TemporaryPath));

            recoveredCluster = await PostgresTestCluster.OpenExistingAsync(
                root,
                port,
                new HowardLab.EbayCrm.AppHost.Core.Diagnostics.SecretValue(password));
            var staleStart = await recoveredCluster.Runtime.StartAsync();
            Assert.Equal(PostgreSqlOperationOutcome.Failed, staleStart.Outcome);
            Assert.Equal("postmaster-pid-stale", staleStart.ReasonCode);
            File.Delete(recoveredCluster.Paths.PostmasterPidFile);
            var started = await recoveredCluster.Runtime.StartAsync();
            recoveredCluster.Identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
            var classified = await CreateRunner(recoveredCluster, store, clusterId).RunAsync();

            Assert.Equal(MigrationOutcome.ExplicitRetryAllowed, classified.Outcome);
            Assert.Equal("migration-not-applied-retry-allowed", classified.ReasonCode);
            Assert.Null(recoveredCluster.Launcher.LastMigrationProcess);
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: false);
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            if (recoveredCluster is not null)
                await recoveredCluster.DisposeAsync();
            else if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_AdvisoryLockContention_FailsTransactionWithoutSchemaAdvance()
    {
        const long advisoryLock = 7626654561034270001;
        await using var cluster = await StartClusterAsync();
        await using var holder = await cluster.LaunchSqlCommandAsync(
            $"SELECT pg_advisory_lock({advisoryLock}); SELECT pg_sleep(300);");
        await WaitForAdvisoryLockAsync(cluster, advisoryLock);
        var source = await File.ReadAllTextAsync(FindMigrationScript());
        var contentionScript = Path.Combine(cluster.Root, "contention-migration.sql");
        await File.WriteAllTextAsync(
            contentionScript,
            source.Replace("BEGIN;", "BEGIN;\nSET LOCAL lock_timeout = '100ms';", StringComparison.Ordinal));
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        var runner = CreateRunnerForScript(cluster, store, Guid.NewGuid(), contentionScript);

        var result = await runner.RunAsync();

        Assert.Equal(MigrationOutcome.Failed, result.Outcome);
        Assert.NotEqual(0, result.ProcessExitCode);
        Assert.Equal(MigrationAttemptState.Failed, (await store.ReadAsync())!.State);
        Assert.Null((await cluster.Runtime.ProbeAsync(cluster.Identity!)).SchemaVersion);
        Assert.False(holder.Process.Completion.IsCompleted);
    }

    [PostgresFact, Trait("Category", "Migration")]
    public async Task RunAsync_MigrationTimeout_VerifiesClusterAndLeavesIndeterminateRunningMarker()
    {
        await using var cluster = await StartClusterAsync();
        var blockingScript = Path.Combine(cluster.Root, "timeout-migration.sql");
        await File.WriteAllTextAsync(blockingScript, "BEGIN; SELECT pg_sleep(300); COMMIT;");
        var store = new AtomicMigrationAttemptStore(cluster.Root);
        var runner = new PostgresMigrationRunner(
            cluster.Runtime,
            cluster.Identity!,
            store,
            Guid.NewGuid(),
            new Version(1, 0, 0),
            0,
            1,
            Path.GetFullPath(blockingScript),
            TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<PostgresMigrationIndeterminateException>(() => runner.RunAsync());

        Assert.Equal("migration-process-timeout-indeterminate", error.ReasonCode);
        Assert.Equal(MigrationAttemptState.Running, (await store.ReadAsync())!.State);
        Assert.Null((await cluster.Runtime.ProbeAsync(cluster.Identity!)).SchemaVersion);
        Assert.True(cluster.Launcher.LastMigrationProcess!.Completion.IsCompleted);
        Assert.False(File.Exists(store.TemporaryPath));
    }

    private static PostgresMigrationRunner CreateRunner(
        PostgresTestCluster cluster,
        AtomicMigrationAttemptStore store,
        Guid expectedClusterId,
        Action<PostgresMigrationStage>? hook = null) => new(
            cluster.Runtime,
            cluster.Identity!,
            store,
            expectedClusterId,
            new Version(1, 0, 0),
            startingSchemaVersion: 0,
            targetSchemaVersion: 1,
            FindMigrationScript(),
            TimeSpan.FromSeconds(20),
            hook);

    private static PostgresMigrationRunner CreateRunnerForScript(
        PostgresTestCluster cluster,
        AtomicMigrationAttemptStore store,
        Guid expectedClusterId,
        string script) => new(
            cluster.Runtime,
            cluster.Identity!,
            store,
            expectedClusterId,
            new Version(1, 0, 0),
            0,
            1,
            Path.GetFullPath(script),
            TimeSpan.FromSeconds(20));

    private static async Task<PostgresTestCluster> StartClusterAsync()
    {
        var cluster = await PostgresTestCluster.CreateAsync();
        var started = await cluster.Runtime.StartAsync();
        cluster.Identity = Assert.IsType<PostgresInstanceIdentity>(started.Identity);
        return cluster;
    }

    private static async Task WaitForAdvisoryLockAsync(PostgresTestCluster cluster, long advisoryLock)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!deadline.IsCancellationRequested)
        {
            if (await cluster.ExecuteSqlScalarAsync(
                $"SELECT NOT pg_try_advisory_lock({advisoryLock});") == "t")
            {
                return;
            }
            await Task.Yield();
        }
        throw new TimeoutException("The advisory-lock holder did not acquire the migration lock.");
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (ArgumentException)
        {
            // The process exited before a wait handle was opened.
        }
    }

    private static string FindMigrationScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "desktop", "windows", "runtime", "migrations", "0001_apphost_control.sql");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate the Task 8 migration SQL.");
    }
}
