using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Tests;

public sealed class RepositoryCloneTransportQueryHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_ResolvesConnectionPatAndNormalizesRepositoryKey()
    {
        var repository = new RepositoryProfile
        {
            Key = "repo-one",
            CloneUrl = "https://example.test/owner/repo.git",
            RepositoryHostConnectionKey = "host-primary",
        };
        var patReference = SecretReference.ByNameAndVersion("host-pat", 4);
        var connection = new ConnectionProfile
        {
            Key = "host-primary",
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                PersonalAccessTokenReference = patReference,
            },
        };
        var repositories = new StubRepositoryStore(repository);
        var connections = new StubConnectionStore(connection);
        var handler = new GetRepositoryCloneTransportQueryHandler(repositories, connections);

        var result = await handler.ExecuteAsync(
            new GetRepositoryCloneTransportQuery("  REPO-ONE  "),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.Succeeded, result.Status);
        Assert.Equal("repo-one", repositories.LastReadKey);
        Assert.Equal("host-primary", connections.LastReadKey);
        Assert.Equal(CloneTransport.HttpsPat, result.Resolution?.Transport);
        Assert.Equal(patReference, result.Resolution?.CredentialReference);
        Assert.True(result.Resolution?.IsReady);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSuccessfulBlockingResolutionWhenCredentialIsMissing()
    {
        var repository = new RepositoryProfile
        {
            Key = "repo-one",
            CloneUrl = "git@example.test:owner/repo.git",
        };
        var handler = new GetRepositoryCloneTransportQueryHandler(
            new StubRepositoryStore(repository),
            new StubConnectionStore()
        );

        var result = await handler.ExecuteAsync(
            new GetRepositoryCloneTransportQuery("repo-one"),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.Succeeded, result.Status);
        Assert.False(result.Resolution?.IsReady);
        Assert.Equal(
            RepositoryCloneTransportIssueCode.MissingSshKeyReference,
            Assert.Single(result.Resolution!.BlockingIssues).Code
        );
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsValidationAndNotFoundOutcomes()
    {
        var handler = new GetRepositoryCloneTransportQueryHandler(
            new StubRepositoryStore(),
            new StubConnectionStore()
        );

        var invalid = await handler.ExecuteAsync(
            new GetRepositoryCloneTransportQuery("not valid"),
            CancellationToken.None
        );
        var missing = await handler.ExecuteAsync(
            new GetRepositoryCloneTransportQuery("missing"),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.ValidationFailed, invalid.Status);
        Assert.Contains("key", invalid.ValidationErrors.Keys);
        Assert.Equal(RepositoryOperationStatus.NotFound, missing.Status);
    }

    private sealed class StubRepositoryStore(params RepositoryProfile[] profiles) : IRepositoryStore
    {
        private readonly Dictionary<string, RepositoryProfile> _profiles = profiles.ToDictionary(
            profile => profile.Key,
            StringComparer.Ordinal
        );

        public string? LastReadKey { get; private set; }

        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

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

    private sealed class StubConnectionStore(params ConnectionProfile[] profiles) : IConnectionStore
    {
        private readonly Dictionary<string, ConnectionProfile> _profiles = profiles.ToDictionary(
            profile => profile.Key,
            StringComparer.Ordinal
        );

        public string? LastReadKey { get; private set; }

        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<ConnectionProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            LastReadKey = key;
            _profiles.TryGetValue(key, out var profile);
            return Task.FromResult(profile);
        }

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
