using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for cached repository profiles.
/// Repository profiles are primarily loaded from JSON configuration,
/// but resolved metadata may be cached for auditability and runtime lookup.
/// Implementations are storage-agnostic; API and worker code must not
/// reference EF Core or any specific persistence technology directly.
/// </summary>
public interface IRepositoryStore
{
    /// <summary>
    /// Get a cached repository profile by its unique key.
    /// Returns null if no profile is cached for the given key.
    /// </summary>
    Task<RepositoryProfile?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Insert or update a cached repository profile.
    /// </summary>
    Task UpsertAsync(
        RepositoryProfile profile,
        CancellationToken cancellationToken
    );
}
