using HowardLab.EbayCrm.AppHost.Windows.Postgres;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HowardLab.EbayCrm.AppHost.Windows.Tests.Postgres;

public sealed class PostmasterPidFileTests
{
    private static readonly string DataDirectory = Path.GetFullPath(@"C:\runtime\postgres-data");

    [Fact]
    public void Parse_RequiresDocumentedIdentityFields()
    {
        var parsed = PostmasterPidFile.Parse(
            $"4321\n{DataDirectory}\n1712345678\n55432\n\n127.0.0.1\n123 456\nready\n",
            DataDirectory);

        Assert.Equal(4321, parsed.ProcessId);
        Assert.Equal(DataDirectory, parsed.CanonicalDataDirectory, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1712345678), parsed.StartTimeUtc);
        Assert.Equal(55432, parsed.Port);
        Assert.Equal("ready", parsed.Status);
    }

    [Theory]
    [MemberData(nameof(InvalidContents))]
    public void Parse_RejectsMalformedOrForeignIdentity(string contents)
    {
        Assert.Throws<PostmasterPidFileException>(() =>
            PostmasterPidFile.Parse(contents, DataDirectory));
    }

    [Fact]
    public void BinaryLayout_RequiresEveryPostgresToolInOneCanonicalDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "initdb.exe", "pg_ctl.exe", "postgres.exe", "psql.exe", "pg_isready.exe" })
            {
                File.WriteAllBytes(Path.Combine(root, name), [0]);
            }

            var layout = PostgresBinaryLayout.Validate(root);

            Assert.Equal(Path.GetFullPath(root), layout.CanonicalBinDirectory, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "postgres.exe"), layout.PostgresExe, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BinaryLayout_RejectsRelativeOrIncompleteDirectories()
    {
        Assert.Throws<ArgumentException>(() => PostgresBinaryLayout.Validate("relative"));

        var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<PostgresBinaryLayoutException>(() => PostgresBinaryLayout.Validate(root));
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public void BinaryLayout_RejectsRequiredBinaryWhoseFinalPathEscapesBinDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-layout-{Guid.NewGuid():N}");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        try
        {
            foreach (var name in new[] { "initdb.exe", "pg_ctl.exe", "psql.exe", "pg_isready.exe" })
                File.WriteAllBytes(Path.Combine(bin, name), [0]);
            var outside = Path.Combine(root, "outside-postgres.exe");
            File.WriteAllBytes(outside, [0]);
            try
            {
                File.CreateSymbolicLink(Path.Combine(bin, "postgres.exe"), outside);
            }
            catch (UnauthorizedAccessException)
            {
                return; // Windows developer-mode/symlink privilege is unavailable on this host.
            }

            Assert.Throws<PostgresBinaryLayoutException>(() => PostgresBinaryLayout.Validate(bin));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BootstrapPasswordFile_IsCurrentUserOnlyBeforeWrite_ThenZeroedAndDeleted()
    {
        const string canary = "bootstrap-password-canary-Aa1!";
        var root = Path.Combine(Path.GetTempPath(), $"ebaycrm-pw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "bootstrap.pw");
        try
        {
            PostgresBootstrapPasswordFile.Write(path, new SecretValue(canary));

            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var expectedSid = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User;
            Assert.Equal(expectedSid, security.GetOwner(typeof(SecurityIdentifier)));
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            var rule = Assert.Single(rules.Cast<FileSystemAccessRule>());
            Assert.Equal(expectedSid, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(canary, File.ReadAllText(path));

            byte[]? beforeDelete = null;
            PostgresBootstrapPasswordFile.ZeroAndDelete(path, candidate => beforeDelete = File.ReadAllBytes(candidate));
            Assert.NotNull(beforeDelete);
            Assert.All(beforeDelete, value => Assert.Equal(0, value));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Read_RejectsOversizedPidFileBeforeParsing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"postmaster-{Guid.NewGuid():N}.pid");
        try
        {
            File.WriteAllText(path, new string('1', (16 * 1024) + 1));
            var error = Assert.Throws<PostmasterPidFileException>(() =>
                PostmasterPidFile.Read(path, DataDirectory));
            Assert.Equal("postmaster-pid-too-large", error.ReasonCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_NormalizesMalformedDataDirectoryPath()
    {
        var contents = "4321\nC:\\invalid\0path\n1712345678\n55432\n\n127.0.0.1\n123 456\nready\n";

        var error = Assert.Throws<PostmasterPidFileException>(() =>
            PostmasterPidFile.Parse(contents, DataDirectory));

        Assert.Equal("postmaster-pid-malformed", error.ReasonCode);
    }

    public static TheoryData<string> InvalidContents() => new()
    {
        "",
        "1234\n",
        $"abc\n{DataDirectory}\n1712345678\n55432\n",
        $"0\n{DataDirectory}\n1712345678\n55432\n",
        $"2147483648\n{DataDirectory}\n1712345678\n55432\n",
        $"1234\n{DataDirectory}\nabc\n55432\n",
        $"1234\n{DataDirectory}\n0\n55432\n",
        $"1234\n{DataDirectory}\n1712345678\nabc\n",
        $"1234\n{DataDirectory}\n1712345678\n0\n",
        $"1234\n{DataDirectory}\n1712345678\n65536\n",
        $"1234\nC:\\other-data\n1712345678\n55432\n",
        $"1234\n{DataDirectory}\n1712345678\n55432\n",
    };
}
