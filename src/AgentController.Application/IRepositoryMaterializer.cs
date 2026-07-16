using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Port for materializing a resolved repository profile into a local workspace.
/// 
/// Materialization encompasses:
/// <list type="bullet">
///   <item>Resolving credentials from the secrets manifest.</item>
///   <item>Cloning the repository using the appropriate transport (HTTPS+PAT, SSH, Local).</item>
/// </list>
///
/// Implementations are transport-agnostic at the contract level but may be
/// environment-specific (e.g. local filesystem, container, VM).
/// Future implementations: <c>ContainerRepositoryMaterializer</c>,
/// <c>VmRepositoryMaterializer</c>.
/// </summary>
public interface IRepositoryMaterializer
{
    /// <summary>
    /// Materialize a resolved repository profile into the specified environment workspace.
    /// 
    /// This method:
    /// <list type="number">
    ///   <item>Clones the repository into <c>{environment.RootPath}/repo</c>
    ///   using the configured transport.</item>
    ///   <item>For HTTPS+PAT: injects credentials via <c>git http.extraHeader</c>
    ///   (not URL embedding).</item>
    ///   <item>For SSH: uses the configured SSH key and <c>GIT_SSH_COMMAND</c>.</item>
    ///   <item>Returns the local checkout path and checkout metadata.</item>
    /// </list>
    /// </summary>
    /// <param name="profile">
    /// The resolved repository profile containing clone URL, branch, and transport.
    /// </param>
    /// <param name="manifest">
    /// The resolved secrets manifest containing credential material for this scope.
    /// Secrets are consumed atomically and must not leak across process boundaries.
    /// </param>
    /// <param name="environment">
    /// The target environment workspace where the repository will be cloned.
    /// The clone target is <c>{environment.RootPath}/repo</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A result containing the local path and checkout metadata.
    /// </returns>
    Task<RepositoryMaterializationResult> MaterializeAsync(
        RepositoryProfile profile,
        ResolvedSecretsManifest manifest,
        EnvironmentHandle environment,
        CancellationToken cancellationToken
    );
}
