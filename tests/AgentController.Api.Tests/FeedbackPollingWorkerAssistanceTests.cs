using System.Security.Cryptography;
using System.Text;
using AgentController.Api;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Api.Tests;

public sealed class FeedbackPollingWorkerAssistanceTests
{
    private static readonly PullRequestReference PullRequest = new()
    {
        EnvironmentKey = "ado-main",
        RepositoryKey = "project/repository",
        PullRequestId = "42",
        PullRequestUrl =
            "https://dev.azure.com/example/project/_git/repository/pullrequest/42",
        SourceBranch = "refs/heads/feature/human-pr",
        TargetBranch = "refs/heads/main",
        SourceCommitSha = "abc123",
    };

    [Fact]
    public async Task AssistanceMarkerWithoutOriginatingRun_StartsZeroCommentSoak()
    {
        await using var harness = await CreateHarnessAsync(threads: []);
        var before = DateTimeOffset.UtcNow;

        await harness.Worker.RunPollCycleForTestingAsync();

        await using var scope = harness.Provider.CreateAsyncScope();
        var feedbackStore = scope.ServiceProvider.GetRequiredService<IReworkFeedbackStore>();
        var watching = await feedbackStore.GetWatchingAsync(CancellationToken.None);
        var row = Assert.Single(watching);

        Assert.Equal(ReworkRequestMode.Assistance, row.RequestMode);
        Assert.Equal(PullRequest.CanonicalKey, row.PullRequest.CanonicalKey);
        Assert.Null(row.OriginatingRunId);
        Assert.Equal(0, row.ThreadCount);
        Assert.Equal("[]", row.FeedbackBundleJson);
        Assert.InRange(row.LastQualifyingCommentAt, before, DateTimeOffset.UtcNow);
        Assert.False(string.IsNullOrWhiteSpace(row.CorrelationId));
    }

    [Fact]
    public async Task SoakedAssistance_MaterializesFreshReadyReworkStoryAndPendingCycle()
    {
        await using var harness = await CreateHarnessAsync(threads: []);
        var oldObservation = DateTimeOffset.UtcNow.AddMinutes(-10);
        var emptyBundleId = ComputeBundleId([]);
        var correlationId = ComputeCorrelationId(PullRequest, emptyBundleId);

        await harness.SeedFeedbackAsync(
            new ReworkFeedbackUpsertRequest
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = PullRequest,
                FeedbackBundleId = emptyBundleId,
                FeedbackBundleJson = "[]",
                ThreadCount = 0,
                FirstQualifyingCommentAt = oldObservation,
                LastQualifyingCommentAt = oldObservation,
                CorrelationId = correlationId,
            }
        );

        await harness.Worker.RunPollCycleForTestingAsync();
        await harness.Worker.RunPollCycleForTestingAsync();

        await using var scope = harness.Provider.CreateAsyncScope();
        var feedbackStore = scope.ServiceProvider.GetRequiredService<IReworkFeedbackStore>();
        Assert.Empty(await feedbackStore.GetWatchingAsync(CancellationToken.None));
        Assert.Empty(await feedbackStore.GetSoakedAsync(CancellationToken.None));

        var feedback = await feedbackStore.GetByCorrelationIdAsync(
            correlationId,
            CancellationToken.None
        );
        Assert.NotNull(feedback);
        Assert.Equal(ReworkFeedbackStatus.Materialized, feedback.Status);
        Assert.Equal(oldObservation, feedback.LastQualifyingCommentAt);
        Assert.NotNull(feedback.AssistanceStoryWorkItemId);
        Assert.NotNull(feedback.AssistanceStoryExternalId);

        var cycleStore = scope.ServiceProvider.GetRequiredService<IReworkCycleStore>();
        var cycle = Assert.Single(await cycleStore.ListPendingAsync(CancellationToken.None));
        Assert.Equal(ReworkRequestMode.Assistance, cycle.RequestMode);
        Assert.Equal(feedback.AssistanceStoryWorkItemId, cycle.WorkItemId);
        Assert.Equal(1, cycle.CycleNumber);
        Assert.Null(cycle.ReactivatedAt);

        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        var story = await workItemStore.GetByIdAsync(
            cycle.WorkItemId,
            CancellationToken.None
        );
        Assert.NotNull(story);
        Assert.Contains("agent-ready-rework", story.Tags);
        Assert.Contains(
            AssistanceStoryMaterializer.BuildCorrelationTag(correlationId),
            story.Tags
        );
        Assert.Contains("Do not create or open another", story.Description);

