using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for managed Azure DevOps environment profiles.
/// Implementations store credential references only; callers must resolve the
/// referenced environment variable outside the persistence layer.
/// </summary>
public interface IAzureDevOpsEnvironmentStore
{
    /// <summary>
    /// Lists all profiles in deterministic key order.
    /// </summary>
    Task<IReadOnlyList<AzureDevOpsEnvironmentProfile>> ListAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets a profile by its stable key, or <see langword="null"/> when it does not exist.
    /// </summary>
    Task<AzureDevOpsEnvironmentProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a profile. Returns <see langword="false"/> when the key already exists.
    /// </summary>
    Task<bool> CreateAsync(
        AzureDevOpsEnvironmentProfile profile,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the mutable profile data for an existing key.
    /// Returns <see langword="false"/> when the key does not exist.
    /// </summary>
    Task<bool> UpdateAsync(
        AzureDevOpsEnvironmentProfile profile,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a profile by key. Returns <see langword="false"/> when the key does not exist.
    /// </summary>
    Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken);
}
