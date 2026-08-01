using System.Globalization;
using AgentController.Api;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Api.Tests;

/// <summary>
/// Integration-style revival worker tests for soak-window persistence across
/// polling windows.
///
/// The persisted observation is the source of truth for soak state. Repeated
/// polls carrying the same feedback bundle must continue the existing soak
/// instead of restarting the timer; a bundle that has already soaked or
/// materialized must not be returned to Watching or materialized twice; and a
/// genuinely newer qualifying comment must still reset the quiet period.
/// </summary>
public sealed class FeedbackPollingWorkerRevivalSoakTests
{
    private const string ConnectionKey = "ado-production";
    private const string ReviewerEmail = "reviewer@example.com";

    private const string RunUrl =
        "https://EXAMPLE.visualstudio.com/payments%20project/_git/PAYMENTS/pullrequest/42/";

    private const string DiscoveredUrl =
        "https://dev.azure.com/example/Payments%20Project/_git/payments/pullrequest/42";

    // ── 1. Unchanged replay continues the soak ─────────────────────

    [Fact]
    public async Task PollCycle_RepeatedUnchangedFeedback_ContinuesSoakWithoutRestarting()
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using var harness = await CreateHarnessAsync(soakMinutes: 5);
        harness.Feedback.SetThreads([Thread("thread-42", createdAt)]);
        await harness.SeedRunAsync(RunUrl);

        // Cycle 1: first observation creates the Watching row.
        await harness.Worker.RunPollCycleForTestingAsync();
        var afterFirst = Assert.Single(await harness.ListFeedbackAsync());
        Assert.Equal(ReworkFeedbackStatus.Watching, afterFirst.Status);
        var rowId = afterFirst.Id;
        var firstComment = afterFirst.FirstQualifyingCommentAt;
        var lastComment = afterFirst.LastQualifyingCommentAt;
        var updatedAt = afterFirst.UpdatedAt;
        var bundleId = afterFirst.FeedbackBundleId;

        // Cycles 2-3: replay the same bundle. Give UtcNow room to advance so a
        // spurious UpdatedAt bump would be detectable.
        await Task.Delay(20);
        await harness.Worker.RunPollCycleForTestingAsync();
        await Task.Delay(20);
        await harness.Worker.RunPollCycleForTestingAsync();

        var feedback = Assert.Single(await harness.ListFeedbackAsync());
        // Row identity, lifecycle, soak baseline, payload, and UpdatedAt are all
        // preserved — the soak keeps progressing instead of restarting.
        Assert.Equal(rowId, feedback.Id);
        Assert.Equal(ReworkFeedbackStatus.Watching, feedback.Status);
        Assert.Equal(bundleId, feedback.FeedbackBundleId);
        Assert.Equal(firstComment, feedback.FirstQualifyingCommentAt);
        Assert.Equal(lastComment, feedback.LastQualifyingCommentAt);
        Assert.Equal(updatedAt, feedback.UpdatedAt);
        Assert.Equal(1, feedback.ThreadCount);

