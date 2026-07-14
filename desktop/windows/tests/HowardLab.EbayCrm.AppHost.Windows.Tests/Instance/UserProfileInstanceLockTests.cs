using System.Security.Cryptography;
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
        using var keeper = new Mutex(initiallyOwned: false, mutexName);
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
}
