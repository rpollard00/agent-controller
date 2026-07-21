using AgentController.Domain;
using AgentController.Domain.Secrets;
using AgentController.Infrastructure;

namespace AgentController.Infrastructure.Tests;

public sealed class GitClonePreflightProbeTests
{
    [Fact]
    public void ClassifyFailure_ReportsRejectedPatAndVersionWithoutSecretValue()
    {
        const string secretValue = "pat-value-must-not-appear";
        var resolution = new RepositoryCloneTransportResolution
        {
            Transport = CloneTransport.HttpsPat,
            CredentialSource = RepositoryCloneCredentialSource.ConnectionPersonalAccessToken,
            CredentialReference = SecretReference.ByNameAndVersion("clone-pat", 5),
        };

        var (code, reason) = GitClonePreflightProbe.ClassifyFailure(
            "fatal: Authentication failed for 'https://example.test/repo.git/'",
            CloneTransport.HttpsPat,
            resolution
        );

        Assert.Equal(ClonePreflightFailureCode.AuthenticationFailed, code);
        Assert.Contains("PAT secret 'clone-pat' (version 5)", reason, StringComparison.Ordinal);
        Assert.Contains("unexpired", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretValue, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyFailure_ReportsInvalidReferencedSshKey()
    {
        var resolution = new RepositoryCloneTransportResolution
        {
            Transport = CloneTransport.Ssh,
            CredentialSource = RepositoryCloneCredentialSource.SshKey,
            CredentialReference = SecretReference.ByName("deploy-key"),
        };

        var (code, reason) = GitClonePreflightProbe.ClassifyFailure(
            "Load key /private/path: invalid format",
            CloneTransport.Ssh,
            resolution
        );

        Assert.Equal(ClonePreflightFailureCode.CredentialInvalid, code);
        Assert.Contains(
            "SSH-key secret 'deploy-key' (latest version)",
            reason,
            StringComparison.Ordinal
        );
        Assert.Contains("format", reason, StringComparison.OrdinalIgnoreCase);
    }
}
