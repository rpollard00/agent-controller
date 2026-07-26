using AgentController.Domain;

namespace AgentController.Application.Tests;

public sealed class ManagedPullRequestDiscoveryContractTests
{
    [Fact]
    public void Snapshot_ExposesCanonicalPullRequestMetadataAndRelationships()
    {
        var snapshot = new ManagedPullRequestSnapshot
        {
            PullRequest = new PullRequestReference
            {
                EnvironmentKey = "production",
                RepositoryKey = "payments-api",
                PullRequestId = "42",
                PullRequestUrl = "https://example.test/pullrequest/42",
                SourceBranch = "feature/retry",
                TargetBranch = "main",
                SourceCommitSha = "abc123",
            },
            Title = "Add retry handling",
            Labels = ["agent-assistance-requested"],
            LinkedWorkItems =
            [
                new PullRequestWorkItemReference
                {
                    WorkItemId = "101",
                    WorkItemUrl = "https://example.test/workitems/101",
                },
            ],
        };

        Assert.Equal("PRODUCTION|PAYMENTS-API|42", snapshot.CanonicalKey);
        Assert.Equal("production", snapshot.EnvironmentKey);
        Assert.Equal("payments-api", snapshot.RepositoryKey);
        Assert.Equal("42", snapshot.PullRequestId);
        Assert.Equal("https://example.test/pullrequest/42", snapshot.PullRequestUrl);
        Assert.Equal("feature/retry", snapshot.SourceBranch);
        Assert.Equal("main", snapshot.TargetBranch);
        Assert.Equal("abc123", snapshot.SourceCommitSha);
        Assert.Equal("Add retry handling", snapshot.Title);
        Assert.Equal("agent-assistance-requested", Assert.Single(snapshot.Labels));
        Assert.Equal("101", Assert.Single(snapshot.LinkedWorkItems).WorkItemId);
    }

    [Fact]
    public void Snapshot_DefaultsToEmptyCollectionsAndIdentity()
    {
        var snapshot = new ManagedPullRequestSnapshot();

        Assert.Equal(string.Empty, snapshot.CanonicalKey);
        Assert.Empty(snapshot.Labels);
        Assert.Empty(snapshot.LinkedWorkItems);
    }
}