        var comment = Assert.Single(harness.Comments.Requests);
        Assert.Equal(AssistanceLifecycleCommentKind.Queued, comment.Kind);
        Assert.Equal(story.ExternalId, comment.AssistanceStoryId);
    }

    [Fact]
    public async Task FailureAfterCycleCreation_RetriesWithoutCreatingDuplicateStory()
    {
        var workSource = new FailOnceReadyWorkSource();
        await using var harness = await CreateHarnessAsync(
            threads: [],
            workSource: workSource
        );
        var oldObservation = DateTimeOffset.UtcNow.AddMinutes(-10);
        var emptyBundleId = ComputeBundleId([]);
        var correlationId = ComputeCorrelationId(PullRequest, emptyBundleId);
        await harness.SeedFeedbackAsync(
            new ReworkFeedbackUpsertRequest
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = PullRequest,
                FeedbackBundleId = emptyBundleId,
                FeedbackBundleJson = "[]",
                ThreadCount = 0,
                FirstQualifyingCommentAt = oldObservation,
                LastQualifyingCommentAt = oldObservation,
                CorrelationId = correlationId,
            }
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Worker.RunPollCycleForTestingAsync()
        );

        await using (var failedScope = harness.Provider.CreateAsyncScope())
        {
            var cycleStore = failedScope.ServiceProvider.GetRequiredService<IReworkCycleStore>();
            var cycle = Assert.Single(
                await cycleStore.ListPendingAsync(CancellationToken.None)
            );
            var workItemStore = failedScope.ServiceProvider.GetRequiredService<IWorkItemStore>();
            var story = await workItemStore.GetByIdAsync(
                cycle.WorkItemId,
                CancellationToken.None
            );
            Assert.NotNull(story);
            Assert.DoesNotContain("agent-ready-rework", story.Tags);

            var feedbackStore = failedScope.ServiceProvider
                .GetRequiredService<IReworkFeedbackStore>();
            var feedback = await feedbackStore.GetByCorrelationIdAsync(
                correlationId,
                CancellationToken.None
            );
            Assert.NotNull(feedback);
            Assert.Equal(ReworkFeedbackStatus.Soaked, feedback.Status);
            Assert.Equal(story.Id, feedback.AssistanceStoryWorkItemId);
        }

        Assert.Equal(1, workSource.CreateCalls);
        Assert.Equal(1, workSource.ReadyCalls);
        Assert.Equal(0, workSource.ReactivateCalls);
        Assert.Empty(harness.Comments.Requests);

        await harness.Worker.RunPollCycleForTestingAsync();

        await using var recoveredScope = harness.Provider.CreateAsyncScope();
        var recoveredCycleStore = recoveredScope.ServiceProvider
            .GetRequiredService<IReworkCycleStore>();
        var recoveredCycle = Assert.Single(
            await recoveredCycleStore.ListPendingAsync(CancellationToken.None)
        );
        var recoveredStory = await recoveredScope.ServiceProvider
            .GetRequiredService<IWorkItemStore>()
            .GetByIdAsync(recoveredCycle.WorkItemId, CancellationToken.None);
        Assert.NotNull(recoveredStory);
        Assert.Contains("agent-ready-rework", recoveredStory.Tags);
        var recoveredFeedback = await recoveredScope.ServiceProvider
            .GetRequiredService<IReworkFeedbackStore>()
            .GetByCorrelationIdAsync(correlationId, CancellationToken.None);
        Assert.NotNull(recoveredFeedback);
        Assert.Equal(ReworkFeedbackStatus.Materialized, recoveredFeedback.Status);
        Assert.Equal(1, workSource.CreateCalls);
        Assert.Equal(2, workSource.ReadyCalls);
        Assert.Equal(0, workSource.ReactivateCalls);
        Assert.Single(harness.Comments.Requests);
    }

    [Fact]
    public async Task UnrecordedExternalStory_IsReconciledByCorrelationTag()
    {
        var workSource = new FailOnceReadyWorkSource(readyFailures: 0);
        var oldObservation = DateTimeOffset.UtcNow.AddMinutes(-10);
        var emptyBundleId = ComputeBundleId([]);
        var correlationId = ComputeCorrelationId(PullRequest, emptyBundleId);
        workSource.SeedExternalStory(
            AssistanceStoryMaterializer.BuildCorrelationTag(correlationId)
        );
        await using var harness = await CreateHarnessAsync(
            threads: [],
            workSource: workSource
        );
        await harness.SeedFeedbackAsync(
            new ReworkFeedbackUpsertRequest
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = PullRequest,
                FeedbackBundleId = emptyBundleId,
                FeedbackBundleJson = "[]",
                ThreadCount = 0,
                FirstQualifyingCommentAt = oldObservation,
                LastQualifyingCommentAt = oldObservation,
                CorrelationId = correlationId,
            }
        );

        await harness.Worker.RunPollCycleForTestingAsync();

        await using var scope = harness.Provider.CreateAsyncScope();
        var feedback = await scope.ServiceProvider
            .GetRequiredService<IReworkFeedbackStore>()
            .GetByCorrelationIdAsync(correlationId, CancellationToken.None);
        Assert.NotNull(feedback);
        Assert.Equal(ReworkFeedbackStatus.Materialized, feedback.Status);
        Assert.Equal("7001", feedback.AssistanceStoryExternalId);
        var cycle = Assert.Single(
            await scope.ServiceProvider
                .GetRequiredService<IReworkCycleStore>()
                .ListPendingAsync(CancellationToken.None)
        );
        Assert.Equal(feedback.AssistanceStoryWorkItemId, cycle.WorkItemId);
        Assert.Equal(0, workSource.CreateCalls);
        Assert.Equal(1, workSource.ReadyCalls);
        Assert.Single(harness.Comments.Requests);
    }

    [Fact]
    public async Task NewerQualifyingAssistanceFeedback_ResetsExistingSoak()
    {
        var commentAt = DateTimeOffset.UtcNow;
        var threads = new ReviewThread[]
        {
            new()
            {
                ThreadId = "thread-7",
                Status = ReviewThreadStatus.Active,
                Comments =
                [
                    new ReviewThreadComment
                    {
                        Author = "reviewer@example.com",
                        Body = "Please cover the null branch.",
                        CreatedAt = commentAt,
                    },
                ],
            },
        };
        await using var harness = await CreateHarnessAsync(threads);
        var bundleId = ComputeBundleId(threads);
        var oldObservation = commentAt.AddMinutes(-10);

        await harness.SeedFeedbackAsync(
            new ReworkFeedbackUpsertRequest
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = PullRequest,
                FeedbackBundleId = bundleId,
                FeedbackBundleJson = "[]",
                ThreadCount = 0,
                FirstQualifyingCommentAt = oldObservation,
                LastQualifyingCommentAt = oldObservation,
                CorrelationId = ComputeCorrelationId(PullRequest, bundleId),
            }
        );

        await harness.Worker.RunPollCycleForTestingAsync();

        await using var scope = harness.Provider.CreateAsyncScope();
        var feedbackStore = scope.ServiceProvider.GetRequiredService<IReworkFeedbackStore>();
        var watching = Assert.Single(
            await feedbackStore.GetWatchingAsync(CancellationToken.None)
        );
        Assert.Equal(commentAt, watching.LastQualifyingCommentAt);
        Assert.Equal(1, watching.ThreadCount);
        Assert.Contains("thread-7", watching.FeedbackBundleJson, StringComparison.Ordinal);
        Assert.Empty(await feedbackStore.GetSoakedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AssistanceMarker_SupersedesOutstandingRevivalObservation()
    {
        await using var harness = await CreateHarnessAsync(
            threads: [],
            labels: ["agent-rework-requested", "AGENT-ASSISTANCE-REQUESTED"]
        );
        await harness.SeedFeedbackAsync(
            new ReworkFeedbackUpsertRequest
            {
                RequestMode = ReworkRequestMode.Revival,
                PullRequest = new PullRequestReference { PullRequestId = "42" },
                OriginatingRunId = "run-prior",
                FeedbackBundleId = "revival-bundle",
                FeedbackBundleJson = "[]",
                ThreadCount = 0,
                FirstQualifyingCommentAt = DateTimeOffset.UtcNow,
                LastQualifyingCommentAt = DateTimeOffset.UtcNow,
            }
        );

        await harness.Worker.RunPollCycleForTestingAsync();

        await using var scope = harness.Provider.CreateAsyncScope();
        var feedbackStore = scope.ServiceProvider.GetRequiredService<IReworkFeedbackStore>();
        var watching = await feedbackStore.GetWatchingAsync(CancellationToken.None);
        var row = Assert.Single(watching);
        Assert.Equal(ReworkRequestMode.Assistance, row.RequestMode);
    }

    private static async Task<TestHarness> CreateHarnessAsync(
        IReadOnlyList<ReviewThread> threads,
        IReadOnlyList<string>? labels = null,
        IWorkSource? workSource = null)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-assistance-soak-{Guid.NewGuid():N}.db"
        );
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["persistence:provider"] = "Sqlite",
                    ["persistence:connectionString"] = $"Data Source={databasePath}",
                    ["workSource:provider"] = "LocalFake",
                    ["feedback:enabled"] = "true",
                    ["feedback:provider"] = "None",
                    ["feedback:soakMinutes"] = "5",
                    ["feedback:allowedReviewers:0"] = "reviewer@example.com",
                }
            )
            .Build();

        var snapshot = new ManagedPullRequestSnapshot
        {
            PullRequest = PullRequest,
            Title = "Human-authored pull request",
            Labels = labels ?? ["agent-assistance-requested"],
        };
        var services = new ServiceCollection();
        services.AddSilentLogging();
        services.AddAgentControllerOptions(configuration);
        services.AddAgentControllerDbContext(configuration);
        services.AddAgentControllerRepositories();
        services.AddAgentControllerNoOpProviders();
        services.AddAgentControllerLocalFakeWorkSource();
        if (workSource is not null)
        {
            services.AddSingleton<IWorkSource>(workSource);
        }
        services.AddSingleton<IManagedPullRequestDiscovery>(
            new StaticPullRequestDiscovery(snapshot)
        );
        services.AddSingleton<IFeedbackSource>(new StaticFeedbackSource(threads));
        services.AddSingleton<IPrLabelSource, ThrowingPrLabelSource>();
        services.AddSingleton<ReviewFeedbackFilterPipeline>();
        var comments = new RecordingPullRequestCommentCreator();
        services.AddSingleton<IPullRequestCommentCreator>(comments);

        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        var worker = new FeedbackPollingWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<FeedbackOptions>>(),
            provider.GetRequiredService<ILogger<FeedbackPollingWorker>>()
        );
        return new TestHarness(databasePath, provider, worker, comments);
    }

    private static string ComputeBundleId(IReadOnlyList<ReviewThread> threads)
    {
        var ids = string.Join(
            '|',
            threads.OrderBy(thread => thread.ThreadId, StringComparer.Ordinal)
                .Select(thread => thread.ThreadId)
        );
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ids)));
    }

    private static string ComputeCorrelationId(
        PullRequestReference pullRequest,
        string bundleId)
    {
        var value = $"assistance|{pullRequest.CanonicalKey}|{bundleId}";
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))
        );
    }

    private sealed class StaticPullRequestDiscovery(ManagedPullRequestSnapshot snapshot)
        : IManagedPullRequestDiscovery
    {
        public Task<IReadOnlyList<ManagedPullRequestSnapshot>> ListActiveAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ManagedPullRequestSnapshot>>([snapshot]);
        }
    }

    private sealed class StaticFeedbackSource(IReadOnlyList<ReviewThread> threads)
        : IFeedbackSource
    {
        public Task<IReadOnlyList<ReworkSignal>> PollAsync(
            FeedbackQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (threads.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<ReworkSignal>>([]);
            }

            var comments = threads.SelectMany(thread => thread.Comments).ToArray();
            var signals = query.OpenPrs.Select(pr => new ReworkSignal
            {
                RequestMode = pr.RequestMode,
                PullRequest = pr.PullRequest,
                OriginatingRunId = pr.OriginatingRunId,
                PullRequestId = pr.PullRequestId,
                Threads = threads,
                FirstQualifyingCommentAt = comments.Min(comment => comment.CreatedAt),
                LastQualifyingCommentAt = comments.Max(comment => comment.CreatedAt),
            }).ToArray();
            return Task.FromResult<IReadOnlyList<ReworkSignal>>(signals);
        }
    }

    private sealed class ThrowingPrLabelSource : IPrLabelSource
    {
        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "Assistance marker gating must use managed discovery labels."
            );
        }
    }

    private sealed class RecordingPullRequestCommentCreator : IPullRequestCommentCreator
    {
        public List<AssistanceLifecycleCommentRequest> Requests { get; } = [];

        public Task CreateAsync(
            PullRequestReference pullRequest,
            AssistanceLifecycleCommentRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FailOnceReadyWorkSource(int readyFailures = 1) : IWorkSource
    {
        private WorkCandidate? _story;
        private int _remainingReadyFailures = readyFailures;

        public int CreateCalls { get; private set; }
        public int ReadyCalls { get; private set; }
        public int ReactivateCalls { get; private set; }

        public void SeedExternalStory(string correlationTag)
        {
            _story = CreateCandidate([correlationTag]);
        }

        public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
            WorkQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WorkCandidate> result = _story is not null
                && (query.Tags is null || query.Tags.All(required =>
                    _story.Tags.Contains(required, StringComparer.OrdinalIgnoreCase)))
                    ? [_story]
                    : [];
            return Task.FromResult(result);
        }

        public Task<CreatedWorkItemResult> CreateAssistanceStoryAsync(
            CreateAssistanceStoryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            Assert.False(request.ReadyForClaim);
            Assert.DoesNotContain("agent-ready-rework", request.CorrelationTags);

            _story = CreateCandidate(
                request.CorrelationTags,
                request.Title,
                request.Description,
                request.EnvironmentKey
            );
            return Task.FromResult(new CreatedWorkItemResult
            {
                ExternalId = _story.ExternalId,
                Url = _story.ExternalUrl!,
                Revision = "1",
                Candidate = _story,
            });
        }

        public Task<WorkCandidate> MakeAssistanceStoryReadyAsync(
            WorkCandidate candidate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadyCalls++;
            if (_remainingReadyFailures > 0)
            {
                _remainingReadyFailures--;
                throw new InvalidOperationException("Simulated publication interruption.");
            }

            _story = candidate with
            {
                Tags = candidate.Tags
                    .Append("agent-ready-rework")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
            return Task.FromResult(_story);
        }

        public Task<ReworkReactivateResult> ReactivateForReworkAsync(
            ReworkReactivateRequest request,
            CancellationToken cancellationToken)
        {
            ReactivateCalls++;
            return Task.FromResult(new ReworkReactivateResult { Success = true });
        }

        public Task<ClaimResult> TryClaimAsync(
            WorkCandidate candidate,
            ClaimRequest claim,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ClaimResult { Success = false });

        public Task UpdateStatusAsync(
            ExternalWorkRef workRef,
            ExternalWorkStatus status,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AddCommentAsync(
            ExternalWorkRef workRef,
            string comment,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef,
            int maxComments,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkItemComment>>([]);

        public Task ReleaseClaimAsync(
            ReleaseClaimRequest request,
            CancellationToken cancellationToken) => Task.CompletedTask;

        private static WorkCandidate CreateCandidate(
            IReadOnlyList<string> correlationTags,
            string title = "Recovered assistance story",
            string description = "Recovered after an interrupted external creation.",
            string? environmentKey = "ado-main")
        {
            return new WorkCandidate
            {
                ExternalId = "7001",
                ExternalUrl = "https://dev.azure.com/example/project/_workitems/edit/7001",
                RepoKey = PullRequest.RepositoryKey,
                Title = title,
                Description = description,
                Status = "New",
                Tags = [$"repo:{PullRequest.RepositoryKey}", .. correlationTags],
                Source = "TestBoards",
                SourceMetadata = new Dictionary<string, string>
                {
                    ["revision"] = "1",
                    ["workSourceEnvironmentKey"] = environmentKey ?? string.Empty,
                },
            };
        }
    }

    private sealed class TestHarness(
        string databasePath,
        ServiceProvider provider,
        FeedbackPollingWorker worker,
        RecordingPullRequestCommentCreator comments) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public FeedbackPollingWorker Worker { get; } = worker;
        public RecordingPullRequestCommentCreator Comments { get; } = comments;

        public async Task SeedFeedbackAsync(ReworkFeedbackUpsertRequest request)
        {
            await using var scope = Provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IReworkFeedbackStore>();
            await store.UpsertAsync(request, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            try
            {
                File.Delete(databasePath);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }
}
