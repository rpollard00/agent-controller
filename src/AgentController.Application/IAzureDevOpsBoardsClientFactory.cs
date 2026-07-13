using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Creates an Azure DevOps client for an effective environment profile.</summary>
public interface IAzureDevOpsBoardsClientFactory
{
    /// <summary>
    /// Creates a client using the profile's organization, project, and PAT environment-variable
    /// reference. The secret value is resolved only inside the infrastructure implementation.
    /// Configured fallback profiles may omit the reference when a one-off operation supplies its
    /// credential directly.
    /// </summary>
    IAzureDevOpsBoardsClient Create(AzureDevOpsEnvironmentProfile profile);
}
