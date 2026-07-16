using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Application port for resolving and persisting secret values.
/// Implementations back the secret onto different storage mechanisms
/// (environment variables, databases, key vaults, etc.).
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Resolve a secret reference to its raw string value.
    /// </summary>
    /// <param name="reference">
    /// The opaque reference identifying the secret (e.g. {Kind:'EnvVar', Id:'ADO_PAT'}).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The resolved secret value, or <c>null</c> if the reference could not be resolved.
    /// </returns>
    Task<string?> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Write or update a secret value in the store.
    /// </summary>
    /// <param name="reference">
    /// The opaque reference identifying the secret.
    /// </param>
    /// <param name="value">The secret value to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A result indicating success or failure of the write operation.
    /// </returns>
    Task<SecretWriteResult> WriteAsync(
        SecretReference reference,
        string value,
        CancellationToken cancellationToken
    );
}
