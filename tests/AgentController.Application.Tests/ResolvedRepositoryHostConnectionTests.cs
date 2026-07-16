namespace AgentController.Application.Tests;

public class ResolvedRepositoryHostConnectionTests
{
    [Fact]
    public void ResolvedRepositoryHostConnection_RecordEquality()
    {
        var profile = new AgentController.Domain.RepositoryHostConnectionProfile
        {
            Key = "ado-repos",
            Provider = "AzureDevOpsRepos",
            OrganizationUrl = "https://dev.azure.com/org",
            Project = "Project",
            PersonalAccessTokenReference =
                AgentController.Domain.Secrets.SecretReference.ByName("ado-pat"),
        };

        var resolvedManaged = new ResolvedRepositoryHostConnection(profile, true);
        var resolvedConfigured = new ResolvedRepositoryHostConnection(profile, false);

        Assert.True(resolvedManaged.IsManaged);
        Assert.False(resolvedConfigured.IsManaged);
        Assert.NotEqual(resolvedManaged, resolvedConfigured);
        Assert.Same(profile, resolvedManaged.Profile);
    }

    [Fact]
    public void ResolvedRepositoryHostConnection_EqualProfilesWithSameManagedFlag()
    {
        var timestamp = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var profileA = new AgentController.Domain.RepositoryHostConnectionProfile
        {
            Key = "ado-repos",
            Provider = "AzureDevOpsRepos",
            OrganizationUrl = "https://dev.azure.com/org",
            Project = "Project",
            PersonalAccessTokenReference =
                AgentController.Domain.Secrets.SecretReference.ByName("ado-pat"),
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };

        var profileB = new AgentController.Domain.RepositoryHostConnectionProfile
        {
            Key = "ado-repos",
            Provider = "AzureDevOpsRepos",
            OrganizationUrl = "https://dev.azure.com/org",
            Project = "Project",
            PersonalAccessTokenReference =
                AgentController.Domain.Secrets.SecretReference.ByName("ado-pat"),
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };

        var resolvedA = new ResolvedRepositoryHostConnection(profileA, true);
        var resolvedB = new ResolvedRepositoryHostConnection(profileB, true);

        Assert.Equal(resolvedA, resolvedB);
    }
}
