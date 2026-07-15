using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Domain;
using AgentController.Infrastructure;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AzureDevOpsConnectivityVerifier"/> covering:
/// - Success path (connectivity ok + repo list mapped into payload, AuthMechanism == "PersonalAccessToken")
/// - Failure path (VerifyConnectivityAsync reports failure → non-success with errors/httpStatus)
/// - Config-error path (missing org/project or unresolved ENV: PAT → non-success with descriptive errors, no throw)
/// </summary>
public sealed class AzureDevOpsConnectivityVerifierTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "TestProject";
    private const string PatEnvVar = "TEST_ADO_PAT_VAR";

    // ─── Success path ───

    [Fact]
    public async Task VerifyAsync_ConnectivitySucceeds_ReturnsSuccessWithReposInPayload()
    {
        // Arrange
        SetPatEnvironmentVariable();
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
        var factory = new MockAzureDevOpsBoardsClientFactory(mockClient);
        var verifier = new AzureDevOpsConnectivityVerifier(factory);

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

        // Assert: factory.Create was called with the profile
        Assert.Same(profile, factory.CreatedProfile);

        ClearPatEnvironmentVariable();
    }

    // ─── Failure path (connectivity check fails) ───

    [Fact]
    public async Task VerifyAsync_ConnectivityFails_ReturnsFailureWithErrorsAndHttpStatus()
    {
        // Arrange
        SetPatEnvironmentVariable();
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
        var verifier = CreateVerifier(mockClient);

        var profile = CreateProfile();

        // Act
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert: non-success
        Assert.False(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.Equal(401, result.HttpStatus);
        Assert.Single(result.Errors);
        Assert.Equal("The provided credentials are invalid.", result.Errors[0]);

        ClearPatEnvironmentVariable();
    }

    [Fact]
    public async Task VerifyAsync_ConnectivityFailsWithNullStatus_ReturnsFailureWithNullHttpStatus()
    {
        // Arrange
        SetPatEnvironmentVariable();
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
        var verifier = CreateVerifier(mockClient);

        var profile = CreateProfile();

        // Act
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Null(result.HttpStatus);
        Assert.Single(result.Errors);
        Assert.Equal("Connection timed out.", result.Errors[0]);

        ClearPatEnvironmentVariable();
    }

    // ─── Config-error: missing organization URL ───

    [Fact]
    public async Task VerifyAsync_MissingOrganizationUrl_ReturnsFailureWithConfigError()
    {
        // Arrange — no PAT env var needed; config validation fails first
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier();
        var verifier = CreateVerifier(mockClient);

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "broken-env",
            DisplayName = "Broken Environment",
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = "", // Missing
            Project = Project,
            PatEnvironmentVariable = PatEnvVar,
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
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier();
        var verifier = CreateVerifier(mockClient);

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "broken-env",
            DisplayName = "Broken Environment",
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = OrgUrl,
            Project = "", // Missing
            PatEnvironmentVariable = PatEnvVar,
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

    // ─── Config-error: missing PAT environment variable reference ───

    [Fact]
    public async Task VerifyAsync_MissingPatEnvironmentVariable_ReturnsFailureWithConfigError()
    {
        // Arrange
        var mockClient = new MockAzureDevOpsBoardsClientForVerifier();
        var verifier = CreateVerifier(mockClient);

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "broken-env",
            DisplayName = "Broken Environment",
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = OrgUrl,
            Project = Project,
            PatEnvironmentVariable = "", // Missing
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

    // ─── Config-error: PAT env var exists but value is not set ───

    [Fact]
    public async Task VerifyAsync_PatEnvVarNotSet_ReturnsFailureWithDescriptiveError()
    {
        // Arrange: ensure the env var is NOT set
        Environment.SetEnvironmentVariable(PatEnvVar, null);

        var mockClient = new MockAzureDevOpsBoardsClientForVerifier();
        var verifier = CreateVerifier(mockClient);

        var profile = CreateProfile();

        // Act — must not throw
        var result = await verifier.VerifyAsync(profile, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.Contains(
            result.Errors,
            e => e.Contains(PatEnvVar, StringComparison.Ordinal)
        );
    }

    // ─── Config-error: multiple config errors aggregated ───

    [Fact]
    public async Task VerifyAsync_MultipleConfigErrors_AggregatesAllErrors()
    {
        // Arrange: missing org URL, missing project, and missing PAT env var
        Environment.SetEnvironmentVariable(PatEnvVar, null);

        var mockClient = new MockAzureDevOpsBoardsClientForVerifier();
        var verifier = CreateVerifier(mockClient);

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "broken-env",
            DisplayName = "Broken Environment",
            Provider = "AzureDevOpsBoards",
            OrganizationUrl = "", // Missing
            Project = "", // Missing
            PatEnvironmentVariable = PatEnvVar,
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
            e => e.Contains("PAT", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ─── Helpers ───

    private static AzureDevOpsConnectivityVerifier CreateVerifier(
        IAzureDevOpsBoardsClient mockClient
    )
    {
        var factory = new MockAzureDevOpsBoardsClientFactory(mockClient);
        return new AzureDevOpsConnectivityVerifier(factory);
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
            PatEnvironmentVariable = PatEnvVar,
        };
    }

    private static void SetPatEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(PatEnvVar, "test-personal-access-token");
    }

    private static void ClearPatEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(PatEnvVar, null);
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
