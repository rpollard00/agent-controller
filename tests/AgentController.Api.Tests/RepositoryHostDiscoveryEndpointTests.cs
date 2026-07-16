using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentController.Application;
using AgentController.Application.Abstractions;
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
/// API/integration tests for the repository host discovery and onboarding endpoints:
/// GET /api/webui/repository-host-connections/{key}/repositories
/// POST /api/webui/repository-host-connections/{key}/repositories/onboard
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields."
)]
public sealed class RepositoryHostDiscoveryEndpointTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-repo-discovery-{Guid.NewGuid():N}.db"
        );

        _factory = new DiscoveryApiFactory(_databasePath);

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

    // ─── GET /repositories: success case ───

    [Fact]
    public async Task ListRepositoriesEndpoint_Success_Returns200WithRepos()
    {
        // Arrange: create a repository host connection.
        var profile = CreateValidProfile("discovery.success", "Discovery Test");
        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: GET repositories
        using var response = await _client.GetAsync(
            "/api/webui/repository-host-connections/discovery.success/repositories"
        );

        // Assert: 200 OK with repo list
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.ValueKind == JsonValueKind.Array);
        var repos = body.EnumerateArray().ToList();
        Assert.Equal(2, repos.Count);

        // Verify first repo shape
        var firstRepo = repos[0];
        Assert.Equal("mock-repo-1", firstRepo.GetProperty("id").GetString());
        Assert.Equal("main-repo", firstRepo.GetProperty("name").GetString());
        Assert.Equal("main", firstRepo.GetProperty("defaultBranch").GetString());
        Assert.NotNull(firstRepo.GetProperty("remoteUrl").GetString());
        Assert.Equal(
            "httpsPat",
            firstRepo.GetProperty("cloneTransportHint").GetString()
        );

        // Cleanup
        await _client.DeleteAsync("/api/webui/repository-host-connections/discovery.success");
    }

    // ─── GET /repositories: missing connection ───

    [Fact]
    public async Task ListRepositoriesEndpoint_MissingConnection_Returns200WithEmptyList()
    {
        // Act: GET repositories for a non-existent connection
        using var response = await _client.GetAsync(
            "/api/webui/repository-host-connections/nonexistent.discovery/repositories"
        );

        // Assert: 200 OK with empty list (not 404)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.ValueKind == JsonValueKind.Array);
        Assert.Empty(body.EnumerateArray());
    }

    // ─── GET /repositories: unsupported provider ───

    [Fact]
    public async Task ListRepositoriesEndpoint_UnsupportedProvider_Returns200WithEmptyList()
    {
        // Arrange: create a connection with an unsupported provider.
        var profile = new
        {
            key = "discovery.unsupported",
            displayName = "Unsupported Provider",
            enabled = true,
            provider = "GitHub", // No host registered
            organizationUrl = "https://github.com/testorg",
            project = "TestProject",
            personalAccessTokenReference = new
            {
                name = "github-token",
            },
        };
        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: GET repositories
        using var response = await _client.GetAsync(
            "/api/webui/repository-host-connections/discovery.unsupported/repositories"
        );

        // Assert: 200 OK with empty list
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.ValueKind == JsonValueKind.Array);
        Assert.Empty(body.EnumerateArray());

        // Cleanup
        await _client.DeleteAsync("/api/webui/repository-host-connections/discovery.unsupported");
    }

    // ─── POST /repositories/onboard: success case ───

    [Fact]
    public async Task OnboardEndpoint_Success_Returns201WithRepositoryProfile()
    {
        // Arrange: create a repository host connection.
        var profile = CreateValidProfile("onboard.success", "Onboard Test");
        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: POST onboard
        var onboardRequest = new
        {
            repositoryId = "mock-repo-1",
            repositoryKey = "onboarded-repo",
        };
        using var response = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections/onboard.success/repositories/onboard",
            onboardRequest
        );

        // Assert: 201 Created
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("onboarded-repo", response.Headers.Location.ToString());

        var body = await ReadJsonAsync(response);
        Assert.Equal("onboarded-repo", body.GetProperty("key").GetString());
        Assert.NotNull(body.GetProperty("cloneUrl").GetString());
        Assert.Equal("main", body.GetProperty("defaultBranch").GetString());

        // Cleanup
        await _client.DeleteAsync("/api/webui/repositories/onboarded-repo");
        await _client.DeleteAsync("/api/webui/repository-host-connections/onboard.success");
    }

    // ─── POST /repositories/onboard: missing connection ───

    [Fact]
    public async Task OnboardEndpoint_MissingConnection_Returns404()
    {
        // Act: POST onboard for a non-existent connection
        var onboardRequest = new
        {
            repositoryId = "some-repo",
            repositoryKey = "should-not-create",
        };
        using var response = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections/nonexistent.onboard/repositories/onboard",
            onboardRequest
        );

        // Assert: 404 Not Found
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Resource not found.", body.GetProperty("title").GetString());
    }

    // ─── POST /repositories/onboard: repo not found on host ───

    [Fact]
    public async Task OnboardEndpoint_RepoNotFoundOnHost_Returns404()
    {
        // Arrange: create a repository host connection.
        var profile = CreateValidProfile("onboard.missing-repo", "Missing Repo Test");
        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: POST onboard for a repo id that doesn't exist on the host
        var onboardRequest = new
        {
            repositoryId = "does-not-exist-repo",
            repositoryKey = "should-not-create",
        };
        using var response = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections/onboard.missing-repo/repositories/onboard",
            onboardRequest
        );

        // Assert: 404 Not Found
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Resource not found.", body.GetProperty("title").GetString());

        // Cleanup
        await _client.DeleteAsync("/api/webui/repository-host-connections/onboard.missing-repo");
    }

    // ─── POST /repositories/onboard: conflict (duplicate key) ───

    [Fact]
    public async Task OnboardEndpoint_DuplicateKey_Returns409()
    {
        // Arrange: create a repository host connection and onboard a repo.
        var profile = CreateValidProfile("onboard.conflict", "Conflict Test");
        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var onboardRequest = new
        {
            repositoryId = "mock-repo-1",
            repositoryKey = "conflict-repo",
        };

        // First onboard succeeds.
        using (var firstResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections/onboard.conflict/repositories/onboard",
            onboardRequest
        ))
        {
            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        }

        // Act: Second onboard with same key should conflict.
        using var secondResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections/onboard.conflict/repositories/onboard",
            onboardRequest
        );

        // Assert: 409 Conflict
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
        var body = await ReadJsonAsync(secondResponse);
        Assert.Equal("Resource conflict.", body.GetProperty("title").GetString());

        // Cleanup
        await _client.DeleteAsync("/api/webui/repositories/conflict-repo");
        await _client.DeleteAsync("/api/webui/repository-host-connections/onboard.conflict");
    }

    // ─── Helpers ───

    private static object CreateValidProfile(string key, string displayName) =>
        new
        {
            key,
            displayName,
            enabled = true,
            provider = "AzureDevOpsRepos",
            organizationUrl = "https://dev.azure.com/testorg/",
            project = "TestProject",
            personalAccessTokenReference = new
            {
                name = "test-pat-secret",
            },
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

    // ─── Test factory with mocked repository host resolver ───

    /// <summary>
    /// WebApplicationFactory that replaces IRepositoryHostResolver
    /// with a mock that returns deterministic repos for listing and onboarding.
    /// </summary>
    private sealed class DiscoveryApiFactory(string databasePath) : SilentWebApplicationFactory
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
                            ["workSource:organizationUrl"] = "",
                            ["workSource:project"] = "",
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

                // Replace the real IRepositoryHostResolver with a mock that returns test repos.
                services.RemoveAll<IRepositoryHostResolver>();
                services.AddSingleton<IRepositoryHostResolver>(new DiscoveryMockResolver());
            });
        }
    }

    /// <summary>
    /// Mock resolver that returns 2 test repos for AzureDevOpsRepos provider
    /// and empty list for unsupported providers.
    /// </summary>
    private sealed class DiscoveryMockResolver : IRepositoryHostResolver
    {
        private static readonly IReadOnlyList<HostRepository> TestRepositories =
        [
            new HostRepository(
                Id: "mock-repo-1",
                Name: "main-repo",
                DefaultBranch: "main",
                RemoteUrl: "https://dev.azure.com/testorg/TestProject/_git/main-repo",
                CloneTransportHint: CloneTransportHint.HttpsPat
            ),
            new HostRepository(
                Id: "mock-repo-2",
                Name: "infra-repo",
                DefaultBranch: "main",
                RemoteUrl: "https://dev.azure.com/testorg/TestProject/_git/infra-repo",
                CloneTransportHint: CloneTransportHint.HttpsPat
            ),
        ];

        public Task<AgentController.Application.Results.RepositoryHostConnectivityResult> VerifyConnectivityAsync(
            RepositoryHostConnectionProfile profile,
            CancellationToken cancellationToken
        )
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromException<AgentController.Application.Results.RepositoryHostConnectivityResult>(
                    new OperationCanceledException(cancellationToken)
                );
            }

            if (string.Equals(profile.Provider, "AzureDevOpsRepos", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(
                    AgentController.Application.Results.RepositoryHostConnectivityResult.SuccessResult(
                        authMechanism: "PersonalAccessToken",
                        httpStatus: 200
                    )
                );
            }

            return Task.FromResult(
                AgentController.Application.Results.RepositoryHostConnectivityResult.FailureResult(
                    new[]
                    {
                        $"Provider '{profile.Provider}' is not supported by any registered repository host.",
                    },
                    authMechanism: string.Empty
                )
            );
        }

        public Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
            RepositoryHostConnectionProfile profile,
            CancellationToken cancellationToken
        )
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromException<IReadOnlyList<HostRepository>>(
                    new OperationCanceledException(cancellationToken)
                );
            }

            if (string.Equals(profile.Provider, "AzureDevOpsRepos", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<IReadOnlyList<HostRepository>>(TestRepositories);
            }

            // Unsupported provider returns empty list.
            return Task.FromResult<IReadOnlyList<HostRepository>>(Array.Empty<HostRepository>());
        }
    }
}
