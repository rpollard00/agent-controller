using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for managed repository profiles.
/// Implementations are storage-agnostic; API and worker code must not
/// reference EF Core or any specific persistence technology directly.
/// </summary>
public interface IRepositoryStore
{
    /// <summary>Lists all profiles in deterministic key order.</summary>
    Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Gets a profile by its unique key, or <see langword="null"/> when absent.</summary>
    Task<RepositoryProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>Creates a profile. Returns <see langword="false"/> for a duplicate key.</summary>
    Task<bool> CreateAsync(RepositoryProfile profile, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the mutable profile data for an existing key.
    /// Returns <see langword="false"/> when the profile does not exist.
    /// </summary>
    Task<bool> UpdateAsync(RepositoryProfile profile, CancellationToken cancellationToken);

    /// <summary>Deletes a profile. Returns <see langword="false"/> when it does not exist.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or updates a profile. Retained for backward-compatible worker seeding.
    /// </summary>
    Task UpsertAsync(RepositoryProfile profile, CancellationToken cancellationToken);
}
