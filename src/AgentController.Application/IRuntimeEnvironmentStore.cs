using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for managed runtime environment profiles.
/// Environment-variable forwarding stores target and source variable names only;
/// implementations must never resolve or persist their values.
/// </summary>
public interface IRuntimeEnvironmentStore
{
    /// <summary>Lists all profiles in deterministic key order.</summary>
    Task<IReadOnlyList<RuntimeEnvironmentProfile>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Gets a profile by its stable key, or <see langword="null"/> when absent.</summary>
    Task<RuntimeEnvironmentProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>Creates a profile. Returns <see langword="false"/> for a duplicate key.</summary>
    Task<bool> CreateAsync(RuntimeEnvironmentProfile profile, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the mutable profile data for an existing key.
    /// Returns <see langword="false"/> when the profile does not exist.
    /// </summary>
    Task<bool> UpdateAsync(RuntimeEnvironmentProfile profile, CancellationToken cancellationToken);

    /// <summary>Deletes a profile. Returns <see langword="false"/> when it does not exist.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);
}
