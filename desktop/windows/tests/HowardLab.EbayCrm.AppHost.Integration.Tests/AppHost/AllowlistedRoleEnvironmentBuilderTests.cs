using HowardLab.EbayCrm.AppHost.Composition;
using HowardLab.EbayCrm.AppHost.Core.Diagnostics;
using HowardLab.EbayCrm.AppHost.Protocol.Control;

namespace HowardLab.EbayCrm.AppHost.Integration.Tests.AppHost;

public sealed class AllowlistedRoleEnvironmentBuilderTests : IDisposable
{
    private const string ParentCanaryKey = "EBAYCRM_PARENT_ENVIRONMENT_CANARY";
    private const string ParentCanaryValue = "must-not-be-inherited";
    private readonly string? _originalCanary = Environment.GetEnvironmentVariable(ParentCanaryKey);
    private readonly string _profileRoot = Path.Combine(
        Path.GetTempPath(),
        $"ebaycrm-role-environment-{Guid.NewGuid():N}");

    [Fact]
    public void Build_ServerPostgres_ReturnsOwnedExactCaseInsensitiveMaps()
    {
        var ordinary = ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        var secrets = BaseSecrets();
        var expectedDatabaseSecret = secrets["PG_DATABASE_URL"];

        var result = Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            secrets);
        ordinary["NODE_ENV"] = "mutated";
        secrets["PG_DATABASE_URL"] = new SecretValue("mutated-secret");

