using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Port for cloning repositories and inspecting source control state.
/// First real implementation: AzureDevOpsReposSourceControlProvider.
/// Note: PR creation is owned by the agent runtime, not this provider.
/// </summary>
public interface ISourceControlProvider
{
    /// <summary>
    /// Clone a repository into the specified environment.
    /// </summary>
    Task<RepositoryCheckout> CloneAsync(
        RepositorySpec spec,
        EnvironmentHandle environment,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Inspect the current status of a source control resource
    /// (branch, commit, PR) for reconciliation purposes.
    /// </summary>
    Task<SourceControlStatus> GetStatusAsync(
        SourceControlRef sourceControlRef,
        CancellationToken cancellationToken
    );
}
