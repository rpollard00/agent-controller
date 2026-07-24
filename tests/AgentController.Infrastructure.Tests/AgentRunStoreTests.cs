using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentController.Infrastructure.Tests;

public sealed class AgentRunStoreTests
{
    [Fact]
    public async Task CreateAndGet_RoundTripsRuntimeEnvironmentSnapshot()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var request = new CreateRunRequest
        {
            WorkItemId = "work-123",
            WorkerId = "worker-1",
            RuntimeType = "PiMateria",
            RuntimeProfileName = "ReeseProjecto LocalWorkspace",
            EnvironmentProviderType = "LocalWorkspace",
        };

        var created = await fixture.Store.CreateAsync(request, CancellationToken.None);
        var persisted = await fixture.Store.GetByIdAsync(created.RunId, CancellationToken.None);

        Assert.Equal(request.RuntimeType, created.RuntimeType);
        Assert.Equal(request.RuntimeProfileName, created.RuntimeProfileName);
        Assert.Equal(request.EnvironmentProviderType, created.EnvironmentProviderType);
        Assert.NotNull(persisted);
        Assert.Equal(request.RuntimeType, persisted.RuntimeType);
        Assert.Equal(request.RuntimeProfileName, persisted.RuntimeProfileName);
        Assert.Equal(request.EnvironmentProviderType, persisted.EnvironmentProviderType);
    }

    [Fact]
    public async Task UpdateRuntimeFields_AppliesSnapshotFieldsOnlyWhenNonNull()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var created = await fixture.Store.CreateAsync(
            new CreateRunRequest
            {
                WorkItemId = "work-123",
                WorkerId = "worker-1",
                RuntimeType = "PiMateria",
                RuntimeProfileName = "Original environment",
                EnvironmentProviderType = "LocalWorkspace",
            },
            CancellationToken.None
        );

        await fixture.Store.UpdateRuntimeFieldsAsync(
            created.RunId,
            new RuntimeFieldUpdate
            {
                RuntimeProfileName = "Updated environment",
                EnvironmentProviderType = null,
            },
            CancellationToken.None
        );

        var afterProfileUpdate = await fixture.Store.GetByIdAsync(
            created.RunId,
            CancellationToken.None
        );
        Assert.NotNull(afterProfileUpdate);
        Assert.Equal("Updated environment", afterProfileUpdate.RuntimeProfileName);
        Assert.Equal("LocalWorkspace", afterProfileUpdate.EnvironmentProviderType);

        await fixture.Store.UpdateRuntimeFieldsAsync(
            created.RunId,
            new RuntimeFieldUpdate
            {
                RuntimeProfileName = null,
                EnvironmentProviderType = "ContainerWorkspace",
            },
            CancellationToken.None
        );

        var afterProviderUpdate = await fixture.Store.GetByIdAsync(
            created.RunId,
            CancellationToken.None
        );
        Assert.NotNull(afterProviderUpdate);
        Assert.Equal("Updated environment", afterProviderUpdate.RuntimeProfileName);
        Assert.Equal("ContainerWorkspace", afterProviderUpdate.EnvironmentProviderType);
    }

    [Fact]
    public async Task Migration_AddsNullableSnapshotColumnsWithoutBackfill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = CreateDbContext(connection);
        var migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260724013554_AddRepositoryWebUrl");

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO AgentRuns
                    (Id, WorkItemId, WorkerId, RuntimeType, Status, RunAttempt, CreatedAt, UpdatedAt)
                VALUES
                    ($id, $workItemId, $workerId, $runtimeType, $status, $runAttempt,
                     $createdAt, $updatedAt)
                """;
            insert.Parameters.AddWithValue("$id", "legacy-run");
            insert.Parameters.AddWithValue("$workItemId", "legacy-work-item");
            insert.Parameters.AddWithValue("$workerId", "legacy-worker");
            insert.Parameters.AddWithValue("$runtimeType", "PiMateria");
            insert.Parameters.AddWithValue("$status", (int)RunLifecycleState.Claimed);
            insert.Parameters.AddWithValue("$runAttempt", 1);
            insert.Parameters.AddWithValue("$createdAt", "2026-07-24 04:00:00+00:00");
            insert.Parameters.AddWithValue("$updatedAt", "2026-07-24 04:00:00+00:00");
            await insert.ExecuteNonQueryAsync();
        }

        await migrator.MigrateAsync();
        var persisted = await new EfAgentRunStore(dbContext).GetByIdAsync(
            "legacy-run",
            CancellationToken.None
        );

        Assert.NotNull(persisted);
        Assert.Null(persisted.RuntimeProfileName);
        Assert.Null(persisted.EnvironmentProviderType);
        Assert.Contains(
            await dbContext.Database.GetAppliedMigrationsAsync(),
            migration =>
                migration.EndsWith(
                    "_AddRuntimeEnvironmentSnapshotToAgentRuns",
                    StringComparison.Ordinal
                )
        );
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
            Store = new EfAgentRunStore(dbContext);
        }

        private SqliteConnection Connection { get; }

        private AgentControllerDbContext DbContext { get; }

        public EfAgentRunStore Store { get; }

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
