using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Integration-style tests for <see cref="AzureDevOpsReposRepositoryHost"/>
/// using a fake ADO client and in-memory secret store.
/// </summary>
public sealed class AzureDevOpsReposRepositoryHostTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "TestProject";
    private const string PatId = "test-pat-id";

    // ─── VerifyConnectivityAsync: success path ───

    [Fact]
    public async Task VerifyConnectivityAsync_ConnectivitySucceeds_ReturnsSuccessWithReposInPayload()
    {
        // Arrange
        var secretStore = CreateSecretStore(PatId, "test-token");
        var fakeClient = new FakeAdoClientForReposHost
        {
            ConnectivityResult = new AzureDevOpsConnectivityResult
            {
                Success = true,
                Status = System.Net.HttpStatusCode.OK,
                Repositories = new List<RepositoryInfo>
                {
                    new() { Id = "repo-1", Name = "main-repo", DefaultBranch = "refs/heads/main", RemoteUrl = "https://dev.azure.com/testorg/TestProject/_git/main-repo" },
                    new() { Id = "repo-2", Name = "infra-repo", DefaultBranch = "refs/heads/develop", RemoteUrl = "https://dev.azure.com/testorg/TestProject/_git/infra-repo" },
                },
            },
        };
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();

        // Act
        var result = await host.VerifyConnectivityAsync(profile, CancellationToken.None);

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
        Assert.Equal("repo-2", repositories[1]["id"]);

        // Assert: PAT was resolved through IManagedSecretStore
        Assert.True(fakeClient.WasCreated);
        Assert.Equal("test-token", fakeClient.ResolvedPat);
    }

    // ─── VerifyConnectivityAsync: connectivity failure from ADO ───

    [Fact]
    public async Task VerifyConnectivityAsync_ConnectivityFails_ReturnsFailureWithErrors()
    {
        // Arrange
        var secretStore = CreateSecretStore(PatId, "test-token");
        var fakeClient = new FakeAdoClientForReposHost
        {
            ConnectivityResult = new AzureDevOpsConnectivityResult
            {
                Success = false,
                Status = System.Net.HttpStatusCode.Unauthorized,
                Error = "The provided credentials are invalid.",
                Repositories = Array.Empty<RepositoryInfo>(),
            },
        };
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();

        // Act
        var result = await host.VerifyConnectivityAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.Equal(401, result.HttpStatus);
        Assert.Single(result.Errors);
        Assert.Equal("The provided credentials are invalid.", result.Errors[0]);
    }

    // ─── VerifyConnectivityAsync: missing organization URL ───

    [Fact]
    public async Task VerifyConnectivityAsync_MissingOrganizationUrl_ReturnsConfigError()
    {
        // Arrange
        var secretStore = CreateSecretStore(PatId, "test-token");
        var fakeClient = new FakeAdoClientForReposHost();
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();
        profile = profile with { OrganizationUrl = "" };

        // Act
        var result = await host.VerifyConnectivityAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("organization URL", StringComparison.OrdinalIgnoreCase));
    }

    // ─── VerifyConnectivityAsync: missing project ───

    [Fact]
    public async Task VerifyConnectivityAsync_MissingProject_ReturnsConfigError()
    {
        // Arrange
        var secretStore = CreateSecretStore(PatId, "test-token");
        var fakeClient = new FakeAdoClientForReposHost();
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();
        profile = profile with { Project = "" };

        // Act
        var result = await host.VerifyConnectivityAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("project", StringComparison.OrdinalIgnoreCase));
    }

    // ─── VerifyConnectivityAsync: PAT resolution fails ───

    [Fact]
    public async Task VerifyConnectivityAsync_PatResolutionFails_ReturnsFailure()
    {
        // Arrange: secret store returns null for the reference
        var secretStore = CreateSecretStore("other-id", "other-token"); // different id
        var fakeClient = new FakeAdoClientForReposHost();
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();

        // Act
        var result = await host.VerifyConnectivityAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("PAT", StringComparison.OrdinalIgnoreCase));
    }

    // ─── VerifyConnectivityAsync: disabled profile ───

    [Fact]
    public async Task VerifyConnectivityAsync_DisabledProfile_ThrowsInvalidOperationException()
    {
        // Arrange
        var secretStore = CreateSecretStore(PatId, "test-token");
        var fakeClient = new FakeAdoClientForReposHost();
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();
        profile = profile with { Enabled = false };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.VerifyConnectivityAsync(profile, CancellationToken.None)
        );
    }

    // ─── ListRepositoriesAsync: success path ───

    [Fact]
    public async Task ListRepositoriesAsync_Success_ReturnsMappedHostRepositories()
    {
        // Arrange
        var secretStore = CreateSecretStore(PatId, "test-token");
        var fakeClient = new FakeAdoClientForReposHost
        {
            Repositories = new List<RepositoryInfo>
            {
                new() { Id = "repo-1", Name = "main-repo", DefaultBranch = "refs/heads/main", RemoteUrl = "https://dev.azure.com/testorg/TestProject/_git/main-repo" },
                new() { Id = "repo-2", Name = "infra-repo", DefaultBranch = "refs/heads/develop", RemoteUrl = "https://dev.azure.com/testorg/TestProject/_git/infra-repo" },
            },
        };
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();

        // Act
        var result = await host.ListRepositoriesAsync(profile, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("repo-1", result[0].Id);
        Assert.Equal("main-repo", result[0].Name);
        Assert.Equal("main", result[0].DefaultBranch); // refs/heads/ stripped
        Assert.Equal("https://dev.azure.com/testorg/TestProject/_git/main-repo", result[0].RemoteUrl);
        Assert.Equal(CloneTransportHint.HttpsPat, result[0].CloneTransportHint);

        Assert.Equal("repo-2", result[1].Id);
        Assert.Equal("develop", result[1].DefaultBranch); // refs/heads/ stripped

        // Assert: PAT was resolved through IManagedSecretStore
        Assert.True(fakeClient.WasCreated);
        Assert.Equal("test-token", fakeClient.ResolvedPat);
    }

    // ─── ListRepositoriesAsync: empty project returns empty list ───

    [Fact]
    public async Task ListRepositoriesAsync_EmptyProject_ReturnsEmptyList()
    {
        // Arrange
        var secretStore = CreateSecretStore(PatId, "test-token");
        var fakeClient = new FakeAdoClientForReposHost();
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();
        profile = profile with { Project = "" };

        // Act
        var result = await host.ListRepositoriesAsync(profile, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    // ─── ListRepositoriesAsync: PAT resolution fails returns empty list ───

    [Fact]
    public async Task ListRepositoriesAsync_PatResolutionFails_ReturnsEmptyList()
    {
        // Arrange: secret store returns null
        var secretStore = CreateSecretStore("other-id", "other-token");
        var fakeClient = new FakeAdoClientForReposHost();
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();

        // Act
        var result = await host.ListRepositoriesAsync(profile, CancellationToken.None);

        // Assert: empty list, no throw
        Assert.Empty(result);
    }

    // ─── ListRepositoriesAsync: refs/heads stripping ───

    [Fact]
    public async Task ListRepositoriesAsync_DefaultBranchStripsRefsHeads()
    {
        // Arrange
        var secretStore = CreateSecretStore(PatId, "test-token");
        var fakeClient = new FakeAdoClientForReposHost
        {
            Repositories = new List<RepositoryInfo>
            {
                new() { Id = "r1", Name = "Repo1", DefaultBranch = "refs/heads/main", RemoteUrl = "https://example.com/r1" },
                new() { Id = "r2", Name = "Repo2", DefaultBranch = "master", RemoteUrl = "https://example.com/r2" }, // no prefix
                new() { Id = "r3", Name = "Repo3", DefaultBranch = null, RemoteUrl = "https://example.com/r3" }, // null
            },
        };
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();

        // Act
        var result = await host.ListRepositoriesAsync(profile, CancellationToken.None);

        // Assert
        Assert.Equal("main", result[0].DefaultBranch); // stripped
        Assert.Equal("master", result[1].DefaultBranch); // unchanged
        Assert.Equal("", result[2].DefaultBranch); // null → empty
    }

    // ─── VerifyConnectivityAsync: cancellation token passthrough ───

    [Fact]
    public async Task VerifyConnectivityAsync_PassesCancellationToken()
    {
        // Arrange
        var secretStore = CreateSecretStore(PatId, "test-token");
        var fakeClient = new FakeAdoClientForReposHost
        {
            ConnectivityResult = new AzureDevOpsConnectivityResult
            {
                Success = true,
                Status = System.Net.HttpStatusCode.OK,
            },
        };
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();
        var cts = new CancellationTokenSource();

        // Act
        var result = await host.VerifyConnectivityAsync(profile, cts.Token);

        // Assert
        Assert.True(result.Success);
        Assert.True(fakeClient.ReceivedCancellation);
    }

    // ─── ListRepositoriesAsync: cancellation token passthrough ───

    [Fact]
    public async Task ListRepositoriesAsync_PassesCancellationToken()
    {
        // Arrange
        var secretStore = CreateSecretStore(PatId, "test-token");
        var fakeClient = new FakeAdoClientForReposHost
        {
            Repositories = new List<RepositoryInfo>
            {
                new() { Id = "r1", Name = "Repo1", DefaultBranch = "main", RemoteUrl = "https://example.com/r1" },
            },
        };
        var host = CreateHost(secretStore, fakeClient);
        var profile = CreateProfile();
        var cts = new CancellationTokenSource();

        // Act
        var result = await host.ListRepositoriesAsync(profile, cts.Token);

        // Assert
        Assert.Single(result);
        Assert.True(fakeClient.ReceivedCancellation);
    }

    // ─── Helpers ───

    private static RepositoryHostConnectionProfile CreateProfile()
    {
        return new RepositoryHostConnectionProfile
        {
            Key = "test-host",
            DisplayName = "Test Host",
            Provider = "AzureDevOpsRepos",
            OrganizationUrl = OrgUrl,
            Project = Project,
            PersonalAccessTokenReference = SecretReference.Database(PatId),
        };
    }

    private static InMemorySecretStore CreateSecretStore(string id, string value)
    {
        var store = new InMemorySecretStore();
        store.Set(id, value);
        return store;
    }

    private static AzureDevOpsReposRepositoryHost CreateHost(
        IManagedSecretStore secretStore,
        FakeAdoClientForReposHost fakeClient
    )
    {
        var factory = new TestReposClientFactory(fakeClient);
        return new AzureDevOpsReposRepositoryHost(factory, secretStore);
    }

    // ─── Test doubles ───

    /// <summary>
    /// In-memory IManagedSecretStore for tests.
    /// </summary>
    private sealed class InMemorySecretStore : IManagedSecretStore
    {
        private readonly Dictionary<string, string> _store = new();

        public void Set(string id, string value) => _store[id] = value;

        public Task<string?> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_store.GetValueOrDefault(reference.Id));
        }

        public Task<SecretWriteResult> WriteAsync(SecretReference reference, string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _store[reference.Id] = value;
            return Task.FromResult(SecretWriteResult.SuccessResult());
        }
    }

    /// <summary>
    /// Fake ADO client that returns pre-configured results.
    /// </summary>
    private sealed class FakeAdoClientForReposHost : IAzureDevOpsBoardsClient
    {
        public AzureDevOpsConnectivityResult ConnectivityResult { get; set; } =
            new() { Success = false };

        public IReadOnlyList<RepositoryInfo> Repositories { get; set; } = Array.Empty<RepositoryInfo>();
        public bool WasCreated { get; set; }
        public string? ResolvedPat { get; set; }
        public bool ReceivedCancellation { get; private set; }

        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
            string organizationUrl, string project, string personalAccessToken, CancellationToken ct)
        {
            ReceivedCancellation = ct.CanBeCanceled;
            return Task.FromResult(ConnectivityResult);
        }

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(string project, CancellationToken ct)
        {
            ReceivedCancellation = ct.CanBeCanceled;
            return Task.FromResult(Repositories);
        }

        // Stub implementations for remaining interface members
        public Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(BoardsQueryParameters p, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<ClaimResult> TryClaimWorkItemAsync(ExternalWorkRef w, ClaimRequest r, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<bool> UpdateWorkItemStatusAsync(ExternalWorkRef w, ExternalWorkStatus s, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task AddCommentAsync(ExternalWorkRef w, string c, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(ExternalWorkRef w, int m, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task ReleaseClaimWorkItemAsync(ReleaseClaimRequest r, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetValidStatesAsync(string p, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// Test factory that wraps a fake client and captures the resolved PAT.
    /// Implements IAzureDevOpsReposClientFactory but injects a pre-configured
    /// fake client instead of building a real one.
    /// </summary>
    private sealed class TestReposClientFactory(FakeAdoClientForReposHost fakeClient)
        : IAzureDevOpsReposClientFactory
    {
        public IAzureDevOpsBoardsClient Create(
            RepositoryHostConnectionProfile profile,
            string personalAccessToken
        )
        {
            fakeClient.WasCreated = true;
            fakeClient.ResolvedPat = personalAccessToken;
            if (!profile.Enabled)
            {
                throw new InvalidOperationException(
                    $"Repository host connection '{profile.Key}' is disabled."
                );
            }
            return fakeClient;
        }
    }
}
