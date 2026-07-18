using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Domain.Secrets;
using AgentController.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Infrastructure.Tests;

public sealed class AzureDevOpsConnectionTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string TestPat = "test-pat-token";

    [Fact]
    public async Task VerifyConnectivityAsync_NoProviderSettings_ReturnsFailure()
    {
        var sut = CreateConnection(pat: TestPat, verifySuccess: true);
        var profile = new ConnectionProfile
        {
            Key = "no-settings",
            Provider = "GitHub",
            ProviderSettings = null,
        };

        var result = await sut.VerifyConnectivityAsync(profile, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.Contains("Azure DevOps settings", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyConnectivityAsync_EmptyOrganizationUrl_ReturnsFailure()
    {
        var sut = CreateConnection(pat: TestPat, verifySuccess: true);
        var profile = new ConnectionProfile
        {
            Key = "empty-url",
            Provider = "AzureDevOps",
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                OrganizationUrl = string.Empty,
                PersonalAccessTokenReference = SecretReference.ByName("test-pat"),
            },
        };

        var result = await sut.VerifyConnectivityAsync(profile, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("organization URL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyConnectivityAsync_NoPatReference_ReturnsFailure()
    {
        var sut = CreateConnection(pat: TestPat, verifySuccess: true);
        var profile = new ConnectionProfile
        {
            Key = "no-pat",
            Provider = "AzureDevOps",
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                OrganizationUrl = OrgUrl,
                PersonalAccessTokenReference = SecretReference.Empty,
            },
        };

        var result = await sut.VerifyConnectivityAsync(profile, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("PAT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyConnectivityAsync_Success_ReturnsSuccessResult()
    {
        var sut = CreateConnection(pat: TestPat, verifySuccess: true);
        var profile = CreateProfile();

        var result = await sut.VerifyConnectivityAsync(profile, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("PersonalAccessToken", result.AuthMechanism);
        Assert.NotNull(result.Payload);
        Assert.IsType<Dictionary<string, object>>(result.Payload);
        Assert.Contains("repositories", ((Dictionary<string, object>)result.Payload!).Keys);
    }

    [Fact]
    public async Task VerifyConnectivityAsync_BoardsClientFailure_ReturnsFailureResult()
    {
        var sut = CreateConnection(pat: TestPat, verifySuccess: false);
        var profile = CreateProfile();

        var result = await sut.VerifyConnectivityAsync(profile, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task VerifyConnectivityAsync_PatResolutionFailure_ReturnsFailureResult()
    {
        var sut = CreateConnection(pat: null!, verifySuccess: true); // null PAT simulates resolution failure
        var profile = CreateProfile();

        var result = await sut.VerifyConnectivityAsync(profile, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("PAT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListProjectsAsync_NoProviderSettings_ReturnsEmpty()
    {
        var sut = CreateConnection(pat: TestPat, verifySuccess: true);
        var profile = new ConnectionProfile
        {
            Key = "no-settings",
            Provider = "GitHub",
            ProviderSettings = null,
        };

        var projects = await sut.ListProjectsAsync(profile, CancellationToken.None);

        Assert.Empty(projects);
    }

    [Fact]
    public async Task ListProjectsAsync_EmptyOrganizationUrl_ReturnsEmpty()
    {
        var sut = CreateConnection(pat: TestPat, verifySuccess: true);
        var profile = new ConnectionProfile
        {
            Key = "empty-url",
            Provider = "AzureDevOps",
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                OrganizationUrl = string.Empty,
                PersonalAccessTokenReference = SecretReference.ByName("test-pat"),
            },
        };

        var projects = await sut.ListProjectsAsync(profile, CancellationToken.None);

        Assert.Empty(projects);
    }

    [Fact]
    public async Task ListProjectsAsync_Success_ReturnsProjects()
    {
        var handler = new MockProjectsHandler(new[]
        {
            new { id = "project-1", name = "ProjectAlpha" },
            new { id = "project-2", name = "ProjectBeta" },
        });

        var sut = CreateConnectionWithHandler(handler, pat: TestPat, verifySuccess: true);
        var profile = CreateProfile();

        var projects = await sut.ListProjectsAsync(profile, CancellationToken.None);

        Assert.Equal(2, projects.Count);
        Assert.Equal("project-1", projects[0].Id);
        Assert.Equal("ProjectAlpha", projects[0].Name);
        Assert.Equal("project-2", projects[1].Id);
        Assert.Equal("ProjectBeta", projects[1].Name);
    }

    [Fact]
    public async Task ListProjectsAsync_HttpError_ReturnsEmpty()
    {
        var handler = new MockProjectsHandler(projects: null!, statusCode: HttpStatusCode.Unauthorized);

        var sut = CreateConnectionWithHandler(handler, pat: TestPat, verifySuccess: true);
        var profile = CreateProfile();

        var projects = await sut.ListProjectsAsync(profile, CancellationToken.None);

        Assert.Empty(projects);
    }

    [Fact]
    public async Task ListProjectsAsync_InvalidPat_ReturnsEmpty()
    {
        var sut = CreateConnection(pat: null!, verifySuccess: true);
        var profile = CreateProfile();

        var projects = await sut.ListProjectsAsync(profile, CancellationToken.None);

        Assert.Empty(projects);
    }

    [Fact]
    public async Task ListProjectsAsync_Cancellation_ReturnsEmpty()
    {
        var handler = new DelayingProjectsHandler();

        var sut = CreateConnectionWithHandler(handler, pat: TestPat, verifySuccess: true);
        var profile = CreateProfile();

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var projects = await sut.ListProjectsAsync(profile, cts.Token);

        Assert.Empty(projects);
    }

    [Fact]
    public async Task ListRepositoriesAsync_NoProject_ReturnsEmpty()
    {
        var sut = CreateConnection(pat: TestPat, verifySuccess: true);
        var profile = CreateProfile();

        var repos = await sut.ListRepositoriesAsync(profile, string.Empty, CancellationToken.None);

        Assert.Empty(repos);
    }

    [Fact]
    public async Task ListRepositoriesAsync_Success_ReturnsRepositories()
    {
        var mockClient = new MockAzureDevOpsBoardsClient
        {
            Repositories = new[]
            {
                new RepositoryInfo
                {
                    Id = "repo-1",
                    Name = "main-repo",
                    DefaultBranch = "refs/heads/main",
                    RemoteUrl = "https://dev.azure.com/testorg/Project/_git/main-repo",
                },
                new RepositoryInfo
                {
                    Id = "repo-2",
                    Name = "infra-repo",
                    DefaultBranch = "refs/heads/develop",
                    RemoteUrl = "https://dev.azure.com/testorg/Project/_git/infra-repo",
                },
            },
        };

        var sut = CreateConnection(pat: TestPat, verifySuccess: true, mockBoardsClient: mockClient);
        var profile = CreateProfile();

        var repos = await sut.ListRepositoriesAsync(profile, "TestProject", CancellationToken.None);

        Assert.Equal(2, repos.Count);
        Assert.Equal("repo-1", repos[0].Id);
        Assert.Equal("main-repo", repos[0].Name);
        Assert.Equal("main", repos[0].DefaultBranch); // refs/heads/ stripped
        Assert.Equal(CloneTransportHint.HttpsPat, repos[0].CloneTransportHint);
        Assert.Equal("develop", repos[1].DefaultBranch); // refs/heads/ stripped
    }

    // ─── Helpers ───────────────────────────────────────────────

    private static ConnectionProfile CreateProfile()
    {
        return new ConnectionProfile
        {
            Key = "azuredevops-testorg",
            DisplayName = "Test ADO Connection",
            Enabled = true,
            Provider = "AzureDevOps",
            Capabilities = [ConnectionCapability.Repositories, ConnectionCapability.WorkTracking],
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                OrganizationUrl = OrgUrl,
                PersonalAccessTokenReference = SecretReference.ByName("test-pat"),
            },
        };
    }

    private static AzureDevOpsConnection CreateConnection(
        string pat,
        bool verifySuccess,
        MockAzureDevOpsBoardsClient? mockBoardsClient = null
    )
    {
        var patResolver = new TestPatResolver(pat);
        var clientFactory = new TestClientFactory(verifySuccess, mockBoardsClient);
        return new AzureDevOpsConnection(clientFactory, patResolver, NullLogger<AzureDevOpsConnection>.Instance);
    }

    private static AzureDevOpsConnection CreateConnectionWithHandler(
        HttpMessageHandler handler,
        string pat,
        bool verifySuccess,
        MockAzureDevOpsBoardsClient? mockBoardsClient = null
    )
    {
        var patResolver = new TestPatResolver(pat);
        var clientFactory = new TestClientFactoryWithHandler(handler, verifySuccess, mockBoardsClient);
        return new AzureDevOpsConnection(clientFactory, patResolver, NullLogger<AzureDevOpsConnection>.Instance, httpHandler: handler);
    }

    // ─── Test stubs ────────────────────────────────────────────

    private sealed class TestPatResolver(string? pat) : AzureDevOpsPatResolver
    {
        public override Task<string?> ResolveFromSecretReferenceAsync(
            SecretReference reference,
            CancellationToken cancellationToken
        ) => Task.FromResult(pat);
    }

    private sealed class TestClientFactory(
        bool verifySuccess,
        MockAzureDevOpsBoardsClient? mockClient
    ) : AzureDevOpsClientFactory
    {
        private readonly bool _verifySuccess = verifySuccess;
        private readonly MockAzureDevOpsBoardsClient? _mockClient = mockClient;

        public override IAzureDevOpsBoardsClient Create(
            string organizationUrl,
            string project,
            string personalAccessToken
        )
        {
            return _mockClient ?? new MockAzureDevOpsBoardsClient { VerifySuccess = _verifySuccess };
        }
    }

    private sealed class TestClientFactoryWithHandler(
        HttpMessageHandler handler,
        bool verifySuccess,
        MockAzureDevOpsBoardsClient? mockClient
    ) : AzureDevOpsClientFactory
    {
        private readonly HttpMessageHandler _handler = handler;
        private readonly bool _verifySuccess = verifySuccess;
        private readonly MockAzureDevOpsBoardsClient? _mockClient = mockClient;

        public override IAzureDevOpsBoardsClient Create(
            string organizationUrl,
            string project,
            string personalAccessToken
        )
        {
            return _mockClient ?? new MockAzureDevOpsBoardsClient
            {
                VerifySuccess = _verifySuccess,
                HttpClient = new HttpClient(_handler) { BaseAddress = new Uri(organizationUrl.TrimEnd('/') + "/") },
            };
        }
    }

    // Mock ADO boards client
    private sealed class MockAzureDevOpsBoardsClient : IAzureDevOpsBoardsClient
    {
        public bool VerifySuccess { get; set; } = true;
        public IReadOnlyList<RepositoryInfo> Repositories { get; set; } = Array.Empty<RepositoryInfo>();
        public HttpClient? HttpClient { get; set; }

        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
            string organizationUrl,
            string project,
            string personalAccessToken,
            CancellationToken ct
        ) => Task.FromResult(
            new AzureDevOpsConnectivityResult
            {
                Success = VerifySuccess,
                Status = VerifySuccess ? HttpStatusCode.OK : HttpStatusCode.Unauthorized,
                Error = VerifySuccess ? null : "Unauthorized",
                Repositories = Repositories,
            }
        );

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
            string project,
            CancellationToken ct
        ) => Task.FromResult(Repositories);

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

    // Mock HTTP handler for projects API
    private sealed class MockProjectsHandler(
        object?[]? projects,
        HttpStatusCode statusCode = HttpStatusCode.OK
    ) : HttpMessageHandler
    {
        private readonly object?[]? _projects = projects;
        private readonly HttpStatusCode _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (_statusCode != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(_statusCode));
            }

            var json = JsonSerializer.Serialize(new { value = _projects });
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    // Delaying handler for cancellation tests
    private sealed class DelayingProjectsHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
