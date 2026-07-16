using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Factory for creating authenticated <see cref="IAzureDevOpsBoardsClient"/> instances
/// from <see cref="RepositoryHostConnectionProfile"/> entries.
/// </summary>
internal interface IAzureDevOpsReposClientFactory
{
    /// <summary>
    /// Create an authenticated ADO client for the given repository host connection profile.
    /// </summary>
    /// <param name="profile">The repository host connection profile.</param>
    /// <param name="personalAccessToken">
    /// The resolved PAT value (already resolved through IManagedSecretStore by the caller).
    /// </param>
    IAzureDevOpsBoardsClient Create(
        RepositoryHostConnectionProfile profile,
        string personalAccessToken
    );
}
