using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Application-layer profile resolution policy for managed repository and environment profiles.
/// </summary>
internal sealed class ManagedProfileResolver : IManagedProfileResolver
{
    private readonly IRepositoryStore _repositoryStore;
    private readonly IWorkSourceEnvironmentStore _workSourceEnvironmentStore;
    private readonly IRuntimeEnvironmentStore _runtimeEnvironmentStore;
    private readonly IConnectionStore _connectionStore;

    public ManagedProfileResolver(
        IRepositoryStore repositoryStore,
        IWorkSourceEnvironmentStore workSourceEnvironmentStore,
        IRuntimeEnvironmentStore runtimeEnvironmentStore,
        IConnectionStore connectionStore
    )
    {
        _repositoryStore = repositoryStore;
        _workSourceEnvironmentStore = workSourceEnvironmentStore;
        _runtimeEnvironmentStore = runtimeEnvironmentStore;
        _connectionStore = connectionStore;
    }

    public async Task<ResolvedControllerProfiles?> ResolveForRepositoryAsync(
        string repositoryKey,
        CancellationToken cancellationToken
    )
    {
        var normalizedKey = NormalizeKey(repositoryKey);
        if (normalizedKey.Length == 0)
        {
            return null;
        }

        var repository = await _repositoryStore.GetByKeyAsync(normalizedKey, cancellationToken);
        if (repository is null || string.IsNullOrWhiteSpace(repository.RuntimeEnvironmentKey))
        {
            return null;
        }

        var runtime = await _runtimeEnvironmentStore.GetByKeyAsync(
            NormalizeKey(repository.RuntimeEnvironmentKey),
            cancellationToken
        );
        if (runtime?.Enabled != true)
        {
            return null;
        }

        var resolvedWorkSource = await ResolveWorkSourceEnvironmentAsync(
            // Work source environment is resolved independently from the repository host connection.
            // The legacy AzureDevOpsEnvironmentKey field has been removed; the first enabled managed
            // work source environment is used.
            null,
            cancellationToken
        );
        var resolvedRepoConnection = await ResolveRepositoryConnectionAsync(
            repository.RepositoryHostConnectionKey,
            cancellationToken
        );

        return new ResolvedControllerProfiles
        {
            Repository = repository,
            RuntimeEnvironment = runtime,
            WorkSourceEnvironment = resolvedWorkSource?.Profile,
            WorkSourceConnection = resolvedWorkSource?.Connection,
            RepositoryConnection = resolvedRepoConnection,
            RepositoryIsManaged = true,
            RuntimeEnvironmentIsManaged = true,
            WorkSourceEnvironmentIsManaged = resolvedWorkSource is not null,
        };
    }

    public async Task<ResolvedWorkSourceEnvironment?> ResolveWorkSourceEnvironmentAsync(
        string? key,
        CancellationToken cancellationToken
    )
    {
        WorkSourceEnvironmentProfile? profile;
        if (!string.IsNullOrWhiteSpace(key))
        {
            profile = await _workSourceEnvironmentStore.GetByKeyAsync(
                NormalizeKey(key),
                cancellationToken
            );
        }
        else
        {
            profile = (
                await _workSourceEnvironmentStore.ListAsync(cancellationToken)
            ).FirstOrDefault(profile => profile.Enabled);
        }

        if (profile?.Enabled != true)
        {
            return null;
        }

        var connection = await ResolveWorkSourceConnectionAsync(profile, cancellationToken);
        return new ResolvedWorkSourceEnvironment(profile, connection, IsManaged: true);
    }

    private async Task<ConnectionProfile?> ResolveWorkSourceConnectionAsync(
        WorkSourceEnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.ConnectionKey))
        {
            return null;
        }

        return await _connectionStore.GetByKeyAsync(
            NormalizeKey(profile.ConnectionKey),
            cancellationToken
        );
    }

    private async Task<ConnectionProfile?> ResolveRepositoryConnectionAsync(
        string? key,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return await _connectionStore.GetByKeyAsync(
            NormalizeKey(key),
            cancellationToken
        );
    }

    public async Task<
        IReadOnlyList<ResolvedWorkSourceEnvironment>
    > ListWorkSourceEnvironmentsAsync(CancellationToken cancellationToken)
    {
        var managedList = await _workSourceEnvironmentStore.ListAsync(cancellationToken);
        var managed = managedList
            .Where(profile => profile.Enabled)
            .Select(async profile => new ResolvedWorkSourceEnvironment(
                profile,
                await ResolveWorkSourceConnectionAsync(profile, cancellationToken),
                IsManaged: true
            ))
            .ToList();

        return await Task.WhenAll(managed);
    }

    private static string NormalizeKey(string? key) =>
        (key ?? string.Empty).Trim().ToLowerInvariant();
}
