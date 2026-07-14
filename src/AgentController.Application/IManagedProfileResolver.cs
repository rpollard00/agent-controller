namespace AgentController.Application;

/// <summary>
/// Resolves the effective repository, Azure DevOps, and runtime profiles used by controller work.
/// Managed persistence takes precedence and static configuration remains the fallback.
/// </summary>
public interface IManagedProfileResolver
{
    /// <summary>Resolves all execution profiles for a repository key.</summary>
    Task<ResolvedControllerProfiles?> ResolveForRepositoryAsync(
        string repositoryKey,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Resolves one enabled work source environment profile by key. When the key is absent, the first
    /// enabled managed profile is selected deterministically. Static configuration is the fallback.
    /// </summary>
    Task<ResolvedWorkSourceEnvironment?> ResolveWorkSourceEnvironmentAsync(
        string? key,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists enabled managed work source environment profiles for polling. Static configuration is returned
    /// only when there are no enabled managed profiles.
    /// </summary>
    Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ListWorkSourceEnvironmentsAsync(
        CancellationToken cancellationToken
    );
}
