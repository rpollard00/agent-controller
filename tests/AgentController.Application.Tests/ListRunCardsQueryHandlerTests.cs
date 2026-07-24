using AgentController.Application.Queries;
using AgentController.Domain;

namespace AgentController.Application.Tests;

public sealed class ListRunCardsQueryHandlerTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 7, 24, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ClassifyCategory_ClassifiesEveryLifecycleState()
    {
        var expected = new Dictionary<RunLifecycleState, string>
        {
            [RunLifecycleState.Queued] = "pending",
            [RunLifecycleState.Claimed] = "pending",
            [RunLifecycleState.EnvironmentProvisioning] = "pending",
            [RunLifecycleState.EnvironmentReady] = "pending",
            [RunLifecycleState.RepositoryCloning] = "pending",
            [RunLifecycleState.RepositoryReady] = "pending",
            [RunLifecycleState.ContextInjected] = "pending",
            [RunLifecycleState.AgentStarting] = "executing",
            [RunLifecycleState.AgentRunning] = "executing",
            [RunLifecycleState.AwaitingResult] = "executing",
            [RunLifecycleState.ResultReceived] = "completed",
            [RunLifecycleState.PrOpened] = "completed",
            [RunLifecycleState.BranchPushed] = "completed",
            [RunLifecycleState.NeedsHuman] = "attention",
            [RunLifecycleState.Completed] = "completed",
            [RunLifecycleState.Failed] = "attention",
            [RunLifecycleState.Cancelled] = "attention",
            [RunLifecycleState.CleanupPending] = "completed",
            [RunLifecycleState.CleanedUp] = "completed",
        };

        Assert.Equal(Enum.GetValues<RunLifecycleState>().Length, expected.Count);
        foreach (var (state, category) in expected)
        {
            Assert.Equal(category, ListRunCardsQueryHandler.ClassifyCategory(state));
        }
    }

    [Fact]
    public async Task ExecuteAsync_IncludesEnrichedReworkSoakCard()
    {
        var run = CreateRun(
            "run-1",
            RunLifecycleState.PrOpened,
            workItemId: "work-1",
            updatedAt: Baseline,
            runtimeType: "PiMateria",
            runAttempt: 2
        );
        var workItem = new WorkCandidate
        {
            Id = "work-1",
            Title = "Address review feedback",
            ExternalUrl = "https://work.example/items/1",
            Source = "AzureDevOpsBoards",
            RepoKey = "repo-1",
        };
        var repository = new RepositoryProfile
        {
            Key = "repo-1",
            CloneUrl = "git@example.test:org/repo.git",
            WebUrl = "https://repos.example/org/repo",
        };
        var feedback = new ReworkFeedback
        {
            Id = "soak-1",
            OriginatingRunId = run.RunId,
            ThreadCount = 3,
            Status = ReworkFeedbackStatus.Watching,
            LastQualifyingCommentAt = Baseline.AddMinutes(5),
            CreatedAt = Baseline.AddMinutes(1),
            UpdatedAt = Baseline.AddMinutes(4),
        };
        var handler = CreateHandler([run], [workItem], [repository], [feedback]);

        var cards = await handler.ExecuteAsync(new ListRunCardsQuery(), CancellationToken.None);

        var card = Assert.Single(cards, item => item.Kind == "rework-soak");
        Assert.Equal("soak-1", card.Id);
        Assert.Equal("Rework feedback soaking", card.Status);
        Assert.Equal("pending", card.Category);
        Assert.Equal(workItem.Title, card.WorkItemTitle);
        Assert.Equal(workItem.ExternalUrl, card.WorkItemUrl);
        Assert.Equal(workItem.Source, card.WorkItemSource);
        Assert.Equal(workItem.RepoKey, card.RepoKey);
        Assert.Equal(repository.WebUrl, card.RepositoryUrl);
        Assert.Equal(run.RuntimeType, card.RuntimeType);
        Assert.Equal(run.RunAttempt, card.RunAttempt);
        Assert.Equal("rework.feedback.soaking", card.LastEventType);
        Assert.Contains("3", card.LastEventMessage);
        Assert.Equal(feedback.LastQualifyingCommentAt, card.LastEventAt);
        Assert.Equal(feedback.CreatedAt, card.CreatedAt);
        Assert.Equal(feedback.UpdatedAt, card.UpdatedAt);
    }

    [Theory]
    [InlineData(
        "https://repos.example/org/repo",
        "https://clone.example/org/repo.git",
        "https://repos.example/org/repo"
    )]
    [InlineData(null, "https://clone.example/org/repo.git", "https://clone.example/org/repo.git")]
    [InlineData(null, "HTTP://clone.example/org/repo.git", "HTTP://clone.example/org/repo.git")]
    [InlineData(null, "git@example.test:org/repo.git", null)]
    [InlineData("  ", "https://clone.example/org/repo.git", "https://clone.example/org/repo.git")]
    public async Task ExecuteAsync_UsesRepositoryUrlFallbackChain(
        string? webUrl,
        string cloneUrl,
        string? expectedUrl
    )
    {
        var run = CreateRun(
            "run-1",
            RunLifecycleState.Queued,
            workItemId: "work-1",
            updatedAt: Baseline
        );
        var workItem = new WorkCandidate
        {
            Id = "work-1",
            Title = "Work",
            RepoKey = "repo-1",
        };
        var repository = new RepositoryProfile
        {
            Key = "repo-1",
            WebUrl = webUrl,
            CloneUrl = cloneUrl,
        };
        var handler = CreateHandler([run], [workItem], [repository]);

        var cards = await handler.ExecuteAsync(new ListRunCardsQuery(), CancellationToken.None);

        Assert.Equal(expectedUrl, Assert.Single(cards).RepositoryUrl);
    }

    [Fact]
    public async Task ExecuteAsync_OrdersMergedCardsByLatestEventOrUpdatedTime()
    {
        var noEvent = CreateRun(
            "run-no-event",
            RunLifecycleState.Queued,
            updatedAt: Baseline.AddMinutes(2)
        );
        var oldEvent = CreateRun(
            "run-old-event",
            RunLifecycleState.AgentRunning,
            updatedAt: Baseline.AddHours(1)
        );
        var newEvent = CreateRun(
            "run-new-event",
            RunLifecycleState.AgentRunning,
            updatedAt: Baseline
        );
        var feedback = new ReworkFeedback
        {
            Id = "soak-newest",
            OriginatingRunId = noEvent.RunId,
            ThreadCount = 1,
            LastQualifyingCommentAt = Baseline.AddMinutes(4),
            CreatedAt = Baseline,
            UpdatedAt = Baseline,
        };
        var events = new[]
        {
            new LifecycleEvent
            {
                Id = "event-old",
                RunId = oldEvent.RunId,
                EventType = "runtime.old",
                CreatedAt = Baseline.AddMinutes(1),
            },
            new LifecycleEvent
            {
                Id = "event-new",
                RunId = newEvent.RunId,
                EventType = "runtime.new",
                Message = "Latest run event",
                CreatedAt = Baseline.AddMinutes(3),
            },
        };
        var handler = CreateHandler(
            [noEvent, oldEvent, newEvent],
            feedback: [feedback],
            events: events
        );

        var cards = await handler.ExecuteAsync(new ListRunCardsQuery(), CancellationToken.None);

        Assert.Equal(
            ["soak-newest", "run-new-event", "run-no-event", "run-old-event"],
            cards.Select(card => card.Id)
        );
        var newEventCard = Assert.Single(cards, card => card.Id == newEvent.RunId);
        Assert.Equal("runtime.new", newEventCard.LastEventType);
        Assert.Equal("Latest run event", newEventCard.LastEventMessage);
        Assert.Equal(Baseline.AddMinutes(3), newEventCard.LastEventAt);
    }

    [Fact]
    public async Task ExecuteAsync_CapsMergedCardsAtTwoHundred()
    {
        var runs = Enumerable
            .Range(0, 200)
            .Select(index =>
                CreateRun(
                    $"run-{index:D3}",
                    RunLifecycleState.Queued,
                    updatedAt: Baseline.AddSeconds(index)
                )
            )
            .ToArray();
        var feedback = new ReworkFeedback
        {
            Id = "soak-newest",
            OriginatingRunId = runs[0].RunId,
            ThreadCount = 1,
            LastQualifyingCommentAt = Baseline.AddHours(1),
            CreatedAt = Baseline,
            UpdatedAt = Baseline,
        };
        var handler = CreateHandler(runs, feedback: [feedback]);

        var cards = await handler.ExecuteAsync(new ListRunCardsQuery(), CancellationToken.None);

        Assert.Equal(200, cards.Count);
        Assert.Equal(feedback.Id, cards[0].Id);
        Assert.DoesNotContain(cards, card => card.Kind == "run" && card.Id == runs[0].RunId);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesLatestActivityFromBeyondFirstCreatedDatePage()
    {
        var runs = Enumerable
            .Range(0, 201)
            .Select(index =>
                CreateRun(
                    $"run-{index:D3}",
                    RunLifecycleState.Queued,
                    createdAt: Baseline.AddSeconds(index),
                    updatedAt: Baseline
                )
            )
            .ToArray();
        var oldestCreatedRun = runs[0];
        var newestEvent = new LifecycleEvent
        {
            Id = "event-newest",
            RunId = oldestCreatedRun.RunId,
            EventType = "runtime.newest",
            CreatedAt = Baseline.AddHours(1),
        };
        var handler = CreateHandler(runs, events: [newestEvent]);

        var cards = await handler.ExecuteAsync(new ListRunCardsQuery(), CancellationToken.None);

        Assert.Equal(200, cards.Count);
        Assert.Equal(oldestCreatedRun.RunId, cards[0].Id);
        Assert.Equal(newestEvent.CreatedAt, cards[0].LastEventAt);
    }

    private static AgentRunHandle CreateRun(
        string id,
        RunLifecycleState status,
        string? workItemId = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        string? runtimeType = null,
        int runAttempt = 1
    ) =>
        new()
        {
            RunId = id,
            WorkItemId = workItemId,
            Status = status,
            RuntimeType = runtimeType,
            RunAttempt = runAttempt,
            CreatedAt = createdAt ?? Baseline,
            UpdatedAt = updatedAt ?? Baseline,
        };

    private static ListRunCardsQueryHandler CreateHandler(
        IEnumerable<AgentRunHandle>? runs = null,
        IEnumerable<WorkCandidate>? workItems = null,
        IEnumerable<RepositoryProfile>? repositories = null,
        IEnumerable<ReworkFeedback>? feedback = null,
        IEnumerable<LifecycleEvent>? events = null
    ) =>
        new(
            new StubAgentRunStore(runs ?? []),
            new StubWorkItemStore(workItems ?? []),
            new StubLifecycleEventStore(events ?? []),
            new StubRepositoryStore(repositories ?? []),
            new StubReworkFeedbackStore(feedback ?? [])
        );

    private sealed class StubAgentRunStore(IEnumerable<AgentRunHandle> runs) : IAgentRunStore
    {
        private readonly IReadOnlyList<AgentRunHandle> _runs = runs.ToList();

        public Task<AgentRunHandle?> GetByIdAsync(string runId, CancellationToken cancellationToken) =>
            Task.FromResult(_runs.SingleOrDefault(run => run.RunId == runId));

        public Task<IReadOnlyList<AgentRunHandle>> ListAsync(
            ListRunsQuery query,
            CancellationToken cancellationToken
        )
        {
            IEnumerable<AgentRunHandle> result = _runs.OrderByDescending(run => run.CreatedAt);
            if (query.Status is not null)
            {
                result = result.Where(run => run.Status == query.Status);
            }
            if (!string.IsNullOrWhiteSpace(query.WorkItemId))
            {
                result = result.Where(run => run.WorkItemId == query.WorkItemId);
            }
            if (query.Offset > 0)
            {
                result = result.Skip(query.Offset);
            }
            if (query.MaxResults > 0)
            {
                result = result.Take(query.MaxResults);
            }

            return Task.FromResult<IReadOnlyList<AgentRunHandle>>(result.ToList());
        }

        public Task<AgentRunHandle> CreateAsync(
            CreateRunRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task UpdateStatusAsync(
            string runId,
            RunLifecycleState status,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task UpdateRuntimeFieldsAsync(
            string runId,
            RuntimeFieldUpdate update,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<AgentRunHandle>> FindStaleAsync(
            TimeSpan staleTimeout,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<int> CountActiveAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AgentRunHandle?> FindLatestRunByWorkItemAsync(
            string workItemId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<AgentRunHandle>> FindRunsForFeedbackAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class StubWorkItemStore(IEnumerable<WorkCandidate> workItems) : IWorkItemStore
    {
        private readonly Dictionary<string, WorkCandidate> _workItems = workItems.ToDictionary(
            workItem => workItem.Id,
            StringComparer.Ordinal
        );

        public Task<WorkCandidate?> GetByIdAsync(string id, CancellationToken cancellationToken)
        {
            _workItems.TryGetValue(id, out var workItem);
            return Task.FromResult(workItem);
        }

        public Task<WorkCandidate> CreateAsync(
            CreateWorkItemRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkCandidate>> ListAsync(
            ListWorkItemsQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
            WorkQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ClaimResult> TryClaimAsync(
            string workItemId,
            ClaimRequest claim,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task UpdateStatusAsync(
            string workItemId,
            string status,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<WorkCandidate> UpsertAsync(
            WorkCandidate candidate,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class StubLifecycleEventStore(IEnumerable<LifecycleEvent> events)
        : ILifecycleEventStore
    {
        private readonly IReadOnlyList<LifecycleEvent> _events = events.ToList();

        public Task<IReadOnlyList<LifecycleEvent>> ListByRunIdAsync(
            string runId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<LifecycleEvent>>(
                _events.Where(lifecycleEvent => lifecycleEvent.RunId == runId).ToList()
            );

        public Task AppendAsync(LifecycleEvent evt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExistsByEventIdAsync(
            string runId,
            string eventId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class StubRepositoryStore(IEnumerable<RepositoryProfile> repositories)
        : IRepositoryStore
    {
        private readonly Dictionary<string, RepositoryProfile> _repositories =
            repositories.ToDictionary(repository => repository.Key, StringComparer.Ordinal);

        public Task<RepositoryProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            _repositories.TryGetValue(key, out var repository);
            return Task.FromResult(repository);
        }

        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> CreateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpsertAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class StubReworkFeedbackStore(IEnumerable<ReworkFeedback> feedback)
        : IReworkFeedbackStore
    {
        private readonly IReadOnlyList<ReworkFeedback> _feedback = feedback.ToList();

        public Task<IReadOnlyList<ReworkFeedback>> GetWatchingAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult(_feedback);

        public Task<ReworkFeedback> UpsertAsync(
            string originatingRunId,
            string pullRequestId,
            string feedbackBundleId,
            string feedbackBundleJson,
            int threadCount,
            DateTimeOffset firstQualifyingCommentAt,
            DateTimeOffset lastQualifyingCommentAt,
            ReworkFeedbackStatus status,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ReworkFeedback?> MarkSoakedAsync(
            string id,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task MarkSupersededAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReworkFeedback>> GetSoakedAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task MarkMaterializedAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
