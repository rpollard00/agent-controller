using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentController.Api.Tests;

/// <summary>
/// API/integration tests for POST /api/webui/work-source-environments/{key}:verify.
/// Covers: success, config-error, missing-environment, unsupported-provider,
/// and legacy diagnostic route removal.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields."
)]
public sealed class WorkSourceConnectivityEndpointTests : IAsyncLifetime
{
    private const string TestSecretName = "test-verify-ado-pat";
    private const string TestPatValue = "test-personal-access-token-verify";

    private string _databasePath = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-verify-{Guid.NewGuid():N}.db"
        );

        _factory = new VerifyConnectivityApiFactory(_databasePath, TestSecretName, TestPatValue);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
        await database.Database.EnsureCreatedAsync();

        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();

        DeleteDatabaseFile(_databasePath);
        DeleteDatabaseFile($"{_databasePath}-shm");
        DeleteDatabaseFile($"{_databasePath}-wal");
        return Task.CompletedTask;
    }

    // ─── Success case: returns 200 with provider-neutral shape, PAT not echoed ───

    [Fact]
    public async Task VerifyEndpoint_Success_Returns200WithResultShapeAndNoPat()
    {
        // Arrange: create a work source environment that the mock factory will serve.
        var profile = CreateValidProfile("Verify.Success", "Success Environment", TestSecretName);

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/work-source-environments",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: POST verify
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/work-source-environments/verify.success:verify",
            null
        );

        // Assert: 200 OK
        var responseBody = await verifyResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        var result = JsonDocument.Parse(responseBody).RootElement;
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("PersonalAccessToken", result.GetProperty("authMechanism").GetString());
        Assert.Equal(200, result.GetProperty("httpStatus").GetInt32());
        Assert.Empty(result.GetProperty("errors").EnumerateArray());

        // Assert: payload contains repository list (from mock client)
        var payload = result.GetProperty("payload");
        Assert.True(payload.TryGetProperty("repositories", out var repos));
        Assert.Equal(2, repos.GetArrayLength());

        // Assert: PAT value is NOT present anywhere in the response body
        Assert.DoesNotContain(TestPatValue, responseBody, StringComparison.Ordinal);
    }

    // ─── Config-error case: secret not in store → non-success, still 200 ───

    [Fact]
    public async Task VerifyEndpoint_ConfigError_SecretNotFound_Returns200WithFailure()
    {
        // Arrange: create an environment whose PAT secret is NOT in the store.
        var profile = CreateValidProfile(
            "Verify.ConfigError",
            "Config Error Environment",
            "NONEXISTENT_SECRET_FOR_TEST"
        );

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/work-source-environments",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: POST verify
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/work-source-environments/verify.configerror:verify",
            null
        );

        // Assert: 200 OK (not 500)
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        var result = await ReadJsonAsync(verifyResponse);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("PersonalAccessToken", result.GetProperty("authMechanism").GetString());

        // Assert: errors describe the config problem
        var errors = result.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.NotEmpty(errors);
        Assert.Contains(
            errors,
            e => e.Contains("NONEXISTENT_SECRET_FOR_TEST", StringComparison.Ordinal)
        );
    }

    // ─── Missing-environment case: non-existent key ───

    [Fact]
    public async Task VerifyEndpoint_MissingEnvironment_Returns200WithNotFound()
    {
        // Act: POST verify for a key that doesn't exist
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/work-source-environments/nonexistent.env:verify",
            null
        );

        // Assert: 200 OK (not 404 — the endpoint always returns 200 with a result object)
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        var result = await ReadJsonAsync(verifyResponse);
        Assert.False(result.GetProperty("success").GetBoolean());

        var errors = result.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.NotEmpty(errors);
        Assert.Contains(
            errors,
            e => e.Contains("not found", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ─── Unsupported-provider case: no verifier registered for provider string ───

    [Fact]
    public async Task VerifyEndpoint_UnsupportedProvider_Returns200WithFailure()
    {
        // Arrange: create an environment with an unsupported provider.
        // Note: personalAccessTokenReference is required by validation regardless of provider.
        var profile = new
        {
            key = "Verify.Unsupported",
            displayName = "Unsupported Provider Environment",
            enabled = true,
            provider = "GitHubIssues", // No verifier registered for this
            organizationUrl = "https://github.com/testorg",
            project = "TestProject",
            completedStates = Array.Empty<string>(),
            tagPrefix = "agent",
            activeState = (string?)null,
            completedState = (string?)null,
            personalAccessTokenReference = new { name = TestSecretName },
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/work-source-environments",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: POST verify
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/work-source-environments/verify.unsupported:verify",
            null
        );

        // Assert: 200 OK
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        var result = await ReadJsonAsync(verifyResponse);
        Assert.False(result.GetProperty("success").GetBoolean());

        var errors = result.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.NotEmpty(errors);
        Assert.Contains(
            errors,
            e => e.Contains("GitHubIssues", StringComparison.Ordinal)
        );
        Assert.Contains(
            errors,
            e => e.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                || e.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
        );
    }

    // ─── Legacy diagnostic route removed (404) ───

    [Fact]
    public async Task LegacyDiagnosticEndpoint_Removed_Returns404()
    {
        // Act: GET the old diagnostic route
        using var response = await _client.GetAsync("/azure-devops/diagnostic");

        // Assert: 404 Not Found — the route was removed
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ─── Helpers ───

    private static object CreateValidProfile(string key, string displayName, string secretName) =>
        new
        {
            key,
            displayName,
            enabled = true,
            provider = "AzureDevOpsBoards",
            organizationUrl = "https://dev.azure.com/testorg/",
            project = "TestProject",
            completedStates = new[] { "Resolved" },
            tagPrefix = "agent",
            activeState = "Active",
            completedState = "Resolved",
            personalAccessTokenReference = new { name = secretName },
        };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static void DeleteDatabaseFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // ─── Test factory with mocked ADO client ───

    /// <summary>
    /// WebApplicationFactory that replaces IAzureDevOpsBoardsClientFactory
    /// with a mock that returns a successful connectivity result with repos.
    /// </summary>
    private sealed class VerifyConnectivityApiFactory(
        string databasePath,
        string testSecretName,
        string testSecretValue) : SilentWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["agentController:workerEnabled"] = "false",
                            ["workSource:provider"] = "LocalFake",
                            ["workSource:organizationUrl"] = "", // Clear appsettings fallback
                            ["workSource:project"] = "", // Clear appsettings fallback
                            ["sourceControl:provider"] = "NoOp",
                            ["environmentProvider:provider"] = "NoOp",
                            ["runtime:provider"] = "NoOp",
                            ["feedback:enabled"] = "false",
                            ["feedback:provider"] = "None",
                        }
                    )
            );

            builder.ConfigureServices(services =>
            {
                // Replace the real DbContext with an in-memory SQLite one.
                services.RemoveAll<AgentControllerDbContext>();
                services.RemoveAll<DbContextOptions<AgentControllerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AgentControllerDbContext>>();
                services.AddDbContext<AgentControllerDbContext>(options =>
                    options.UseSqlite($"Data Source={databasePath}")
                );

                // Replace the real ADO client factory with a mock that returns
                // a successful connectivity result with two test repositories.
                services.RemoveAll<IAzureDevOpsBoardsClientFactory>();
                services.AddSingleton<IAzureDevOpsBoardsClientFactory>(
                    new MockAzureDevOpsBoardsClientFactory()
                );

                // Register in-memory named secret store with test secret.
                var secretStore = new AgentController.Domain.Secrets.InMemorySecretStore();
                secretStore.CreateAsync(testSecretName, testSecretValue, CancellationToken.None)
                    .GetAwaiter().GetResult();
                services.RemoveAll<Domain.Secrets.ISecretStore>();
                services.RemoveAll<Domain.Secrets.ISecretManager>();
                services.AddSingleton<Domain.Secrets.ISecretStore>(secretStore);
                services.AddSingleton<Domain.Secrets.ISecretManager>(secretStore);
                services.TryAddSingleton<AgentController.Infrastructure.AzureDevOpsPatResolver>();
            });
        }
    }

    /// <summary>
    /// Mock factory returning a client that always succeeds with two repos.
    /// </summary>
    private sealed class MockAzureDevOpsBoardsClientFactory : IAzureDevOpsBoardsClientFactory
    {
        public IAzureDevOpsBoardsClient Create(WorkSourceEnvironmentProfile profile) =>
            new MockAzureDevOpsBoardsClient();
    }

    /// <summary>
    /// Mock ADO client that returns a successful connectivity result with two repos.
    /// </summary>
    private sealed class MockAzureDevOpsBoardsClient : IAzureDevOpsBoardsClient
    {
        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
            string organizationUrl,
            string project,
            string personalAccessToken,
            CancellationToken ct
        ) =>
            Task.FromResult(
                new AzureDevOpsConnectivityResult
                {
                    Success = true,
                    Status = System.Net.HttpStatusCode.OK,
                    Repositories = new List<RepositoryInfo>
                    {
                        new()
                        {
                            Id = "mock-repo-1",
                            Name = "main-repo",
                            DefaultBranch = "refs/heads/main",
                            RemoteUrl = "https://dev.azure.com/testorg/TestProject/_git/main-repo",
                        },
                        new()
                        {
                            Id = "mock-repo-2",
                            Name = "infra-repo",
                            DefaultBranch = "refs/heads/main",
                            RemoteUrl = "https://dev.azure.com/testorg/TestProject/_git/infra-repo",
                        },
                    },
                }
            );

        // Stub implementations for remaining interface members (not used by verifier endpoint)
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

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
            string project,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<RepositoryInfo>>(Array.Empty<RepositoryInfo>());

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
