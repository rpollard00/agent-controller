using AgentController.Application;

namespace AgentController.Infrastructure;

/// <summary>
/// Abstract base for constructing authenticated <see cref="IAzureDevOpsBoardsClient"/>
/// instances from organization URL, project, and resolved PAT.
///
/// Used by both the work-source (Boards) and repo-host (Repos) ADO paths
/// so they share one authenticated ADO client construction path.
/// Concrete implementation: <see cref="DefaultAzureDevOpsClientFactory"/>.
/// </summary>
internal abstract class AzureDevOpsClientFactory
{
    /// <summary>
    /// Create an authenticated ADO client from connection parameters.
    /// </summary>
    /// <param name="organizationUrl">The Azure DevOps organization URL.</param>
    /// <param name="project">The Azure DevOps project name.</param>
    /// <param name="personalAccessToken">The resolved PAT value (already resolved through ISecretStore).</param>
    public abstract IAzureDevOpsBoardsClient Create(
        string organizationUrl,
        string project,
        string personalAccessToken
    );
}
