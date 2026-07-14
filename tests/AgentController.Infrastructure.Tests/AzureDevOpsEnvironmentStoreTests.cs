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

public sealed class AzureDevOpsEnvironmentStoreTests
{
    [Fact]
    public async Task CreateAndGet_RoundTripsProfileAndCredentialReference()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var profile = CreateProfile("production");
        const string rawPat = "raw-pat-must-not-be-persisted";
        Environment.SetEnvironmentVariable(profile.PatEnvironmentVariable, rawPat);

        try
        {
            var created = await fixture.Store.CreateAsync(profile, CancellationToken.None);
            var persisted = await fixture.Store.GetByKeyAsync(profile.Key, CancellationToken.None);

            Assert.True(created);
            AssertProfile(profile, Assert.IsType<AzureDevOpsEnvironmentProfile>(persisted));

            await using var command = fixture.Connection.CreateCommand();
            command.CommandText = "SELECT PatEnvironmentVariable FROM WorkSourceEnvironments WHERE Key = $key";
            command.Parameters.AddWithValue("$key", profile.Key);
            var storedReference = Assert.IsType<string>(await command.ExecuteScalarAsync());
            Assert.Equal("ADO_PRODUCTION_PAT", storedReference);
            Assert.NotEqual(rawPat, storedReference);
        }
        finally
        {
            Environment.SetEnvironmentVariable(profile.PatEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task List_ReturnsProfilesInDeterministicKeyOrder()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        Assert.True(await fixture.Store.CreateAsync(CreateProfile("zulu"), CancellationToken.None));
        Assert.True(await fixture.Store.CreateAsync(CreateProfile("alpha"), CancellationToken.None));
        Assert.True(await fixture.Store.CreateAsync(CreateProfile("middle"), CancellationToken.None));

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

        var profiles = await fixture.Store.ListAsync(CancellationToken.None);
        var persisted = Assert.Single(profiles);
        Assert.Equal(original.DisplayName, persisted.DisplayName);
    }

    [Fact]
    public async Task Update_ReplacesAllMutableFieldsAndPreservesCreationTimestamp()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var original = CreateProfile("staging");
        var updated = original with
        {
            DisplayName = "Staging Boards",
            Enabled = false,
            OrganizationUrl = "https://dev.azure.com/updated-organization",
            Project = "Updated Project",
            WorkItemType = "Product Backlog Item",
            EligibleTags = ["agent", "ready"],
            ExcludedTags = ["manual", "blocked"],
            EligibleStates = ["Approved", "Committed"],
            ExcludedStates = ["Removed"],
            ActiveState = "Doing",
            CompletedState = "Done",
            PatEnvironmentVariable = "ADO_STAGING_PAT_V2",
            UpdatedAt = new DateTimeOffset(2026, 7, 13, 18, 0, 0, TimeSpan.Zero),
        };

        Assert.True(await fixture.Store.CreateAsync(original, CancellationToken.None));
        Assert.True(await fixture.Store.UpdateAsync(updated, CancellationToken.None));

        var persisted = await fixture.Store.GetByKeyAsync(updated.Key, CancellationToken.None);
        AssertProfile(updated, Assert.IsType<AzureDevOpsEnvironmentProfile>(persisted));
        Assert.Equal(original.CreatedAt, persisted!.CreatedAt);
    }

    [Fact]
    public async Task Update_ReturnsFalseWhenProfileDoesNotExist()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var updated = await fixture.Store.UpdateAsync(
            CreateProfile("missing"),
            CancellationToken.None);

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
    public async Task Migration_CreatesProfileTableWithoutCredentialValueColumn()
    {
        await using var fixture = await StoreFixture.CreateAsync(useMigrations: true);

        var appliedMigrations = await fixture.DbContext.Database.GetAppliedMigrationsAsync();
        Assert.Contains(
            appliedMigrations,
            migration => migration.EndsWith(
                "_RenameToWorkSourceEnvironments",
                StringComparison.Ordinal));

        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('WorkSourceEnvironments')";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("PatEnvironmentVariable", columns);
        Assert.DoesNotContain("PersonalAccessToken", columns);
        Assert.DoesNotContain("Pat", columns);
        Assert.Contains("Provider", columns);
        Assert.Contains("TagPrefix", columns);
        Assert.Contains("CompletedStatesJson", columns);
        Assert.DoesNotContain("WorkItemType", columns);
        Assert.DoesNotContain("EligibleTagsJson", columns);
        Assert.DoesNotContain("ExcludedTagsJson", columns);
        Assert.DoesNotContain("EligibleStatesJson", columns);
        Assert.DoesNotContain("ExcludedStatesJson", columns);
    }

    [Fact]
    public void RepositoryRegistration_ResolvesAzureDevOpsEnvironmentStore()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"ado-environment-di-{Guid.NewGuid():N}.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = $"Data Source={databasePath}",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddAgentControllerDbContext(configuration);
        services.AddAgentControllerRepositories();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IAzureDevOpsEnvironmentStore>();
        Assert.IsType<EfAzureDevOpsEnvironmentStore>(store);
    }

    private static AzureDevOpsEnvironmentProfile CreateProfile(string key)
    {
        return new AzureDevOpsEnvironmentProfile
        {
            Key = key,
            DisplayName = $"{key} boards",
            Enabled = true,
            OrganizationUrl = "https://dev.azure.com/example",
            Project = "Agent Controller",
            WorkItemType = "User Story",
            EligibleTags = ["ready", "agent"],
            ExcludedTags = ["manual"],
            EligibleStates = ["New", "Approved"],
            ExcludedStates = ["Closed", "Removed"],
            ActiveState = "Active",
            CompletedState = "Resolved",
            PatEnvironmentVariable = "ADO_PRODUCTION_PAT",
            CreatedAt = new DateTimeOffset(2026, 7, 13, 17, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 7, 13, 17, 30, 0, TimeSpan.Zero),
        };
    }

    private static void AssertProfile(
        AzureDevOpsEnvironmentProfile expected,
        AzureDevOpsEnvironmentProfile actual)
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.OrganizationUrl, actual.OrganizationUrl);
        Assert.Equal(expected.Project, actual.Project);
        Assert.Equal(expected.WorkItemType, actual.WorkItemType);
        Assert.Equal(expected.EligibleTags, actual.EligibleTags);
        Assert.Equal(expected.ExcludedTags, actual.ExcludedTags);
        Assert.Equal(expected.EligibleStates, actual.EligibleStates);
        Assert.Equal(expected.ExcludedStates, actual.ExcludedStates);
        Assert.Equal(expected.ActiveState, actual.ActiveState);
        Assert.Equal(expected.CompletedState, actual.CompletedState);
        Assert.Equal(expected.PatEnvironmentVariable, actual.PatEnvironmentVariable);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(
            SqliteConnection connection,
            AgentControllerDbContext dbContext)
        {
            Connection = connection;
            DbContext = dbContext;
            Store = new EfAzureDevOpsEnvironmentStore(dbContext);
        }

        public SqliteConnection Connection { get; }

        public AgentControllerDbContext DbContext { get; }

        public EfAzureDevOpsEnvironmentStore Store { get; }

        public static async Task<StoreFixture> CreateAsync(bool useMigrations = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AgentControllerDbContext>()
                .UseSqlite(
                    connection,
                    sqlite => sqlite.MigrationsAssembly("AgentController.Migrations"))
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
