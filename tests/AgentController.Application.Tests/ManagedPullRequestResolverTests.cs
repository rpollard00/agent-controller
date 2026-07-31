using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Application.Tests;

public sealed class ManagedPullRequestResolverTests
{
    [Fact]
    public void Resolve_UsesDiscoveredCanonicalIdentityAcrossUrlForms()
    {
        var discovered = new ManagedPullRequestSnapshot
        {
            PullRequest = new PullRequestReference
            {
                EnvironmentKey = "ado-production",
                RepositoryKey = "payments-profile",
                PullRequestId = "42",
                PullRequestUrl = "https://dev.azure.com/Example/Payments%20Project/_git/payments/pullrequest/42",
            },
        };

        var result = ManagedPullRequestResolver.Resolve(
            "https://EXAMPLE.visualstudio.com/payments%20project/_git/PAYMENTS/pullrequest/42/",
            [discovered],
            []
        );

        Assert.True(result.IsResolved);
        Assert.Same(discovered, result.Snapshot);
        Assert.Equal("payments-profile", result.Snapshot?.RepositoryKey);
    }

    [Fact]
    public void Resolve_WhenDiscoveryIsUnavailable_UsesProfileCoordinatesNotUrlRepositoryKey()
    {
        var profile = new RepositoryProfile
        {
            Key = "custom-payments-profile",
            Project = "Payments Project",
            RemoteIdentity = "payments-id",
            WebUrl = "https://dev.azure.com/example/Payments%20Project/_git/payments",
            RepositoryHostConnectionKey = "ado-production",
        };

        var result = ManagedPullRequestResolver.Resolve(
            "https://dev.azure.com/example/Payments%20Project/_git/payments/pullrequest/42",
            [],
            [profile]
        );

        Assert.True(result.IsResolved);
        Assert.Equal("ado-production", result.Snapshot?.PullRequest.EnvironmentKey);
        Assert.Equal("custom-payments-profile", result.Snapshot?.RepositoryKey);
        Assert.Equal("42", result.Snapshot?.PullRequestId);
    }

    [Fact]
    public void Resolve_WhenProfileCoordinatesAreAmbiguous_FailsClosed()
    {
        var profiles = new[]
        {
            Profile("payments-one"),
            Profile("payments-two"),
        };

        var result = ManagedPullRequestResolver.Resolve(
            "https://dev.azure.com/example/Payments%20Project/_git/payments/pullrequest/42",
            [],
            profiles
        );

        Assert.Equal(ManagedPullRequestResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.CandidateCount);
        Assert.False(result.IsResolved);
    }

    private static RepositoryProfile Profile(string key) => new()
    {
        Key = key,
        Project = "Payments Project",
        RemoteIdentity = "payments-id",
        WebUrl = "https://dev.azure.com/example/Payments%20Project/_git/payments",
        RepositoryHostConnectionKey = "ado-production",
    };
}
