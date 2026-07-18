using AgentController.Application.Commands;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Tests;

public sealed class SecretDeletionHandlerTests
{
    [Fact]
    public async Task Delete_RemovesUnreferencedSecret()
    {
        var secrets = new InMemorySecretStore();
        await secrets.CreateAsync("test-secret", "write-only-value");
        var handler = CreateHandler(secrets);

        var result = await handler.HandleAsync(
            new DeleteSecretCommand("test-secret"),
            CancellationToken.None
        );

        Assert.Equal(SecretOperationStatus.Succeeded, result.Status);
        Assert.DoesNotContain(
            await secrets.ListAsync(CancellationToken.None),
            secret => string.Equals(secret.Name, "test-secret", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Delete_ReturnsNotFoundForUnknownSecret()
    {
        var secrets = new InMemorySecretStore();
        var handler = CreateHandler(secrets);

        var result = await handler.HandleAsync(
            new DeleteSecretCommand("missing"),
            CancellationToken.None
        );

        Assert.Equal(SecretOperationStatus.NotFound, result.Status);
        Assert.Contains(
            "missing",
            Assert.IsType<string>(result.Detail),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Delete_ReturnsValidationFailedForMissingName(string? name)
    {
        var secrets = new InMemorySecretStore();
        var handler = CreateHandler(secrets);

        var result = await handler.HandleAsync(
            new DeleteSecretCommand(name!),
            CancellationToken.None
        );

        Assert.Equal(SecretOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("name", result.ValidationErrors.Keys);
    }

    [Fact]
    public async Task Delete_ReturnsValidationFailedForTooLongName()
    {
        var secrets = new InMemorySecretStore();
        var handler = CreateHandler(secrets);

        var result = await handler.HandleAsync(
            new DeleteSecretCommand(new string('x', 257)),
            CancellationToken.None
        );

        Assert.Equal(SecretOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("name", result.ValidationErrors.Keys);
    }

    [Fact]
    public async Task Delete_ReturnsConflictWhileAConnectionReferencesSecret()
    {
        var secrets = new InMemorySecretStore();
        await secrets.CreateAsync("test-secret", "write-only-value");
        var connections = new FakeConnectionStore(
            CreateAdoConnection("ado-main", "test-secret")
        );
        var handler = CreateHandler(secrets, connections);

        var result = await handler.HandleAsync(
            new DeleteSecretCommand("test-secret"),
            CancellationToken.None
        );

        Assert.Equal(SecretOperationStatus.Conflict, result.Status);
        Assert.Contains(
            "connection 'ado-main'",
            Assert.IsType<string>(result.Detail),
            StringComparison.Ordinal
        );
        Assert.Contains(
            await secrets.ListAsync(CancellationToken.None),
            secret => string.Equals(secret.Name, "test-secret", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Delete_AllowsDeletionWhenConnectionsReferenceOtherSecrets()
    {
        var secrets = new InMemorySecretStore();
        await secrets.CreateAsync("test-secret", "write-only-value");
        var connections = new FakeConnectionStore(
            CreateAdoConnection("ado-main", "other-secret"),
            CreateAdoConnection("ado-unconfigured", null)
        );
        var handler = CreateHandler(secrets, connections);

        var result = await handler.HandleAsync(
            new DeleteSecretCommand("test-secret"),
            CancellationToken.None
        );

        Assert.Equal(SecretOperationStatus.Succeeded, result.Status);
        Assert.DoesNotContain(
            await secrets.ListAsync(CancellationToken.None),
            secret => string.Equals(secret.Name, "test-secret", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Delete_ReturnsConflictWhileARepositoryReferencesSecret()
    {
        var secrets = new InMemorySecretStore();
        await secrets.CreateAsync("test-secret", "write-only-value");
        var repositories = new FakeRepositoryStore(
            new RepositoryProfile
            {
                Key = "service-a",
                PersonalAccessTokenSecretName = "test-secret",
            }
        );
        var handler = CreateHandler(secrets, repositories: repositories);

        var result = await handler.HandleAsync(
            new DeleteSecretCommand("test-secret"),
            CancellationToken.None
        );

        Assert.Equal(SecretOperationStatus.Conflict, result.Status);
        Assert.Contains(
            "repository 'service-a'",
            Assert.IsType<string>(result.Detail),
            StringComparison.Ordinal
        );
        Assert.Contains(
            await secrets.ListAsync(CancellationToken.None),
            secret => string.Equals(secret.Name, "test-secret", StringComparison.Ordinal)
        );
    }

    private static DeleteSecretCommandHandler CreateHandler(
        InMemorySecretStore secrets,
        FakeConnectionStore? connections = null,
        FakeRepositoryStore? repositories = null
    ) =>
        new(
            secrets,
            connections ?? new FakeConnectionStore(),
            repositories ?? new FakeRepositoryStore()
        );

    private static ConnectionProfile CreateAdoConnection(string key, string? secretName) =>
        new()
        {
            Key = key,
            DisplayName = $"{key} connection",
            Provider = "AzureDevOps",
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                OrganizationUrl = "https://dev.azure.com/example",
                PersonalAccessTokenReference = secretName is null
                    ? SecretReference.Empty
                    : SecretReference.ByName(secretName),
            },
        };

    private sealed class FakeConnectionStore(params ConnectionProfile[] profiles)
        : IConnectionStore
    {
        private readonly IReadOnlyList<ConnectionProfile> _profiles = profiles;

        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult(_profiles);

        public Task<ConnectionProfile?> GetByKeyAsync(
            string key,
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

    private sealed class FakeRepositoryStore(params RepositoryProfile[] profiles)
        : IRepositoryStore
    {
        private readonly IReadOnlyList<RepositoryProfile> _profiles = profiles;

        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult(_profiles);

        public Task<RepositoryProfile?> GetByKeyAsync(
            string key,
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
}
