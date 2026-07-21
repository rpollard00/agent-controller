using AgentController.Domain.Secrets;

namespace AgentController.Domain.Tests;

public sealed class RepositoryCloneTransportResolverTests
{
    [Theory]
    [InlineData("git@example.test:owner/repo.git")]
    [InlineData("ssh://git@example.test/owner/repo.git")]
    public void Resolve_SshUrlWithoutKey_ReturnsBlockingConfiguration(string cloneUrl)
    {
        var resolution = RepositoryCloneTransportResolver.Resolve(
            new RepositoryProfile { CloneUrl = cloneUrl }
        );

        Assert.Equal(CloneTransport.Ssh, resolution.Transport);
        Assert.Equal(RepositoryCloneCredentialSource.SshKey, resolution.CredentialSource);
        Assert.Null(resolution.CredentialReference);
        Assert.False(resolution.IsReady);
        var issue = Assert.Single(resolution.BlockingIssues);
        Assert.Equal(RepositoryCloneTransportIssueCode.MissingSshKeyReference, issue.Code);
        Assert.Equal("sshKeyReference", issue.Field);
    }

    [Fact]
    public void Resolve_SshUrlWithPinnedKey_ReturnsReadyCredentialReference()
    {
        var reference = SecretReference.ByNameAndVersion("deploy-key", 3);
        var resolution = RepositoryCloneTransportResolver.Resolve(
            new RepositoryProfile
            {
                CloneUrl = "git@example.test:owner/repo.git",
                SshKeyReference = reference,
            }
        );

        Assert.Equal(CloneTransport.Ssh, resolution.Transport);
        Assert.Equal(reference, resolution.CredentialReference);
        Assert.True(resolution.IsReady);
        Assert.Empty(resolution.BlockingIssues);
    }

    [Fact]
    public void Resolve_HttpsUrlWithoutConnection_ReturnsBlockingConfiguration()
    {
        var resolution = RepositoryCloneTransportResolver.Resolve(
            new RepositoryProfile { CloneUrl = "https://example.test/owner/repo.git" }
        );

        Assert.Equal(CloneTransport.HttpsPat, resolution.Transport);
        Assert.Equal(
            RepositoryCloneCredentialSource.ConnectionPersonalAccessToken,
            resolution.CredentialSource
        );
        Assert.False(resolution.IsReady);
        Assert.Equal(
            RepositoryCloneTransportIssueCode.MissingRepositoryHostConnection,
            Assert.Single(resolution.BlockingIssues).Code
        );
    }

    [Fact]
    public void Resolve_HttpsUrlWithMissingConnection_ReportsReferencedConnection()
    {
        var resolution = RepositoryCloneTransportResolver.Resolve(
            new RepositoryProfile
            {
                CloneUrl = "https://example.test/owner/repo.git",
                RepositoryHostConnectionKey = "missing-host",
            }
        );

        var issue = Assert.Single(resolution.BlockingIssues);
        Assert.Equal(
            RepositoryCloneTransportIssueCode.RepositoryHostConnectionNotFound,
            issue.Code
        );
        Assert.Contains("missing-host", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_HttpsUrlUsesPinnedPatFromConnection()
    {
        var reference = SecretReference.ByNameAndVersion("host-pat", 2);
        var repository = new RepositoryProfile
        {
            CloneUrl = "https://example.test/owner/repo.git",
            RepositoryHostConnectionKey = "host-primary",
        };
        var connection = new ConnectionProfile
        {
            Key = "host-primary",
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                PersonalAccessTokenReference = reference,
            },
        };

        var resolution = RepositoryCloneTransportResolver.Resolve(repository, connection);

        Assert.Equal(CloneTransport.HttpsPat, resolution.Transport);
        Assert.Equal(reference, resolution.CredentialReference);
        Assert.True(resolution.IsReady);
        Assert.Empty(resolution.BlockingIssues);
    }

    [Fact]
    public void Resolve_HttpsUrlReportsConnectionWithoutPat()
    {
        var repository = new RepositoryProfile
        {
            CloneUrl = "https://example.test/owner/repo.git",
            RepositoryHostConnectionKey = "host-primary",
        };
        var connection = new ConnectionProfile
        {
            Key = "host-primary",
            ProviderSettings = new AzureDevOpsConnectionSettings(),
        };

        var resolution = RepositoryCloneTransportResolver.Resolve(repository, connection);

        Assert.Equal(
            RepositoryCloneTransportIssueCode.MissingPersonalAccessTokenReference,
            Assert.Single(resolution.BlockingIssues).Code
        );
        Assert.False(resolution.IsReady);
    }

    [Fact]
    public void Resolve_LocalPathDoesNotRequireCredentials()
    {
        var resolution = RepositoryCloneTransportResolver.Resolve(
            new RepositoryProfile { CloneUrl = "/srv/repositories/example" }
        );

        Assert.Equal(CloneTransport.Local, resolution.Transport);
        Assert.Equal(RepositoryCloneCredentialSource.None, resolution.CredentialSource);
        Assert.Null(resolution.CredentialReference);
        Assert.True(resolution.IsReady);
    }

    [Fact]
    public void Resolve_UnsupportedUrlReturnsBlockingConfiguration()
    {
        var resolution = RepositoryCloneTransportResolver.Resolve(
            new RepositoryProfile { CloneUrl = "ftp://example.test/owner/repo.git" }
        );

        Assert.Equal(CloneTransport.Unspecified, resolution.Transport);
        Assert.Equal(
            RepositoryCloneTransportIssueCode.UnsupportedCloneUrl,
            Assert.Single(resolution.BlockingIssues).Code
        );
        Assert.False(resolution.IsReady);
    }

    [Fact]
    public void Resolve_ReportsExplicitTransportThatConflictsWithUrl()
    {
        var resolution = RepositoryCloneTransportResolver.Resolve(
            new RepositoryProfile
            {
                CloneUrl = "https://example.test/owner/repo.git",
                Transport = CloneTransport.Ssh,
                SshKeyReference = SecretReference.ByName("deploy-key"),
            }
        );

        Assert.Equal(CloneTransport.Ssh, resolution.Transport);
        Assert.Contains(
            resolution.BlockingIssues,
            issue => issue.Code == RepositoryCloneTransportIssueCode.ConfiguredTransportMismatch
        );
        Assert.False(resolution.IsReady);
    }
}
