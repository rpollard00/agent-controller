using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application.Tests;

public sealed class RepositoryOnboardingHandlerTests
{
    [Fact]
    public async Task ListAndGet_ReturnStoredProfilesAndNormalizeLookupKey()
    {
        var repositories = new FakeRepositoryStore(
            CreateProfile("zulu"),
            CreateProfile("repo-one")
        );
        var listHandler = new ListRepositoriesQueryHandler(repositories);
        var getHandler = new GetRepositoryByKeyQueryHandler(repositories);

        var listed = await listHandler.ExecuteAsync(
            new ListRepositoriesQuery(),
            CancellationToken.None
        );
        var read = await getHandler.ExecuteAsync(
            new GetRepositoryByKeyQuery("  REPO-ONE  "),
            CancellationToken.None
        );

        Assert.Equal(["repo-one", "zulu"], listed.Select(profile => profile.Key));
        Assert.Equal(RepositoryOperationStatus.Succeeded, read.Status);
        Assert.Equal("repo-one", Assert.IsType<RepositoryProfile>(read.Repository).Key);
        Assert.Equal("repo-one", repositories.LastReadKey);
    }

    [Fact]
    public async Task Get_ReturnsTypedValidationAndNotFoundOutcomes()
    {
        var repositories = new FakeRepositoryStore();
        var handler = new GetRepositoryByKeyQueryHandler(repositories);

        var invalid = await handler.ExecuteAsync(
            new GetRepositoryByKeyQuery("not valid"),
            CancellationToken.None
        );
        var missing = await handler.ExecuteAsync(
            new GetRepositoryByKeyQuery("missing"),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.ValidationFailed, invalid.Status);
        Assert.Contains("key", invalid.ValidationErrors.Keys);
        Assert.Equal(RepositoryOperationStatus.NotFound, missing.Status);
        Assert.Empty(missing.ValidationErrors);
    }

    [Fact]
    public async Task Create_NormalizesProfileAndReferencedKeysBeforePersisting()
    {
        var repositories = new FakeRepositoryStore();
        var runtimeEnvironments = new FakeRuntimeEnvironmentStore("runtime-local");
        var hostConnections = new FakeRepositoryHostConnectionStore("host-primary");
        var handler = new CreateRepositoryCommandHandler(
            repositories,
            runtimeEnvironments,
            hostConnections
        );
        var profile = new RepositoryProfile
        {
            Key = "  Repo.One  ",
            CloneUrl = "  https://example.test/repo.git  ",
            DefaultBranch = "  feature/onboarding  ",
            Transport = CloneTransport.HttpsPat,
            EnvironmentProfile = " legacy-environment ",
            RuntimeProfile = " legacy-runtime ",
            RepositoryHostConnectionKey = " HOST-PRIMARY ",
            RuntimeEnvironmentKey = " RUNTIME-LOCAL ",
            AllowedPaths = [" ./src//Features/ ", "src\\Features", " tests/ "],
        };

        var result = await handler.HandleAsync(
            new CreateRepositoryCommand(profile),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.Succeeded, result.Status);
        var persisted = Assert.IsType<RepositoryProfile>(repositories.LastCreated);
        Assert.Same(persisted, result.Repository);
        Assert.Equal("repo.one", persisted.Key);
        Assert.Equal("https://example.test/repo.git", persisted.CloneUrl);
        Assert.Equal("feature/onboarding", persisted.DefaultBranch);
        Assert.Equal("legacy-environment", persisted.EnvironmentProfile);
        Assert.Equal("legacy-runtime", persisted.RuntimeProfile);
        Assert.Equal("host-primary", persisted.RepositoryHostConnectionKey);
        Assert.Equal("runtime-local", persisted.RuntimeEnvironmentKey);
        Assert.Equal(["src/Features", "tests"], persisted.AllowedPaths);
    }

