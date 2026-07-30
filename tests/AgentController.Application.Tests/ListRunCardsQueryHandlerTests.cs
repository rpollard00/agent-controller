using AgentController.Application.Queries;
using AgentController.Application.Abstractions;
using AgentController.Domain;
using Microsoft.Extensions.Options;

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
            runtimeProfileName: "ReeseProjecto LocalWorkspace",
            environmentProviderType: "LocalWorkspace",
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
        var handler = CreateHandler(
            [run],
            [workItem],
            [repository],
            [feedback],
            feedbackSoakDuration: TimeSpan.FromMinutes(17)
        );

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
        Assert.Equal(run.RuntimeProfileName, card.RuntimeProfileName);
        Assert.Equal(run.EnvironmentProviderType, card.EnvironmentProviderType);
        Assert.Equal(run.RunAttempt, card.RunAttempt);
        Assert.Equal(ReworkRequestMode.Revival, card.RequestMode);
        Assert.Equal(ReworkFeedbackStatus.Watching, card.FeedbackStatus);
        Assert.Null(card.CycleNumber);
        Assert.Null(card.ConsumingRunId);
        Assert.Equal("rework.feedback.soaking", card.LastEventType);
        Assert.Contains("3", card.LastEventMessage);
        Assert.Equal(feedback.LastQualifyingCommentAt, card.LastEventAt);
        Assert.Equal(Baseline.AddMinutes(22), card.SoakEligibleAt);
        Assert.Equal(TimeSpan.Zero, card.SoakEligibleAt?.Offset);
        Assert.Equal(feedback.CreatedAt, card.CreatedAt);
        Assert.Equal(feedback.UpdatedAt, card.UpdatedAt);

        var runCard = Assert.Single(cards, item => item.Kind == "run");
        Assert.Equal(run.RuntimeType, runCard.RuntimeType);
        Assert.Equal(run.RuntimeProfileName, runCard.RuntimeProfileName);
        Assert.Equal(run.EnvironmentProviderType, runCard.EnvironmentProviderType);
        Assert.Null(runCard.SoakEligibleAt);
    }

    [Theory]
    [InlineData(ReworkRequestMode.Revival)]
    [InlineData(ReworkRequestMode.Assistance)]
    public async Task ExecuteAsync_ProjectsDeadlineForBothWatchingRequestModes(
        ReworkRequestMode requestMode
    )
    {
        var feedback = new ReworkFeedback
        {
            Id = $"watching-{requestMode}",
            RequestMode = requestMode,
            Status = ReworkFeedbackStatus.Watching,
            LastQualifyingCommentAt = Baseline.ToOffset(TimeSpan.FromHours(-4)),
            CreatedAt = Baseline,
            UpdatedAt = Baseline,
        };
        var handler = CreateHandler(
            feedback: [feedback],
            feedbackSoakDuration: TimeSpan.FromMinutes(23)
        );

        var card = Assert.Single(
            await handler.ExecuteAsync(new ListRunCardsQuery(), CancellationToken.None)
        );

        Assert.Equal(Baseline.AddMinutes(23), card.SoakEligibleAt);
        Assert.Equal(TimeSpan.Zero, card.SoakEligibleAt?.Offset);
    }

    [Theory]
    [InlineData(ReworkFeedbackStatus.Soaked)]
    [InlineData(ReworkFeedbackStatus.Materialized)]
    public async Task ExecuteAsync_OmitsDeadlineForLaterFeedbackStates(
        ReworkFeedbackStatus status
    )
    {
        var feedback = new ReworkFeedback
        {
            Id = $"feedback-{status}",
            Status = status,
            LastQualifyingCommentAt = Baseline,
            CreatedAt = Baseline,
            UpdatedAt = Baseline,
        };
        var handler = CreateHandler(feedback: [feedback]);

        var card = Assert.Single(
            await handler.ExecuteAsync(new ListRunCardsQuery(), CancellationToken.None)
        );

        Assert.Null(card.SoakEligibleAt);
    }

    [Fact]
    public void CreateTrackingCard_OmitsDeadlineForSupersededFeedback()
    {
        var feedback = new ReworkFeedback
        {
            Id = "feedback-superseded",
            Status = ReworkFeedbackStatus.Superseded,
            LastQualifyingCommentAt = Baseline,
            CreatedAt = Baseline,
            UpdatedAt = Baseline,
        };

        var card = RunCardFactory.CreateTrackingCard(
            feedback,
            cycle: null,
            associatedRun: null,
            workItem: null,
            repositoryUrl: null,
            feedbackSoakDuration: TimeSpan.FromMinutes(5)
        );

        Assert.Null(card.SoakEligibleAt);
    }

    [Fact]
    public async Task ExecuteAsync_ProjectsMaterializedAssistanceOntoConsumingRunCard()
    {
        var pullRequest = new PullRequestReference
        {
            EnvironmentKey = "ado-prod",
            RepositoryKey = "repo-1",
            PullRequestId = "42",
            PullRequestUrl = "https://dev.azure.test/repo/pullrequest/42",
            SourceBranch = "refs/heads/contributor/change",
            TargetBranch = "refs/heads/main",
            SourceCommitSha = "abc123",
        };
        var story = new WorkCandidate
        {
            Id = "story-local-1",
            ExternalId = "8042",
            ExternalUrl = "https://dev.azure.test/workitems/8042",
            Title = "Assist PR 42",
            RepoKey = "repo-1",
            Source = "AzureDevOpsBoards",
        };
        var run = CreateRun(
            "run-assistance",
            RunLifecycleState.AgentRunning,
            workItemId: story.Id,
            updatedAt: Baseline.AddMinutes(4)
        );
        var feedback = new ReworkFeedback
        {
            Id = "feedback-assistance",
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = pullRequest,
            PullRequestId = pullRequest.PullRequestId,
            FeedbackBundleId = "bundle-assistance",
            CorrelationId = "correlation-assistance",
            AssistanceStoryWorkItemId = story.Id,
            AssistanceStoryExternalId = story.ExternalId,
            AssistanceStoryUrl = story.ExternalUrl,
            Status = ReworkFeedbackStatus.Materialized,
            CreatedAt = Baseline,
            UpdatedAt = Baseline.AddMinutes(3),
        };
        var cycle = new ReworkCycle
        {
            Id = "cycle-assistance",
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = pullRequest,
            WorkItemId = story.Id,
            CycleNumber = 2,
            FeedbackBundleId = feedback.FeedbackBundleId,
            CorrelationId = feedback.CorrelationId,
            Status = ReworkCycleStatus.Consumed,
            NewRunId = run.RunId,
        };
        var handler = CreateHandler(
            [run],
            [story],
            feedback: [feedback],
            cycles: [cycle]
        );

        var cards = await handler.ExecuteAsync(new ListRunCardsQuery(), CancellationToken.None);

        var card = Assert.Single(cards);
        Assert.Equal("run", card.Kind);
        Assert.Equal(ReworkRequestMode.Assistance, card.RequestMode);
        Assert.Equal(pullRequest, card.PullRequest);
        Assert.Equal(2, card.CycleNumber);
        Assert.Equal(story.Id, card.AssistanceStoryWorkItemId);
        Assert.Equal(story.ExternalId, card.AssistanceStoryExternalId);
        Assert.Equal(story.ExternalUrl, card.AssistanceStoryUrl);
        Assert.Equal(ReworkFeedbackStatus.Materialized, card.FeedbackStatus);
        Assert.Equal(ReworkCycleStatus.Consumed, card.CycleStatus);
        Assert.Equal(run.RunId, card.ConsumingRunId);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesQueuedAssistanceStoryBeforeRunExists()
    {
        var pullRequest = new PullRequestReference
        {
            EnvironmentKey = "ado-prod",
            RepositoryKey = "repo-1",
            PullRequestId = "43",
            PullRequestUrl = "https://dev.azure.test/repo/pullrequest/43",
            SourceBranch = "refs/heads/human/change",
            TargetBranch = "refs/heads/main",
            SourceCommitSha = "def456",
        };
        var story = new WorkCandidate
        {
            Id = "story-local-2",
            ExternalId = "8043",
            ExternalUrl = "https://dev.azure.test/workitems/8043",
            Title = "Assist PR 43",
            RepoKey = "repo-1",
            Source = "AzureDevOpsBoards",
        };
        var feedback = new ReworkFeedback
        {
            Id = "feedback-queued",
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = pullRequest,
            PullRequestId = pullRequest.PullRequestId,
            FeedbackBundleId = "bundle-queued",
            CorrelationId = "correlation-queued",
            AssistanceStoryWorkItemId = story.Id,
            AssistanceStoryExternalId = story.ExternalId,
            AssistanceStoryUrl = story.ExternalUrl,
            Status = ReworkFeedbackStatus.Materialized,
            CreatedAt = Baseline,
            UpdatedAt = Baseline.AddMinutes(2),
        };
        var cycle = new ReworkCycle
        {
            Id = "cycle-queued",
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = pullRequest,
            WorkItemId = story.Id,
            CycleNumber = 1,
            FeedbackBundleId = feedback.FeedbackBundleId,
            CorrelationId = feedback.CorrelationId,
            Status = ReworkCycleStatus.Pending,
        };
        var handler = CreateHandler(
            workItems: [story],
            feedback: [feedback],
            cycles: [cycle]
        );

        var cards = await handler.ExecuteAsync(new ListRunCardsQuery(), CancellationToken.None);

        var card = Assert.Single(cards);
        Assert.Equal("rework-soak", card.Kind);
        Assert.Equal("Assistance story queued", card.Status);
        Assert.Equal(ReworkRequestMode.Assistance, card.RequestMode);
        Assert.Equal(ReworkFeedbackStatus.Materialized, card.FeedbackStatus);
        Assert.Equal(ReworkCycleStatus.Pending, card.CycleStatus);
        Assert.Equal(1, card.CycleNumber);
        Assert.Equal(story.ExternalId, card.AssistanceStoryExternalId);
        Assert.Null(card.ConsumingRunId);
        Assert.Equal("assistance.story.queued", card.LastEventType);
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
        string? runtimeProfileName = null,
        string? environmentProviderType = null,
        int runAttempt = 1
    ) =>
        new()
        {
            RunId = id,
            WorkItemId = workItemId,
            Status = status,
            RuntimeType = runtimeType,
            RuntimeProfileName = runtimeProfileName,
            EnvironmentProviderType = environmentProviderType,
            RunAttempt = runAttempt,
            CreatedAt = createdAt ?? Baseline,
            UpdatedAt = updatedAt ?? Baseline,
        };

    private static ListRunCardsQueryHandler CreateHandler(
        IEnumerable<AgentRunHandle>? runs = null,
        IEnumerable<WorkCandidate>? workItems = null,
        IEnumerable<RepositoryProfile>? repositories = null,
        IEnumerable<ReworkFeedback>? feedback = null,
        IEnumerable<LifecycleEvent>? events = null,
        IEnumerable<ReworkCycle>? cycles = null,
        TimeSpan? feedbackSoakDuration = null
    ) =>
        new(
            new StubAgentRunStore(runs ?? []),
            new StubWorkItemStore(workItems ?? []),
            new StubLifecycleEventStore(events ?? []),
            new StubRepositoryStore(repositories ?? []),
            new StubReworkFeedbackStore(feedback ?? []),
            new StubReworkCycleStore(cycles ?? []),
            Options.Create(
                new FeedbackSoakOptionsView
                {
                    SoakDuration = feedbackSoakDuration ?? TimeSpan.FromMinutes(5),
                }
            )
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
        ) =>
            Task.FromResult<IReadOnlyList<ReworkFeedback>>(
                _feedback.Where(item => item.Status == ReworkFeedbackStatus.Watching).ToList()
            );

        public Task<IReadOnlyList<ReworkFeedback>> GetTrackedAsync(
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<ReworkFeedback>>(
                _feedback.Where(item => item.Status != ReworkFeedbackStatus.Superseded).ToList()
            );

        public Task<ReworkFeedback> UpsertAsync(
            ReworkFeedbackUpsertRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ReworkFeedback> UpsertAsync(
            string? originatingRunId,
            string pullRequestId,
            string feedbackBundleId,
            string feedbackBundleJson,
            int threadCount,
            DateTimeOffset firstQualifyingCommentAt,
            DateTimeOffset lastQualifyingCommentAt,
            ReworkFeedbackStatus status,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ReworkFeedback?> GetByCorrelationIdAsync(
            string correlationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ReworkFeedback> RecordAssistanceStoryAsync(
            string id,
            AssistanceStoryReceipt receipt,
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

    private sealed class StubReworkCycleStore(IEnumerable<ReworkCycle> cycles)
        : IReworkCycleStore
    {
        private readonly IReadOnlyList<ReworkCycle> _cycles = cycles.ToList();

        public Task<ReworkCycle?> GetPendingForWorkItemAsync(
            string workItemId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                _cycles.FirstOrDefault(cycle =>
                    cycle.WorkItemId == workItemId
                    && cycle.Status == ReworkCycleStatus.Pending
                )
            );

        public Task<ReworkCycle?> GetConsumedByRunIdAsync(
            string runId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                _cycles.FirstOrDefault(cycle =>
                    cycle.NewRunId == runId
                    && cycle.Status == ReworkCycleStatus.Consumed
                )
            );

        public Task<IReadOnlyList<ReworkCycle>> ListPendingAsync(
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<ReworkCycle>>(
                _cycles.Where(cycle => cycle.Status == ReworkCycleStatus.Pending).ToList()
            );

        public Task<IReadOnlyList<ReworkCycle>> ListConsumedAsync(
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<ReworkCycle>>(
                _cycles.Where(cycle => cycle.Status == ReworkCycleStatus.Consumed).ToList()
            );

        public Task<bool> ExistsByFeedbackBundleIdAsync(
            string feedbackBundleId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(
            ReworkRequestMode requestMode,
            PullRequestReference pullRequest,
            string feedbackBundleId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ReworkCycle?> GetByCorrelationIdAsync(
            string correlationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ReworkCycle> CreateAsync(
            ReworkCycleCreateRequest request,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ReworkCycle> CreateAsync(
            string workItemId,
            int cycleNumber,
            string? priorRunId,
            string branchName,
            string pullRequestUrl,
            string baseCommitSha,
            string feedbackBundleJson,
            string feedbackBundleId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task MarkConsumedAsync(
            string id,
            string newRunId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<int> GetMaxCycleNumberAsync(
            string workItemId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<int> GetMaxAssistanceCycleNumberAsync(
            PullRequestReference pullRequest,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task MarkReactivatedAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
