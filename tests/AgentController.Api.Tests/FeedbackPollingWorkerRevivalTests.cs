using AgentController.Api;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentController.Infrastructure.Options;

namespace AgentController.Api.Tests;

public sealed class FeedbackPollingWorkerRevivalTests
{
    private const string ConnectionKey = "ado-production";
    private const string ReviewerEmail = "reviewer@example.com";

    private static readonly ReviewThread QualifyingThread = new()
    {
        ThreadId = "thread-42",
        Status = ReviewThreadStatus.Active,
        Comments =
        [
            new ReviewThreadComment
            {
                Author = ReviewerEmail,
                AuthorIdentities =
                [
                    new ReviewerIdentity
                    {
                        Kind = "email",
                        Value = ReviewerEmail,
                    },
                ],
                Body = "Please address this unresolved review comment.",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        ],
    };

    [Fact]
    public async Task PollCycle_UsesCanonicalManagedProfileAndReviewerPolicyForRevival()
    {
        var profile = Profile("custom-payments-profile");
        var runUrl =
            "https://EXAMPLE.visualstudio.com/payments%20project/_git/PAYMENTS/pullrequest/42/";
        var discovered = Snapshot(
            profile,
            "https://dev.azure.com/example/Payments%20Project/_git/payments/pullrequest/42"
        );
        await using var harness = await CreateHarnessAsync(
            [profile],
            [discovered],
            [QualifyingThread]
        );
        var run = await harness.SeedRunAsync(runUrl);

        await harness.Worker.RunPollCycleForTestingAsync();

        var query = Assert.Single(harness.Feedback.Queries);
        var pr = Assert.Single(query.OpenPrs);
        Assert.Equal(ReworkRequestMode.Revival, pr.RequestMode);
        Assert.Equal("custom-payments-profile", pr.RepoKey);
        Assert.Equal("custom-payments-profile", pr.PullRequest.RepositoryKey);
        Assert.Equal(ConnectionKey, pr.PullRequest.EnvironmentKey);
        Assert.Equal("AzureDevOps", pr.ReviewerIdentityProvider);
        Assert.Equal(
            [new ReviewerIdentity { Kind = "email", Value = ReviewerEmail }],
            pr.ReviewerIdentities
        );

        await using var scope = harness.Provider.CreateAsyncScope();
        var feedbackStore = scope.ServiceProvider.GetRequiredService<IReworkFeedbackStore>();
        var watching = Assert.Single(await feedbackStore.GetWatchingAsync(CancellationToken.None));
        Assert.Equal(ReworkRequestMode.Revival, watching.RequestMode);
        Assert.Equal(run.RunId, watching.OriginatingRunId);
        Assert.Equal("custom-payments-profile", watching.PullRequest.RepositoryKey);
        Assert.Equal(1, watching.ThreadCount);
    }

    [Fact]
    public async Task PollCycle_UsesProfileFallbackWhenManagedDiscoveryThrows()
    {
        var profile = Profile("custom-payments-profile");
        var runUrl =
            "https://dev.azure.com/EXAMPLE/Payments%20Project/_git/PAYMENTS/pullrequest/42/?view=discussion";
        await using var harness = await CreateHarnessAsync(
            [profile],
            discoveredPullRequests: [],
            [QualifyingThread],
            discoveryThrows: true
        );
        var run = await harness.SeedRunAsync(runUrl);

        await harness.Worker.RunPollCycleForTestingAsync();

        var query = Assert.Single(harness.Feedback.Queries);
        var pr = Assert.Single(query.OpenPrs);
        Assert.Equal("custom-payments-profile", pr.RepoKey);
        Assert.Equal(ConnectionKey, pr.PullRequest.EnvironmentKey);
        Assert.Equal("42", pr.PullRequestId);
        Assert.Contains(
            harness.Logs.Messages,
            message => message.Contains(
                "Managed pull request discovery failed",
                StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            harness.Logs.Messages,
            message => message.Contains("PAT=", StringComparison.Ordinal)
        );

        await using var scope = harness.Provider.CreateAsyncScope();
        var watching = Assert.Single(
            await scope.ServiceProvider
                .GetRequiredService<IReworkFeedbackStore>()
                .GetWatchingAsync(CancellationToken.None)
        );
        Assert.Equal(run.RunId, watching.OriginatingRunId);
        Assert.Equal("custom-payments-profile", watching.PullRequest.RepositoryKey);
    }

    [Fact]
    public async Task PollCycle_FailsClosedWhenRevivalRepositoryIsMissing()
    {
        var unrelatedProfile = Profile(
            "other-profile",
            "Other Project",
            "other-repository",
            "other-repository"
        );
        var runUrl =
            "https://dev.azure.com/example/Payments%20Project/_git/payments/pullrequest/42";
        await using var harness = await CreateHarnessAsync(
            [unrelatedProfile],
            discoveredPullRequests: [],
            [QualifyingThread]
        );
        var run = await harness.SeedRunAsync(runUrl);

        await harness.Worker.RunPollCycleForTestingAsync();

        Assert.Empty(harness.Feedback.Queries);
        Assert.Contains(
            harness.Logs.Messages,
            message => message.Contains(run.RunId, StringComparison.Ordinal)
                && message.Contains("42", StringComparison.Ordinal)
                && message.Contains("Skipping Revival", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            harness.Logs.Messages,
            message => message.Contains(runUrl, StringComparison.Ordinal)
        );

        await using var scope = harness.Provider.CreateAsyncScope();
        Assert.Empty(
            await scope.ServiceProvider
                .GetRequiredService<IReworkFeedbackStore>()
                .GetWatchingAsync(CancellationToken.None)
        );
    }

    [Fact]
    public async Task PollCycle_FailsClosedWhenRevivalRepositoryIsAmbiguous()
    {
        var first = Profile("payments-profile-one");
        var second = Profile("payments-profile-two");
        var runUrl =
            "https://dev.azure.com/example/Payments%20Project/_git/payments/pullrequest/42";
        await using var harness = await CreateHarnessAsync(
            [first, second],
            discoveredPullRequests: [],
            [QualifyingThread]
        );
        var run = await harness.SeedRunAsync(runUrl);

        await harness.Worker.RunPollCycleForTestingAsync();

        Assert.Empty(harness.Feedback.Queries);
        Assert.Contains(
            harness.Logs.Messages,
            message => message.Contains(run.RunId, StringComparison.Ordinal)
                && message.Contains("matched 2", StringComparison.Ordinal)
                && message.Contains("ambiguous", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            harness.Logs.Messages,
            message => message.Contains(runUrl, StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task PollCycle_AssistanceMarkerWinsForTheSameEligibleRun()
    {
        var profile = Profile("custom-payments-profile");
        var runUrl =
            "https://EXAMPLE.visualstudio.com/payments%20project/_git/PAYMENTS/pullrequest/42/";
        var discovered = Snapshot(
            profile,
            "https://dev.azure.com/example/Payments%20Project/_git/payments/pullrequest/42",
            ["agent-rework-requested", "agent-assistance-requested"]
        );
        await using var harness = await CreateHarnessAsync(
            [profile],
            [discovered],
            threads: []
        );
        var run = await harness.SeedRunAsync(runUrl);

        await harness.Worker.RunPollCycleForTestingAsync();

        var query = Assert.Single(harness.Feedback.Queries);
        Assert.Equal(ReworkRequestMode.Assistance, query.OpenPrs.Single().RequestMode);
        Assert.Equal(run.RunId, query.OpenPrs.Single().OriginatingRunId);
        Assert.Equal("agent-assistance-requested", query.ReworkMarkerTag);

        await using var scope = harness.Provider.CreateAsyncScope();
        var watching = Assert.Single(
            await scope.ServiceProvider
                .GetRequiredService<IReworkFeedbackStore>()
                .GetWatchingAsync(CancellationToken.None)
        );
        Assert.Equal(ReworkRequestMode.Assistance, watching.RequestMode);
        Assert.Equal(run.RunId, watching.OriginatingRunId);
    }

    private static async Task<TestHarness> CreateHarnessAsync(
        IReadOnlyList<RepositoryProfile> profiles,
        IReadOnlyList<ManagedPullRequestSnapshot> discoveredPullRequests,
        IReadOnlyList<ReviewThread> threads,
        bool discoveryThrows = false)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-revival-worker-{Guid.NewGuid():N}.db"
        );
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["persistence:provider"] = "Sqlite",
                    ["persistence:connectionString"] = $"Data Source={databasePath}",
                    ["feedback:enabled"] = "true",
                    ["feedback:provider"] = "None",
                    ["feedback:soakMinutes"] = "5",
                }
            )
            .Build();

        var logs = new RecordingLoggerProvider();
        var feedback = new RecordingFeedbackSource(threads);
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
            new TestPullRequestDiscovery(discoveredPullRequests, discoveryThrows)
        );
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
            foreach (var profile in profiles)
            {
                await repositoryStore.UpsertAsync(profile, CancellationToken.None);
            }
        }

        var worker = new FeedbackPollingWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsMonitor<FeedbackOptions>>(),
            provider.GetRequiredService<ILogger<FeedbackPollingWorker>>()
        );
        return new TestHarness(databasePath, provider, worker, feedback, logs);
    }

