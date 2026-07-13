using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using AgentController.Infrastructure.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure.Tests;

public sealed class RuntimeEnvironmentStoreTests
{
    [Fact]
    public async Task CreateAndGet_RoundTripsStructuredSettingsWithoutResolvingSecrets()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var profile = CreateProfile("local-pi");
        const string sourceVariable = "CONTROLLER_ADO_PAT";
        const string rawPat = "raw-pat-must-not-be-persisted";
        Environment.SetEnvironmentVariable(sourceVariable, rawPat);

        try
        {
            var created = await fixture.Store.CreateAsync(profile, CancellationToken.None);
            var persisted = await fixture.Store.GetByKeyAsync(profile.Key, CancellationToken.None);

            Assert.True(created);
            AssertProfile(profile, Assert.IsType<RuntimeEnvironmentProfile>(persisted));

            await using var command = fixture.Connection.CreateCommand();
            command.CommandText =
                "SELECT ForwardEnvironmentVariablesJson FROM RuntimeEnvironments WHERE Key = $key";
            command.Parameters.AddWithValue("$key", profile.Key);
            var storedMappings = Assert.IsType<string>(await command.ExecuteScalarAsync());
            Assert.Contains(sourceVariable, storedMappings, StringComparison.Ordinal);
            Assert.DoesNotContain(rawPat, storedMappings, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sourceVariable, null);
        }
    }

    [Fact]
    public async Task List_ReturnsProfilesInDeterministicKeyOrder()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        Assert.True(await fixture.Store.CreateAsync(CreateProfile("zulu"), CancellationToken.None));
        Assert.True(
            await fixture.Store.CreateAsync(CreateProfile("alpha"), CancellationToken.None)
        );
        Assert.True(
            await fixture.Store.CreateAsync(CreateProfile("middle"), CancellationToken.None)
        );

        var profiles = await fixture.Store.ListAsync(CancellationToken.None);

        Assert.Equal(["alpha", "middle", "zulu"], profiles.Select(x => x.Key));
    }

    [Fact]
    public async Task Create_RejectsDuplicateKeyWithoutReplacingExistingProfile()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var original = CreateProfile("shared");
        var duplicate = original with { DisplayName = "Replacement" };

        Assert.True(await fixture.Store.CreateAsync(original, CancellationToken.None));
        Assert.False(await fixture.Store.CreateAsync(duplicate, CancellationToken.None));

        var persisted = Assert.Single(await fixture.Store.ListAsync(CancellationToken.None));
        Assert.Equal(original.DisplayName, persisted.DisplayName);
    }

    [Fact]
    public async Task Update_ReplacesAllMutableFieldsAndPreservesCreationTimestamp()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var original = CreateProfile("staging");
        var updated = original with
        {
            DisplayName = "Staging container runtime",
            Enabled = false,
            EnvironmentProvider = "ContainerWorkspace",
            EnvironmentSettings = new EnvironmentProviderSettings
            {
                WorkspaceRoot = "/srv/agent-controller/staging",
            },
            RuntimeProvider = "UpdatedPiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = "/opt/pi/bin/pi",
                ControllerBaseUrl = "https://updated-controller.example.test",
                PtyWrapperPath = "/usr/bin/script",
                PtyWrapperArgs = "--quiet --command",
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.NewWork] = "updated-new-work",
                    [ExecutionKind.Rework] = "updated-rework",
                },
                ForwardEnvironmentVariables = new Dictionary<string, string>
                {
                    ["GITHUB_TOKEN"] = "CONTROLLER_GITHUB_TOKEN",
                    ["AZURE_DEVOPS_PAT"] = "CONTROLLER_ADO_PAT_V2",
                },
            },
            UpdatedAt = new DateTimeOffset(2026, 7, 13, 18, 15, 0, TimeSpan.Zero),
        };

        Assert.True(await fixture.Store.CreateAsync(original, CancellationToken.None));
        Assert.True(await fixture.Store.UpdateAsync(updated, CancellationToken.None));

        var persisted = await fixture.Store.GetByKeyAsync(updated.Key, CancellationToken.None);
        AssertProfile(updated, Assert.IsType<RuntimeEnvironmentProfile>(persisted));
        Assert.Equal(original.CreatedAt, persisted!.CreatedAt);
    }

    [Fact]
    public async Task Update_ReturnsFalseWhenProfileDoesNotExist()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var updated = await fixture.Store.UpdateAsync(
            CreateProfile("missing"),
            CancellationToken.None
        );

        Assert.False(updated);
    }

    [Fact]
    public async Task Delete_RemovesProfileAndReportsMissingKeys()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var profile = CreateProfile("temporary");
        Assert.True(await fixture.Store.CreateAsync(profile, CancellationToken.None));

        Assert.True(await fixture.Store.DeleteAsync(profile.Key, CancellationToken.None));
        Assert.Null(await fixture.Store.GetByKeyAsync(profile.Key, CancellationToken.None));
        Assert.False(await fixture.Store.DeleteAsync(profile.Key, CancellationToken.None));
    }

    [Fact]
    public async Task Migration_CreatesStructuredProfileTableWithoutSecretValueColumns()
    {
        await using var fixture = await StoreFixture.CreateAsync(useMigrations: true);

        var appliedMigrations = await fixture.DbContext.Database.GetAppliedMigrationsAsync();
        Assert.Contains(
            appliedMigrations,
            migration =>
                migration.EndsWith("_PersistRuntimeEnvironmentProfiles", StringComparison.Ordinal)
        );

        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('RuntimeEnvironments')";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("EnvironmentProvider", columns);
        Assert.Contains("WorkspaceRoot", columns);
        Assert.Contains("RuntimeProvider", columns);
        Assert.Contains("PiExecutablePath", columns);
        Assert.Contains("ControllerBaseUrl", columns);
        Assert.Contains("LoadoutsJson", columns);
        Assert.Contains("ForwardEnvironmentVariablesJson", columns);
        Assert.DoesNotContain(
            columns,
            column => column.Contains("Secret", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            columns,
            column => column.Contains("Credential", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void RepositoryRegistration_ResolvesRuntimeEnvironmentStore()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"runtime-environment-di-{Guid.NewGuid():N}.db"
        );
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["persistence:provider"] = "Sqlite",
                    ["persistence:connectionString"] = $"Data Source={databasePath}",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddAgentControllerDbContext(configuration);
        services.AddAgentControllerRepositories();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IRuntimeEnvironmentStore>();
        Assert.IsType<EfRuntimeEnvironmentStore>(store);
    }

    private static RuntimeEnvironmentProfile CreateProfile(string key)
    {
        return new RuntimeEnvironmentProfile
        {
            Key = key,
            DisplayName = $"{key} runtime",
            Enabled = true,
            EnvironmentProvider = "LocalWorkspace",
            EnvironmentSettings = new EnvironmentProviderSettings
            {
                WorkspaceRoot = "/var/lib/agent-controller/runs",
            },
            RuntimeProvider = "PiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = "/usr/local/bin/pi",
                ControllerBaseUrl = "https://controller.example.test",
                PtyWrapperPath = "script",
                PtyWrapperArgs = "-qfc",
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.NewWork] = "new-work",
                    [ExecutionKind.Rework] = "rework",
                },
                ForwardEnvironmentVariables = new Dictionary<string, string>
                {
                    ["AZURE_DEVOPS_EXT_PAT"] = "CONTROLLER_ADO_PAT",
                    ["AZURE_DEVOPS_PAT"] = "CONTROLLER_ADO_PAT",
                },
            },
            CreatedAt = new DateTimeOffset(2026, 7, 13, 17, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 7, 13, 17, 30, 0, TimeSpan.Zero),
        };
    }

    private static void AssertProfile(
        RuntimeEnvironmentProfile expected,
        RuntimeEnvironmentProfile actual
    )
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.EnvironmentProvider, actual.EnvironmentProvider);
        Assert.Equal(
            expected.EnvironmentSettings.WorkspaceRoot,
            actual.EnvironmentSettings.WorkspaceRoot
        );
        Assert.Equal(expected.RuntimeProvider, actual.RuntimeProvider);
        Assert.Equal(
            expected.RuntimeSettings.PiExecutablePath,
            actual.RuntimeSettings.PiExecutablePath
        );
        Assert.Equal(
            expected.RuntimeSettings.ControllerBaseUrl,
            actual.RuntimeSettings.ControllerBaseUrl
        );
        Assert.Equal(
            expected.RuntimeSettings.PtyWrapperPath,
            actual.RuntimeSettings.PtyWrapperPath
        );
        Assert.Equal(
            expected.RuntimeSettings.PtyWrapperArgs,
            actual.RuntimeSettings.PtyWrapperArgs
        );
        Assert.Equal(
            expected.RuntimeSettings.Loadouts.OrderBy(x => x.Key),
            actual.RuntimeSettings.Loadouts.OrderBy(x => x.Key)
        );
        Assert.Equal(
            expected.RuntimeSettings.ForwardEnvironmentVariables.OrderBy(x => x.Key),
            actual.RuntimeSettings.ForwardEnvironmentVariables.OrderBy(x => x.Key)
        );
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(SqliteConnection connection, AgentControllerDbContext dbContext)
        {
            Connection = connection;
            DbContext = dbContext;
            Store = new EfRuntimeEnvironmentStore(dbContext);
        }

        public SqliteConnection Connection { get; }

        public AgentControllerDbContext DbContext { get; }

        public EfRuntimeEnvironmentStore Store { get; }

        public static async Task<StoreFixture> CreateAsync(bool useMigrations = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AgentControllerDbContext>()
                .UseSqlite(
                    connection,
                    sqlite => sqlite.MigrationsAssembly("AgentController.Migrations")
                )
                .Options;
            var dbContext = new AgentControllerDbContext(options);

            if (useMigrations)
            {
                await dbContext.Database.MigrateAsync();
            }
            else
            {
                await dbContext.Database.EnsureCreatedAsync();
            }

            return new StoreFixture(connection, dbContext);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
