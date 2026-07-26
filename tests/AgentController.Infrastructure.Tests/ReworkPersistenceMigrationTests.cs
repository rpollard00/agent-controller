using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentController.Infrastructure.Tests;

public sealed class ReworkPersistenceMigrationTests
{
    [Fact]
    public async Task Migration_DefaultsLegacyRowsToRevivalAndPreservesRunLineage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = CreateDbContext(connection);
        var migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260724042327_AddRuntimeEnvironmentSnapshotToAgentRuns");

        await InsertLegacyRowsAsync(connection);
        await migrator.MigrateAsync();

        var feedback = Assert.Single(
            await new EfReworkFeedbackStore(dbContext).GetWatchingAsync(CancellationToken.None));
        Assert.Equal(ReworkRequestMode.Revival, feedback.RequestMode);
        Assert.Equal("legacy-originating-run", feedback.OriginatingRunId);
        Assert.False(feedback.PullRequest.HasCanonicalIdentity);
        Assert.Null(feedback.CorrelationId);
        Assert.Null(feedback.AssistanceStoryWorkItemId);
        Assert.Null(feedback.AssistanceStoryExternalId);

        var cycle = await new EfReworkCycleStore(dbContext).GetPendingForWorkItemAsync(
            "legacy-work-item",
            CancellationToken.None);
        Assert.NotNull(cycle);
        Assert.Equal(ReworkRequestMode.Revival, cycle.RequestMode);
        Assert.Equal("legacy-prior-run", cycle.PriorRunId);
        Assert.False(cycle.PullRequest.HasCanonicalIdentity);
        Assert.Null(cycle.CorrelationId);

        Assert.Contains(
            await dbContext.Database.GetAppliedMigrationsAsync(),
            migration => migration.EndsWith(
                "_PersistAssistanceMaterializationState",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migration_MakesAssistanceRunLineageNullable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = CreateDbContext(connection);
        await dbContext.Database.MigrateAsync();

        Assert.False(await IsRequiredAsync(connection, "ReworkFeedback", "OriginatingRunId"));
        Assert.False(await IsRequiredAsync(connection, "ReworkCycles", "PriorRunId"));
    }

    private static AgentControllerDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AgentControllerDbContext>()
            .UseSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly("AgentController.Migrations"))
            .Options;
        return new AgentControllerDbContext(options);
    }

    private static async Task InsertLegacyRowsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ReworkFeedback
                (Id, OriginatingRunId, PullRequestId, FeedbackBundleId, FeedbackBundleJson,
                 FirstQualifyingCommentAt, LastQualifyingCommentAt, ThreadCount, Status,
                 CreatedAt, UpdatedAt)
            VALUES
                ('legacy-feedback', 'legacy-originating-run', '42', 'legacy-feedback-bundle',
                 '[]', '2026-07-24 04:00:00+00:00', '2026-07-24 04:00:00+00:00', 0, 0,
                 '2026-07-24 04:00:00+00:00', '2026-07-24 04:00:00+00:00');

            INSERT INTO ReworkCycles
                (Id, WorkItemId, CycleNumber, PriorRunId, BranchName, PullRequestUrl,
                 BaseCommitSha, FeedbackBundleJson, FeedbackBundleId, Status, CreatedAt)
            VALUES
                ('legacy-cycle', 'legacy-work-item', 1, 'legacy-prior-run', 'feature/legacy',
                 'https://dev.azure.com/example/_git/repo/pullrequest/42', 'abc123', '[]',
                 'legacy-cycle-bundle', 0, '2026-07-24 04:00:00+00:00');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> IsRequiredAsync(
        SqliteConnection connection,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return reader.GetInt32(3) == 1;
        }

        throw new InvalidOperationException($"Column '{table}.{column}' was not found.");
    }
}
