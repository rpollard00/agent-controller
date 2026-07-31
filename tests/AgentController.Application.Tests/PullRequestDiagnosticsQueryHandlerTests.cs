using System.Reflection;
using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Application.Tests;

public sealed class PullRequestDiagnosticsQueryHandlerTests
{
    [Fact]
    public async Task HumanAuthoredAssistance_WithNoComments_IsEligible()
    {
        var fixture = Fixture.Create(["agent-assistance-requested"], runs: []);

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.True(detail.Eligible);
        Assert.Equal(PullRequestRequestMatch.Assistance, detail.Request);
        Assert.Equal(0, detail.FeedbackTrace!.QualifyingThreadCount);
        Assert.True(Assert.Single(detail.Checks, check => check.Code == "originating-lineage").Passed);
    }

    [Fact]
    public async Task BothLabels_UseAssistancePrecedence()
    {
        var fixture = Fixture.Create(
            ["agent-rework-requested", "AGENT-ASSISTANCE-REQUESTED"], runs: []);

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.True(detail.Eligible);
        Assert.Equal(PullRequestRequestMatch.Both, detail.Request);
        Assert.Equal(PullRequestDiagnosticOutcome.AssistanceTakesPrecedence, detail.Outcome);
    }

    [Fact]
    public async Task Revival_UsesExactMarkerAndSharedQualifyingFeedbackTrace()
    {
        var run = EligibleRun();
        var qualifyingThread = new ReviewThread
        {
            ThreadId = "thread-1",
            Status = ReviewThreadStatus.Active,
            Comments =
            [
                new ReviewThreadComment
                {
                    Author = "reviewer@example.test",
                    AuthorIdentities =
                    [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.test" }],
                    Body = "Please fix this.",
                },
            ],
        };
        var fixture = Fixture.Create(["agent-rework-requested"], [run], [qualifyingThread]);

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.True(detail.Eligible);
        Assert.Equal(1, detail.FeedbackTrace!.QualifyingThreadCount);
        Assert.DoesNotContain(detail.FeedbackTrace.GetType().GetProperties(), property =>
            property.Name.Contains("Body", StringComparison.OrdinalIgnoreCase));

        var wrongCase = Fixture.Create(["Agent-Rework-Requested"], [run], [qualifyingThread]);
        var rejected = await wrongCase.ExecuteAsync();
        Assert.NotNull(rejected);
        Assert.False(rejected.Eligible);
        Assert.Equal(PullRequestRequestMatch.None, rejected.Request);
    }

    [Theory]
    [InlineData(
        "https://dev.azure.com/org/Project/_git/orders/pullrequest/42",
        "https://dev.azure.com/org/Project/_git/orders/pullrequest/42/")]
    [InlineData(
        "https://dev.azure.com/org/Project/_git/orders/pullrequest/42?view=discussion",
        "https://org.visualstudio.com/Project/_git/orders/pullrequest/42")]
    public async Task Revival_MatchesOriginatingRunWithProductionIdentitySemantics(
        string discoveredUrl,
        string runUrl)
    {
        var run = EligibleRun() with { PullRequestUrl = runUrl };
        var fixture = Fixture.Create(
            ["agent-rework-requested"],
            [run],
            [QualifyingThread()],
            snapshotUrl: discoveredUrl,
            repositoryKey: "Project/orders");

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.True(detail.Eligible);
        Assert.True(Assert.Single(detail.Checks, check => check.Code == "originating-lineage").Passed);
    }

    [Fact]
    public async Task Revival_WithoutOriginatingRun_ExplainsMissingLineage()
    {
        var fixture = Fixture.Create(
            ["agent-rework-requested"],
            runs: [],
            threads: [QualifyingThread()]);

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.False(detail.Eligible);
        Assert.False(Assert.Single(detail.Checks, check => check.Code == "originating-lineage").Passed);
    }

    [Fact]
    public async Task Revival_WithNonqualifyingFeedback_ExplainsFeedbackRequirement()
    {
        var nonqualifyingThread = new ReviewThread
        {
            ThreadId = "thread-1",
            Status = ReviewThreadStatus.Active,
            Comments = [new ReviewThreadComment { Author = "other@example.test", Body = "Change requested" }],
        };
        var fixture = Fixture.Create(
            ["agent-rework-requested"],
            [EligibleRun()],
            [nonqualifyingThread]);

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.False(detail.Eligible);
        Assert.Equal(0, detail.FeedbackTrace!.QualifyingThreadCount);
        Assert.False(Assert.Single(detail.Checks, check => check.Code == "qualifying-feedback").Passed);
    }

    [Theory]
    [InlineData("completed", true, false, "active-status")]
    [InlineData("active", false, false, "required-metadata")]
    [InlineData("active", true, true, "active-rework")]
    public async Task Revival_ExplainsInactiveMissingMetadataAndActiveRework(
        string status,
        bool includeUrl,
        bool blocked,
        string failedCheck)
    {
        var fixture = Fixture.Create(
            ["agent-rework-requested"],
            [EligibleRun()],
            [QualifyingThread()],
            status,
            includeUrl,
            blocked);

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.False(detail.Eligible);
        Assert.False(Assert.Single(detail.Checks, check => check.Code == failedCheck).Passed);
    }

    private static AgentRunHandle EligibleRun() => new()
    {
        RunId = "run-1",
        WorkItemId = "work-1",
        PullRequestUrl = "https://example.test/orders/pullrequest/42",
        Status = RunLifecycleState.Completed,
    };

    private static ReviewThread QualifyingThread() => new()
    {
        ThreadId = "thread-1",
        Status = ReviewThreadStatus.Active,
        Comments =
        [
            new ReviewThreadComment
            {
                Author = "reviewer@example.test",
                AuthorIdentities =
                [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.test" }],
                Body = "Change requested",
            },
        ],
    };

    private sealed class Fixture
    {
        private readonly GetPullRequestDiagnosticsQueryHandler _handler;
        private readonly string _repositoryKey;

        private Fixture(GetPullRequestDiagnosticsQueryHandler handler, string repositoryKey)
        {
            _handler = handler;
            _repositoryKey = repositoryKey;
        }

        public static Fixture Create(
            IReadOnlyList<string> labels,
            IReadOnlyList<AgentRunHandle> runs,
            IReadOnlyList<ReviewThread>? threads = null,
            string status = "active",
            bool includeUrl = true,
            bool blocked = false,
            string? snapshotUrl = null,
            string repositoryKey = "orders")
        {
            var pullRequest = new PullRequestReference
            {
                EnvironmentKey = "ado",
                RepositoryKey = repositoryKey,
                PullRequestId = "42",
                PullRequestUrl = includeUrl
                    ? snapshotUrl ?? "https://example.test/orders/pullrequest/42"
                    : string.Empty,
                SourceBranch = "refs/heads/feature",
                TargetBranch = "refs/heads/main",
            };
            var snapshot = new ManagedPullRequestSnapshot
            {
                PullRequest = pullRequest,
                Title = "Improve orders",
                Status = status,
                Labels = labels,
                LinkedWorkItems = [new PullRequestWorkItemReference { WorkItemId = "7", WorkItemUrl = "https://example.test/7" }],
            };
            var discovery = Stub<IManagedPullRequestDiagnosticDiscovery>.Create((method, _) =>
                method.Name == nameof(IManagedPullRequestDiagnosticDiscovery.ListAsync)
                    ? Task.FromResult(new ManagedPullRequestDiscoveryPage
                    {
                        Items = [snapshot],
                        Page = 1,
                        PageSize = 100,
                        Total = 1,
                    })
                    : throw new NotSupportedException());
            var repositoryStore = Stub<IRepositoryStore>.Create((method, _) => method.Name switch
            {
                nameof(IRepositoryStore.GetByKeyAsync) => Task.FromResult<RepositoryProfile?>(new RepositoryProfile
                {
                    Key = repositoryKey,
                    RepositoryHostConnectionKey = "ado",
                    ReviewerIdentities =
                    [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.test" }],
                }),
                _ => throw new NotSupportedException(method.Name),
            });
            var runStore = Stub<IAgentRunStore>.Create((method, args) => method.Name switch
            {
                nameof(IAgentRunStore.FindRunsForFeedbackAsync) => Task.FromResult(runs),
                nameof(IAgentRunStore.GetByIdAsync) => Task.FromResult<AgentRunHandle?>(
                    blocked && (string)args[0]! == "new-run"
                        ? new AgentRunHandle { RunId = "new-run", Status = RunLifecycleState.AgentRunning }
                        : null),
                _ => throw new NotSupportedException(method.Name),
            });
            var consumed = blocked
                ? new[] { new ReworkCycle { WorkItemId = "work-1", NewRunId = "new-run", Status = ReworkCycleStatus.Consumed } }
                : [];
            var cycleStore = Stub<IReworkCycleStore>.Create((method, _) => method.Name switch
            {
                nameof(IReworkCycleStore.ListConsumedAsync) => Task.FromResult<IReadOnlyList<ReworkCycle>>(consumed),
                nameof(IReworkCycleStore.ListPendingAsync) => Task.FromResult<IReadOnlyList<ReworkCycle>>([]),
                _ => throw new NotSupportedException(method.Name),
            });
            var feedbackStore = Stub<IReworkFeedbackStore>.Create((method, _) => method.Name switch
            {
                nameof(IReworkFeedbackStore.GetTrackedAsync) => Task.FromResult<IReadOnlyList<ReworkFeedback>>([]),
                _ => throw new NotSupportedException(method.Name),
            });
            var signal = new ReworkSignal
            {
                PullRequest = pullRequest,
                PullRequestId = "42",
                Threads = threads ?? [],
            };
            var feedbackSource = Stub<IFeedbackSource>.Create((method, _) =>
                method.Name == nameof(IFeedbackSource.PollAsync)
                    ? Task.FromResult<IReadOnlyList<ReworkSignal>>([signal])
                    : throw new NotSupportedException());
            var labelSource = Stub<IPrLabelSource>.Create((method, _) =>
                method.Name == nameof(IPrLabelSource.GetLabelsAsync)
                    ? Task.FromResult<IReadOnlyList<PrLabel>>(labels.Select(label => new PrLabel { Name = label }).ToArray())
                    : throw new NotSupportedException());
            var options = new PullRequestDiagnosticOptions();
            var pipeline = new ReviewFeedbackFilterPipeline(
                labelSource, NullLogger<ReviewFeedbackFilterPipeline>.Instance);
            return new Fixture(new GetPullRequestDiagnosticsQueryHandler(
                discovery, repositoryStore, runStore, cycleStore, feedbackStore,
                feedbackSource, pipeline, options), repositoryKey);
        }

        public Task<PullRequestDiagnosticDetail?> ExecuteAsync() => _handler.ExecuteAsync(
            new GetPullRequestDiagnosticsQuery
            {
                SourceControlEnvironmentKey = "ado",
                RepositoryKey = _repositoryKey,
                PullRequestId = "42",
            }, CancellationToken.None);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1852:Seal internal types", Justification = "DispatchProxy subclasses this type at runtime.")]
    private class Stub<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[], object?> _handler = null!;

        public static T Create(Func<MethodInfo, object?[], object?> handler)
        {
            var proxy = DispatchProxy.Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handler(targetMethod!, args ?? []);
    }
}
