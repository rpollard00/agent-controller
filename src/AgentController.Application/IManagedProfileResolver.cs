namespace AgentController.Application;

/// <summary>
/// Resolves the managed repository, work source, and runtime profiles used by controller work.
/// </summary>
public interface IManagedProfileResolver
{
    /// <summary>Resolves all execution profiles for a repository key.</summary>
    Task<ResolvedControllerProfiles?> ResolveForRepositoryAsync(
        string repositoryKey,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Resolves one enabled managed work source environment profile by key. When the key is absent,
    /// the first enabled managed profile is selected deterministically.
    /// </summary>
    Task<ResolvedWorkSourceEnvironment?> ResolveWorkSourceEnvironmentAsync(
        string? key,
        CancellationToken cancellationToken
    );

    /// <summary>Lists enabled managed work source environment profiles for polling.</summary>
    Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ListWorkSourceEnvironmentsAsync(
        CancellationToken cancellationToken
    );
}