        // Nothing soaked yet, so no rework cycle was materialized.
        Assert.Equal(0, await harness.CountCyclesAsync());
    }

    // ── 2. Eventual threshold advancement + single materialization ─

    [Fact]
    public async Task PollCycle_AlreadyMaterializedBundle_IsNotRewatchedOrRematerialized()
    {
        // Comment older than the soak window so the first cycle soaks and
        // materializes in one pass.
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using var harness = await CreateHarnessAsync(soakMinutes: 5);
        harness.Feedback.SetThreads([Thread("thread-42", createdAt)]);
        await harness.SeedRunAsync(RunUrl);

        // Cycle 1: Watching -> Soaked -> Materialized, and one cycle is created.
        await harness.Worker.RunPollCycleForTestingAsync();
        var afterFirst = Assert.Single(await harness.ListFeedbackAsync());
        Assert.Equal(ReworkFeedbackStatus.Materialized, afterFirst.Status);
        var rowId = afterFirst.Id;
        Assert.Equal(1, await harness.CountCyclesAsync());

        // Cycles 2-3: the same bundle reappears but must not restart the soak or
        // re-materialize.
        await harness.Worker.RunPollCycleForTestingAsync();
        await harness.Worker.RunPollCycleForTestingAsync();

        var feedback = Assert.Single(await harness.ListFeedbackAsync());
        Assert.Equal(rowId, feedback.Id);
        Assert.Equal(ReworkFeedbackStatus.Materialized, feedback.Status);
        Assert.Equal(afterFirst.LastQualifyingCommentAt, feedback.LastQualifyingCommentAt);
        Assert.Equal(afterFirst.UpdatedAt, feedback.UpdatedAt);

        // Still exactly one materialized cycle and no Watching/Soaked rows.
        Assert.Equal(1, await harness.CountCyclesAsync());
        Assert.Empty(await harness.GetWatchingAsync());
        Assert.Empty(await harness.GetSoakedAsync());

        // The skip path was exercised and logged.
        Assert.Contains(
            harness.Logs.Messages,
            message => message.Contains("already advanced", StringComparison.Ordinal)
                && message.Contains("Materialized", StringComparison.Ordinal)
        );
    }

    // ── 3. Newer qualifying comment resets the quiet period ────────

    [Fact]
    public async Task PollCycle_NewerQualifyingComment_ResetsQuietPeriodPreservingIdentity()
    {
        var firstComment = DateTimeOffset.UtcNow.AddMinutes(-4);
        await using var harness = await CreateHarnessAsync(soakMinutes: 5);
        harness.Feedback.SetThreads([Thread("thread-42", firstComment)]);
        await harness.SeedRunAsync(RunUrl);

        // Cycle 1: within the soak window, so it stays Watching.
        await harness.Worker.RunPollCycleForTestingAsync();
        var afterFirst = Assert.Single(await harness.ListFeedbackAsync());
        Assert.Equal(ReworkFeedbackStatus.Watching, afterFirst.Status);
        Assert.Equal(firstComment, afterFirst.LastQualifyingCommentAt);
        var rowId = afterFirst.Id;
        var firstAnchor = afterFirst.FirstQualifyingCommentAt;
        var updatedAt = afterFirst.UpdatedAt;

        // A genuinely newer comment in the same thread (same bundle id) arrives.
        var newerComment = DateTimeOffset.UtcNow.AddMinutes(-1);
        harness.Feedback.SetThreads([Thread("thread-42", newerComment)]);

        // Cycle 2: the quiet period resets to the newer comment.
        await harness.Worker.RunPollCycleForTestingAsync();
        var feedback = Assert.Single(await harness.ListFeedbackAsync());

        // Identity and earliest anchor are preserved; the quiet-period anchor
        // advances to the newer comment and the row stays Watching.
        Assert.Equal(rowId, feedback.Id);
        Assert.Equal(firstAnchor, feedback.FirstQualifyingCommentAt);
        Assert.Equal(newerComment, feedback.LastQualifyingCommentAt);
        Assert.Equal(ReworkFeedbackStatus.Watching, feedback.Status);
        Assert.True(feedback.UpdatedAt > updatedAt);
    }

    // ── 4. A genuinely changed bundle restarts the soak ────────────

    [Fact]
    public async Task PollCycle_ChangedBundle_SupersedesWatchingRowAndStartsFreshSoak()
    {
        await using var harness = await CreateHarnessAsync(soakMinutes: 5);

        // Bundle A: a single thread within the soak window stays Watching.
        harness.Feedback.SetThreads([Thread("thread-a", DateTimeOffset.UtcNow.AddMinutes(-1))]);
        await harness.SeedRunAsync(RunUrl);
        await harness.Worker.RunPollCycleForTestingAsync();
        var bundleA = Assert.Single(await harness.ListFeedbackAsync());
        Assert.Equal(ReworkFeedbackStatus.Watching, bundleA.Status);
        var bundleAId = bundleA.Id;

        // Bundle B: a different thread id changes the bundle hash.
        harness.Feedback.SetThreads([Thread("thread-b", DateTimeOffset.UtcNow.AddMinutes(-2))]);
        await harness.Worker.RunPollCycleForTestingAsync();

        var feedback = await harness.ListFeedbackAsync();
        Assert.Equal(2, feedback.Count);

        var prior = Assert.Single(feedback, row => row.Id == bundleAId);
        Assert.Equal(ReworkFeedbackStatus.Superseded, prior.Status);

        var current = Assert.Single(
            feedback,
            row => row.Status == ReworkFeedbackStatus.Watching
        );
        Assert.NotEqual(bundleA.FeedbackBundleId, current.FeedbackBundleId);
        Assert.Equal(0, await harness.CountCyclesAsync());
    }

    // ── Harness & helpers ──────────────────────────────────────────

    private static ReviewThread Thread(string threadId, DateTimeOffset createdAt) => new()
    {
        ThreadId = threadId,
        Status = ReviewThreadStatus.Active,
        Comments =
        [
            new ReviewThreadComment
            {
                Author = ReviewerEmail,
                AuthorIdentities =
                [
                    new ReviewerIdentity { Kind = "email", Value = ReviewerEmail },
                ],
                Body = "Please address this unresolved review comment.",
                CreatedAt = createdAt,
            },
        ],
    };

    private static async Task<TestHarness> CreateHarnessAsync(int soakMinutes)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-revival-soak-{Guid.NewGuid():N}.db"
        );
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["persistence:provider"] = "Sqlite",
                    ["persistence:connectionString"] = $"Data Source={databasePath}",
                    ["feedback:enabled"] = "true",
                    ["feedback:provider"] = "None",
                    ["feedback:soakMinutes"] = soakMinutes.ToString(CultureInfo.InvariantCulture),
                }
            )
            .Build();

        var logs = new RecordingLoggerProvider();
        var feedback = new MutableFeedbackSource();
        var profile = Profile();
        var discovered = Snapshot(profile, DiscoveredUrl);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(logs);
        });
        services.AddAgentControllerOptions(configuration);
        services.AddAgentControllerDbContext(configuration);
        services.AddAgentControllerRepositories();
        services.AddApplicationHandlers();
        services.AddAgentControllerNoOpProviders();
        services.AddSingleton<IManagedPullRequestDiscovery>(
            new TestPullRequestDiscovery([discovered]));
        services.AddSingleton<IFeedbackSource>(feedback);
        services.AddSingleton<IPrLabelSource, MarkerLabelSource>();
        services.AddSingleton<ReviewFeedbackFilterPipeline>();

        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            await db.Database.EnsureCreatedAsync();

            var connectionStore = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
            await connectionStore.CreateAsync(
                new ConnectionProfile
                {
                    Key = ConnectionKey,
                    Provider = "AzureDevOps",
                    Capabilities = [ConnectionCapability.Repositories],
                },
                CancellationToken.None
            );

            var repositoryStore = scope.ServiceProvider.GetRequiredService<IRepositoryStore>();
            await repositoryStore.UpsertAsync(profile, CancellationToken.None);
        }

        var worker = new FeedbackPollingWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<FeedbackOptions>>(),
            provider.GetRequiredService<ILogger<FeedbackPollingWorker>>()
        );
        return new TestHarness(databasePath, provider, worker, feedback, logs);
    }

    private static RepositoryProfile Profile() => new()
    {
        Key = "custom-payments-profile",
        Project = "Payments Project",
        RemoteIdentity = "payments-id",
        WebUrl = "https://dev.azure.com/example/Payments%20Project/_git/payments",
        RepositoryHostConnectionKey = ConnectionKey,
        ReviewerIdentities =
        [
            new ReviewerIdentity { Kind = "email", Value = ReviewerEmail },
        ],
    };

    private static ManagedPullRequestSnapshot Snapshot(
        RepositoryProfile profile,
        string pullRequestUrl) => new()
        {
            PullRequest = new PullRequestReference
            {
                EnvironmentKey = profile.RepositoryHostConnectionKey!,
                RepositoryKey = profile.Key,
                PullRequestId = "42",
                PullRequestUrl = pullRequestUrl,
                SourceBranch = "refs/heads/feature/review",
                SourceCommitSha = "abc123",
            },
            Labels = [],
        };

    private sealed class TestPullRequestDiscovery(
        IReadOnlyList<ManagedPullRequestSnapshot> snapshots) : IManagedPullRequestDiscovery
    {
        public Task<IReadOnlyList<ManagedPullRequestSnapshot>> ListActiveAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshots);
        }
    }

    private sealed class MutableFeedbackSource : IFeedbackSource
    {
        private IReadOnlyList<ReviewThread> _threads = [];

        public List<FeedbackQuery> Queries { get; } = [];

        public void SetThreads(IReadOnlyList<ReviewThread> threads) => _threads = threads;

        public Task<IReadOnlyList<ReworkSignal>> PollAsync(
            FeedbackQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            if (_threads.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<ReworkSignal>>([]);
            }

            var comments = _threads.SelectMany(thread => thread.Comments).ToArray();
            return Task.FromResult<IReadOnlyList<ReworkSignal>>(
                query.OpenPrs
                    .Select(pr => new ReworkSignal
                    {
                        RequestMode = pr.RequestMode,
                        PullRequest = pr.PullRequest,
                        OriginatingRunId = pr.OriginatingRunId,
                        PullRequestId = pr.PullRequestId,
                        Threads = _threads,
                        FirstQualifyingCommentAt = comments.Min(comment => comment.CreatedAt),
                        LastQualifyingCommentAt = comments.Max(comment => comment.CreatedAt),
                    })
                    .ToArray()
            );
        }
    }

    private sealed class MarkerLabelSource : IPrLabelSource
    {
        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<PrLabel>>(
                [new PrLabel { Name = "agent-rework-requested" }]
            );
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

        public void Dispose() { }
    }

    private sealed class RecordingLogger(ICollection<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose() { }
    }

    private sealed class TestHarness(
        string databasePath,
        ServiceProvider provider,
        FeedbackPollingWorker worker,
        MutableFeedbackSource feedback,
        RecordingLoggerProvider logs) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public FeedbackPollingWorker Worker { get; } = worker;
        public MutableFeedbackSource Feedback { get; } = feedback;
        public RecordingLoggerProvider Logs { get; } = logs;

        public async Task<AgentRunHandle> SeedRunAsync(string pullRequestUrl)
        {
            await using var scope = Provider.CreateAsyncScope();
            var runStore = scope.ServiceProvider.GetRequiredService<IAgentRunStore>();
            var run = await runStore.CreateAsync(
                new CreateRunRequest
                {
                    WorkItemId = "work-item-42",
                    WorkerId = "test-worker",
                    InitialStatus = RunLifecycleState.PrOpened,
                },
                CancellationToken.None
            );
            await runStore.UpdateRuntimeFieldsAsync(
                run.RunId,
                new RuntimeFieldUpdate
                {
                    PullRequestUrl = pullRequestUrl,
                    BranchName = "refs/heads/feature/review",
                    CommitSha = "abc123",
                },
                CancellationToken.None
            );
            return (await runStore.GetByIdAsync(run.RunId, CancellationToken.None))!;
        }

        public async Task<List<ReworkFeedback>> ListFeedbackAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            var entities = await db.ReworkFeedback.ToListAsync();
            return entities
                .OrderBy(entity => entity.CreatedAt)
                .Select(entity => new ReworkFeedback
                {
                    Id = entity.Id,
                    RequestMode = (ReworkRequestMode)entity.RequestMode,
                    PullRequestId = entity.PullRequestId,
                    FeedbackBundleId = entity.FeedbackBundleId,
                    FeedbackBundleJson = entity.FeedbackBundleJson ?? string.Empty,
                    FirstQualifyingCommentAt = entity.FirstQualifyingCommentAt,
                    LastQualifyingCommentAt = entity.LastQualifyingCommentAt,
                    ThreadCount = entity.ThreadCount,
                    Status = (ReworkFeedbackStatus)entity.Status,
                    CreatedAt = entity.CreatedAt,
                    UpdatedAt = entity.UpdatedAt,
                })
                .ToList();
        }

        public async Task<List<ReworkFeedback>> GetWatchingAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IReworkFeedbackStore>();
            return (await store.GetWatchingAsync(CancellationToken.None)).ToList();
        }

        public async Task<List<ReworkFeedback>> GetSoakedAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IReworkFeedbackStore>();
            return (await store.GetSoakedAsync(CancellationToken.None)).ToList();
        }

        public async Task<int> CountCyclesAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            return await db.ReworkCycles.CountAsync();
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
