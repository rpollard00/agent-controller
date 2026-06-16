using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Options;
using AgentController.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

public class InfrastructureSmokeTests
{
    [Fact]
    public void InfrastructureLayer_ReferencesApplicationAndDomain()
    {
        // Prove Infrastructure -> Application and Domain dependencies are resolvable.
        var appType = typeof(IWorkSource);
        Assert.NotNull(appType);

        var domainType = typeof(WorkCandidate);
        Assert.NotNull(domainType);
    }

    [Fact]
    public void NoOpWorkSource_ImplementsInterface()
    {
        var provider = new NoOpWorkSource();
        Assert.IsAssignableFrom<IWorkSource>(provider);
    }

    [Fact]
    public void NoOpSourceControlProvider_ImplementsInterface()
    {
        var provider = new NoOpSourceControlProvider();
        Assert.IsAssignableFrom<ISourceControlProvider>(provider);
    }

    [Fact]
    public void NoOpEnvironmentProvider_ImplementsInterface()
    {
        var provider = new NoOpEnvironmentProvider();
        Assert.IsAssignableFrom<IEnvironmentProvider>(provider);
    }

    [Fact]
    public void NoOpAgentRuntime_ImplementsInterface()
    {
        var provider = new NoOpAgentRuntime();
        Assert.IsAssignableFrom<IAgentRuntime>(provider);
    }

