using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HowardLab.EbayCrm.AppHost.Windows.Diagnostics;
using HowardLab.EbayCrm.AppHost.Windows.Instance;
using Microsoft.Win32.SafeHandles;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Diagnostics;

public sealed class WindowsDiagnosticSegmentFactoryTests
{
    [Fact]
    public async Task OpenAsyncCreatesOnlyFixedSlotNameAndTruncatesOnReuse()
    {
        using var profile = new TemporaryProfile();
        var factory = new WindowsDiagnosticSegmentFactory(DataProfileIdentity.Create(profile.Path));

        await using (var first = await factory.OpenAsync(0, CancellationToken.None))
        {
            await first.WriteAsync("stale"u8.ToArray());
        }

        await using (var reused = await factory.OpenAsync(0, CancellationToken.None))
        {
            Assert.Equal(0, reused.Length);
        }

        var logs = Path.Combine(profile.Path, "logs");
        Assert.Equal(["apphost-0.jsonl"], Directory.EnumerateFiles(logs).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public async Task OpenAsyncRejectsSlotsOutsideFixedFour(int slot)
    {
        using var profile = new TemporaryProfile();
        var factory = new WindowsDiagnosticSegmentFactory(DataProfileIdentity.Create(profile.Path));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            factory.OpenAsync(slot, CancellationToken.None).AsTask());

        Assert.False(Directory.Exists(Path.Combine(profile.Path, "logs")));
    }

    [Fact]
    public async Task CreatedDirectoryAndSegmentHaveExactProtectedCurrentUserOnlyDacl()
    {
        using var profile = new TemporaryProfile();
        var factory = new WindowsDiagnosticSegmentFactory(DataProfileIdentity.Create(profile.Path));

        await using var segment = await factory.OpenAsync(3, CancellationToken.None);

        AssertCurrentUserOnly(new DirectoryInfo(Path.Combine(profile.Path, "logs")).GetAccessControl());
        AssertCurrentUserOnly(new FileInfo(Path.Combine(profile.Path, "logs", "apphost-3.jsonl")).GetAccessControl());
    }

    [Fact]
    public async Task ProductionValidatorReadsSecurityDescriptorFromExactDirectoryAndSegmentHandles()
    {
        using var profile = new TemporaryProfile();
        var queriedPaths = new List<string>();
        var factory = new WindowsDiagnosticSegmentFactory(
            DataProfileIdentity.Create(profile.Path),
            beforeDirectoryHandleOpenForTests: null,
            readSecurityDescriptor: ReadDescriptor);

        await using var segment = Assert.IsType<FileStream>(
            await factory.OpenAsync(2, CancellationToken.None));

        Assert.Equal(2, queriedPaths.Count);
        Assert.True(StringComparer.OrdinalIgnoreCase.Equals(
            Path.Combine(profile.Path, "logs"),
            queriedPaths[0]));
        Assert.True(StringComparer.OrdinalIgnoreCase.Equals(
            Path.Combine(profile.Path, "logs", "apphost-2.jsonl"),
            queriedPaths[1]));

        RawSecurityDescriptor ReadDescriptor(SafeFileHandle handle)
        {
            queriedPaths.Add(GetFinalPath(handle));
            return CreateCurrentUserOnlyDescriptor(isCallback: false);
        }
    }

    [Fact]
    public async Task CallbackAllowAceFailsClosedBeforeCreatingSegment()
    {
        using var profile = new TemporaryProfile();
        var factory = new WindowsDiagnosticSegmentFactory(
            DataProfileIdentity.Create(profile.Path),
            beforeDirectoryHandleOpenForTests: null,
            readSecurityDescriptor: _ => CreateCurrentUserOnlyDescriptor(isCallback: true));

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            factory.OpenAsync(0, CancellationToken.None).AsTask());

        Assert.Equal("The diagnostic path DACL is not current-user-only.", error.Message);
        Assert.False(File.Exists(Path.Combine(profile.Path, "logs", "apphost-0.jsonl")));
    }

    [Fact]
    public async Task PreExistingBroadDirectoryFailsClosedWithoutCreatingSegment()
    {
        using var profile = new TemporaryProfile();
        var logs = Directory.CreateDirectory(Path.Combine(profile.Path, "logs"));
        var factory = new WindowsDiagnosticSegmentFactory(DataProfileIdentity.Create(profile.Path));

        await Assert.ThrowsAnyAsync<Exception>(() => factory.OpenAsync(0, CancellationToken.None).AsTask());

        Assert.Empty(Directory.EnumerateFiles(logs.FullName));
    }

