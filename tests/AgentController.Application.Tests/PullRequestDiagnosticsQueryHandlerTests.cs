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

    [Theory]
    [InlineData("email", "reviewer@example.test", "email", "REVIEWER@EXAMPLE.TEST")]
    [InlineData("identityId", "D2719B5C-3F2B-4E8E-9A4F-4E2D1F3A1B90", "identityId", "d2719b5c-3f2b-4e8e-9a4f-4e2d1f3a1b90")]
    [InlineData("descriptor", "aad.reviewer", "descriptor", "aad.reviewer")]
    public async Task Revival_UsesAzureDevOpsReviewerPolicyForEachIdentityKind(
        string configuredKind,
        string configuredValue,
        string authorKind,
        string authorValue)
    {
        var thread = new ReviewThread
        {
            ThreadId = "thread-1",
            Status = ReviewThreadStatus.Active,
            Comments =
            [
                new ReviewThreadComment
                {
                    Author = "reviewer",
                    AuthorIdentities = [new ReviewerIdentity { Kind = authorKind, Value = authorValue }],
                    Body = "Please fix this.",
                },
            ],
        };
        var fixture = Fixture.Create(
            ["agent-rework-requested"],
            [EligibleRun()],
            [thread],
            reviewerIdentities: [new ReviewerIdentity { Kind = configuredKind, Value = configuredValue }]);

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.True(detail.Eligible);
        Assert.True(Assert.Single(detail.Checks, check => check.Code == "reviewer-configuration").Passed);
        Assert.Equal(1, detail.FeedbackTrace!.ActiveThreadCount);
        Assert.Equal(1, detail.FeedbackTrace.AllowlistedReviewerThreadCount);
        Assert.Equal(1, detail.FeedbackTrace.NonEmptyContentThreadCount);
        Assert.Equal(1, detail.FeedbackTrace.QualifyingThreadCount);
    }

    [Fact]
    public async Task Revival_WithNonmatchingReviewerAuthor_ReportsFilteredStages()
    {
        var thread = new ReviewThread
        {
            ThreadId = "thread-1",
            Status = ReviewThreadStatus.Active,
            Comments =
            [
                new ReviewThreadComment
                {
                    Author = "other",
                    AuthorIdentities = [new ReviewerIdentity { Kind = "descriptor", Value = "aad.other" }],
                    Body = "Change requested",
                },
            ],
        };
        var fixture = Fixture.Create(
            ["agent-rework-requested"],
            [EligibleRun()],
            [thread],
            reviewerIdentities: [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.test" }]);

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.False(detail.Eligible);
        Assert.False(Assert.Single(detail.Checks, check => check.Code == "qualifying-feedback").Passed);
        Assert.True(Assert.Single(detail.Checks, check => check.Code == "reviewer-configuration").Passed);
        Assert.Equal(1, detail.FeedbackTrace!.ActiveThreadCount);
        Assert.Equal(0, detail.FeedbackTrace.AllowlistedReviewerThreadCount);
        Assert.Equal(0, detail.FeedbackTrace.NonEmptyContentThreadCount);
        Assert.Equal(0, detail.FeedbackTrace.QualifyingThreadCount);
    }

    [Fact]
    public async Task Revival_WithEmptyRepositoryReviewers_FailsClosedAndReportsUnconfigured()
    {
        var fixture = Fixture.Create(
            ["agent-rework-requested"],
            [EligibleRun()],
            [QualifyingThread()],
            reviewerIdentities: []);

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.False(detail.Eligible);
        Assert.False(Assert.Single(detail.Checks, check => check.Code == "reviewer-configuration").Passed);
        Assert.Equal("Revival fails closed because no reviewer is configured.",
            Assert.Single(detail.Checks, check => check.Code == "reviewer-configuration").Reason);
        Assert.Equal(FeedbackMarkerCheckStatus.NotAttempted, detail.FeedbackTrace!.MarkerStatus);
        Assert.False(detail.FeedbackTrace.ReviewerAllowlistConfigured);
        Assert.Equal(1, detail.FeedbackTrace.ActiveThreadCount);
        Assert.Equal(0, detail.FeedbackTrace.AllowlistedReviewerThreadCount);
        Assert.Equal(0, detail.FeedbackTrace.QualifyingThreadCount);
    }

    [Fact]
    public async Task Revival_WithInvalidAzureDevOpsReviewerIdentity_FailsClosed()
    {
        var fixture = Fixture.Create(
            ["agent-rework-requested"],
            [EligibleRun()],
            [QualifyingThread()],
            reviewerIdentities: [new ReviewerIdentity { Kind = "identityId", Value = "not-a-guid" }]);

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.False(detail.Eligible);
        Assert.False(detail.FeedbackTrace!.ReviewerAllowlistConfigured);
        Assert.Equal(0, detail.FeedbackTrace.AllowlistedReviewerThreadCount);
        Assert.Equal(0, detail.FeedbackTrace.QualifyingThreadCount);
    }

    [Fact]
    public async Task Revival_WithUnsupportedRepositoryProvider_FailsClosed()
    {
        var fixture = Fixture.Create(
            ["agent-rework-requested"],
            [EligibleRun()],
            [QualifyingThread()],
            reviewerProvider: "GitHub");

        var detail = await fixture.ExecuteAsync();

        Assert.NotNull(detail);
        Assert.False(detail.Eligible);
        Assert.False(detail.FeedbackTrace!.ReviewerAllowlistConfigured);
        Assert.Equal(0, detail.FeedbackTrace.QualifyingThreadCount);
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
            string repositoryKey = "orders",
            IReadOnlyList<ReviewerIdentity>? reviewerIdentities = null,
            string reviewerProvider = "AzureDevOps")
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
                    ReviewerIdentities = reviewerIdentities ??
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
            var connectionStore = Stub<IConnectionStore>.Create((method, _) => method.Name switch
            {
                nameof(IConnectionStore.GetByKeyAsync) => Task.FromResult<ConnectionProfile?>(new ConnectionProfile
                {
                    Key = "ado",
                    Provider = reviewerProvider,
                }),
                _ => throw new NotSupportedException(method.Name),
            });
            var options = new PullRequestDiagnosticOptions();
            var policyResolver = new ReviewerIdentityPolicyResolver(
                [new AzureDevOpsReviewerIdentityPolicy()]);
            var pipeline = new ReviewFeedbackFilterPipeline(
                labelSource,
                NullLogger<ReviewFeedbackFilterPipeline>.Instance,
                policyResolver);
            return new Fixture(new GetPullRequestDiagnosticsQueryHandler(
                discovery, repositoryStore, runStore, cycleStore, feedbackStore,
                feedbackSource, pipeline, options, connectionStore, policyResolver), repositoryKey);
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
