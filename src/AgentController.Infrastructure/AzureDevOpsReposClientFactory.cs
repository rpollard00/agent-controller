using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Creates short-lived <see cref="IAzureDevOpsBoardsClient"/> instances from
/// <see cref="RepositoryHostConnectionProfile"/> entries. Delegates client
/// construction to the shared <see cref="AzureDevOpsClientFactory"/>.
/// </summary>
internal sealed class AzureDevOpsReposClientFactory(
    AzureDevOpsClientFactory clientFactory
) : IAzureDevOpsReposClientFactory
{
    /// <summary>
    /// Create an authenticated ADO client for the given repository host connection profile.
    /// The PAT is resolved via <see cref="Domain.Secrets.ISecretStore"/> by the caller and passed in.
    /// </summary>
    public IAzureDevOpsBoardsClient Create(
        RepositoryHostConnectionProfile profile,
        string personalAccessToken
    )
    {
        if (!profile.Enabled)
        {
            throw new InvalidOperationException(
                $"Repository host connection '{profile.Key}' is disabled."
            );
        }

        // Delegate to the shared client factory.
        return clientFactory.Create(
            profile.OrganizationUrl,
            profile.Project,
            personalAccessToken
        );
    }
}
