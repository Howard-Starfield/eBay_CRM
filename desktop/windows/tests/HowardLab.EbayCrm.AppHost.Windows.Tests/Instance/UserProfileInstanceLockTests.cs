using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HowardLab.EbayCrm.AppHost.Windows.Instance;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Instance;

public sealed class UserProfileInstanceLockTests
{
    [Fact]
    public void Identity_CannotBypassCanonicalFactoryWithPublicConstructor()
    {
        Assert.Empty(typeof(DataProfileIdentity).GetConstructors());
    }

    [Fact]
    public void Identity_CanonicalizesAndHashesEquivalentPaths()
    {
        using var directory = new TemporaryDirectory();

        var first = DataProfileIdentity.Create(directory.Path);
        var second = DataProfileIdentity.Create(directory.Path + Path.DirectorySeparatorChar);

        Assert.Equal(first.CanonicalPath, second.CanonicalPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(first.ProfileHash, second.ProfileHash, StringComparer.Ordinal);
        Assert.Equal(64, first.ProfileHash.Length);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                first.CanonicalPath.ToUpperInvariant()))),
            first.ProfileHash);
    }

    [Theory]
    [InlineData(@"\\server\share\profile")]
    [InlineData(@"\\?\C:\profile")]
    [InlineData(@"C:relative-profile")]
    [InlineData(@"A:\profile")]
    public void Identity_RejectsProfilesThatAreNotLocalFixedStorage(string path)
    {
        var error = Assert.Throws<ProfileOwnershipException>(() => DataProfileIdentity.Create(path));

        Assert.Equal(ProfileOwnershipErrorCode.ProfileMustBeLocalFixedStorage, error.Code);
        Assert.Equal("Data profile ownership error: ProfileMustBeLocalFixedStorage.", error.Message);
        Assert.DoesNotContain(path, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Identity_RejectsReparsePointInExistingAncestorUsingDeterministicInspector()
    {
        var inspector = new ReparsePointPathInspector(@"C:\profiles\junction");

        var error = Assert.Throws<ProfileOwnershipException>(() =>
            DataProfileIdentity.Create(@"C:\profiles\junction\child", inspector));

        Assert.Equal(ProfileOwnershipErrorCode.ProfileMustBeLocalFixedStorage, error.Code);
    }

    [Fact]
    public void Identity_RejectsRealDirectorySymbolicLinkWhenCreationIsSupported()
    {
        using var parent = new TemporaryDirectory();
        using var target = new TemporaryDirectory();
        var link = Path.Combine(parent.Path, "profile-link");
        try
        {
            Directory.CreateSymbolicLink(link, target.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var error = Assert.Throws<ProfileOwnershipException>(() =>
            DataProfileIdentity.Create(Path.Combine(link, "child")));

        Assert.Equal(ProfileOwnershipErrorCode.ProfileMustBeLocalFixedStorage, error.Code);
    }

    [Fact]
    public async Task TryAcquireAsync_AllowsExactlyOneOfTwentySameProfileContenders()
    {
        using var directory = new TemporaryDirectory();
        var identity = DataProfileIdentity.Create(directory.Path);

        var attempts = Enumerable.Range(0, 20)
            .Select(_ => UserProfileInstanceLock.TryAcquireAsync(identity, CancellationToken.None).AsTask())
            .ToArray();
        var locks = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            Assert.Single(locks, instanceLock => instanceLock is not null);
        }
        finally
        {
            foreach (var instanceLock in locks)
            {
                if (instanceLock is not null)
                {
                    await instanceLock.DisposeAsync();
                }
            }
        }
    }

    [Fact]
    public async Task TryAcquireAsync_AllowsDifferentProfilesIndependently()
    {
        using var firstDirectory = new TemporaryDirectory();
        using var secondDirectory = new TemporaryDirectory();

        await using var first = await UserProfileInstanceLock.TryAcquireAsync(
            DataProfileIdentity.Create(firstDirectory.Path),
            CancellationToken.None);
        await using var second = await UserProfileInstanceLock.TryAcquireAsync(
            DataProfileIdentity.Create(secondDirectory.Path),
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task TryAcquireAsync_IgnoresStaleDiagnosticContentsAndReleasesForNextOwner()
    {
        using var directory = new TemporaryDirectory();
        var runtimeDirectory = Path.Combine(directory.Path, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        var lockPath = Path.Combine(runtimeDirectory, "profile.lock");
        await File.WriteAllTextAsync(lockPath, "stale and untrusted diagnostic text");
        var identity = DataProfileIdentity.Create(directory.Path);

        var first = await UserProfileInstanceLock.TryAcquireAsync(identity, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Throws<IOException>(() => new FileStream(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite));
        Assert.Null(await UserProfileInstanceLock.TryAcquireAsync(identity, CancellationToken.None));
        await first.DisposeAsync();
        Assert.Contains(identity.ProfileHash, await File.ReadAllTextAsync(lockPath), StringComparison.Ordinal);

        await using var second = await UserProfileInstanceLock.TryAcquireAsync(identity, CancellationToken.None);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task TryAcquireAsync_TreatsAbandonedGlobalMutexAsAcquiredAndStillTakesFileLock()
    {
        using var directory = new TemporaryDirectory();
        var identity = DataProfileIdentity.Create(directory.Path);
        var mutexName = UserProfileInstanceLock.BuildMutexName(identity);
        var security = new MutexSecurity();
        using var current = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new MutexAccessRule(
            current.User!,
            MutexRights.FullControl,
            AccessControlType.Allow));
        using var keeper = MutexAcl.Create(
            initiallyOwned: false,
            mutexName,
            out var createdNew,
            security);
        Assert.True(createdNew);
        using var acquired = new ManualResetEventSlim(initialState: false);
        var abandoningThread = new Thread(() =>
        {
            using var owner = Mutex.OpenExisting(mutexName);
            owner.WaitOne();
            acquired.Set();
        });
        abandoningThread.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(abandoningThread.Join(TimeSpan.FromSeconds(5)));

        await using var instanceLock = await UserProfileInstanceLock.TryAcquireAsync(
            identity,
            CancellationToken.None);

        Assert.NotNull(instanceLock);
        Assert.Throws<IOException>(() => new FileStream(
            Path.Combine(identity.CanonicalPath, "runtime", "profile.lock"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite));
    }

    [Fact]
    public async Task TryAcquireAsync_RejectsPreExistingMutexWithBroaderDacl()
    {
        using var directory = new TemporaryDirectory();
        var identity = DataProfileIdentity.Create(directory.Path);
        var security = new MutexSecurity();
        using var current = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new MutexAccessRule(
            current.User!,
            MutexRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        using var broad = MutexAcl.Create(
            initiallyOwned: false,
            UserProfileInstanceLock.BuildMutexName(identity),
            out var createdNew,
            security);
        Assert.True(createdNew);

        var error = await Assert.ThrowsAsync<ProfileOwnershipException>(() =>
            UserProfileInstanceLock.TryAcquireAsync(identity, CancellationToken.None).AsTask());

        Assert.Equal(ProfileOwnershipErrorCode.ProfileMutexSecurityMismatch, error.Code);
        Assert.Equal("Data profile ownership error: ProfileMutexSecurityMismatch.", error.Message);
    }

    [Fact]
    public async Task TryAcquireAsync_RejectsPreExistingMutexWhoseDaclCannotBeVerified()
    {
        using var directory = new TemporaryDirectory();
        var identity = DataProfileIdentity.Create(directory.Path);
        var security = new MutexSecurity();
        using var current = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new MutexAccessRule(
            current.User!,
            MutexRights.Modify | MutexRights.Synchronize,
            AccessControlType.Allow));
        using var unverifiable = MutexAcl.Create(
            initiallyOwned: false,
            UserProfileInstanceLock.BuildMutexName(identity),
            out var createdNew,
            security);
        Assert.True(createdNew);

        var error = await Assert.ThrowsAsync<ProfileOwnershipException>(() =>
            UserProfileInstanceLock.TryAcquireAsync(identity, CancellationToken.None).AsTask());

        Assert.Equal(ProfileOwnershipErrorCode.ProfileMutexSecurityMismatch, error.Code);
        Assert.Equal("Data profile ownership error: ProfileMutexSecurityMismatch.", error.Message);
    }

    [Fact]
    public async Task TryAcquireAsync_ProductionMutexDaclValidatesAndSecondContenderIsRejectedNormally()
    {
        using var directory = new TemporaryDirectory();
        var identity = DataProfileIdentity.Create(directory.Path);
        await using var first = await UserProfileInstanceLock.TryAcquireAsync(identity, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(await UserProfileInstanceLock.TryAcquireAsync(identity, CancellationToken.None));
    }

    [Fact]
    public async Task TryAcquireAsync_CancellationAfterMutexOwnershipReleasesBeforeCallerCompletes()
    {
        using var directory = new TemporaryDirectory();
        var identity = DataProfileIdentity.Create(directory.Path);
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new GatedProfileLockFileSystem();
        var acquisition = UserProfileInstanceLock.TryAcquireAsync(
            identity,
            cancellation.Token,
            fileSystem).AsTask();
        Assert.True(fileSystem.Entered.Wait(TimeSpan.FromSeconds(5)));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            acquisition.WaitAsync(TimeSpan.FromSeconds(5)));
        fileSystem.Release.Set();

        await using var next = await UserProfileInstanceLock.TryAcquireAsync(
            identity,
            CancellationToken.None);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task TryAcquireAsync_ReparseDetectedAfterDirectoryCreationReleasesOwnership()
    {
        using var directory = new TemporaryDirectory();
        var identity = DataProfileIdentity.Create(directory.Path);
        var fileSystem = new ReparseAfterCreateProfileLockFileSystem();

        var error = await Assert.ThrowsAsync<ProfileOwnershipException>(() =>
            UserProfileInstanceLock.TryAcquireAsync(
                identity,
                CancellationToken.None,
                fileSystem).AsTask());
        await using var next = await UserProfileInstanceLock.TryAcquireAsync(identity, CancellationToken.None);

        Assert.Equal(ProfileOwnershipErrorCode.ProfileMustBeLocalFixedStorage, error.Code);
        Assert.NotNull(next);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ebaycrm-task6-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class GatedProfileLockFileSystem : IProfileLockFileSystem
    {
        internal ManualResetEventSlim Entered { get; } = new(initialState: false);
        internal ManualResetEventSlim Release { get; } = new(initialState: false);

        public void CreateDirectory(string path, CancellationToken cancellationToken)
        {
            Entered.Set();
            Release.Wait(cancellationToken);
            WindowsProfileLockFileSystem.Instance.CreateDirectory(path, cancellationToken);
        }

        public FileStream OpenLockFile(string path, CancellationToken cancellationToken) =>
            WindowsProfileLockFileSystem.Instance.OpenLockFile(path, cancellationToken);

        public void ValidateProfilePath(string path, CancellationToken cancellationToken) =>
            WindowsProfileLockFileSystem.Instance.ValidateProfilePath(path, cancellationToken);

        public void WriteDiagnostic(
            FileStream lockFile,
            string profileHash,
            CancellationToken cancellationToken) =>
            WindowsProfileLockFileSystem.Instance.WriteDiagnostic(
                lockFile,
                profileHash,
                cancellationToken);
    }

    private sealed class ReparseAfterCreateProfileLockFileSystem : IProfileLockFileSystem
    {
        public void CreateDirectory(string path, CancellationToken cancellationToken) =>
            WindowsProfileLockFileSystem.Instance.CreateDirectory(path, cancellationToken);

        public FileStream OpenLockFile(string path, CancellationToken cancellationToken) =>
            WindowsProfileLockFileSystem.Instance.OpenLockFile(path, cancellationToken);

        public void ValidateProfilePath(string path, CancellationToken cancellationToken) =>
            throw new ProfileOwnershipException(ProfileOwnershipErrorCode.ProfileMustBeLocalFixedStorage);

        public void WriteDiagnostic(
            FileStream lockFile,
            string profileHash,
            CancellationToken cancellationToken) =>
            WindowsProfileLockFileSystem.Instance.WriteDiagnostic(
                lockFile,
                profileHash,
                cancellationToken);
    }

    private sealed class ReparsePointPathInspector(string reparsePath) : IProfilePathInspector
    {
        public DriveType GetDriveType(string root) => DriveType.Fixed;

        public FileAttributes? TryGetAttributes(string path) =>
            string.Equals(path, reparsePath, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory;
    }
}
