namespace AgentController.Domain.Secrets;

/// <summary>
/// Provider-neutral read port for resolving secret values by name.
/// 
/// This is the runtime path used by consumers (e.g. work-source PAT resolution)
/// to obtain a plaintext secret value. It does not expose DB, encryption,
/// or storage concerns.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Resolve the plaintext value of a secret by name.
    /// </summary>
    /// <param name="name">
    /// The secret name to resolve.
    /// </param>
    /// <param name="version">
    /// Optional version number. When omitted, resolves to the latest version.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The plaintext secret value, or <c>null</c> if the secret (or version) does not exist.
    /// </returns>
    Task<string?> ResolveAsync(
        string name,
        int? version = null,
        CancellationToken cancellationToken = default
    );
}
