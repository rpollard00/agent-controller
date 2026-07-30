using AgentController.Application;
using AgentController.Application.Queries;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentController.Infrastructure.Tests;

public sealed class BoardItemDiagnosticsPersistenceTests
{
    [Fact]
    public async Task Detail_ResolvesUpsertedControllerIdForPendingAssistanceCycle()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AgentControllerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AgentControllerDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var discoveredItem = new WorkCandidate
        {
            Id = "wi_42",
            ExternalId = "42",
            ExternalUrl = "https://boards.test/42",
            Source = "AzureDevOpsBoards",
            Title = "Investigate pickup",
            RepoKey = "orders",
            Status = "New",
            Tags = ["agent-ready-rework", "repo:orders"],
        };
        var workItems = new EfWorkItemStore(db);
        var persistedItem = await workItems.UpsertAsync(discoveredItem, CancellationToken.None);
        Assert.NotEqual(discoveredItem.Id, persistedItem.Id);
        Assert.StartsWith("wi_42_", persistedItem.Id, StringComparison.Ordinal);
        var resolvedItem = await workItems.GetByExternalIdentityAsync(
            discoveredItem.Source,
            discoveredItem.ExternalId,
            CancellationToken.None);
        Assert.Equal(persistedItem.Id, resolvedItem!.Id);

        var cycles = new EfReworkCycleStore(db);
        var pullRequest = new PullRequestReference
        {
            EnvironmentKey = "ado-main",
            RepositoryKey = "orders",
            PullRequestId = "17",
            PullRequestUrl = "https://dev.azure.com/example/_git/orders/pullrequest/17",
            SourceBranch = "feature/assistance",
            TargetBranch = "main",
            SourceCommitSha = "abc123",
        };
        await cycles.CreateAsync(
            new ReworkCycleCreateRequest
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = pullRequest,
                WorkItemId = persistedItem.Id,
                BranchName = pullRequest.SourceBranch,
                PullRequestUrl = pullRequest.PullRequestUrl,
                BaseCommitSha = pullRequest.SourceCommitSha,
                FeedbackBundleJson = "[]",
                FeedbackBundleId = "board-diagnostics-assistance-17",
                CorrelationId = "board-diagnostics-cycle-17",
            },
            CancellationToken.None);

        var snapshot = new ManagedBoardItemSnapshot
        {
            WorkSourceEnvironmentKey = "boards",
            Project = "Project One",
            Item = discoveredItem,
        };
        var handler = new GetBoardItemDiagnosticsQueryHandler(
            new SnapshotDiscovery(snapshot),
            new ProfileResolver(),
            new RepositoryStore(),
            workItems,
            cycles);

        var detail = await handler.ExecuteAsync(
            new GetBoardItemDiagnosticsQuery
            {
                WorkSourceEnvironmentKey = "boards",
                ItemId = "42",
            },
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.True(detail.Eligible);
        Assert.Equal(BoardItemMatchResult.Eligible, detail.Match);
        Assert.True(Assert.Single(
            detail.Checks,
            check => check.Code == "pending-assistance-cycle").Passed);
        Assert.Equal(persistedItem.Id, (await cycles.GetPendingForWorkItemAsync(
            persistedItem.Id,
            CancellationToken.None))!.WorkItemId);
        Assert.Null(await cycles.GetPendingForWorkItemAsync(
            discoveredItem.Id,
            CancellationToken.None));
    }

    private sealed class SnapshotDiscovery(ManagedBoardItemSnapshot snapshot)
        : IManagedBoardItemDiscovery
    {
        public Task<ManagedBoardItemDiscoveryPage> ListAsync(
            ManagedBoardItemDiscoveryQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ManagedBoardItemSnapshot?> GetAsync(
            ManagedBoardItemDiscoveryItemQuery query,
            CancellationToken cancellationToken) => Task.FromResult<ManagedBoardItemSnapshot?>(snapshot);
    }

    private sealed class ProfileResolver : IManagedProfileResolver
    {
        private readonly ResolvedWorkSourceEnvironment _environment = new(
            new WorkSourceEnvironmentProfile
            {
                Key = "boards",
                DisplayName = "Boards",
                Project = "Project One",
                Enabled = true,
                TagPrefix = "agent",
            },
            null);

        public Task<ResolvedControllerProfiles?> ResolveForRepositoryAsync(
            string repositoryKey,
            CancellationToken cancellationToken) => Task.FromResult<ResolvedControllerProfiles?>(
                string.Equals(repositoryKey, "orders", StringComparison.OrdinalIgnoreCase)
                    ? new ResolvedControllerProfiles
                    {
                        Repository = new RepositoryProfile { Key = "orders" },
                    }
                    : null);

        public Task<ResolvedWorkSourceEnvironment?> ResolveWorkSourceEnvironmentAsync(
            string? key,
            CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedWorkSourceEnvironment?>(_environment);

        public Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ListWorkSourceEnvironmentsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResolvedWorkSourceEnvironment>>([_environment]);

        public Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ListConfiguredWorkSourceEnvironmentsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResolvedWorkSourceEnvironment>>([_environment]);
    }

    private sealed class RepositoryStore : IRepositoryStore
    {
        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RepositoryProfile>>(
                [new RepositoryProfile { Key = "orders" }]);

        public Task<RepositoryProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> CreateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            string key,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpsertAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
