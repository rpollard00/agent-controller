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

    /// <summary>
    /// Run a diagnostic preflight that validates clone-readiness before
    /// the worker commits to a claim.
    ///
    /// Verifies:
    /// <list type="bullet">
    ///   <item>The configured clone URL is parseable.</item>
    ///   <item>The inferred transport has a present, correctly typed secret version.</item>
    ///   <item>A non-interactive, credentialed <c>git ls-remote</c> succeeds.</item>
    /// </list>
    ///
    /// The probe uses the same ephemeral PAT/SSH setup as cloning. On failure the
    /// returned <see cref="ClonePreflightResult"/> contains a stable category and
    /// safe credential-reference metadata, never plaintext secret values.
    /// </summary>
    Task<ClonePreflightResult> CheckClonePreflightAsync(
        RepositorySpec spec,
        CancellationToken cancellationToken
    );
}
