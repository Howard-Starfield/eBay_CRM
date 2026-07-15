using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HowardLab.EbayCrm.AppHost.Core.Migrations;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using HowardLab.EbayCrm.AppHost.Windows.Postgres;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Postgres;

public sealed class AtomicMigrationAttemptStoreTests
{
    [Fact]
    public async Task WriteAsync_RoundTripsStrictRecordWithCurrentUserOnlyAcl()
    {
        using var profile = new TemporaryProfile();
        var store = new AtomicMigrationAttemptStore(profile.Path);
        var record = Record(MigrationAttemptState.Running);

        await store.WriteAsync(record);
        var actual = await store.ReadAsync();

        Assert.Equal(record, actual);
        Assert.False(File.Exists(store.TemporaryPath));
        var security = new FileInfo(store.MarkerPath).GetAccessControl();
        var currentSid = WindowsIdentity.GetCurrent().User!;
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Equal(currentSid, rule.IdentityReference));
    }

    [Fact]
    public async Task WriteAsync_RoundTripsFourComponentAppVersionExactly()
    {
        using var profile = new TemporaryProfile();
        var store = new AtomicMigrationAttemptStore(profile.Path);
        var baseline = Record(MigrationAttemptState.Running);
        var record = new MigrationAttemptRecord(
            baseline.OperationId,
            new Version(1, 2, 3, 4),
            baseline.StartingSchemaVersion,
            baseline.TargetSchemaVersion,
            baseline.State,
            baseline.StartedAtUtc,
            baseline.FinishedAtUtc,
            baseline.ReasonCode);

        await store.WriteAsync(record);

        Assert.Equal(record, await store.ReadAsync());
    }

    [Fact]
    public async Task WriteAsync_AtomicallyReplacesExistingRecordAndRemovesStaleTemp()
    {
        using var profile = new TemporaryProfile();
        var store = new AtomicMigrationAttemptStore(profile.Path);
        await store.WriteAsync(Record(MigrationAttemptState.Running));
        await File.WriteAllTextAsync(store.TemporaryPath, "stale");
        var terminal = Record(MigrationAttemptState.Succeeded);

        await store.WriteAsync(terminal);

        Assert.Equal(terminal, await store.ReadAsync());
        Assert.False(File.Exists(store.TemporaryPath));
    }

    [Fact]
    public async Task WriteAsync_InterruptedBeforeRenameLeavesOldRecordAndReadReconcilesTemp()
    {
        using var profile = new TemporaryProfile();
        var store = new AtomicMigrationAttemptStore(profile.Path);
        var original = Record(MigrationAttemptState.Running);
        await store.WriteAsync(original);
        var interrupted = new AtomicMigrationAttemptStore(profile.Path, stage =>
        {
            if (stage == AtomicMigrationWriteStage.BeforeRename) throw new IOException("injected");
        });

        await Assert.ThrowsAsync<IOException>(() => interrupted.WriteAsync(Record(MigrationAttemptState.Failed)).AsTask());

        Assert.Equal(original, await store.ReadAsync());
        Assert.False(File.Exists(store.TemporaryPath));
    }

    [Theory]
    [MemberData(nameof(InvalidMarkers))]
    public async Task ReadAsync_RejectsMalformedOrAmbiguousMarker(string json, string reasonCode)
    {
        using var profile = new TemporaryProfile();
        var store = new AtomicMigrationAttemptStore(profile.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.MarkerPath)!);
        await File.WriteAllTextAsync(store.MarkerPath, json, new UTF8Encoding(false));

        var error = await Assert.ThrowsAsync<MigrationAttemptStoreException>(() => store.ReadAsync().AsTask());

        Assert.Equal(reasonCode, error.ReasonCode);
    }

    public static TheoryData<string, string> InvalidMarkers => new()
    {
        { ValidJson().Replace("\"operationId\"", "\"operationId\":\"00000000-0000-0000-0000-000000000001\",\"operationId\"", StringComparison.Ordinal), "migration-marker-duplicate-property" },
        { ValidJson().Replace("\"Running\"", "\"Unknown\"", StringComparison.Ordinal), "migration-marker-invalid" },
        { ValidJson().Replace("\"2026-07-14T12:00:00.0000000+00:00\"", "\"not-a-time\"", StringComparison.Ordinal), "migration-marker-invalid" },
        { ValidJson().Replace("\"recordVersion\":1", "\"recordVersion\":2", StringComparison.Ordinal), "migration-marker-version-unsupported" },
        { ValidJson().Replace("\"reasonCode\":\"migration-running\"", "\"reasonCode\":\"migration-running\",\"password\":\"secret\"", StringComparison.Ordinal), "migration-marker-unknown-property" },
        { ValidJson().Replace("00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000000", StringComparison.Ordinal), "migration-marker-invalid" },
        { ValidJson().Replace("\"appVersion\":\"1.2.3\"", "\"appVersion\":\"1.2\"", StringComparison.Ordinal), "migration-marker-invalid" },
        { ValidJson().Replace("\"state\":\"Running\"", "\"state\":\"1\"", StringComparison.Ordinal), "migration-marker-invalid" },
        { ValidJson().Replace("\"recordVersion\":1", "\"recordVersion\":\"1\"", StringComparison.Ordinal), "migration-marker-invalid" },
    };

    [Fact]
    public async Task ReadAsync_RejectsOversizeMarkerWithoutAllocatingUnboundedInput()
    {
        using var profile = new TemporaryProfile();
        var store = new AtomicMigrationAttemptStore(profile.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.MarkerPath)!);
        await File.WriteAllBytesAsync(store.MarkerPath, new byte[AtomicMigrationAttemptStore.MaximumMarkerBytes + 1]);

        var error = await Assert.ThrowsAsync<MigrationAttemptStoreException>(() => store.ReadAsync().AsTask());

        Assert.Equal("migration-marker-oversize", error.ReasonCode);
    }

    [Fact]
    public async Task WriteAsync_DoesNotSerializeSecretsOrUnboundedDiagnosticValues()
    {
        using var profile = new TemporaryProfile();
        var store = new AtomicMigrationAttemptStore(profile.Path);
        await store.WriteAsync(Record(MigrationAttemptState.Failed));

        var json = await File.ReadAllTextAsync(store.MarkerPath);

        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_RejectsReparseRuntimeDirectory()
    {
        using var profile = new TemporaryProfile();
        using var target = new TemporaryProfile();
        var runtime = Path.Combine(profile.Path, "runtime");
        try
        {
            Directory.CreateSymbolicLink(runtime, target.Path);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }
        var store = new AtomicMigrationAttemptStore(profile.Path);

        await Assert.ThrowsAsync<ProfileOwnershipException>(() =>
            store.WriteAsync(Record(MigrationAttemptState.Running)).AsTask());
    }

    [Fact]
    public async Task WriteAsync_ReadOnlyRuntimeDirectoryFailsWithoutPublishingMarkerOrTemp()
    {
        using var profile = new TemporaryProfile();
        var store = new AtomicMigrationAttemptStore(profile.Path);
        var runtime = new DirectoryInfo(Path.GetDirectoryName(store.MarkerPath)!);
        runtime.Create();
        var original = runtime.GetAccessControl();
        using var current = WindowsIdentity.GetCurrent();
        var denied = runtime.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(
            current.User!,
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.CreateFiles |
            FileSystemRights.CreateDirectories |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny));
        runtime.SetAccessControl(denied);
        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                store.WriteAsync(Record(MigrationAttemptState.Running)).AsTask());
            var deniedByFileSystem = error is UnauthorizedAccessException ||
                error is System.ComponentModel.Win32Exception { NativeErrorCode: 5 };
            Assert.True(deniedByFileSystem, error.ToString());

            Assert.False(File.Exists(store.MarkerPath));
            Assert.False(File.Exists(store.TemporaryPath));
        }
        finally
        {
            runtime.SetAccessControl(original);
        }
    }

    private static MigrationAttemptRecord Record(MigrationAttemptState state)
    {
        var started = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        return new MigrationAttemptRecord(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            new Version(1, 2, 3),
            0,
            1,
            state,
            started,
            state == MigrationAttemptState.Running ? null : started.AddSeconds(1),
            state switch
            {
                MigrationAttemptState.Running => "migration-running",
                MigrationAttemptState.Succeeded => "migration-target-verified",
                _ => "migration-process-failed",
            });
    }

    private static string ValidJson() =>
        "{\"recordVersion\":1,\"operationId\":\"00000000-0000-0000-0000-000000000001\",\"appVersion\":\"1.2.3\",\"startingSchemaVersion\":0,\"targetSchemaVersion\":1,\"state\":\"Running\",\"startedAtUtc\":\"2026-07-14T12:00:00.0000000+00:00\",\"finishedAtUtc\":null,\"reasonCode\":\"migration-running\"}";

    private sealed class TemporaryProfile : IDisposable
    {
        internal TemporaryProfile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ebaycrm-task8-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