        Assert.Equal(8, result.Environment.Count);
        Assert.Equal("production", result.Environment["node_env"]);
        Assert.Equal("postgres-desktop", result.Environment["runtime_backend"]);
        Assert.Equal("3000", result.Environment["node_port"]);
        Assert.DoesNotContain("DISABLE_DB_MIGRATIONS", result.Environment.Keys);
        Assert.Equal(2, result.SecretEnvironment.Count);
        Assert.Same(expectedDatabaseSecret, result.SecretEnvironment["pg_database_url"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, string>)result.Environment).Add("EXTRA", "value"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, SecretValue>)result.SecretEnvironment).Add(
                "EXTRA_SECRET",
                new SecretValue("value")));
    }

    [Fact]
    public void Build_WorkerPostgres_UsesWorkerOnlyMigrationBoundary()
    {
        var result = Build(
            RuntimeRole.Worker,
            AppHostRuntimeBackend.PostgresDesktop,
            WorkerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
            BaseSecrets());

        Assert.Equal("true", result.Environment["disable_db_migrations"]);
        Assert.DoesNotContain("NODE_PORT", result.Environment.Keys);
    }

    [Fact]
    public void Build_NeverCopiesParentEnvironment()
    {
        Environment.SetEnvironmentVariable(ParentCanaryKey, ParentCanaryValue);

        var result = Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
            BaseSecrets());

        Assert.DoesNotContain(ParentCanaryKey, result.Environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(ParentCanaryKey, result.SecretEnvironment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(ParentCanaryValue, result.Environment.Values);
    }

    [Fact]
    public void Build_PreservesSecretClassificationAndExcludesExecutorOwnedControlKeys()
    {
        var secrets = BaseSecrets();
        secrets["ENCRYPTION_KEY"] = new SecretValue("encryption-secret");
        secrets["FALLBACK_ENCRYPTION_KEY"] = new SecretValue("fallback-secret");

        var result = Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
            secrets);

        Assert.Equal(4, result.SecretEnvironment.Count);
        Assert.All(secrets, pair => Assert.Same(pair.Value, result.SecretEnvironment[pair.Key]));
        Assert.All(secrets.Keys, key => Assert.DoesNotContain(key, result.Environment.Keys));
        Assert.DoesNotContain(result.Environment.Keys, RoleLaunchPlan.ReservedEnvironmentKeys.Contains);
        Assert.DoesNotContain(result.SecretEnvironment.Keys, RoleLaunchPlan.ReservedEnvironmentKeys.Contains);
    }

    [Theory]
    [InlineData("NODE_ENV")]
    [InlineData("SERVER_URL")]
    [InlineData("STORAGE_LOCAL_PATH")]
    [InlineData("NODE_PORT")]
    public void Build_MissingRequiredOrdinaryKey_FailsClosed(string key)
    {
        var ordinary = ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        ordinary.Remove(key);

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            BaseSecrets()));
    }

    [Theory]
    [InlineData("UNEXPECTED")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("DOTENV_CONFIG_PATH")]
    [InlineData("DOTENV_CONFIG_DEBUG")]
    [InlineData(".env")]
    [InlineData("NODE_PRELOAD")]
    public void Build_UnknownPreloadOrDotenvOrdinaryKey_FailsClosed(string key)
    {
        var ordinary = ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        ordinary[key] = "value";

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            BaseSecrets()));
    }

    [Fact]
    public void Build_CaseInsensitiveOrdinaryDuplicate_FailsClosed()
    {
        var ordinary = ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        ordinary.Add("node_env", "production");

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            BaseSecrets()));
    }

    [Fact]
    public void Build_CaseInsensitiveSecretDuplicate_FailsClosed()
    {
        var secrets = BaseSecrets();
        secrets.Add("pg_database_url", new SecretValue("postgres://duplicate"));

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
            secrets));
    }

    [Fact]
    public void Build_CrossMapCollision_FailsClosed()
    {
        var ordinary = ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        ordinary["PG_DATABASE_URL"] = "misclassified-secret";

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            BaseSecrets()));
    }

    [Theory]
    [InlineData("NODE_ENV", "development")]
    [InlineData("RUNTIME_BACKEND", "POSTGRES-DESKTOP")]
    [InlineData("SERVER_URL", "https://127.0.0.1:3000")]
    [InlineData("SERVER_URL", "http://example.com:3000")]
    [InlineData("SERVER_URL", "http://127.0.0.1:3000/path")]
    [InlineData("STORAGE_TYPE", "s3")]
    [InlineData("IS_CONFIG_VARIABLES_IN_DB_ENABLED", "true")]
    [InlineData("NODE_PORT", "0")]
    [InlineData("NODE_PORT", "03000")]
    [InlineData("NODE_PORT", "65536")]
    public void Build_InvalidExactOrdinaryValue_FailsClosed(string key, string value)
    {
        var ordinary = ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        ordinary[key] = value;

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            BaseSecrets()));
    }

    [Fact]
    public void Build_StoragePathOutsideProfile_FailsClosed()
    {
        var ordinary = ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        ordinary["STORAGE_LOCAL_PATH"] = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            BaseSecrets()));
    }

    [Fact]
    public void Build_SystemRootMustBeAnExistingLocalDirectory()
    {
        var ordinary = ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        ordinary["SystemRoot"] = Path.Combine(
            Path.GetTempPath(),
            $"missing-system-root-{Guid.NewGuid():N}");

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            BaseSecrets()));
    }

    [Fact]
    public void Build_ExistingDirectoryOtherThanCanonicalWindowsRootFailsClosed()
    {
        var ordinary = ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        ordinary["SystemRoot"] = Environment.SystemDirectory;

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            BaseSecrets()));
    }

    [Theory]
    [InlineData("SystemRoot", "ads")]
    [InlineData("STORAGE_LOCAL_PATH", "ads")]
    [InlineData("SystemRoot", "trailing-dot")]
    [InlineData("SystemRoot", "trailing-space")]
    [InlineData("STORAGE_LOCAL_PATH", "trailing-dot")]
    [InlineData("STORAGE_LOCAL_PATH", "trailing-space")]
    public void Build_NonCanonicalLocalPathAlias_FailsClosed(string key, string kind)
    {
        var ordinary = ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        var basePath = key == "SystemRoot"
            ? Path.Combine(Path.GetPathRoot(_profileRoot)!, "Windows")
            : Path.Combine(_profileRoot, "storage");
        ordinary[key] = kind switch
        {
            "ads" => basePath + ":stream",
            "trailing-dot" => basePath + ".",
            "trailing-space" => basePath + " ",
            _ => throw new InvalidOperationException(),
        };

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            BaseSecrets()));
    }

    [Theory]
    [InlineData("server-has-worker-key")]
    [InlineData("worker-has-server-key")]
    [InlineData("worker-missing-worker-key")]
    [InlineData("worker-invalid-worker-value")]
    public void Build_InvalidRoleSpecificOrdinaryShape_FailsClosed(string kind)
    {
        var role = kind == "server-has-worker-key" ? RuntimeRole.Server : RuntimeRole.Worker;
        var ordinary = role == RuntimeRole.Server
            ? ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop)
            : WorkerOrdinary(AppHostRuntimeBackend.PostgresDesktop);
        switch (kind)
        {
            case "server-has-worker-key":
                ordinary["DISABLE_DB_MIGRATIONS"] = "true";
                break;
            case "worker-has-server-key":
                ordinary["NODE_PORT"] = "3000";
                break;
            case "worker-missing-worker-key":
                ordinary.Remove("DISABLE_DB_MIGRATIONS");
                break;
            case "worker-invalid-worker-value":
                ordinary["DISABLE_DB_MIGRATIONS"] = "false";
                break;
        }

        AssertInvalid(() => Build(
            role,
            AppHostRuntimeBackend.PostgresDesktop,
            ordinary,
            BaseSecrets()));
    }

    [Theory]
    [InlineData("PG_DATABASE_URL")]
    [InlineData("APP_SECRET")]
    public void Build_MissingRequiredSecret_FailsClosed(string key)
    {
        var secrets = BaseSecrets();
        secrets.Remove(key);

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
            secrets));
    }

    [Theory]
    [InlineData("PG_DATABASE_URL")]
    [InlineData("APP_SECRET")]
    [InlineData("ENCRYPTION_KEY")]
    [InlineData("FALLBACK_ENCRYPTION_KEY")]
    public void Build_WhitespaceOrNulSecret_FailsClosed(string key)
    {
        foreach (var value in new[] { "   ", "bad\0secret" })
        {
            var secrets = BaseSecrets();
            secrets[key] = new SecretValue(value);

            AssertInvalid(() => Build(
                RuntimeRole.Server,
                AppHostRuntimeBackend.PostgresDesktop,
                ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
                secrets));
        }
    }

    [Fact]
    public void Build_NullSecretValue_FailsClosed()
    {
        var secrets = BaseSecrets();
        secrets["APP_SECRET"] = null!;

        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
            secrets));
    }

    [Fact]
    public void Build_PostgresDesktopRejectsEveryRedisSecret()
    {
        foreach (var key in new[] { "REDIS_URL", "REDIS_QUEUE_URL" })
        {
            var secrets = BaseSecrets();
            secrets[key] = new SecretValue("redis://127.0.0.1:6379");

            AssertInvalid(() => Build(
                RuntimeRole.Server,
                AppHostRuntimeBackend.PostgresDesktop,
                ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
                secrets));
        }
    }

    [Fact]
    public void Build_RedisCompatibilityRequiresRedisUrlAndAcceptsOptionalQueueUrl()
    {
        var missing = BaseSecrets();
        AssertInvalid(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.RedisCompatibility,
            ServerOrdinary(AppHostRuntimeBackend.RedisCompatibility),
            missing));

        var secrets = BaseSecrets();
        secrets["REDIS_URL"] = new SecretValue("redis://127.0.0.1:6379");
        secrets["REDIS_QUEUE_URL"] = new SecretValue("rediss://:password@127.0.0.1:6380/1");
        var result = Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.RedisCompatibility,
            ServerOrdinary(AppHostRuntimeBackend.RedisCompatibility),
            secrets);

        Assert.Equal("redis", result.Environment["RUNTIME_BACKEND"]);
        Assert.Same(secrets["REDIS_URL"], result.SecretEnvironment["redis_url"]);
        Assert.Same(secrets["REDIS_QUEUE_URL"], result.SecretEnvironment["redis_queue_url"]);
    }

    [Theory]
    [InlineData("mysql://user:password@127.0.0.1:5432/ebaycrm")]
    [InlineData("postgres://user:password@192.168.1.10:5432/ebaycrm")]
    [InlineData("postgres://user:password@localhost:5432/ebaycrm")]
    [InlineData("postgres://user:password@127.0.0.1/ebaycrm")]
    [InlineData("postgres://user:password@127.0.0.1:80/ebaycrm")]
    [InlineData("postgres://127.0.0.1:5432/ebaycrm")]
    [InlineData("postgres://user@127.0.0.1:5432/ebaycrm")]
    [InlineData("postgres://:password@127.0.0.1:5432/ebaycrm")]
    [InlineData("postgres://user:%00@127.0.0.1:5432/ebaycrm")]
    [InlineData("postgres://%00:password@127.0.0.1:5432/ebaycrm")]
    [InlineData("postgres://user:password@127.0.0.1:5432")]
    [InlineData("postgres://user:password@127.0.0.1:5432/")]
    [InlineData("postgres://user:password@127.0.0.1:5432/database/extra")]
    [InlineData("postgres://user:password@127.0.0.1:5432/database?ssl=true")]
    [InlineData("postgres://user:password@127.0.0.1:5432/database#fragment")]
    [InlineData("postgres://user:password@127.0.0.1:5432/database%00name")]
    [InlineData("postgres://user:password@127.0.0.1:5432/database%0Aname")]
    [InlineData("postgres://user:password@127.0.0.1:5432/database%20name")]
    [InlineData("postgres://user:password@127.0.0.1:5432/database%3Fname")]
    [InlineData("postgres://user:password@127.0.0.1:5432/database%ZZname")]
    public void Build_InvalidPostgresDatabaseUrlFailsWithoutValueLeak(string url)
    {
        var secrets = BaseSecrets();
        secrets["PG_DATABASE_URL"] = new SecretValue(url);

        AssertInvalidWithoutValueLeak(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
            secrets), url);
    }

    [Fact]
    public void Build_PostgresqlSchemeWithLocalCredentialsAndOneDatabaseIsValid()
    {
        var secrets = BaseSecrets();
        secrets["PG_DATABASE_URL"] =
            new SecretValue("postgresql://user:password@127.0.0.1:5432/ebaycrm");

        var result = Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.PostgresDesktop,
            ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
            secrets);

        Assert.Same(secrets["PG_DATABASE_URL"], result.SecretEnvironment["PG_DATABASE_URL"]);
    }

    [Theory]
    [InlineData("REDIS_URL", "http://127.0.0.1:6379")]
    [InlineData("REDIS_URL", "redis://192.168.1.10:6379")]
    [InlineData("REDIS_URL", "redis://localhost:6379")]
    [InlineData("REDIS_URL", "redis://127.0.0.1")]
    [InlineData("REDIS_URL", "redis://127.0.0.1:80")]
    [InlineData("REDIS_URL", "redis://user@127.0.0.1:6379")]
    [InlineData("REDIS_URL", "redis://:%00@127.0.0.1:6379")]
    [InlineData("REDIS_URL", "redis://user:%0A@127.0.0.1:6379")]
    [InlineData("REDIS_URL", "redis://127.0.0.1:6379/database")]
    [InlineData("REDIS_URL", "redis://127.0.0.1:6379/1/2")]
    [InlineData("REDIS_URL", "redis://127.0.0.1:6379/1?option=true")]
    [InlineData("REDIS_URL", "redis://127.0.0.1:6379/1#fragment")]
    [InlineData("REDIS_QUEUE_URL", "http://127.0.0.1:6380")]
    [InlineData("REDIS_QUEUE_URL", "redis://10.0.0.1:6380")]
    [InlineData("REDIS_QUEUE_URL", "redis://localhost:6380")]
    [InlineData("REDIS_QUEUE_URL", "redis://127.0.0.1")]
    [InlineData("REDIS_QUEUE_URL", "redis://127.0.0.1:6380/not-a-number")]
    public void Build_InvalidRequiredOrOptionalRedisUrlFailsWithoutValueLeak(string key, string url)
    {
        var secrets = BaseSecrets();
        secrets["REDIS_URL"] = new SecretValue("redis://127.0.0.1:6379");
        secrets[key] = new SecretValue(url);

        AssertInvalidWithoutValueLeak(() => Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.RedisCompatibility,
            ServerOrdinary(AppHostRuntimeBackend.RedisCompatibility),
            secrets), url);
    }

    [Fact]
    public void Build_PrintablePunctuationInDatabaseCredentialsIsValid()
    {
        var secrets = BaseSecrets();
        secrets["PG_DATABASE_URL"] =
            new SecretValue("postgres://user.name:pa%24%24-word%21@127.0.0.1:5432/ebaycrm");
        secrets["REDIS_URL"] =
            new SecretValue("rediss://cache.user:p%40ss%21@127.0.0.1:6379/1");

        var result = Build(
            RuntimeRole.Server,
            AppHostRuntimeBackend.RedisCompatibility,
            ServerOrdinary(AppHostRuntimeBackend.RedisCompatibility),
            secrets);

        Assert.Same(secrets["PG_DATABASE_URL"], result.SecretEnvironment["PG_DATABASE_URL"]);
        Assert.Same(secrets["REDIS_URL"], result.SecretEnvironment["REDIS_URL"]);
    }

    [Fact]
    public void Build_DatabaseRoleFailsClosed()
    {
        AssertInvalid(() => Build(
            RuntimeRole.Database,
            AppHostRuntimeBackend.PostgresDesktop,
            ServerOrdinary(AppHostRuntimeBackend.PostgresDesktop),
            BaseSecrets()));
    }

    private AllowlistedRoleEnvironment Build(
        RuntimeRole role,
        AppHostRuntimeBackend backend,
        IReadOnlyDictionary<string, string> ordinary,
        IReadOnlyDictionary<string, SecretValue> secrets) =>
        AllowlistedRoleEnvironmentBuilder.Build(new AllowlistedRoleEnvironmentRequest(
            role,
            backend,
            _profileRoot,
            ordinary,
            secrets));

    private Dictionary<string, string> ServerOrdinary(AppHostRuntimeBackend backend)
    {
        var ordinary = CommonOrdinary(backend);
        ordinary["NODE_PORT"] = "3000";
        return ordinary;
    }

    private Dictionary<string, string> WorkerOrdinary(AppHostRuntimeBackend backend)
    {
        var ordinary = CommonOrdinary(backend);
        ordinary["DISABLE_DB_MIGRATIONS"] = "true";
        return ordinary;
    }

    private Dictionary<string, string> CommonOrdinary(AppHostRuntimeBackend backend) =>
        new(StringComparer.Ordinal)
        {
            ["SystemRoot"] = CanonicalWindowsRoot(),
            ["NODE_ENV"] = "production",
            ["RUNTIME_BACKEND"] = backend == AppHostRuntimeBackend.PostgresDesktop
                ? "postgres-desktop"
                : "redis",
            ["SERVER_URL"] = "http://127.0.0.1:3000",
            ["STORAGE_TYPE"] = "local",
            ["STORAGE_LOCAL_PATH"] = Path.Combine(_profileRoot, "storage"),
            ["IS_CONFIG_VARIABLES_IN_DB_ENABLED"] = "false",
        };

    private static Dictionary<string, SecretValue> BaseSecrets() =>
        new(StringComparer.Ordinal)
        {
            ["PG_DATABASE_URL"] = new("postgres://user:password@127.0.0.1:5432/ebaycrm"),
            ["APP_SECRET"] = new("application-secret"),
        };

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<AppHostOptionsException>(action);
        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.Equal("role-launch-plan-invalid", error.Message);
        Assert.Null(error.InnerException);
    }

    private static void AssertInvalidWithoutValueLeak(Action action, string value)
    {
        var error = Assert.Throws<AppHostOptionsException>(action);
        Assert.Equal("role-launch-plan-invalid", error.ReasonCode);
        Assert.Equal("role-launch-plan-invalid", error.Message);
        Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    private static string CanonicalWindowsRoot() =>
        Directory.GetParent(Environment.SystemDirectory)?.FullName
        ?? throw new InvalidOperationException("windows-root-unavailable");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ParentCanaryKey, _originalCanary);
        GC.SuppressFinalize(this);
    }
}
