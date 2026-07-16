using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Application-layer profile resolution policy. Persisted repository data wins over appsettings;
/// associated environment profiles are used only while enabled, otherwise configuration is used.
/// </summary>
internal sealed class ManagedProfileResolver : IManagedProfileResolver
{
    private readonly IRepositoryStore _repositoryStore;
    private readonly IWorkSourceEnvironmentStore _workSourceEnvironmentStore;
    private readonly IRuntimeEnvironmentStore _runtimeEnvironmentStore;
    private readonly IRepositoryHostConnectionStore _repositoryHostConnectionStore;
    private readonly IConfiguredProfileSource _configuredProfiles;

    public ManagedProfileResolver(
        IRepositoryStore repositoryStore,
        IWorkSourceEnvironmentStore workSourceEnvironmentStore,
        IRuntimeEnvironmentStore runtimeEnvironmentStore,
        IRepositoryHostConnectionStore repositoryHostConnectionStore,
        IConfiguredProfileSource configuredProfiles
    )
    {
        _repositoryStore = repositoryStore;
        _workSourceEnvironmentStore = workSourceEnvironmentStore;
        _runtimeEnvironmentStore = runtimeEnvironmentStore;
        _repositoryHostConnectionStore = repositoryHostConnectionStore;
        _configuredProfiles = configuredProfiles;
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
        var repositoryIsManaged = repository is not null;
        repository ??= _configuredProfiles.GetRepository(normalizedKey);

        if (repository is null)
        {
            return null;
        }

        var configuredRuntime = _configuredProfiles.GetRuntimeEnvironment(repository);
        var runtime = configuredRuntime;
        var runtimeIsManaged = false;

        if (!string.IsNullOrWhiteSpace(repository.RuntimeEnvironmentKey))
        {
            var managedRuntime = await _runtimeEnvironmentStore.GetByKeyAsync(
                NormalizeKey(repository.RuntimeEnvironmentKey),
                cancellationToken
            );

            if (managedRuntime?.Enabled == true)
            {
                runtime = managedRuntime;
                runtimeIsManaged = true;
            }
        }

        var resolvedWorkSource = await ResolveWorkSourceEnvironmentAsync(
            // Work source environment is resolved independently from the repository host connection.
            // The legacy AzureDevOpsEnvironmentKey field has been removed; repositories now
            // reference a work source via the managed profile resolver's fallback logic.
            null,
            cancellationToken
        );
        var resolvedHostConnection = await ResolveRepositoryHostConnectionAsync(
            repository.RepositoryHostConnectionKey,
            cancellationToken
        );

        return new ResolvedControllerProfiles
        {
            Repository = repository,
            RuntimeEnvironment = runtime,
            WorkSourceEnvironment = resolvedWorkSource?.Profile,
            RepositoryHostConnection = resolvedHostConnection,
            RepositoryIsManaged = repositoryIsManaged,
            RuntimeEnvironmentIsManaged = runtimeIsManaged,
            WorkSourceEnvironmentIsManaged = resolvedWorkSource?.IsManaged == true,
        };
    }

    public async Task<ResolvedWorkSourceEnvironment?> ResolveWorkSourceEnvironmentAsync(
        string? key,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var managed = await _workSourceEnvironmentStore.GetByKeyAsync(
                NormalizeKey(key),
                cancellationToken
            );

            if (managed?.Enabled == true)
            {
                return new ResolvedWorkSourceEnvironment(managed, IsManaged: true);
            }
        }
        else
        {
            var managed = (
                await _workSourceEnvironmentStore.ListAsync(cancellationToken)
            ).FirstOrDefault(profile => profile.Enabled);

            if (managed is not null)
            {
                return new ResolvedWorkSourceEnvironment(managed, IsManaged: true);
            }
        }

        var configured = _configuredProfiles.GetWorkSourceEnvironment();
        return configured is null
            ? null
            : new ResolvedWorkSourceEnvironment(configured, IsManaged: false);
    }

    private async Task<ResolvedRepositoryHostConnection?> ResolveRepositoryHostConnectionAsync(
        string? key,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            var managed = await _repositoryHostConnectionStore.GetByKeyAsync(
                NormalizeKey(key),
                cancellationToken
            );

            if (managed?.Enabled == true)
            {
                return new ResolvedRepositoryHostConnection(managed, IsManaged: true);
            }
        }

        return null;
    }

    public async Task<
        IReadOnlyList<ResolvedWorkSourceEnvironment>
    > ListWorkSourceEnvironmentsAsync(CancellationToken cancellationToken)
    {
        var managed = (await _workSourceEnvironmentStore.ListAsync(cancellationToken))
            .Where(profile => profile.Enabled)
            .Select(profile => new ResolvedWorkSourceEnvironment(profile, IsManaged: true))
            .ToList();

        if (managed.Count > 0)
        {
            return managed;
        }

        var configured = _configuredProfiles.GetWorkSourceEnvironment();
        return configured is null
            ? []
            : [new ResolvedWorkSourceEnvironment(configured, IsManaged: false)];
    }

    private static string NormalizeKey(string? key) =>
        (key ?? string.Empty).Trim().ToLowerInvariant();
}