    [Fact]
    public async Task NoOpWorkSource_FindEligibleAsync_ReturnsEmptyList()
    {
        var source = new NoOpWorkSource();
        var candidates = await source.FindEligibleAsync(new WorkQuery(), CancellationToken.None);

        Assert.NotNull(candidates);
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task NoOpWorkSource_TryClaimAsync_ReturnsFailed()
    {
        var source = new NoOpWorkSource();
        var candidate = new WorkCandidate { Id = "c1", ExternalId = "1", RepoKey = "r", Title = "t", Source = "f" };
        var claim = new ClaimRequest { WorkerId = "w1" };

        var result = await source.TryClaimAsync(candidate, claim, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Null(result.WorkRef);
        Assert.Null(result.LeaseToken);
    }

    [Fact]
    public async Task NoOpSourceControlProvider_CloneAsync_IsDeterministic()
    {
        var provider = new NoOpSourceControlProvider();
        var spec = new RepositorySpec { RepoKey = "example", CloneUrl = "https://example.com/repo" };
        var env = new EnvironmentHandle { Id = "env-1" };

        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);

        Assert.Equal("example", checkout.RepoKey);
        Assert.Equal(DateTimeOffset.UnixEpoch, checkout.ClonedAt);
        Assert.Null(checkout.CommitSha);
        Assert.Equal(string.Empty, checkout.LocalPath);
    }

    [Fact]
    public async Task NoOpSourceControlProvider_GetStatusAsync_ReturnsNotExists()
    {
        var provider = new NoOpSourceControlProvider();
        var scRef = new SourceControlRef { Provider = "fake", RepoKey = "r" };

        var status = await provider.GetStatusAsync(scRef, CancellationToken.None);

        Assert.False(status.Exists);
        Assert.Null(status.PullRequestUrl);
        Assert.Null(status.PullRequestStatus);
    }

    [Fact]
    public async Task NoOpAgentRuntime_StartAsync_IsDeterministic()
    {
        var runtime = new NoOpAgentRuntime();
        var spec = new AgentRunSpec { RunId = "run-1" };

        var handle = await runtime.StartAsync(spec, CancellationToken.None);

        Assert.Equal("run-1", handle.RunId);
        Assert.Equal(RunLifecycleState.Queued, handle.Status);
        Assert.Equal(DateTimeOffset.UnixEpoch, handle.StartedAt);
        Assert.Null(handle.RuntimeRunId);
    }

    [Fact]
    public async Task NoOpAgentRuntime_GetStatusAsync_ReflectsHandle()
    {
        var runtime = new NoOpAgentRuntime();
        var handle = new AgentRunHandle { RunId = "run-1", Status = RunLifecycleState.Queued };

        var status = await runtime.GetStatusAsync(handle, CancellationToken.None);

        Assert.Equal(RunLifecycleState.Queued, status.Status);
        Assert.Equal("run-1", handle.RunId);
        Assert.Null(status.Error);
        Assert.Null(status.Events);
    }

    [Fact]
    public async Task NoOpEnvironmentProvider_CreateAsync_ReturnsHandle()
    {
        var provider = new NoOpEnvironmentProvider();
        var spec = new EnvironmentSpec { RunId = "run-1", Profile = "default" };

        var handle = await provider.CreateAsync(spec, CancellationToken.None);

        Assert.NotNull(handle);
        Assert.Contains("run-1", handle.Id);
        Assert.Equal("NoOp", handle.ProviderType);
    }

    [Fact]
    public async Task NoOpEnvironmentProvider_ExecuteAsync_ReturnsSuccess()
    {
        var provider = new NoOpEnvironmentProvider();
        var handle = new EnvironmentHandle { Id = "env-1" };
        var cmd = new CommandSpec { Command = "dotnet", Arguments = new List<string> { "build" } };

        var result = await provider.ExecuteAsync(handle, cmd, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(TimeSpan.Zero, result.Duration);
    }

    // ──────────────────────────────────────────────
    // PersistenceConnectionResolver tests
    // ──────────────────────────────────────────────

    [Fact]
    public void PersistenceConnectionResolver_ThrowsWhenConnectionStringIsMissing()
    {
        var options = new PersistenceOptions
        {
            Provider = "Sqlite",
            ConnectionString = string.Empty
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => PersistenceConnectionResolver.Resolve(options));

        Assert.Contains("connection string", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PersistenceConnectionResolver_ThrowsWhenConnectionStringIsNull()
    {
        var options = new PersistenceOptions
        {
            Provider = "Sqlite",
            ConnectionString = null!
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => PersistenceConnectionResolver.Resolve(options));

        Assert.Contains("connection string", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PersistenceConnectionResolver_ThrowsWhenOptionsIsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => PersistenceConnectionResolver.Resolve(null!));
    }

    [Fact]
    public void PersistenceConnectionResolver_RejectsRelativeSqlitePath()
    {
        var options = new PersistenceOptions
        {
            Provider = "Sqlite",
            ConnectionString = "Data Source=test.db"
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => PersistenceConnectionResolver.Resolve(options));

        Assert.Contains("not allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test.db", ex.Message);
    }

    [Fact]
    public void PersistenceConnectionResolver_ExpandsTildePathToUserHome()
    {
        var options = new PersistenceOptions
        {
            Provider = "Sqlite",
            ConnectionString = "Data Source=~/.agent-work-controller/agent-controller.db"
        };

        var resolved = PersistenceConnectionResolver.Resolve(options);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains(home, resolved, StringComparison.Ordinal);
        Assert.Contains(".agent-work-controller", resolved, StringComparison.Ordinal);
        Assert.Contains("agent-controller.db", resolved, StringComparison.Ordinal);
        Assert.DoesNotContain("~", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceConnectionResolver_ExpandsTildeAloneToUserHome()
    {
        var options = new PersistenceOptions
        {
            Provider = "Sqlite",
            ConnectionString = "Data Source=~"
        };

        var resolved = PersistenceConnectionResolver.Resolve(options);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains($"Data Source={home}", resolved, StringComparison.Ordinal);
        Assert.DoesNotContain("~", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceConnectionResolver_PassesThroughAbsolutePath()
    {
        var options = new PersistenceOptions
        {
            Provider = "Sqlite",
            ConnectionString = "Data Source=/tmp/agent-controller-test.db"
        };

        var resolved = PersistenceConnectionResolver.Resolve(options);

        Assert.Contains("/tmp/agent-controller-test.db", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceConnectionResolver_PassesThroughNonSqliteProvider()
    {
        var pgConnString = "Host=localhost;Database=agents;Username=dev;Password=secret";
        var options = new PersistenceOptions
        {
            Provider = "Postgres",
            ConnectionString = pgConnString
        };

        var resolved = PersistenceConnectionResolver.Resolve(options);

        Assert.Equal(pgConnString, resolved);
    }

    [Fact]
    public void PersistenceConnectionResolver_TildePathResolvesIndependentlyOfCurrentDirectory()
    {
        // Tilde paths must expand to the user home regardless of the
        // current working directory.
        var tildePath = "Data Source=~/.agent-work-controller/agent-controller.db";
        var options = new PersistenceOptions
        {
            Provider = "Sqlite",
            ConnectionString = tildePath
        };

        var originalCwd = Directory.GetCurrentDirectory();
        try
        {
            // Switch to a different directory — the resolved path must
            // be the same as when called from the project root.
            Directory.SetCurrentDirectory(Path.GetTempPath());

            var resolvedFromTemp = PersistenceConnectionResolver.Resolve(options);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.StartsWith($"Data Source={home}", resolvedFromTemp, StringComparison.Ordinal);
            Assert.Contains(".agent-work-controller", resolvedFromTemp, StringComparison.Ordinal);
            Assert.DoesNotContain("~", resolvedFromTemp, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public void PersistenceConnectionResolver_RejectsSqliteDataSourcesWithoutPath()
    {
        var options = new PersistenceOptions
        {
            Provider = "Sqlite",
            ConnectionString = "Data Source="
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => PersistenceConnectionResolver.Resolve(options));

        Assert.Contains("Data Source value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // SqliteDatabaseEnsurer tests
    // ──────────────────────────────────────────────

    [Fact]
    public void SqliteDatabaseEnsurer_CreatesParentDirectoryWhenMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ensurer-test-" + Guid.NewGuid());
        var dbDir = Path.Combine(tempRoot, "nested", "data");
        var dbPath = Path.Combine(dbDir, "test.db");
        var connStr = $"Data Source={dbPath}";

        try
        {
            var created = SqliteDatabaseEnsurer.EnsureDirectory(connStr);

            Assert.NotNull(created);
            Assert.Equal(dbDir, created);
            Assert.True(Directory.Exists(dbDir), "Parent directory should be created");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SqliteDatabaseEnsurer_ReturnsNullWhenDirectoryAlreadyExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ensurer-test-" + Guid.NewGuid());
        var dbPath = Path.Combine(tempDir, "test.db");
        var connStr = $"Data Source={dbPath}";

        try
        {
            Directory.CreateDirectory(tempDir);

            // First call should create nothing because dir already exists
            var result = SqliteDatabaseEnsurer.EnsureDirectory(connStr);

            Assert.Null(result);
            Assert.True(Directory.Exists(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SqliteDatabaseEnsurer_SkipsInMemoryDataSource()
    {
        var result = SqliteDatabaseEnsurer.EnsureDirectory("Data Source=:memory:");

        Assert.Null(result);
    }

    [Fact]
    public void SqliteDatabaseEnsurer_SkipsRelativeDataSource()
    {
        // Relative paths are rejected by PersistenceConnectionResolver before
        // reaching this method, but this helper is defensive.
        var result = SqliteDatabaseEnsurer.EnsureDirectory("Data Source=relative.db");

        Assert.Null(result);
    }

    [Fact]
    public void SqliteDatabaseEnsurer_ReturnsNullForNullConnectionString()
    {
        var result = SqliteDatabaseEnsurer.EnsureDirectory(null!);

        Assert.Null(result);
    }

    [Fact]
    public void SqliteDatabaseEnsurer_ReturnsNullForEmptyConnectionString()
    {
        var result = SqliteDatabaseEnsurer.EnsureDirectory(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void SqliteDatabaseEnsurer_ReturnsNullForWhitespaceConnectionString()
    {
        var result = SqliteDatabaseEnsurer.EnsureDirectory("   ");

        Assert.Null(result);
    }

    [Fact]
    public void SqliteDatabaseEnsurer_ReturnsNullWhenDataSourceIsMissing()
    {
        var result = SqliteDatabaseEnsurer.EnsureDirectory("Data Source=");

        Assert.Null(result);
    }

    // ──────────────────────────────────────────────
    // Migration runner integration tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task MigrationRunner_UsesConfiguredDatabaseLocation()
    {
        // The migration runner must create the database at the configured
        // path, not at a relative fallback like "agent-controller.db".
        var tempRoot = Path.Combine(Path.GetTempPath(), "migration-runner-test-" + Guid.NewGuid());
        var dbDir = Path.Combine(tempRoot, "custom", "data");
        var dbPath = Path.Combine(dbDir, "agent-controller.db");

        var originalProvider = Environment.GetEnvironmentVariable("persistence__provider");
        var originalConnString = Environment.GetEnvironmentVariable("persistence__connectionString");

        try
        {
            Environment.SetEnvironmentVariable("persistence__provider", "Sqlite");
            Environment.SetEnvironmentVariable(
                "persistence__connectionString",
                $"Data Source={dbPath}");

            var exitCode = await Program.Main(Array.Empty<string>());

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(dbPath),
                $"Database file should exist at configured path: {dbPath}");
            Assert.True(Directory.Exists(dbDir),
                $"Parent directory should be created: {dbDir}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("persistence__provider", originalProvider);
            Environment.SetEnvironmentVariable("persistence__connectionString", originalConnString);

            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MigrationRunner_CreatesParentDirectoryBeforeMigration()
    {
        // When the parent directory does not exist before migration,
        // the runner must create it so MigrateAsync can succeed.
        var tempRoot = Path.Combine(Path.GetTempPath(), "migration-parent-test-" + Guid.NewGuid());
        var dbDir = Path.Combine(tempRoot, "nonexistent", "subdir");
        var dbPath = Path.Combine(dbDir, "migrations.db");

        // Verify the directory does NOT exist before we start
        Assert.False(Directory.Exists(dbDir),
            "Pre-condition: directory should not exist yet");

        var originalProvider = Environment.GetEnvironmentVariable("persistence__provider");
        var originalConnString = Environment.GetEnvironmentVariable("persistence__connectionString");

        try
        {
            Environment.SetEnvironmentVariable("persistence__provider", "Sqlite");
            Environment.SetEnvironmentVariable(
                "persistence__connectionString",
                $"Data Source={dbPath}");

            var exitCode = await Program.Main(Array.Empty<string>());

            // The migration should succeed — the directory was created first
            Assert.Equal(0, exitCode);
            Assert.True(Directory.Exists(dbDir),
                $"Parent directory should exist after migration runner: {dbDir}");
            Assert.True(File.Exists(dbPath),
                $"Database should exist at: {dbPath}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("persistence__provider", originalProvider);
            Environment.SetEnvironmentVariable("persistence__connectionString", originalConnString);

            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ──────────────────────────────────────────────
    // Azure DevOps Boards smoke tests
    // ──────────────────────────────────────────────

    [Fact]
    public void AzureDevOpsBoardsClient_ImplementsInterface()
    {
        var type = typeof(AzureDevOpsBoardsClient);
        Assert.True(
            typeof(IAzureDevOpsBoardsClient).IsAssignableFrom(type),
            "AzureDevOpsBoardsClient should implement IAzureDevOpsBoardsClient");
    }

    [Fact]
    public void AzureDevOpsBoardsWorkSource_ImplementsInterface()
    {
        var type = typeof(AzureDevOpsBoardsWorkSource);
        Assert.True(
            typeof(IWorkSource).IsAssignableFrom(type),
            "AzureDevOpsBoardsWorkSource should implement IWorkSource");
    }

    [Fact]
    public void AzureDevOpsBoardsOptions_ResolvePat_EnvPrefixIsCaseInsensitive()
    {
        var envName = "AZDO_CASE_TEST_PAT";
        var expected = "case-insensitive-pat";

        try
        {
            Environment.SetEnvironmentVariable(envName, expected);

            // Lowercase 'env:' prefix
            var lowerOpt = new AzureDevOpsBoardsOptions
            {
                PersonalAccessToken = $"env:{envName}",
            };
            Assert.Equal(expected, lowerOpt.ResolvePersonalAccessToken());

            // Mixed case 'Env:' prefix
            var mixedOpt = new AzureDevOpsBoardsOptions
            {
                PersonalAccessToken = $"Env:{envName}",
            };
            Assert.Equal(expected, mixedOpt.ResolvePersonalAccessToken());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Fact]
    public void IAzureDevOpsBoardsClient_IsDefinedInApplicationLayer()
    {
        var type = typeof(IAzureDevOpsBoardsClient);
        Assert.True(type.IsInterface, "IAzureDevOpsBoardsClient should be an interface");
        Assert.Equal("AgentController.Application", type.Namespace);
    }

    [Fact]
    public void AzureDevOpsBoardsWorkSource_DiRegistration_WithValidationDisabled_Succeeds()
    {
        // Test that DI registration succeeds when validateConnection=false
        // (used in test/development scenarios without real Azure DevOps creds)
        var envName = "AZDO_DI_TEST_PAT";
        try
        {
            Environment.SetEnvironmentVariable(envName, "test-pat-value");

            var configValues = new Dictionary<string, string?>
            {
                ["workSource:provider"] = "AzureDevOpsBoards",
                ["workSource:organizationUrl"] = "https://dev.azure.com/testorg",
                ["workSource:project"] = "TestProject",
                ["azureDevOps:personalAccessToken"] = $"ENV:{envName}",
            };

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddAgentControllerOptions(config);
            services.AddAgentControllerAzureDevOpsBoardsWorkSource(validateConnection: false);

            var provider = services.BuildServiceProvider();

            // Should be able to resolve IWorkSource as AzureDevOpsBoardsWorkSource
            var workSource = provider.GetRequiredService<IWorkSource>();
            Assert.IsType<AzureDevOpsBoardsWorkSource>(workSource);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    [Fact]
    public void AzureDevOpsBoardsWorkSource_DiRegistration_WithValidationEnabled_ThrowsOnMissingConfig()
    {
        // Test that eager validation catches missing configuration.
        // Validation runs when IAzureDevOpsBoardsClient is first resolved
        // (scoped), not at IWorkSource singleton registration time.
        var configValues = new Dictionary<string, string?>
        {
            ["workSource:provider"] = "AzureDevOpsBoards",
            // Missing: organizationUrl, project
            ["azureDevOps:personalAccessToken"] = "",  // Missing PAT
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddAgentControllerOptions(config);
        services.AddAgentControllerAzureDevOpsBoardsWorkSource(validateConnection: true);

        var provider = services.BuildServiceProvider();

        // The singleton IWorkSource resolves fine (it's lazy).
        // Validation fires when the scoped IAzureDevOpsBoardsClient is resolved.
        using var scope = provider.CreateScope();
        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClient>());
    }
}
