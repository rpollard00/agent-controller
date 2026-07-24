using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application.Tests;

public sealed class ManagedProfileResolverTests
{
    [Fact]
    public async Task ResolveForRepositoryAsync_ReturnsManagedRepositoryAndEnabledAssociations()
    {
        var managedRepository = Repository(
            "orders",
            "https://managed.example/orders.git",
            hostConnectionKey: "managed-ado",
            runtimeKey: "managed-runtime"
        );
        var managedRuntime = Runtime("managed-runtime", enabled: true, "/managed/workspaces");
        var managedAzureDevOps = AzureDevOps("managed-ado", enabled: true, "ManagedProject");
        var resolver = CreateResolver(
            [managedRepository],
            [managedAzureDevOps],
            [managedRuntime]
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveForRepositoryAsync_MissingOrDisabledManagedRuntimeReturnsNull(
        bool runtimeExists
    )
    {
        var repository = Repository(
            "orders",
            "https://managed.example/orders.git",
            runtimeKey: "managed-runtime"
        );
        var resolver = CreateResolver(
            [repository],
            [],
            runtimeExists ? [Runtime("managed-runtime", enabled: false, "/disabled")] : []
        );

        var result = await resolver.ResolveForRepositoryAsync("orders", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveForRepositoryAsync_NoManagedRepositoryReturnsNull()
    {
        var resolver = CreateResolver(
            [],
            [],
            [Runtime("managed-runtime", enabled: true, "/managed")]
        );

        var result = await resolver.ResolveForRepositoryAsync("orders", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveForRepositoryAsync_RepositoryWithoutRuntimeReferenceReturnsNull()
    {
        var resolver = CreateResolver(
            [Repository("orders", "https://managed.example/orders.git")],
            [],
            [Runtime("managed-runtime", enabled: true, "/managed")]
        );

        var result = await resolver.ResolveForRepositoryAsync("orders", CancellationToken.None);

        Assert.Null(result);
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
        var resolver = CreateResolver([managedRepository], [], [managedRuntime]);

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
            []
        );

        var environments = await resolver.ListWorkSourceEnvironmentsAsync(CancellationToken.None);

        Assert.Equal(
            ["alpha", "zeta"],
            environments.Select(environment => environment.Profile.Key)
        );
        Assert.All(environments, environment => Assert.True(environment.IsManaged));
    }

    [Fact]
    public async Task ListWorkSourceEnvironmentsAsync_NoEnabledManagedProfilesReturnsEmpty()
    {
        var resolver = CreateResolver(
            [],
            [AzureDevOps("disabled", enabled: false, "Disabled")],
            []
        );

        var environments = await resolver.ListWorkSourceEnvironmentsAsync(CancellationToken.None);

        Assert.Empty(environments);
    }

    [Fact]
    public async Task ResolveWorkSourceEnvironmentAsync_NoEnabledManagedProfileReturnsNull()
    {
        var resolver = CreateResolver(
            [],
            [AzureDevOps("disabled", enabled: false, "Disabled")],
            []
        );

        var environment = await resolver.ResolveWorkSourceEnvironmentAsync(
            null,
            CancellationToken.None
        );

        Assert.Null(environment);
    }

    private static ManagedProfileResolver CreateResolver(
        IReadOnlyList<RepositoryProfile> repositories,
        IReadOnlyList<WorkSourceEnvironmentProfile> workSourceEnvironments,
        IReadOnlyList<RuntimeEnvironmentProfile> runtimes
    )
    {
        return new ManagedProfileResolver(
            new RepositoryStore(repositories),
            new WorkSourceStore(workSourceEnvironments),
            new RuntimeStore(runtimes),
            new ConnectionStore()
        );
    }

    private static RepositoryProfile Repository(
        string key,
        string cloneUrl,
        string? hostConnectionKey = null,
        string? runtimeKey = null
    )
    {
        return new RepositoryProfile
        {
            Key = key,
            CloneUrl = cloneUrl,
            DefaultBranch = "main",
            RepositoryHostConnectionKey = hostConnectionKey,
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
            ConnectionKey = "azuredevops-example",
            Project = project,
        };
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

    private sealed class ConnectionStore : IConnectionStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ConnectionProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        ) => Task.FromResult<ConnectionProfile?>(null);

        public Task<bool> CreateAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
