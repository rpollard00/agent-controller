using AgentController.Domain;
using AgentController.Domain.Secrets;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Tests;

public sealed class EfConnectionStoreTests
{
    [Fact]
    public async Task CreateAndGet_RoundTripsProfile()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var profile = CreateProfile("azuredevops-testorg");

        var created = await fixture.Store.CreateAsync(profile, CancellationToken.None);
        var persisted = await fixture.Store.GetByKeyAsync(profile.Key, CancellationToken.None);

        Assert.True(created);
        AssertProfile(profile, Assert.IsType<ConnectionProfile>(persisted));
    }

    [Fact]
    public async Task List_ReturnsProfilesInDeterministicKeyOrder()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        Assert.True(await fixture.Store.CreateAsync(CreateProfile("zulu-org"), CancellationToken.None));
        Assert.True(await fixture.Store.CreateAsync(CreateProfile("alpha-org"), CancellationToken.None));
        Assert.True(await fixture.Store.CreateAsync(CreateProfile("middle-org"), CancellationToken.None));

        var profiles = await fixture.Store.ListAsync(CancellationToken.None);

        Assert.Equal(["alpha-org", "middle-org", "zulu-org"], profiles.Select(x => x.Key));
    }

    [Fact]
    public async Task Create_RejectsDuplicateKeyWithoutReplacingExistingProfile()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var original = CreateProfile("shared");
        var duplicate = original with { DisplayName = "Replaced Name" };

        Assert.True(await fixture.Store.CreateAsync(original, CancellationToken.None));
        Assert.False(await fixture.Store.CreateAsync(duplicate, CancellationToken.None));

        var persisted = Assert.Single(await fixture.Store.ListAsync(CancellationToken.None));
        AssertProfile(original, persisted);
    }

    [Fact]
    public async Task Update_ReplacesMutableFieldsAndPreservesCreationTimestamp()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var original = CreateProfile("updated");
        var updated = original with
        {
            DisplayName = "Updated Display Name",
            Enabled = false,
            Capabilities = [ConnectionCapability.WorkTracking, ConnectionCapability.ExecutionHost],
        };

        Assert.True(await fixture.Store.CreateAsync(original, CancellationToken.None));
        var createdAt = await ReadColumnAsync(fixture.Connection, original.Key, "CreatedAt");

        Assert.True(await fixture.Store.UpdateAsync(updated, CancellationToken.None));

        var persisted = await fixture.Store.GetByKeyAsync(updated.Key, CancellationToken.None);
        AssertProfile(updated, Assert.IsType<ConnectionProfile>(persisted));
        Assert.Equal(
            createdAt,
            await ReadColumnAsync(fixture.Connection, updated.Key, "CreatedAt")
        );
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
    public async Task RoundTrip_PreservesCapabilitiesAndProviderSettingsJson()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var profile = new ConnectionProfile
        {
            Key = "ado-multi-cap",
            DisplayName = "ADO Multi-Cap",
            Enabled = true,
            Provider = "AzureDevOps",
            Capabilities = [ConnectionCapability.Repositories, ConnectionCapability.WorkTracking],
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                OrganizationUrl = "https://dev.azure.com/multi-cap-org",
                PersonalAccessTokenReference = SecretReference.ByName("ado-pat-multi"),
            },
        };

        Assert.True(await fixture.Store.CreateAsync(profile, CancellationToken.None));

        // Debug: read raw JSON from DB
        var rawJson = await ReadColumnAsync(fixture.Connection, profile.Key, "ProviderSettingsJson");
        Assert.NotNull(rawJson); // Should not be null
        Console.WriteLine($"DEBUG rawJson: {rawJson}");

        var persisted = await fixture.Store.GetByKeyAsync(profile.Key, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(2, persisted.Capabilities.Count);
        Assert.Contains(ConnectionCapability.Repositories, persisted.Capabilities);
        Assert.Contains(ConnectionCapability.WorkTracking, persisted.Capabilities);

        var adoSettings = Assert.IsType<AzureDevOpsConnectionSettings>(persisted.ProviderSettings);
        Assert.Equal("https://dev.azure.com/multi-cap-org", adoSettings.OrganizationUrl);
        Assert.Equal("ado-pat-multi", adoSettings.PersonalAccessTokenReference.Name);
    }

    [Fact]
    public async Task RoundTrip_NullProviderSettings_RemainsNull()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var profile = new ConnectionProfile
        {
            Key = "no-settings",
            DisplayName = "No Settings",
            Provider = "GitHub", // Reserved provider with no settings type
            Capabilities = [ConnectionCapability.Repositories],
            ProviderSettings = null,
        };

        Assert.True(await fixture.Store.CreateAsync(profile, CancellationToken.None));
        var persisted = await fixture.Store.GetByKeyAsync(profile.Key, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Null(persisted.ProviderSettings);
    }

    [Fact]
    public async Task RoundTrip_EmptyCapabilities_ListsEmpty()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var profile = new ConnectionProfile
        {
            Key = "no-caps",
            DisplayName = "No Capabilities",
            Capabilities = Array.Empty<ConnectionCapability>(),
        };

        Assert.True(await fixture.Store.CreateAsync(profile, CancellationToken.None));
        var persisted = await fixture.Store.GetByKeyAsync(profile.Key, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Empty(persisted.Capabilities);
    }

    [Fact]
    public async Task RoundTrip_AllThreeCapabilities_BitmaskRoundTrips()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var profile = new ConnectionProfile
        {
            Key = "all-caps",
            DisplayName = "All Capabilities",
            Capabilities = [ConnectionCapability.Repositories, ConnectionCapability.WorkTracking, ConnectionCapability.ExecutionHost],
        };

        Assert.True(await fixture.Store.CreateAsync(profile, CancellationToken.None));
        var persisted = await fixture.Store.GetByKeyAsync(profile.Key, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(3, persisted.Capabilities.Count);
        Assert.Contains(ConnectionCapability.Repositories, persisted.Capabilities);
        Assert.Contains(ConnectionCapability.WorkTracking, persisted.Capabilities);
        Assert.Contains(ConnectionCapability.ExecutionHost, persisted.Capabilities);
    }

    private static ConnectionProfile CreateProfile(string key)
    {
        return new ConnectionProfile
        {
            Key = key,
            DisplayName = $"Connection for {key}",
            Enabled = true,
            Provider = "AzureDevOps",
            Capabilities = [ConnectionCapability.Repositories, ConnectionCapability.WorkTracking],
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                OrganizationUrl = $"https://dev.azure.com/{key}",
                PersonalAccessTokenReference = SecretReference.ByName($"pat-{key}"),
            },
        };
    }

    private static void AssertProfile(ConnectionProfile expected, ConnectionProfile actual)
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.Capabilities, actual.Capabilities);

        if (expected.ProviderSettings is AzureDevOpsConnectionSettings expectedSettings)
        {
            var actualSettings = Assert.IsType<AzureDevOpsConnectionSettings>(actual.ProviderSettings);
            Assert.Equal(expectedSettings.OrganizationUrl, actualSettings.OrganizationUrl);
            Assert.Equal(expectedSettings.PersonalAccessTokenReference.Name, actualSettings.PersonalAccessTokenReference.Name);
        }
        else
        {
            Assert.Null(actual.ProviderSettings);
        }
    }

    private static async Task<string> ReadColumnAsync(
        SqliteConnection connection,
        string key,
        string column
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM Connections WHERE Key = $key";
        command.Parameters.AddWithValue("$key", key);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static AgentControllerDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AgentControllerDbContext>()
            .UseSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly("AgentController.Migrations")
            )
            .Options;
        return new AgentControllerDbContext(options);
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(SqliteConnection connection, AgentControllerDbContext dbContext)
        {
            Connection = connection;
            DbContext = dbContext;
            Store = new EfConnectionStore(dbContext);
        }

        public SqliteConnection Connection { get; }

        public AgentControllerDbContext DbContext { get; }

        public EfConnectionStore Store { get; }

        public static async Task<StoreFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var dbContext = CreateDbContext(connection);
            await dbContext.Database.EnsureCreatedAsync();
            return new StoreFixture(connection, dbContext);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
