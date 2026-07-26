using AgentController.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Application.Tests;

public sealed class ReworkRequestContractTests
{
    [Fact]
    public void FeedbackContracts_CarryAssistanceModeAndPullRequestWithoutOriginatingRun()
    {
        var pullRequest = new PullRequestReference
        {
            EnvironmentKey = "prod",
            RepositoryKey = "repo",
            PullRequestId = "42",
            PullRequestUrl = "https://example.test/pullrequest/42",
            SourceBranch = "feature",
            TargetBranch = "main",
            SourceCommitSha = "abc123",
        };
        var prUnderTest = new PrUnderTest
        {
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = pullRequest,
            OriginatingRunId = null,
            WorkItemId = null,
        };
        var signal = new ReworkSignal
        {
            RequestMode = prUnderTest.RequestMode,
            PullRequest = prUnderTest.PullRequest,
            OriginatingRunId = prUnderTest.OriginatingRunId,
        };

        Assert.Equal(ReworkRequestMode.Assistance, prUnderTest.RequestMode);
        Assert.Equal(ReworkRequestMode.Assistance, signal.RequestMode);
        Assert.Same(pullRequest, signal.PullRequest);
        Assert.Null(prUnderTest.OriginatingRunId);
        Assert.Null(signal.OriginatingRunId);
        Assert.Null(prUnderTest.WorkItemId);
    }

    [Fact]
    public async Task FilterPipeline_PreservesRequestModeAndPullRequestReference()
    {
        var pullRequest = new PullRequestReference
        {
            EnvironmentKey = "prod",
            RepositoryKey = "repo",
            PullRequestId = "42",
        };
        var query = new FeedbackQuery
        {
            OpenPrs =
            [
                new PrUnderTest
                {
                    RequestMode = ReworkRequestMode.Assistance,
                    PullRequest = pullRequest,
                    PullRequestId = "42",
                },
            ],
            AllowedReviewers = new HashSet<string> { "reviewer@example.test" },
        };
        var signals = new ReworkSignal[]
        {
            new()
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequest = pullRequest,
                PullRequestId = "42",
                Threads =
                [
                    new ReviewThread
                    {
                        ThreadId = "thread-1",
                        Status = ReviewThreadStatus.Active,
                        Comments =
                        [
                            new ReviewThreadComment
                            {
                                Author = "reviewer@example.test",
                                Body = "Please clean this up.",
                                CreatedAt = DateTimeOffset.UtcNow,
                            },
                        ],
                    },
                ],
            },
        };
        var pipeline = new ReviewFeedbackFilterPipeline(
            new MarkerLabelSource(),
            NullLogger<ReviewFeedbackFilterPipeline>.Instance
        );

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        var filtered = Assert.Single(result);
        Assert.Equal(ReworkRequestMode.Assistance, filtered.RequestMode);
        Assert.Same(pullRequest, filtered.PullRequest);
    }

    [Fact]
    public void FeedbackContracts_DefaultToRevival()
    {
        Assert.Equal(ReworkRequestMode.Revival, new PrUnderTest().RequestMode);
        Assert.Equal(ReworkRequestMode.Revival, new ReworkSignal().RequestMode);
    }

    private sealed class MarkerLabelSource : IPrLabelSource
    {
        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<PrLabel>>(
            [new PrLabel { Name = "agent-rework-requested" }]
        );
    }
}
