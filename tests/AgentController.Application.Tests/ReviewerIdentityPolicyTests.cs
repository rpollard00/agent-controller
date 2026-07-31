using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Domain.Secrets;

namespace AgentController.Application.Tests;

public sealed class ReviewerIdentityPolicyTests
{
    [Fact]
    public void AzureDevOpsPolicy_ExposesSupportedKindsAndNormalizesValues()
    {
        var policy = new AzureDevOpsReviewerIdentityPolicy();

        Assert.Equal(
            ["email", "identityId", "descriptor"],
            policy.SupportedIdentityKinds.Select(kind => kind.Kind)
        );

        var email = policy.ValidateAndNormalize(
            new ReviewerIdentity { Kind = " uniqueName ", Value = " Reviewer@Example.COM " }
        );
        var identityId = policy.ValidateAndNormalize(
            new ReviewerIdentity
            {
                Kind = "id",
                Value = "{01234567-89ab-cdef-0123-456789abcdef}",
            }
        );

        Assert.True(email.IsValid);
        Assert.Equal("email", email.NormalizedIdentity?.Kind);
        Assert.Equal("reviewer@example.com", email.NormalizedIdentity?.Value);
        Assert.True(identityId.IsValid);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", identityId.Identity?.Value);
    }

    [Fact]
    public void AzureDevOpsPolicy_RejectsMalformedIdentityValues()
    {
        var policy = new AzureDevOpsReviewerIdentityPolicy();

        var invalidEmail = policy.ValidateAndNormalize(
            new ReviewerIdentity { Kind = "email", Value = "not-an-email" }
        );
        var invalidId = policy.ValidateAndNormalize(
            new ReviewerIdentity { Kind = "identityId", Value = "not-a-guid" }
        );

        Assert.False(invalidEmail.IsValid);
        Assert.Contains(invalidEmail.Errors, error => error.Contains("email", StringComparison.OrdinalIgnoreCase));
        Assert.False(invalidId.IsValid);
        Assert.Contains(invalidId.Errors, error => error.Contains("GUID", StringComparison.Ordinal));
    }

    [Fact]
    public void AzureDevOpsPolicy_MatchesOnlyTheSameIdentityKind()
    {
        var policy = new AzureDevOpsReviewerIdentityPolicy();
        var configured = new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" };

        Assert.True(
            policy.Matches(
                configured,
                new ReviewerIdentity { Kind = "uniqueName", Value = "REVIEWER@EXAMPLE.COM" }
            )
        );
        Assert.False(
            policy.Matches(
                configured,
                new ReviewerIdentity
                {
                    Kind = "descriptor",
                    Value = "reviewer@example.com",
                }
            )
        );
    }

    [Fact]
    public async Task Create_NormalizesAndDeduplicatesReviewerIdentitiesByKind()
    {
        var repositories = new FakeRepositoryStore();
        var handler = new CreateRepositoryCommandHandler(
            repositories,
            new RuntimeEnvironmentStoreForPolicyTests("runtime-default"),
            new ConnectionStoreForPolicyTests(
                new ConnectionProfile { Key = "ado-main", Provider = "AzureDevOps" }
            ),
            new InMemorySecretStore()
        );

        var result = await handler.HandleAsync(
            new CreateRepositoryCommand(
                new RepositoryProfile
                {
                    Key = "policy-test",
                    CloneUrl = "git@example.test:repository.git",
                    DefaultBranch = "main",
                    Transport = CloneTransport.Ssh,
                    RepositoryHostConnectionKey = "ado-main",
                    RuntimeEnvironmentKey = "runtime-default",
                    ReviewerIdentities =
                    [
                        new ReviewerIdentity
                        {
                            Kind = "email",
                            Value = "Reviewer@Example.COM",
                        },
                        new ReviewerIdentity
                        {
                            Kind = "uniqueName",
                            Value = "reviewer@example.com",
                        },
                    ],
                }
            ),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.Succeeded, result.Status);
        var identities = Assert.IsType<RepositoryProfile>(result.Repository).ReviewerIdentities;
        var identity = Assert.Single(identities);
        Assert.Equal("email", identity.Kind);
        Assert.Equal("reviewer@example.com", identity.Value);
    }

    [Fact]
    public async Task Create_RejectsReviewerIdentitiesForUnsupportedProvider()
    {
        var repositories = new FakeRepositoryStore();
        var handler = new CreateRepositoryCommandHandler(
            repositories,
            new RuntimeEnvironmentStoreForPolicyTests("runtime-default"),
            new ConnectionStoreForPolicyTests(
                new ConnectionProfile { Key = "github-main", Provider = "GitHub" }
            ),
            new InMemorySecretStore()
        );

        var result = await handler.HandleAsync(
            new CreateRepositoryCommand(
                new RepositoryProfile
                {
                    Key = "unsupported-policy",
                    CloneUrl = "git@example.test:repository.git",
                    DefaultBranch = "main",
                    Transport = CloneTransport.Ssh,
                    RepositoryHostConnectionKey = "github-main",
                    RuntimeEnvironmentKey = "runtime-default",
                    ReviewerIdentities =
                    [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }],
                }
            ),
            CancellationToken.None
        );

        Assert.Equal(RepositoryOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("reviewerIdentities", result.ValidationErrors.Keys);
        Assert.Empty(repositories.Created);
    }

    private sealed class FakeRepositoryStore : IRepositoryStore
    {
        internal List<RepositoryProfile> Created { get; } = [];

        public Task<IReadOnlyList<RepositoryProfile>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RepositoryProfile>>(Created);

        public Task<RepositoryProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Created.SingleOrDefault(profile => profile.Key == key));

        public Task<bool> CreateAsync(RepositoryProfile profile, CancellationToken cancellationToken)
        {
            Created.Add(profile);
            return Task.FromResult(true);
        }

        public Task<bool> UpdateAsync(RepositoryProfile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpsertAsync(RepositoryProfile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RuntimeEnvironmentStoreForPolicyTests(params string[] keys)
        : IRuntimeEnvironmentStore
    {
        private readonly HashSet<string> _keys = new(keys, StringComparer.Ordinal);

        public Task<IReadOnlyList<RuntimeEnvironmentProfile>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuntimeEnvironmentProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<RuntimeEnvironmentProfile?>(
                _keys.Contains(key) ? new RuntimeEnvironmentProfile { Key = key } : null
            );

        public Task<bool> CreateAsync(RuntimeEnvironmentProfile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(RuntimeEnvironmentProfile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ConnectionStoreForPolicyTests(params ConnectionProfile[] profiles)
        : IConnectionStore
    {
        private readonly IReadOnlyDictionary<string, ConnectionProfile> _profiles = profiles.ToDictionary(
            profile => profile.Key,
            StringComparer.Ordinal
        );

        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(_profiles.Values.ToArray());

        public Task<ConnectionProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(
                _profiles.TryGetValue(key, out var profile) ? profile : null
            );

        public Task<bool> CreateAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateAsync(ConnectionProfile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