    [Fact]
    public async Task Create_RejectsInvalidCloneBranchTransportAndPathsWithoutPersisting()
    {
        var repositories = new FakeRepositoryStore();
        var handler = new CreateRepositoryCommandHandler(
            repositories,
            new FakeRuntimeEnvironmentStore(),
            new FakeRepositoryHostConnectionStore()
        );
        var profile = CreateProfile("invalid") with
        {
            CloneUrl = "ftp://example.test/repo.git",
            DefaultBranch = "bad branch..name",
            Transport = (CloneTransport)999,
            AllowedPaths = ["../secrets", "/absolute/path", ""],
        };

        var result = await handler.HandleAsync(
            new CreateRepositoryCommand(profile),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("cloneUrl", result.ValidationErrors.Keys);
        Assert.Contains("defaultBranch", result.ValidationErrors.Keys);
        Assert.Contains("transport", result.ValidationErrors.Keys);
        Assert.Contains("allowedPaths", result.ValidationErrors.Keys);
        Assert.Null(repositories.LastCreated);
    }

    [Fact]
    public async Task Create_RejectsMissingEnvironmentReferences()
    {
        var repositories = new FakeRepositoryStore();
        var handler = new CreateRepositoryCommandHandler(
            repositories,
            new FakeRuntimeEnvironmentStore(),
            new FakeRepositoryHostConnectionStore()
        );
        var profile = CreateProfile("referencing") with
        {
            RepositoryHostConnectionKey = "missing-host",
            RuntimeEnvironmentKey = "missing-runtime",
        };

        var result = await handler.HandleAsync(
            new CreateRepositoryCommand(profile),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("repositoryHostConnectionKey", result.ValidationErrors.Keys);
        Assert.Contains("runtimeEnvironmentKey", result.ValidationErrors.Keys);
        Assert.Null(repositories.LastCreated);
    }

    [Fact]
    public async Task Create_ReturnsConflictForDuplicateNormalizedKey()
    {
        var repositories = new FakeRepositoryStore(CreateProfile("shared"));
        var handler = new CreateRepositoryCommandHandler(
            repositories,
            new FakeRuntimeEnvironmentStore(),
            new FakeRepositoryHostConnectionStore()
        );

        var result = await handler.HandleAsync(
            new CreateRepositoryCommand(CreateProfile(" SHARED ")),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.Conflict, result.Status);
        Assert.Contains("shared", Assert.IsType<string>(result.Detail), StringComparison.Ordinal);
        Assert.Equal("shared", repositories.LastCreated?.Key);
    }

    [Fact]
    public async Task Update_NormalizesMutableFieldsAndKeepsMatchingKey()
    {
        var repositories = new FakeRepositoryStore(CreateProfile("service"));
        var handler = new UpdateRepositoryCommandHandler(
            repositories,
            new FakeRuntimeEnvironmentStore(),
            new FakeRepositoryHostConnectionStore()
        );
        var update = CreateProfile(" SERVICE ") with
        {
            DefaultBranch = " release/v2 ",
            AllowedPaths = ["src\\Api\\", "./tests//Unit"],
        };

        var result = await handler.HandleAsync(
            new UpdateRepositoryCommand(" service ", update),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.Succeeded, result.Status);
        var persisted = Assert.IsType<RepositoryProfile>(repositories.LastUpdated);
        Assert.Equal("service", persisted.Key);
        Assert.Equal("release/v2", persisted.DefaultBranch);
        Assert.Equal(["src/Api", "tests/Unit"], persisted.AllowedPaths);
    }

    [Fact]
    public async Task Update_RejectsAnAttemptToChangeTheImmutableKey()
    {
        var repositories = new FakeRepositoryStore(CreateProfile("original"));
        var handler = new UpdateRepositoryCommandHandler(
            repositories,
            new FakeRuntimeEnvironmentStore(),
            new FakeRepositoryHostConnectionStore()
        );

        var result = await handler.HandleAsync(
            new UpdateRepositoryCommand("original", CreateProfile("replacement")),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.ValidationFailed, result.Status);
        Assert.Contains(
            "immutable",
            result.ValidationErrors["key"].Single(),
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Null(repositories.LastUpdated);
    }

    [Fact]
    public async Task Update_ReturnsNotFoundWhenRepositoryDisappears()
    {
        var repositories = new FakeRepositoryStore();
        var handler = new UpdateRepositoryCommandHandler(
            repositories,
            new FakeRuntimeEnvironmentStore(),
            new FakeRepositoryHostConnectionStore()
        );

        var result = await handler.HandleAsync(
            new UpdateRepositoryCommand("missing", CreateProfile("missing")),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Delete_NormalizesKeyAndReturnsTypedMissingOutcome()
    {
        var repositories = new FakeRepositoryStore(CreateProfile("temporary"));
        var handler = new DeleteRepositoryCommandHandler(repositories);

        var deleted = await handler.HandleAsync(
            new DeleteRepositoryCommand(" TEMPORARY "),
            CancellationToken.None
        );
        var missing = await handler.HandleAsync(
            new DeleteRepositoryCommand("temporary"),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.Succeeded, deleted.Status);
        Assert.Equal("temporary", repositories.LastDeletedKey);
        Assert.Equal(RepositoryOperationStatus.NotFound, missing.Status);
    }

    [Fact]
    public void AddApplicationHandlers_RegistersRepositoryCommandsAndQueries()
    {
        var services = new ServiceCollection();

        services.AddApplicationHandlers();

        AssertRegistration<
            ICommandHandler<CreateRepositoryCommand, RepositoryOperationResult>,
            CreateRepositoryCommandHandler
        >(services);
        AssertRegistration<
            ICommandHandler<UpdateRepositoryCommand, RepositoryOperationResult>,
            UpdateRepositoryCommandHandler
        >(services);
        AssertRegistration<
            ICommandHandler<DeleteRepositoryCommand, RepositoryOperationResult>,
            DeleteRepositoryCommandHandler
        >(services);
        AssertRegistration<
            IQueryHandler<ListRepositoriesQuery, IReadOnlyList<RepositoryProfile>>,
            ListRepositoriesQueryHandler
        >(services);
        AssertRegistration<
            IQueryHandler<GetRepositoryByKeyQuery, RepositoryOperationResult>,
            GetRepositoryByKeyQueryHandler
        >(services);
    }

    private static RepositoryProfile CreateProfile(string key) =>
        new()
        {
            Key = key,
            CloneUrl = "git@example.test:repository.git",
            DefaultBranch = "main",
            Transport = CloneTransport.Ssh,
            AllowedPaths = ["src", "tests"],
        };

    private static void AssertRegistration<TService, TImplementation>(IServiceCollection services)
    {
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(TService)
                && descriptor.ImplementationType == typeof(TImplementation)
                && descriptor.Lifetime == ServiceLifetime.Scoped
        );
    }

    private sealed class FakeRepositoryStore : IRepositoryStore
    {
        private readonly Dictionary<string, RepositoryProfile> _profiles;

        public FakeRepositoryStore(params RepositoryProfile[] profiles)
        {
            _profiles = profiles.ToDictionary(profile => profile.Key, StringComparer.Ordinal);
        }

        public string? LastReadKey { get; private set; }

        public RepositoryProfile? LastCreated { get; private set; }

        public RepositoryProfile? LastUpdated { get; private set; }

        public string? LastDeletedKey { get; private set; }

        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<RepositoryProfile> profiles = _profiles
                .Values.OrderBy(profile => profile.Key, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(profiles);
        }

        public Task<RepositoryProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            LastReadKey = key;
            _profiles.TryGetValue(key, out var profile);
            return Task.FromResult(profile);
        }

        public Task<bool> CreateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        )
        {
            LastCreated = profile;
            return Task.FromResult(_profiles.TryAdd(profile.Key, profile));
        }

        public Task<bool> UpdateAsync(
            RepositoryProfile profile,
            CancellationToken cancellationToken
        )
        {
            LastUpdated = profile;
            if (!_profiles.ContainsKey(profile.Key))
            {
                return Task.FromResult(false);
            }

            _profiles[profile.Key] = profile;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
        {
            LastDeletedKey = key;
            return Task.FromResult(_profiles.Remove(key));
        }

        public Task UpsertAsync(RepositoryProfile profile, CancellationToken cancellationToken)
        {
            _profiles[profile.Key] = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkSourceEnvironmentStore(params string[] keys)
        : IWorkSourceEnvironmentStore
    {
        private readonly HashSet<string> _keys = new(keys, StringComparer.Ordinal);

        public Task<IReadOnlyList<WorkSourceEnvironmentProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<WorkSourceEnvironmentProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            WorkSourceEnvironmentProfile? profile = _keys.Contains(key)
                ? new WorkSourceEnvironmentProfile { Key = key }
                : null;
            return Task.FromResult(profile);
        }

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

    private sealed class FakeRuntimeEnvironmentStore(params string[] keys)
        : IRuntimeEnvironmentStore
    {
        private readonly HashSet<string> _keys = new(keys, StringComparer.Ordinal);

        public Task<IReadOnlyList<RuntimeEnvironmentProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<RuntimeEnvironmentProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            RuntimeEnvironmentProfile? profile = _keys.Contains(key)
                ? new RuntimeEnvironmentProfile { Key = key }
                : null;
            return Task.FromResult(profile);
        }

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

    private sealed class FakeRepositoryHostConnectionStore(params string[] keys)
        : IRepositoryHostConnectionStore
    {
        private readonly HashSet<string> _keys = new(keys, StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<RepositoryHostConnectionProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<RepositoryHostConnectionProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            RepositoryHostConnectionProfile? profile = _keys.Contains(key)
                ? new RepositoryHostConnectionProfile { Key = key }
                : null;
            return Task.FromResult(profile);
        }

        public Task<bool> CreateAsync(
            RepositoryHostConnectionProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(
            RepositoryHostConnectionProfile profile,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
