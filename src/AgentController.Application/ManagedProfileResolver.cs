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
    private readonly IConfiguredProfileSource _configuredProfiles;

    public ManagedProfileResolver(
        IRepositoryStore repositoryStore,
        IWorkSourceEnvironmentStore workSourceEnvironmentStore,
        IRuntimeEnvironmentStore runtimeEnvironmentStore,
        IConfiguredProfileSource configuredProfiles
    )
    {
        _repositoryStore = repositoryStore;
        _workSourceEnvironmentStore = workSourceEnvironmentStore;
        _runtimeEnvironmentStore = runtimeEnvironmentStore;
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

        var resolvedAzureDevOps = await ResolveAzureDevOpsEnvironmentAsync(
            repository.AzureDevOpsEnvironmentKey,
            cancellationToken
        );

        return new ResolvedControllerProfiles
        {
            Repository = repository,
            RuntimeEnvironment = runtime,
            AzureDevOpsEnvironment = resolvedAzureDevOps?.Profile,
            RepositoryIsManaged = repositoryIsManaged,
            RuntimeEnvironmentIsManaged = runtimeIsManaged,
            AzureDevOpsEnvironmentIsManaged = resolvedAzureDevOps?.IsManaged == true,
        };
    }

    public async Task<ResolvedAzureDevOpsEnvironment?> ResolveAzureDevOpsEnvironmentAsync(
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
                return new ResolvedAzureDevOpsEnvironment(managed, IsManaged: true);
            }
        }
        else
        {
            var managed = (
                await _workSourceEnvironmentStore.ListAsync(cancellationToken)
            ).FirstOrDefault(profile => profile.Enabled);

            if (managed is not null)
            {
                return new ResolvedAzureDevOpsEnvironment(managed, IsManaged: true);
            }
        }

        var configured = _configuredProfiles.GetAzureDevOpsEnvironment();
        return configured is null
            ? null
            : new ResolvedAzureDevOpsEnvironment(configured, IsManaged: false);
    }

    public async Task<
        IReadOnlyList<ResolvedAzureDevOpsEnvironment>
    > ListAzureDevOpsEnvironmentsAsync(CancellationToken cancellationToken)
    {
        var managed = (await _workSourceEnvironmentStore.ListAsync(cancellationToken))
            .Where(profile => profile.Enabled)
            .Select(profile => new ResolvedAzureDevOpsEnvironment(profile, IsManaged: true))
            .ToList();

        if (managed.Count > 0)
        {
            return managed;
        }

        var configured = _configuredProfiles.GetAzureDevOpsEnvironment();
        return configured is null
            ? []
            : [new ResolvedAzureDevOpsEnvironment(configured, IsManaged: false)];
    }

    private static string NormalizeKey(string? key) =>
        (key ?? string.Empty).Trim().ToLowerInvariant();
}
