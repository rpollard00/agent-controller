using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for unified connection profiles.
/// Implementations store named-secret references only; callers must resolve the
/// referenced secret through <see cref="AgentController.Domain.Secrets.ISecretStore"/>
/// outside the persistence layer.
/// </summary>
public interface IConnectionStore
{
    /// <summary>
    /// Lists all connection profiles in deterministic key order.
    /// </summary>
    Task<IReadOnlyList<ConnectionProfile>> ListAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets a connection profile by its stable key, or <see langword="null"/> when it does not exist.
    /// </summary>
    Task<ConnectionProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a connection profile. Returns <see langword="false"/> when the key already exists.
    /// </summary>
    Task<bool> CreateAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the mutable profile data for an existing key.
    /// Returns <see langword="false"/> when the key does not exist.
    /// </summary>
    Task<bool> UpdateAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a connection profile by key. Returns <see langword="false"/> when the key does not exist.
    /// </summary>
    Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken);
}
