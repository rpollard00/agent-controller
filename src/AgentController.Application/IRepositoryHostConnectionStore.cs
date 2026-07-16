using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for managed repository host connection profiles.
/// Implementations store secret references only; callers must resolve the
/// referenced secret through <see cref="ISecretStore"/> outside the persistence layer.
/// </summary>
public interface IRepositoryHostConnectionStore
{
    /// <summary>
    /// Lists all profiles in deterministic key order.
    /// </summary>
    Task<IReadOnlyList<RepositoryHostConnectionProfile>> ListAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets a profile by its stable key, or <see langword="null"/> when it does not exist.
    /// </summary>
    Task<RepositoryHostConnectionProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a profile. Returns <see langword="false"/> when the key already exists.
    /// </summary>
    Task<bool> CreateAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the mutable profile data for an existing key.
    /// Returns <see langword="false"/> when the key does not exist.
    /// </summary>
    Task<bool> UpdateAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a profile by key. Returns <see langword="false"/> when the key does not exist.
    /// </summary>
    Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken);
}
