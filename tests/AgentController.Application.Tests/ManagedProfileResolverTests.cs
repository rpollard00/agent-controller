using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Application.Tests;

public sealed class ManagedProfileResolverTests
{
    [Fact]
    public async Task ResolveForRepositoryAsync_PrefersManagedRepositoryAndEnabledAssociations()
    {
        var managedRepository = Repository(
            "orders",
            "https://managed.example/orders.git",
            azureDevOpsKey: "managed-ado",
            runtimeKey: "managed-runtime"
        );
        var managedRuntime = Runtime("managed-runtime", enabled: true, "/managed/workspaces");
        var managedAzureDevOps = AzureDevOps("managed-ado", enabled: true, "ManagedProject");
        var fallback = new StubConfiguredProfileSource(
            Repository("orders", "https://configured.example/orders.git"),
            Runtime("configured-runtime", enabled: true, "/configured/workspaces"),
            AzureDevOps("configured-ado", enabled: true, "ConfiguredProject")
        );
        var resolver = CreateResolver(
            [managedRepository],
            [managedAzureDevOps],
            [managedRuntime],
            fallback
        );

        var result = await resolver.ResolveForRepositoryAsync(" ORDERS ", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.RepositoryIsManaged);
        Assert.True(result.RuntimeEnvironmentIsManaged);
        Assert.True(result.WorkSourceEnvironmentIsManaged);
        Assert.Equal("https://managed.example/orders.git", result.Repository.CloneUrl);
        Assert.Equal("managed-runtime", result.RuntimeEnvironment.Key);
        Assert.Equal("managed-ado", result.WorkSourceEnvironment?.Key);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ResolveForRepositoryAsync_DisabledOrMissingAssociationsUseConfiguredFallback(
        bool runtimeExists,
        bool azureDevOpsExists
    )
    {
        var repository = Repository(
            "orders",
            "https://managed.example/orders.git",
            azureDevOpsKey: "managed-ado",
            runtimeKey: "managed-runtime"
        );
        var configuredRuntime = Runtime("legacy-runtime", enabled: true, "/legacy/workspaces");
        var configuredAzureDevOps = AzureDevOps("appsettings", enabled: true, "LegacyProject");
        var resolver = CreateResolver(
            [repository],
            azureDevOpsExists
                ? [AzureDevOps("managed-ado", enabled: false, "DisabledProject")]
                : [],
            runtimeExists ? [Runtime("managed-runtime", enabled: false, "/disabled")] : [],
            new StubConfiguredProfileSource(null, configuredRuntime, configuredAzureDevOps)
        );

        var result = await resolver.ResolveForRepositoryAsync("orders", CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.RuntimeEnvironmentIsManaged);
        Assert.False(result.WorkSourceEnvironmentIsManaged);
        Assert.Same(configuredRuntime, result.RuntimeEnvironment);
        Assert.Same(configuredAzureDevOps, result.WorkSourceEnvironment);
    }

    [Fact]
    public async Task ResolveForRepositoryAsync_UsesNewManagedRepositoryWithoutConfiguredEntry()
    {
        var managedRepository = Repository(
            "new-repository",
            "https://managed.example/new.git",
            runtimeKey: "local-pi"
        );
        var managedRuntime = Runtime("local-pi", enabled: true, "/managed/root");
        var resolver = CreateResolver(
            [managedRepository],
            [],
            [managedRuntime],
            new StubConfiguredProfileSource(
                repository: null,
                runtime: Runtime("fallback", enabled: true, workspaceRoot: null),
                workSourceEnvironment: null
            )
        );

        var result = await resolver.ResolveForRepositoryAsync(
            "new-repository",
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.True(result.RepositoryIsManaged);
        Assert.True(result.RuntimeEnvironmentIsManaged);
        Assert.Equal("/managed/root", result.RuntimeEnvironment.EnvironmentSettings.WorkspaceRoot);
    }

    [Fact]
    public async Task ListWorkSourceEnvironmentsAsync_ReturnsOnlyEnabledManagedProfilesInStoreOrder()
    {
        var resolver = CreateResolver(
            [],
            [
                AzureDevOps("alpha", enabled: true, "Alpha"),
                AzureDevOps("disabled", enabled: false, "Disabled"),
                AzureDevOps("zeta", enabled: true, "Zeta"),
            ],
            [],
            new StubConfiguredProfileSource(
                null,
                Runtime("fallback", enabled: true, workspaceRoot: null),
                AzureDevOps("appsettings", enabled: true, "Configured")
            )
        );

        var environments = await resolver.ListWorkSourceEnvironmentsAsync(CancellationToken.None);

        Assert.Equal(
            ["alpha", "zeta"],
            environments.Select(environment => environment.Profile.Key)
        );
        Assert.All(environments, environment => Assert.True(environment.IsManaged));
    }

    [Fact]
    public async Task ListWorkSourceEnvironmentsAsync_NoEnabledManagedProfilesUsesAppsettings()
    {
        var configured = AzureDevOps("appsettings", enabled: true, "Configured");
        var resolver = CreateResolver(
            [],
            [AzureDevOps("disabled", enabled: false, "Disabled")],
            [],
            new StubConfiguredProfileSource(
                null,
                Runtime("fallback", enabled: true, workspaceRoot: null),
                configured
            )
        );

        var environments = await resolver.ListWorkSourceEnvironmentsAsync(CancellationToken.None);

        var environment = Assert.Single(environments);
        Assert.False(environment.IsManaged);
        Assert.Same(configured, environment.Profile);
    }

    private static ManagedProfileResolver CreateResolver(
        IReadOnlyList<RepositoryProfile> repositories,
        IReadOnlyList<WorkSourceEnvironmentProfile> workSourceEnvironments,
        IReadOnlyList<RuntimeEnvironmentProfile> runtimes,
        IConfiguredProfileSource configuredProfiles
    )
    {
        return new ManagedProfileResolver(
            new RepositoryStore(repositories),
            new WorkSourceStore(workSourceEnvironments),
            new RuntimeStore(runtimes),
            configuredProfiles
        );
    }

    private static RepositoryProfile Repository(
        string key,
        string cloneUrl,
        string? azureDevOpsKey = null,
        string? runtimeKey = null
    )
    {
        return new RepositoryProfile
        {
            Key = key,
            CloneUrl = cloneUrl,
            DefaultBranch = "main",
            AzureDevOpsEnvironmentKey = azureDevOpsKey,
            RuntimeEnvironmentKey = runtimeKey,
        };
    }

    private static RuntimeEnvironmentProfile Runtime(
        string key,
        bool enabled,
        string? workspaceRoot
    )
    {
        return new RuntimeEnvironmentProfile
        {
            Key = key,
            DisplayName = key,
            Enabled = enabled,
            EnvironmentProvider = "LocalWorkspace",
            EnvironmentSettings = new EnvironmentProviderSettings { WorkspaceRoot = workspaceRoot },
            RuntimeProvider = "PiMateria",
        };
    }

    private static WorkSourceEnvironmentProfile AzureDevOps(
        string key,
        bool enabled,
        string project
    )
    {
        return new WorkSourceEnvironmentProfile
        {
            Key = key,
            DisplayName = key,
            Enabled = enabled,
            Provider = "AzureDevOpsBoards",
            TagPrefix = "agent",
            OrganizationUrl = "https://dev.azure.com/example",
            Project = project,
            PatEnvironmentVariable = "TEST_ADO_PAT",
        };
    }

    private sealed class StubConfiguredProfileSource(
        RepositoryProfile? repository,
        RuntimeEnvironmentProfile runtime,
        WorkSourceEnvironmentProfile? workSourceEnvironment
    ) : IConfiguredProfileSource
    {
        public RepositoryProfile? GetRepository(string key) =>
            repository?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true
                ? repository
                : null;

        public RuntimeEnvironmentProfile GetRuntimeEnvironment(
            RepositoryProfile repositoryProfile
        ) => runtime;

        public WorkSourceEnvironmentProfile? GetWorkSourceEnvironment() => workSourceEnvironment;
    }

    private sealed class RepositoryStore(IReadOnlyList<RepositoryProfile> profiles)
        : IRepositoryStore
    {
        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult(profiles);

        public Task<RepositoryProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        ) => Task.FromResult(profiles.SingleOrDefault(profile => profile.Key == key));

        public Task<bool> CreateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpsertAsync(RepositoryProfile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class WorkSourceStore(IReadOnlyList<WorkSourceEnvironmentProfile> profiles)
        : IWorkSourceEnvironmentStore
    {
        public Task<IReadOnlyList<WorkSourceEnvironmentProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult(profiles);

        public Task<WorkSourceEnvironmentProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        ) => Task.FromResult(profiles.SingleOrDefault(profile => profile.Key == key));

        public Task<bool> CreateAsync(
            WorkSourceEnvironmentProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            WorkSourceEnvironmentProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RuntimeStore(IReadOnlyList<RuntimeEnvironmentProfile> profiles)
        : IRuntimeEnvironmentStore
    {
        public Task<IReadOnlyList<RuntimeEnvironmentProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult(profiles);

        public Task<RuntimeEnvironmentProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        ) => Task.FromResult(profiles.SingleOrDefault(profile => profile.Key == key));

        public Task<bool> CreateAsync(
            RuntimeEnvironmentProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            RuntimeEnvironmentProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