    private static RepositoryProfile Profile(
        string key,
        string project = "Payments Project",
        string remoteIdentity = "payments-id",
        string repositoryName = "payments") => new()
        {
            Key = key,
            Project = project,
            RemoteIdentity = remoteIdentity,
            WebUrl =
                $"https://dev.azure.com/example/{Uri.EscapeDataString(project)}/_git/{repositoryName}",
            RepositoryHostConnectionKey = ConnectionKey,
            ReviewerIdentities =
            [
                new ReviewerIdentity
                {
                    Kind = "email",
                    Value = ReviewerEmail,
                },
            ],
        };

    private static ManagedPullRequestSnapshot Snapshot(
        RepositoryProfile profile,
        string pullRequestUrl,
        IReadOnlyList<string>? labels = null) => new()
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
            Labels = labels ?? [],
        };

    private sealed class TestPullRequestDiscovery(
        IReadOnlyList<ManagedPullRequestSnapshot> snapshots,
        bool throws) : IManagedPullRequestDiscovery
    {
        public Task<IReadOnlyList<ManagedPullRequestSnapshot>> ListActiveAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (throws)
            {
                throw new InvalidOperationException("PAT=should-not-be-logged");
            }

            return Task.FromResult(snapshots);
        }
    }

    private sealed class RecordingFeedbackSource(IReadOnlyList<ReviewThread> threads)
        : IFeedbackSource
    {
        public List<FeedbackQuery> Queries { get; } = [];

        public Task<IReadOnlyList<ReworkSignal>> PollAsync(
            FeedbackQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            if (threads.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<ReworkSignal>>([]);
            }

            var comments = threads.SelectMany(thread => thread.Comments).ToArray();
            return Task.FromResult<IReadOnlyList<ReworkSignal>>(
                query.OpenPrs
                    .Select(pr => new ReworkSignal
                    {
                        RequestMode = pr.RequestMode,
                        PullRequest = pr.PullRequest,
                        OriginatingRunId = pr.OriginatingRunId,
                        PullRequestId = pr.PullRequestId,
                        Threads = threads,
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
        RecordingFeedbackSource feedback,
        RecordingLoggerProvider logs) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;
        public FeedbackPollingWorker Worker { get; } = worker;
        public RecordingFeedbackSource Feedback { get; } = feedback;
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
