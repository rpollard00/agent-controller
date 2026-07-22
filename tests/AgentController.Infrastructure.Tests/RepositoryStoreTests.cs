using AgentController.Domain;
using AgentController.Domain.Secrets;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentController.Infrastructure.Tests;

public sealed class RepositoryStoreTests
{
    [Fact]
    public async Task CreateAndGet_RoundTripsProfile()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var profile = CreateProfile("example");

        var created = await fixture.Store.CreateAsync(profile, CancellationToken.None);
        var persisted = await fixture.Store.GetByKeyAsync(profile.Key, CancellationToken.None);

        Assert.True(created);
        AssertProfile(profile, Assert.IsType<RepositoryProfile>(persisted));
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
        var duplicate = original with { CloneUrl = "https://example.test/replacement.git" };

        Assert.True(await fixture.Store.CreateAsync(original, CancellationToken.None));
        Assert.False(await fixture.Store.CreateAsync(duplicate, CancellationToken.None));

        var persisted = Assert.Single(await fixture.Store.ListAsync(CancellationToken.None));
        AssertProfile(original, persisted);
    }

    [Fact]
    public async Task Update_ReplacesAllMutableFieldsAndPreservesCreationTimestamp()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var original = CreateProfile("updated");
        var updated = original with
        {
            CloneUrl = "/srv/git/updated",
            DefaultBranch = "develop",
            Transport = CloneTransport.Local,
            EnvironmentProfile = "legacy-environment-v2",
            RuntimeProfile = "legacy-runtime-v2",
            RepositoryHostConnectionKey = "ado-staging",
            RuntimeEnvironmentKey = null,
            SshKeyReference = SecretReference.ByName("staging-deploy-key"),
        };

        Assert.True(await fixture.Store.CreateAsync(original, CancellationToken.None));
        var createdAt = await ReadColumnAsync(fixture.Connection, original.Key, "CreatedAt");

        Assert.True(await fixture.Store.UpdateAsync(updated, CancellationToken.None));

        var persisted = await fixture.Store.GetByKeyAsync(updated.Key, CancellationToken.None);
        AssertProfile(updated, Assert.IsType<RepositoryProfile>(persisted));
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
    public async Task Upsert_CreatesAndUpdatesProfilesForWorkerCompatibility()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var original = CreateProfile("seeded");
        var updated = original with
        {
            DefaultBranch = "release",
            RepositoryHostConnectionKey = null,
            RuntimeEnvironmentKey = "runtime-production",
        };

        await fixture.Store.UpsertAsync(original, CancellationToken.None);
        await fixture.Store.UpsertAsync(updated, CancellationToken.None);

        var persisted = await fixture.Store.GetByKeyAsync(updated.Key, CancellationToken.None);
        AssertProfile(updated, Assert.IsType<RepositoryProfile>(persisted));
        Assert.Single(await fixture.Store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Migration_PreservesLegacyRowsWithNullManagedAssociations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = CreateDbContext(connection);
        var migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260713181859_PersistRuntimeEnvironmentProfiles");

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO Repositories
                    (Key, CloneUrl, DefaultBranch, EnvironmentProfile, RuntimeProfile,
                     CreatedAt, UpdatedAt, Transport)
                VALUES
                    ($key, $cloneUrl, $defaultBranch, $environmentProfile, $runtimeProfile,
                     $createdAt, $updatedAt, $transport)
                """;
            insert.Parameters.AddWithValue("$key", "legacy");
            insert.Parameters.AddWithValue("$cloneUrl", "git@example.test:legacy.git");
            insert.Parameters.AddWithValue("$defaultBranch", "main");
            insert.Parameters.AddWithValue("$environmentProfile", "legacy-environment");
            insert.Parameters.AddWithValue("$runtimeProfile", "legacy-runtime");
            insert.Parameters.AddWithValue("$createdAt", "2026-07-01 12:00:00+00:00");
            insert.Parameters.AddWithValue("$updatedAt", "2026-07-01 12:00:00+00:00");
            insert.Parameters.AddWithValue("$transport", (int)CloneTransport.Ssh);
            await insert.ExecuteNonQueryAsync();
        }

        await migrator.MigrateAsync();
        var store = new EfRepositoryStore(dbContext);

        var profile = await store.GetByKeyAsync("legacy", CancellationToken.None);

        Assert.NotNull(profile);
        Assert.Null(profile.RepositoryHostConnectionKey);
        Assert.Null(profile.RuntimeEnvironmentKey);
        Assert.Null(profile.SshKeyReference);
        Assert.Equal(CloneTransport.Ssh, profile.Transport);
        Assert.Contains(
            await dbContext.Database.GetAppliedMigrationsAsync(),
            migration =>
                migration.EndsWith("_ExpandRepositoryProfilePersistence", StringComparison.Ordinal)
        );
    }

    private static RepositoryProfile CreateProfile(string key)
    {
        return new RepositoryProfile
        {
            Key = key,
            CloneUrl = $"git@example.test:{key}.git",
            DefaultBranch = "main",
            Transport = CloneTransport.Ssh,
            EnvironmentProfile = "legacy-environment",
            RuntimeProfile = "legacy-runtime",
            RepositoryHostConnectionKey = "ado-production",
            RuntimeEnvironmentKey = "runtime-local",
            SshKeyReference = SecretReference.ByNameAndVersion("production-deploy-key", 2),
        };
    }

    private static void AssertProfile(RepositoryProfile expected, RepositoryProfile actual)
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.CloneUrl, actual.CloneUrl);
        Assert.Equal(expected.DefaultBranch, actual.DefaultBranch);
        Assert.Equal(expected.Transport, actual.Transport);
        Assert.Equal(expected.EnvironmentProfile, actual.EnvironmentProfile);
        Assert.Equal(expected.RuntimeProfile, actual.RuntimeProfile);
        Assert.Equal(expected.RepositoryHostConnectionKey, actual.RepositoryHostConnectionKey);
        Assert.Equal(expected.RuntimeEnvironmentKey, actual.RuntimeEnvironmentKey);
        Assert.Equal(expected.SshKeyReference, actual.SshKeyReference);
    }

    private static async Task<string> ReadColumnAsync(
        SqliteConnection connection,
        string key,
        string column
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM Repositories WHERE Key = $key";
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
            Store = new EfRepositoryStore(dbContext);
        }

        public SqliteConnection Connection { get; }

        public AgentControllerDbContext DbContext { get; }

        public EfRepositoryStore Store { get; }

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
