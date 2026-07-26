namespace AgentController.Domain.Tests;

public sealed class ReworkRequestTests
{
    [Fact]
    public void ReworkRequestMode_HasStablePersistedValues()
    {
        Assert.Equal(0, (int)ReworkRequestMode.Revival);
        Assert.Equal(1, (int)ReworkRequestMode.Assistance);
    }

    [Fact]
    public void PullRequestReference_CanonicalKeyUsesStableIdentityOnly()
    {
        var reference = new PullRequestReference
        {
            EnvironmentKey = " prod ",
            RepositoryKey = "Team/Repo",
            PullRequestId = "42",
            PullRequestUrl = "https://example.test/pullrequest/42",
            SourceBranch = "refs/heads/feature",
            TargetBranch = "refs/heads/main",
            SourceCommitSha = "abc123",
        };

        var updatedSnapshot = reference with
        {
            PullRequestUrl = "https://example.test/pullrequest/42?view=updated",
            SourceBranch = "feature",
            TargetBranch = "main",
            SourceCommitSha = "def456",
        };

        Assert.True(reference.HasCanonicalIdentity);
        Assert.Equal("PROD|TEAM%2FREPO|42", reference.CanonicalKey);
        Assert.Equal(reference.CanonicalKey, updatedSnapshot.CanonicalKey);
    }

    [Fact]
    public void PullRequestReference_MissingIdentityComponentHasNoCanonicalKey()
    {
        var reference = new PullRequestReference
        {
            RepositoryKey = "repo",
            PullRequestId = "42",
        };

        Assert.False(reference.HasCanonicalIdentity);
        Assert.Empty(reference.CanonicalKey);
    }

    [Fact]
    public void ReworkRecords_DefaultToRevivalAndAllowAssistanceWithoutRunLineage()
    {
        var pullRequest = new PullRequestReference
        {
            EnvironmentKey = "prod",
            RepositoryKey = "repo",
            PullRequestId = "42",
        };
        var feedback = new ReworkFeedback
        {
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = pullRequest,
            OriginatingRunId = null,
        };
        var cycle = new ReworkCycle
        {
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = pullRequest,
            PriorRunId = null,
        };
        var context = new ReworkContext
        {
            RequestMode = ReworkRequestMode.Assistance,
            PullRequest = pullRequest,
            PriorRunId = null,
        };

        Assert.Equal(ReworkRequestMode.Revival, new ReworkFeedback().RequestMode);
        Assert.Equal(ReworkRequestMode.Revival, new ReworkCycle().RequestMode);
        Assert.Equal(ReworkRequestMode.Revival, new ReworkContext().RequestMode);
        Assert.Null(feedback.OriginatingRunId);
        Assert.Null(cycle.PriorRunId);
        Assert.Null(context.PriorRunId);
        Assert.Same(pullRequest, feedback.PullRequest);
        Assert.Same(pullRequest, cycle.PullRequest);
        Assert.Same(pullRequest, context.PullRequest);
    }
}
