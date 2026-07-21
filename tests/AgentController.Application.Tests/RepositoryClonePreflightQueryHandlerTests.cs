using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Tests;

public sealed class RepositoryClonePreflightQueryHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_UsesManagedRepositoryAndConnectionContext()
    {
        var repository = new RepositoryProfile
        {
            Key = "repo-one",
            CloneUrl = "https://example.test/owner/repo.git",
            DefaultBranch = "develop",
            RepositoryHostConnectionKey = "host-primary",
        };
        var connection = new ConnectionProfile
        {
            Key = "host-primary",
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                PersonalAccessTokenReference = SecretReference.ByNameAndVersion("clone-pat", 4),
            },
        };
        var expected = ClonePreflightResult.Failed(
            CloneTransport.HttpsPat,
            repository.CloneUrl,
            "The referenced PAT was not found.",
            ClonePreflightFailureCode.CredentialNotFound,
            RepositoryCloneCredentialSource.ConnectionPersonalAccessToken,
            SecretReference.ByNameAndVersion("clone-pat", 4)
        );
        var sourceControl = new RecordingSourceControlProvider(expected);
        var handler = new RunRepositoryClonePreflightQueryHandler(
            new StubRepositoryStore(repository),
            new StubConnectionStore(connection),
            sourceControl
        );

        var result = await handler.ExecuteAsync(
            new RunRepositoryClonePreflightQuery("  REPO-ONE  "),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.Succeeded, result.Status);
        Assert.Same(expected, result.Preflight);
        Assert.Equal("repo-one", sourceControl.Spec?.RepoKey);
        Assert.Equal("develop", sourceControl.Spec?.DefaultBranch);
        Assert.Same(repository, sourceControl.Spec?.Profile);
        Assert.Same(connection, sourceControl.Spec?.RepositoryConnection);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotProbeInvalidOrMissingRepository()
    {
        var sourceControl = new RecordingSourceControlProvider(
            ClonePreflightResult.Ok(CloneTransport.Local, "/tmp/repo")
        );
        var handler = new RunRepositoryClonePreflightQueryHandler(
            new StubRepositoryStore(),
            new StubConnectionStore(),
            sourceControl
        );

        var invalid = await handler.ExecuteAsync(
            new RunRepositoryClonePreflightQuery("not valid"),
            CancellationToken.None
        );
        var missing = await handler.ExecuteAsync(
            new RunRepositoryClonePreflightQuery("missing"),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.ValidationFailed, invalid.Status);
        Assert.Equal(RepositoryOperationStatus.NotFound, missing.Status);
        Assert.Null(sourceControl.Spec);
    }

    private sealed class RecordingSourceControlProvider(ClonePreflightResult result)
        : ISourceControlProvider
    {
        public RepositorySpec? Spec { get; private set; }

        public Task<ClonePreflightResult> CheckClonePreflightAsync(
            RepositorySpec spec,
            CancellationToken cancellationToken
        )
        {
            Spec = spec;
            return Task.FromResult(result);
        }

        public Task<RepositoryCheckout> CloneAsync(
            RepositorySpec spec,
            EnvironmentHandle environment,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<SourceControlStatus> GetStatusAsync(
            SourceControlRef sourceControlRef,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class StubRepositoryStore(params RepositoryProfile[] profiles) : IRepositoryStore
    {
        private readonly Dictionary<string, RepositoryProfile> _profiles = profiles.ToDictionary(
            profile => profile.Key,
            StringComparer.Ordinal
        );

        public Task<RepositoryProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            _profiles.TryGetValue(key, out var profile);
            return Task.FromResult(profile);
        }

        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

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

        public Task<ConnectionProfile?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            _profiles.TryGetValue(key, out var profile);
            return Task.FromResult(profile);
        }

        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

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