    [Fact]
    public async Task PreExistingBroadSegmentFailsClosedRatherThanReusingIt()
    {
        using var profile = new TemporaryProfile();
        var factory = new WindowsDiagnosticSegmentFactory(DataProfileIdentity.Create(profile.Path));
        var path = Path.Combine(profile.Path, "logs", "apphost-1.jsonl");
        await using (var created = await factory.OpenAsync(1, CancellationToken.None))
        {
            await created.WriteAsync("preserve"u8.ToArray());
        }

        var security = new FileInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);

        await Assert.ThrowsAnyAsync<Exception>(() => factory.OpenAsync(1, CancellationToken.None).AsTask());

        Assert.Equal("preserve", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PreExistingDirectoryAtSlotFailsClosedWithoutRetryLoop()
    {
        using var profile = new TemporaryProfile();
        var factory = new WindowsDiagnosticSegmentFactory(DataProfileIdentity.Create(profile.Path));
        await using (var segment = await factory.OpenAsync(0, CancellationToken.None))
        {
        }

        var slot = Path.Combine(profile.Path, "logs", "apphost-0.jsonl");
        File.Delete(slot);
        Directory.CreateDirectory(slot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var open = Task.Run(async () => await factory.OpenAsync(0, deadline.Token));
        var completed = await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(open, completed);
        await Assert.ThrowsAnyAsync<Exception>(async () => await open);
    }

    [Fact]
    public async Task DanglingReparsePointAtSlotFailsClosedWithoutRetryLoop()
    {
        using var profile = new TemporaryProfile();
        var factory = new WindowsDiagnosticSegmentFactory(DataProfileIdentity.Create(profile.Path));
        await using (var segment = await factory.OpenAsync(0, CancellationToken.None))
        {
        }

        var slot = Path.Combine(profile.Path, "logs", "apphost-0.jsonl");
        File.Delete(slot);
        try
        {
            File.CreateSymbolicLink(slot, Path.Combine(profile.Path, "missing-target"));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var open = Task.Run(async () => await factory.OpenAsync(0, deadline.Token));
        var completed = await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(open, completed);
        await Assert.ThrowsAnyAsync<Exception>(async () => await open);
    }

    [Fact]
    public async Task ReparsePointLogsDirectoryIsRejectedWithoutTouchingTarget()
    {
        using var profile = new TemporaryProfile();
        using var target = new TemporaryProfile();
        var logs = Path.Combine(profile.Path, "logs");
        try
        {
            Directory.CreateSymbolicLink(logs, target.Path);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var factory = new WindowsDiagnosticSegmentFactory(DataProfileIdentity.Create(profile.Path));
        await Assert.ThrowsAnyAsync<Exception>(() => factory.OpenAsync(0, CancellationToken.None).AsTask());

        Assert.Empty(Directory.EnumerateFileSystemEntries(target.Path));
    }

    [Fact]
    public async Task DirectoryReplacementRaceIsRejectedBeforeTargetIsTouched()
    {
        using var profile = new TemporaryProfile();
        using var target = new TemporaryProfile();
        var logs = Path.Combine(profile.Path, "logs");
        var replaced = false;
        var factory = new WindowsDiagnosticSegmentFactory(
            DataProfileIdentity.Create(profile.Path),
            beforeDirectoryHandleOpenForTests: () =>
            {
                if (replaced)
                {
                    return;
                }

                replaced = true;
                Directory.Delete(logs);
                Directory.CreateSymbolicLink(logs, target.Path);
            });

        await Assert.ThrowsAnyAsync<Exception>(() => factory.OpenAsync(2, CancellationToken.None).AsTask());

        Assert.Empty(Directory.EnumerateFileSystemEntries(target.Path));
    }

    private static void AssertCurrentUserOnly(FileSystemSecurity security)
    {
        Assert.True(security.AreAccessRulesProtected);
        var currentSid = WindowsIdentity.GetCurrent().User!;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var rule = Assert.Single(rules);
        Assert.False(rule.IsInherited);
        Assert.Equal(currentSid, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }

    private static RawSecurityDescriptor CreateCurrentUserOnlyDescriptor(bool isCallback)
    {
        var currentSid = WindowsIdentity.GetCurrent().User!;
        var dacl = new RawAcl(GenericAcl.AclRevision, capacity: 1);
        dacl.InsertAce(
            0,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                (int)FileSystemRights.FullControl,
                currentSid,
                isCallback,
                opaque: null));
        return new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            currentSid,
            currentSid,
            systemAcl: null,
            dacl);
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(capacity: 512);
        var length = GetFinalPathNameByHandle(handle, buffer, checked((uint)buffer.Capacity), flags: 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new IOException("Could not resolve the test file handle.");
        }

        const string devicePrefix = @"\\?\";
        var path = buffer.ToString();
        return Path.GetFullPath(path.StartsWith(devicePrefix, StringComparison.Ordinal)
            ? path[devicePrefix.Length..]
            : path);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    private sealed class TemporaryProfile : IDisposable
    {
        internal TemporaryProfile()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ebaycrm-diagnostic-segments-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
