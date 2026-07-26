using AgentController.Application;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;

namespace AgentController.Infrastructure.Tests;

public sealed class PullRequestDiscoveryTests
{
    [Fact]
    public async Task NoOpDiscovery_ReturnsEmptyCollection()
    {
        var discovery = new NoOpPullRequestDiscovery();

        var snapshots = await discovery.ListActiveAsync(CancellationToken.None);

        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task LocalDiscovery_MapsActivePullRequestSnapshot()
    {
        var options = new LocalPullRequestOptions
        {
            Definitions =
            [
                new LocalPullRequestDefinition
                {
                    EnvironmentKey = " local ",
                    RepositoryKey = " repo-one ",
                    PullRequestId = " 42 ",
                    Title = " Improve validation ",
                    PullRequestUrl = " https://example.test/pullrequest/42 ",
                    SourceBranch = " feature/validation ",
                    TargetBranch = " main ",
                    SourceCommitSha = " abc123 ",
                    Labels = [" agent-assistance-requested ", "AGENT-ASSISTANCE-REQUESTED", "review"],
                    LinkedWorkItems =
                    [
                        new LocalPullRequestWorkItemDefinition
                        {
                            WorkItemId = " 101 ",
                            WorkItemUrl = " https://example.test/workitems/101 ",
                        },
                    ],
                },
            ],
        };
        var discovery = new LocalPullRequestDiscovery(
            new TestOptionsMonitor<LocalPullRequestOptions>(options)
        );

        var snapshots = await discovery.ListActiveAsync(CancellationToken.None);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("LOCAL|REPO-ONE|42", snapshot.CanonicalKey);
        Assert.Equal("Improve validation", snapshot.Title);
        Assert.Equal("repo-one", snapshot.RepositoryKey);
        Assert.Equal("feature/validation", snapshot.SourceBranch);
        Assert.Equal("main", snapshot.TargetBranch);
        Assert.Equal("abc123", snapshot.SourceCommitSha);
        Assert.Equal("https://example.test/pullrequest/42", snapshot.PullRequestUrl);
        Assert.Equal(["agent-assistance-requested", "review"], snapshot.Labels);
        var link = Assert.Single(snapshot.LinkedWorkItems);
        Assert.Equal("101", link.WorkItemId);
        Assert.Equal("https://example.test/workitems/101", link.WorkItemUrl);
    }

    [Fact]
    public async Task LocalDiscovery_SkipsInvalidAndDuplicateCanonicalIdentities()
    {
        var options = new LocalPullRequestOptions
        {
            Definitions =
            [
                new LocalPullRequestDefinition
                {
                    EnvironmentKey = "local",
                    RepositoryKey = "repo",
                    PullRequestId = "7",
                    Title = "First",
                },
                new LocalPullRequestDefinition
                {
                    EnvironmentKey = "LOCAL",
                    RepositoryKey = "REPO",
                    PullRequestId = "7",
                    Title = "Duplicate",
                },
                new LocalPullRequestDefinition
                {
                    EnvironmentKey = "local",
                    PullRequestId = "8",
                    Title = "Missing repository",
                },
            ],
        };
        var discovery = new LocalPullRequestDiscovery(
            new TestOptionsMonitor<LocalPullRequestOptions>(options)
        );

        var snapshots = await discovery.ListActiveAsync(CancellationToken.None);

        Assert.Equal("First", Assert.Single(snapshots).Title);
    }

    [Fact]
    public async Task LocalLabelMutation_IsCaseInsensitiveIdempotentAndVisibleToDiscovery()
    {
        var options = new TestOptionsMonitor<LocalPullRequestOptions>(
            new LocalPullRequestOptions
            {
                Definitions =
                [
                    new LocalPullRequestDefinition
                    {
                        EnvironmentKey = "local",
                        RepositoryKey = "repo",
                        PullRequestId = "42",
                        Labels = ["Agent-Assistance-Requested", "keep"],
                    },
                ],
            }
        );
        var state = new LocalPullRequestState(options);
        var discovery = new LocalPullRequestDiscovery(state);
        var mutator = new LocalPullRequestLabelMutator(state);
        var pullRequest = Assert.Single(
            await discovery.ListActiveAsync(CancellationToken.None)
        ).PullRequest;
        var mutation = new PullRequestLabelMutation
        {
            LabelsToAdd = [" KEEP ", " assistance-in-progress ", "ASSISTANCE-IN-PROGRESS"],
            LabelsToRemove = [" agent-assistance-requested ", "AGENT-ASSISTANCE-REQUESTED"],
        };

        await mutator.MutateAsync(pullRequest, mutation, CancellationToken.None);
        await mutator.MutateAsync(pullRequest, mutation, CancellationToken.None);

        var updated = Assert.Single(
            await discovery.ListActiveAsync(CancellationToken.None)
        );
        Assert.Equal(["keep", "assistance-in-progress"], updated.Labels);
    }

    [Fact]
    public async Task NoOpLabelMutation_HonorsCancellation()
    {
        var mutator = new NoOpPullRequestLabelMutator();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            mutator.MutateAsync(
                new(),
                new PullRequestLabelMutation(),
                cancellation.Token
            )
        );
    }

    [Fact]
    public async Task LocalDiscovery_HonorsCancellation()
    {
        var discovery = new LocalPullRequestDiscovery(
            new TestOptionsMonitor<LocalPullRequestOptions>(
                new LocalPullRequestOptions
                {
                    Definitions = [new LocalPullRequestDefinition()],
                }
            )
        );
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            discovery.ListActiveAsync(cancellation.Token)
        );
    }
}
