using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Domain;

namespace AgentController.Application.Tests;

public sealed class BoardItemDiagnosticsQueryHandlerTests
{
    [Theory]
    [InlineData("agent-ready", "", "New", null, BoardItemMatchResult.MissingTags)]
    [InlineData("agent-ready;repo:typo", "typo", "New", null, BoardItemMatchResult.MissingTags)]
    [InlineData("agent-ready;repo:orders", "orders", "Completed", null, BoardItemMatchResult.Excluded)]
    [InlineData("agent-ready;agent-active;repo:orders", "orders", "New", null, BoardItemMatchResult.Excluded)]
    [InlineData("agent-ready-rework;repo:orders", "orders", "New", null, BoardItemMatchResult.Excluded)]
    [InlineData("agent-ready-rework;repo:orders", "orders", "New", ReworkRequestMode.Revival, BoardItemMatchResult.Excluded)]
    public async Task Detail_ClassifiesMissingAndExcludedItems(
        string tags,
        string repoKey,
        string state,
        ReworkRequestMode? cycleMode,
        BoardItemMatchResult expected)
    {
        var fixture = Fixture.Create(tags, repoKey, state, "agent", cycleMode);

        var detail = await fixture.DetailHandler.ExecuteAsync(
            new GetBoardItemDiagnosticsQuery
            {
                WorkSourceEnvironmentKey = "boards",
                ItemId = "42",
            },
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(expected, detail.Match);
        Assert.False(detail.Eligible);
        Assert.Equal(tags.Split(';'), detail.Tags);
        Assert.Equal(tags.Split(';'), fixture.Snapshot.Item.Tags); // diagnostics never mutate provider data
    }

    [Fact]
    public async Task Detail_DisabledSourceIsExcluded()
    {
        var fixture = Fixture.Create(
            "agent-ready;repo:orders", "orders", "New", "agent", null, sourceEnabled: false);

        var detail = await fixture.DetailHandler.ExecuteAsync(
            new GetBoardItemDiagnosticsQuery
            {
                WorkSourceEnvironmentKey = "boards",
                ItemId = "42",
            },
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(BoardItemMatchResult.Excluded, detail.Match);
        Assert.False(Assert.Single(detail.Checks, check => check.Code == "source-enabled").Passed);
    }

    [Fact]
    public async Task Detail_UsesCustomPrefixCaseSemanticsAndPendingAssistanceCycle()
    {
        var fixture = Fixture.Create(
            "TEAM-READY-REWORK;REPO:ORDERS",
            "ORDERS",
            "New",
            "team",
            ReworkRequestMode.Assistance);

        var detail = await fixture.DetailHandler.ExecuteAsync(
            new GetBoardItemDiagnosticsQuery
            {
                WorkSourceEnvironmentKey = "BOARDS",
                ItemId = "42",
            },
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.True(detail.Eligible);
        Assert.Equal(BoardItemMatchResult.Eligible, detail.Match);
        Assert.Equal("team-ready", detail.RecognizedReadyTag);
        Assert.Equal("team-ready-rework", detail.RecognizedReadyReworkTag);
        Assert.Contains("repo:orders", detail.RecognizedRepositoryTags);
        Assert.All(detail.Checks, check => Assert.True(check.Passed, check.Reason));
    }

    [Fact]
    public async Task Detail_ResolvesProviderIdentityBeforeLookingUpAssistanceCycle()
    {
        var fixture = Fixture.Create(
            "agent-ready-rework;repo:orders",
            "orders",
            "New",
            "agent",
            ReworkRequestMode.Assistance,
            persistedWorkItemId: "wi_42_persisted-guid");

        var detail = await fixture.DetailHandler.ExecuteAsync(
            new GetBoardItemDiagnosticsQuery
            {
                WorkSourceEnvironmentKey = "boards",
                ItemId = "42",
            },
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.True(detail.Eligible);
        Assert.Equal("wi_42", fixture.Snapshot.Item.Id);
        Assert.Equal("wi_42_persisted-guid", fixture.Cycles.LastRequestedWorkItemId);
    }

    [Fact]
    public async Task List_ProjectsDiscoveryMetadataAndResolvedRepository()
    {
        var fixture = Fixture.Create("agent-ready;repo:orders", "orders", "New", "agent", null);

        var page = await fixture.ListHandler.ExecuteAsync(
            new ListBoardItemDiagnosticsQuery { Page = 2, PageSize = 20, IncludeTerminal = true },
            CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("42", item.Id);
        Assert.Equal("Investigate pickup", item.Title);
        Assert.Equal("https://boards.test/42", item.Url);
        Assert.Equal("Project One", item.Project);
        Assert.Equal("boards", item.WorkSourceEnvironmentKey);
        Assert.Equal("orders", item.RepositoryKey);
        Assert.Equal(BoardItemMatchResult.Eligible, item.Match);
        Assert.Equal(2, page.Page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(81, page.Total);
        Assert.True(fixture.Discovery.LastListQuery!.IncludeTerminal);
    }

    private sealed class Fixture
    {
        private Fixture(
            SnapshotDiscovery discovery,
            ManagedBoardItemSnapshot snapshot,
            ListBoardItemDiagnosticsQueryHandler listHandler,
            GetBoardItemDiagnosticsQueryHandler detailHandler,
            CycleStore cycles)
        {
            Discovery = discovery;
            Snapshot = snapshot;
            ListHandler = listHandler;
            DetailHandler = detailHandler;
            Cycles = cycles;
        }

        public SnapshotDiscovery Discovery { get; }
        public ManagedBoardItemSnapshot Snapshot { get; }
        public ListBoardItemDiagnosticsQueryHandler ListHandler { get; }
        public GetBoardItemDiagnosticsQueryHandler DetailHandler { get; }
        public CycleStore Cycles { get; }

        public static Fixture Create(
            string tags,
            string repoKey,
            string state,
            string tagPrefix,
            ReworkRequestMode? cycleMode,
            bool sourceEnabled = true,
            string persistedWorkItemId = "wi_42_persisted")
        {
            var snapshot = new ManagedBoardItemSnapshot
            {
                WorkSourceEnvironmentKey = "boards",
                Project = "Project One",
                Item = new WorkCandidate
                {
                    Id = "wi_42",
                    ExternalId = "42",
                    ExternalUrl = "https://boards.test/42",
                    Source = "AzureDevOpsBoards",
                    Title = "Investigate pickup",
                    RepoKey = repoKey,
                    Status = state,
                    Tags = tags.Split(';'),
                },
            };
            var discovery = new SnapshotDiscovery(snapshot);
            var resolver = new ProfileResolver(tagPrefix, sourceEnabled);
            var repositories = new RepositoryStore();
            var workItems = new WorkItemStore(new WorkCandidate
            {
                Id = persistedWorkItemId,
                Source = snapshot.Item.Source,
                ExternalId = snapshot.Item.ExternalId,
            });
            var cycles = new CycleStore(cycleMode is null ? null : new ReworkCycle
            {
                WorkItemId = persistedWorkItemId,
                RequestMode = cycleMode.Value,
            });
            return new Fixture(
                discovery,
                snapshot,
                new ListBoardItemDiagnosticsQueryHandler(
                    discovery, resolver, repositories, workItems, cycles),
                new GetBoardItemDiagnosticsQueryHandler(
                    discovery, resolver, repositories, workItems, cycles),
                cycles);
        }
    }

    private sealed class SnapshotDiscovery(ManagedBoardItemSnapshot snapshot) : IManagedBoardItemDiscovery
    {
        public ManagedBoardItemDiscoveryQuery? LastListQuery { get; private set; }

        public Task<ManagedBoardItemDiscoveryPage> ListAsync(
            ManagedBoardItemDiscoveryQuery query,
            CancellationToken cancellationToken)
        {
            LastListQuery = query;
            return Task.FromResult(new ManagedBoardItemDiscoveryPage
            {
                Items = [snapshot], Page = query.Page, PageSize = query.PageSize, Total = 81,
            });
        }

        public Task<ManagedBoardItemSnapshot?> GetAsync(
            ManagedBoardItemDiscoveryItemQuery query,
            CancellationToken cancellationToken) => Task.FromResult<ManagedBoardItemSnapshot?>(
                string.Equals(query.ItemId, snapshot.Item.ExternalId, StringComparison.OrdinalIgnoreCase)
                    ? snapshot
                    : null);
    }

    private sealed class ProfileResolver(string tagPrefix, bool sourceEnabled) : IManagedProfileResolver
    {
        private readonly ResolvedWorkSourceEnvironment _environment = new(
            new WorkSourceEnvironmentProfile
            {
                Key = "boards", DisplayName = "Boards", Project = "Project One",
                Enabled = sourceEnabled, TagPrefix = tagPrefix,
            }, null);

        public Task<ResolvedControllerProfiles?> ResolveForRepositoryAsync(string repositoryKey, CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedControllerProfiles?>(
                string.Equals(repositoryKey.Trim(), "orders", StringComparison.OrdinalIgnoreCase)
                    ? new ResolvedControllerProfiles { Repository = new RepositoryProfile { Key = "orders" } }
                    : null);
        public Task<ResolvedWorkSourceEnvironment?> ResolveWorkSourceEnvironmentAsync(string? key, CancellationToken cancellationToken) => Task.FromResult<ResolvedWorkSourceEnvironment?>(_environment);
        public Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ListWorkSourceEnvironmentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResolvedWorkSourceEnvironment>>([_environment]);
        public Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ListConfiguredWorkSourceEnvironmentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ResolvedWorkSourceEnvironment>>([_environment]);
    }

    private sealed class RepositoryStore : IRepositoryStore
    {
        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RepositoryProfile>>([new RepositoryProfile { Key = "orders" }]);
        public Task<RepositoryProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CreateAsync(RepositoryProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(RepositoryProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertAsync(RepositoryProfile profile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class WorkItemStore(WorkCandidate persistedItem) : IWorkItemStore
    {
        public Task<WorkCandidate?> GetByExternalIdentityAsync(
            string source,
            string externalId,
            CancellationToken cancellationToken) => Task.FromResult<WorkCandidate?>(
                string.Equals(source, persistedItem.Source, StringComparison.Ordinal)
                && string.Equals(externalId, persistedItem.ExternalId, StringComparison.Ordinal)
                    ? persistedItem
                    : null);
        public Task<WorkCandidate> CreateAsync(CreateWorkItemRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkCandidate>> ListAsync(ListWorkItemsQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkCandidate?> GetByIdAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(WorkQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClaimResult> TryClaimAsync(string workItemId, ClaimRequest claim, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateStatusAsync(string workItemId, string status, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkCandidate> UpsertAsync(WorkCandidate candidate, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CycleStore(ReworkCycle? cycle) : IReworkCycleStore
    {
        public string? LastRequestedWorkItemId { get; private set; }

        public Task<ReworkCycle?> GetPendingForWorkItemAsync(string workItemId, CancellationToken cancellationToken)
        {
            LastRequestedWorkItemId = workItemId;
            return Task.FromResult(cycle?.WorkItemId == workItemId ? cycle : null);
        }
        public Task<ReworkCycle?> GetConsumedByRunIdAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ExistsByFeedbackBundleIdAsync(string feedbackBundleId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(ReworkRequestMode requestMode, PullRequestReference pullRequest, string feedbackBundleId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReworkCycle?> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReworkCycle> CreateAsync(ReworkCycleCreateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReworkCycle> CreateAsync(string workItemId, int cycleNumber, string? priorRunId, string branchName, string pullRequestUrl, string baseCommitSha, string feedbackBundleJson, string feedbackBundleId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkConsumedAsync(string id, string newRunId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReworkCycle>> ListPendingAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReworkCycle>> ListConsumedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> GetMaxCycleNumberAsync(string workItemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> GetMaxAssistanceCycleNumberAsync(PullRequestReference pullRequest, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkReactivatedAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
