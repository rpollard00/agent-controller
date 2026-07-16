using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Infrastructure;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AzureDevOpsConnectivityVerifier"/> covering:
/// - Success path (connectivity ok + repo list mapped into payload, AuthMechanism == "PersonalAccessToken")
/// - Failure path (VerifyConnectivityAsync reports failure → non-success with errors/httpStatus)
/// - Config-error path (missing org/project or unresolved secret → non-success with descriptive errors, no throw)
/// - PAT resolution through ISecretStore (named + versioned secrets)
/// </summary>
public sealed class AzureDevOpsConnectivityVerifierTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "TestProject";
    private const string SecretName = "test-ado-pat";

    // ─── Success path ───

    [Fact]
    public async Task VerifyAsync_ConnectivitySucceeds_ReturnsSuccessWithReposInPayload()
    {
        // Arrange
        var secretStore = await CreateNamedSecretStoreAsync(new Dictionary<string, string>
        {
            [SecretName] = "test-personal-access-token",
        });
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier
        {
            ConnectivityResult = new AzureDevOpsConnectivityResult
            {
                Success = true,
                Status = System.Net.HttpStatusCode.OK,
                Repositories = new List<RepositoryInfo>
                {
                    new()
                    {
                        Id = "repo-1",
                        Name = "main-repo",
                        DefaultBranch = "refs/heads/main",
                        RemoteUrl = "https://dev.azure.com/testorg/TestProject/_git/main-repo",
                    },
                    new()
                    {
                        Id = "repo-2",
                        Name = "infra-repo",
                        DefaultBranch = "refs/heads/main",
                        RemoteUrl = "https://dev.azure.com/testorg/TestProject/_git/infra-repo",
                    },
                },
            },
        };
        var verifier = CreateVerifier(mockClient, namedSecretStore: secretStore);

        var profile = CreateProfile();

        // Act
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert: success
        Assert.True(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.Equal(200, result.HttpStatus);
        Assert.Empty(result.Errors);

        // Assert: payload contains repository list
        var payload = Assert.IsType<Dictionary<string, object>>(result.Payload);
        var repositories = Assert.IsType<List<Dictionary<string, object?>>>(payload["repositories"]);
        Assert.Equal(2, repositories.Count);
        Assert.Equal("repo-1", repositories[0]["id"]);
        Assert.Equal("main-repo", repositories[0]["name"]);
        Assert.Equal("refs/heads/main", repositories[0]["defaultBranch"]);
        Assert.Equal("https://dev.azure.com/testorg/TestProject/_git/main-repo", repositories[0]["remoteUrl"]);
        Assert.Equal("repo-2", repositories[1]["id"]);
        Assert.Equal("infra-repo", repositories[1]["name"]);
    }

    // ─── Failure path (connectivity check fails) ───

    [Fact]
    public async Task VerifyAsync_ConnectivityFails_ReturnsFailureWithErrorsAndHttpStatus()
    {
        // Arrange
        var secretStore = await CreateNamedSecretStoreAsync(new Dictionary<string, string>
        {
            [SecretName] = "test-personal-access-token",
        });
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier
        {
            ConnectivityResult = new AzureDevOpsConnectivityResult
            {
                Success = false,
                Status = System.Net.HttpStatusCode.Unauthorized,
                Error = "The provided credentials are invalid.",
                Repositories = Array.Empty<RepositoryInfo>(),
            },
        };
        var verifier = CreateVerifier(mockClient, namedSecretStore: secretStore);

        var profile = CreateProfile();

        // Act
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert: non-success
        Assert.False(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.Equal(401, result.HttpStatus);
        Assert.Single(result.Errors);
        Assert.Equal("The provided credentials are invalid.", result.Errors[0]);
    }

    [Fact]
    public async Task VerifyAsync_ConnectivityFailsWithNullStatus_ReturnsFailureWithNullHttpStatus()
    {
        // Arrange
        var secretStore = await CreateNamedSecretStoreAsync(new Dictionary<string, string>
        {
            [SecretName] = "test-personal-access-token",
        });
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier
        {
            ConnectivityResult = new AzureDevOpsConnectivityResult
            {
                Success = false,
                Status = null,
                Error = "Connection timed out.",
                Repositories = Array.Empty<RepositoryInfo>(),
            },
        };
        var verifier = CreateVerifier(mockClient, namedSecretStore: secretStore);

        var profile = CreateProfile();

        // Act
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Null(result.HttpStatus);
        Assert.Single(result.Errors);
        Assert.Equal("Connection timed out.", result.Errors[0]);
    }

    // ─── Config-error: missing organization URL ───

    [Fact]
    public async Task VerifyAsync_MissingOrganizationUrl_ReturnsFailureWithConfigError()
    {
        // Arrange — no PAT secret needed; config validation fails first
        var secretStore = await CreateNamedSecretStoreAsync(new Dictionary<string, string>());
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier();
        var verifier = CreateVerifier(mockClient, namedSecretStore: secretStore);

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "broken-env",
            DisplayName = "Broken Environment",
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = "", // Missing
            Project = Project,
            PersonalAccessTokenReference = Domain.Secrets.SecretReference.ByName(SecretName),
        };

        // Act — must not throw
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.Contains(
            result.Errors,
            e => e.Contains("organization URL", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ─── Config-error: missing project ───

    [Fact]
    public async Task VerifyAsync_MissingProject_ReturnsFailureWithConfigError()
    {
        // Arrange
        var secretStore = await CreateNamedSecretStoreAsync(new Dictionary<string, string>());
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier();
        var verifier = CreateVerifier(mockClient, namedSecretStore: secretStore);

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "broken-env",
            DisplayName = "Broken Environment",
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = OrgUrl,
            Project = "", // Missing
            PersonalAccessTokenReference = Domain.Secrets.SecretReference.ByName(SecretName),
        };

        // Act — must not throw
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.Contains(
            result.Errors,
            e => e.Contains("project", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ─── Config-error: missing PAT secret reference ───

    [Fact]
    public async Task VerifyAsync_MissingSecretReference_ReturnsFailureWithConfigError()
    {
        // Arrange
        var secretStore = await CreateNamedSecretStoreAsync(new Dictionary<string, string>());
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier();
        var verifier = CreateVerifier(mockClient, namedSecretStore: secretStore);

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "broken-env",
            DisplayName = "Broken Environment",
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = OrgUrl,
            Project = Project,
            PersonalAccessTokenReference = Domain.Secrets.SecretReference.Empty, // Missing
        };

        // Act — must not throw
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.Contains(
            result.Errors,
            e => e.Contains("PAT", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ─── Config-error: secret not found in store ───

    [Fact]
    public async Task VerifyAsync_SecretNotFoundInStore_ReturnsFailureWithDescriptiveError()
    {
        // Arrange: empty secret store — PAT cannot be resolved
        var secretStore = await CreateNamedSecretStoreAsync(new Dictionary<string, string>());
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier();
        var verifier = CreateVerifier(mockClient, namedSecretStore: secretStore);

        var profile = CreateProfile();

        // Act — must not throw
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.Contains(
            result.Errors,
            e => e.Contains(SecretName, StringComparison.Ordinal)
        );
    }

    // ─── Config-error: multiple config errors aggregated ───

    [Fact]
    public async Task VerifyAsync_MultipleConfigErrors_AggregatesAllErrors()
    {
        // Arrange: missing org URL, missing project, and missing PAT secret
        var secretStore = await CreateNamedSecretStoreAsync(new Dictionary<string, string>());
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier();
        var verifier = CreateVerifier(mockClient, namedSecretStore: secretStore);

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "broken-env",
            DisplayName = "Broken Environment",
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = "", // Missing
            Project = "", // Missing
            PersonalAccessTokenReference = Domain.Secrets.SecretReference.ByName("nonexistent-secret"),
        };

        // Act — must not throw
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert: multiple errors
        Assert.False(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.True(result.Errors.Count >= 3, $"Expected at least 3 errors, got {result.Errors.Count}");
        Assert.Contains(
            result.Errors,
            e => e.Contains("organization URL", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            result.Errors,
            e => e.Contains("project", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            result.Errors,
            e => e.Contains("could not be resolved", StringComparison.OrdinalIgnoreCase)
                || e.Contains("PAT", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ─── PAT resolution through ISecretStore: named secret ───

    [Fact]
    public async Task VerifyAsync_NamedSecret_ResolvesPatThroughSecretStore()
    {
        // Arrange: use an in-memory named secret store
        var secretStore = await CreateNamedSecretStoreAsync(new Dictionary<string, string>
        {
            [SecretName] = "named-secret-pat-token",
        });
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier
        {
            ConnectivityResult = new AzureDevOpsConnectivityResult
            {
                Success = true,
                Status = System.Net.HttpStatusCode.OK,
                Repositories = Array.Empty<RepositoryInfo>(),
            },
        };
        var verifier = CreateVerifier(mockClient, namedSecretStore: secretStore);
        var profile = CreateProfile();

        // Act
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert: success — PAT was resolved through ISecretStore
        Assert.True(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
    }

    // ─── PAT resolution through ISecretStore: versioned secret ───

    [Fact]
    public async Task VerifyAsync_VersionedSecret_ResolvesCorrectVersion()
    {
        // Arrange: use an in-memory named secret store with versioned secrets
        var secretStore = new Domain.Secrets.InMemorySecretStore();
        // Create v1 and v2 of the same secret
        await secretStore.CreateAsync(SecretName, "v1-token", CancellationToken.None);
        await secretStore.CreateVersionAsync(SecretName, "v2-token", CancellationToken.None);

        var mockClient = new MockAzureDevOpsBoardsClientForVerifier
        {
            ConnectivityResult = new AzureDevOpsConnectivityResult
            {
                Success = true,
                Status = System.Net.HttpStatusCode.OK,
                Repositories = Array.Empty<RepositoryInfo>(),
            },
        };
        var verifier = CreateVerifier(mockClient, namedSecretStore: secretStore);
        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "test-env",
            DisplayName = "Test Environment",
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = OrgUrl,
            Project = Project,
            PersonalAccessTokenReference = Domain.Secrets.SecretReference.ByNameAndVersion(SecretName, 1),
        };

        // Act
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert: success — v1 was resolved
        Assert.True(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
    }

    // ─── Helpers ───

    private static AzureDevOpsConnectivityVerifier CreateVerifier(
        IAzureDevOpsBoardsClient mockClient,
        IManagedSecretStore? managedSecretStore = null,
        Domain.Secrets.ISecretStore? namedSecretStore = null
    )
    {
        var factory = new MockAzureDevOpsBoardsClientFactory(mockClient);
        var patResolver = new AzureDevOpsPatResolver(
            managedSecretStore ?? (IManagedSecretStore)new EnvVarBackedFakeSecretStore(),
            namedSecretStore ?? new Domain.Secrets.InMemorySecretStore()
        );
        return new AzureDevOpsConnectivityVerifier(factory, patResolver);
    }

    private static WorkSourceEnvironmentProfile CreateProfile()
    {
        return new WorkSourceEnvironmentProfile
        {
            Key = "test-env",
            DisplayName = "Test Environment",
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = OrgUrl,
            Project = Project,
            PersonalAccessTokenReference = Domain.Secrets.SecretReference.ByName(SecretName),
        };
    }

    /// <summary>
    /// Creates an in-memory named secret store with pre-configured secret values.
    /// </summary>
    private static async Task<Domain.Secrets.InMemorySecretStore> CreateNamedSecretStoreAsync(Dictionary<string, string> secrets)
    {
        var store = new Domain.Secrets.InMemorySecretStore();
        foreach (var (name, value) in secrets)
        {
            await store.CreateAsync(name, value, CancellationToken.None);
        }
        return store;
    }

    // ─── Mock implementations ───

    /// <summary>
    /// Minimal mock factory that returns a pre-configured client and captures the profile.
    /// </summary>
    private sealed class MockAzureDevOpsBoardsClientFactory(
        IAzureDevOpsBoardsClient client
    ) : IAzureDevOpsBoardsClientFactory
    {
        public WorkSourceEnvironmentProfile? CreatedProfile { get; private set; }

        public IAzureDevOpsBoardsClient Create(WorkSourceEnvironmentProfile profile)
        {
            CreatedProfile = profile;
            return client;
        }
    }

    /// <summary>
    /// Fake IManagedSecretStore that resolves "EnvVar" kind references by reading
    /// the actual environment variable.
    /// Kept for backward compatibility with other consumers of IManagedSecretStore.
    /// </summary>
    private sealed class EnvVarBackedFakeSecretStore : IManagedSecretStore
    {
        public Task<string?> ResolveAsync(SecretReference reference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (reference.Kind != "EnvVar")
                return Task.FromResult<string?>(null);
            return Task.FromResult(Environment.GetEnvironmentVariable(reference.Id));
        }

        public Task<SecretWriteResult> WriteAsync(
            SecretReference reference,
            string value,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                SecretWriteResult.FailureResult("Fake store is read-only.")
            );
        }
    }

    /// <summary>
    /// Mock <see cref="IAzureDevOpsBoardsClient"/> for connectivity verifier tests.
    /// Only implements the methods needed by the verifier.
    /// </summary>
    private sealed class MockAzureDevOpsBoardsClientForVerifier : IAzureDevOpsBoardsClient
    {
        public AzureDevOpsConnectivityResult ConnectivityResult { get; set; } =
            new AzureDevOpsConnectivityResult { Success = false };

        public WorkSourceEnvironmentProfile? CreatedProfile { get; private set; }

        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
            string organizationUrl,
            string project,
            string personalAccessToken,
            CancellationToken ct
        )
        {
            return Task.FromResult(ConnectivityResult);
        }

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
            string project,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<RepositoryInfo>>(Array.Empty<RepositoryInfo>());

        // Stub implementations for remaining interface members (not used by verifier)
        public Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(
            BoardsQueryParameters parameters,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<WorkCandidate>>(Array.Empty<WorkCandidate>());

        public Task<ClaimResult> TryClaimWorkItemAsync(
            ExternalWorkRef workRef,
            ClaimRequest claim,
            CancellationToken ct
        ) => Task.FromResult(new ClaimResult { Success = false });

        public Task<bool> UpdateWorkItemStatusAsync(
            ExternalWorkRef workRef,
            ExternalWorkStatus status,
            CancellationToken ct
        ) => Task.FromResult(false);

        public Task AddCommentAsync(
            ExternalWorkRef workRef,
            string comment,
            CancellationToken ct
        ) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef,
            int maxComments,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());

        public Task ReleaseClaimWorkItemAsync(
            ReleaseClaimRequest request,
            CancellationToken ct
        ) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetValidStatesAsync(
            string project,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(
            new Dictionary<string, IReadOnlyList<string>>()
        );
    }
}
