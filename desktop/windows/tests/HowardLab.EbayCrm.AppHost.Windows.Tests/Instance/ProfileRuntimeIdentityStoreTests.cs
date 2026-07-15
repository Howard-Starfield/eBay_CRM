using System.Text;
using HowardLab.EbayCrm.AppHost.Windows.Instance;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Instance;

public sealed class ProfileRuntimeIdentityStoreTests
{
    [Fact]
    public async Task OpenOrCreate_PersistsStableClusterIdentityAndOnlyProtectedCredentialBytes()
    {
        using var profile = TestProfile.Create();
        var identity = DataProfileIdentity.Create(profile.Root);
        var store = new ProfileRuntimeIdentityStore();

        var first = await store.OpenOrCreateAsync(identity, existingCluster: false);
        var plaintext = first.Password.RevealForChildEnvironment();
        var reopened = await store.OpenOrCreateAsync(identity, existingCluster: true);

        Assert.NotEqual(Guid.Empty, first.ClusterId);
        Assert.Equal(first.ClusterId, reopened.ClusterId);
        Assert.Equal(plaintext, reopened.Password.RevealForChildEnvironment());
        foreach (var file in Directory.EnumerateFiles(profile.Root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(plaintext, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
        }
    }

    [Fact]
    public async Task OpenOrCreate_ExistingClusterWithoutIdentityFailsClosed()
    {
        using var profile = TestProfile.Create();
        var store = new ProfileRuntimeIdentityStore();

        var error = await Assert.ThrowsAsync<ProfileRuntimeIdentityException>(
            () => store.OpenOrCreateAsync(DataProfileIdentity.Create(profile.Root), existingCluster: true));

        Assert.Equal("profile-runtime-identity-missing", error.ReasonCode);
        Assert.Empty(Directory.EnumerateFiles(profile.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OpenOrCreate_MovedProtectedCredentialFailsClosed()
    {
        using var source = TestProfile.Create();
        using var destination = TestProfile.Create();
        var store = new ProfileRuntimeIdentityStore();
        _ = await store.OpenOrCreateAsync(DataProfileIdentity.Create(source.Root), existingCluster: false);
        Directory.CreateDirectory(Path.Combine(destination.Root, "runtime"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source.Root, "runtime")))
        {
            File.Copy(file, Path.Combine(destination.Root, "runtime", Path.GetFileName(file)));
        }

        var error = await Assert.ThrowsAsync<ProfileRuntimeIdentityException>(
            () => store.OpenOrCreateAsync(DataProfileIdentity.Create(destination.Root), existingCluster: true));

        Assert.Equal("profile-runtime-identity-mismatch", error.ReasonCode);
    }

    [Fact]
    public async Task OpenOrCreate_ReopensTheSameProfileThroughDifferentPathCasing()
    {
        using var profile = TestProfile.Create();
        var store = new ProfileRuntimeIdentityStore();
        var first = await store.OpenOrCreateAsync(
            DataProfileIdentity.Create(profile.Root),
            existingCluster: false);

        var reopened = await store.OpenOrCreateAsync(
            DataProfileIdentity.Create(profile.Root.ToUpperInvariant()),
            existingCluster: true);

        Assert.Equal(first.ClusterId, reopened.ClusterId);
        Assert.Equal(
            first.Password.RevealForChildEnvironment(),
            reopened.Password.RevealForChildEnvironment());
    }

    private sealed class TestProfile : IDisposable
    {
        private TestProfile(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
        }

        internal string Root { get; }

        internal static TestProfile Create() => new(Path.Combine(
            Path.GetTempPath(),
            $"ebaycrm-task9-identity-{Guid.NewGuid():N}"));

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
