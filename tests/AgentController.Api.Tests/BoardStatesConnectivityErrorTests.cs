using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentController.Api.Tests;

/// <summary>
/// HTTP-level tests for the board-states endpoint's failure-to-connectivity-error mapping.
/// Verifies that each failure mode returns a structured Problem response (502 Bad Gateway)
/// instead of a raw 500, and that no secrets leak into the error payload.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields."
)]
public sealed class BoardStatesConnectivityErrorTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private WebUiApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-boardstates-{Guid.NewGuid():N}.db"
        );
        _factory = new WebUiApiFactory(_databasePath);

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

    /// <summary>
    /// (a) Disabled source returns a disabled connectivity error, not 500.
    /// </summary>
    [Fact]
    public async Task BoardStates_DisabledSource_Returns502ConnectivityError()
    {
        // Arrange: create an enabled environment, then disable it.
        const string patVar = "BOARD_STATES_DISABLED_TEST_PAT";
        var profile = new
        {
            key = "ado.disabled",
            displayName = "Disabled ADO",
            enabled = true,
            provider = "AzureDevOpsBoards",
            organizationUrl = "https://dev.azure.com/example",
            project = "TestProject",
            tagPrefix = "agent",
            activeState = (string?)null,
            completedState = (string?)null,
            patEnvironmentVariable = patVar,
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/work-source-environments",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Disable the environment.
        var update = new
        {
            key = "ado.disabled",
            displayName = "Disabled ADO",
            enabled = false,
            provider = "AzureDevOpsBoards",
            organizationUrl = "https://dev.azure.com/example",
            project = "TestProject",
            tagPrefix = "agent",
            activeState = (string?)null,
            completedState = (string?)null,
            patEnvironmentVariable = patVar,
        };
        using var updateResponse = await _client.PutAsJsonAsync(
            "/api/webui/work-source-environments/ado.disabled",
            update
        );
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        // Act: query board-states on the disabled environment.
        using var boardStatesResponse = await _client.GetAsync(
            "/api/webui/work-source-environments/ado.disabled/board-states"
        );

        // Assert: 502 Bad Gateway with "disabled" in the detail, not 500.
        Assert.Equal(HttpStatusCode.BadGateway, boardStatesResponse.StatusCode);
        Assert.Equal("application/problem+json", boardStatesResponse.Content.Headers.ContentType?.MediaType);

        var problem = await ReadJsonAsync(boardStatesResponse);
        Assert.Equal(502, problem.GetProperty("status").GetInt32());
        Assert.Equal("Connectivity error.", problem.GetProperty("title").GetString());
        var detail = problem.GetProperty("detail").GetString();
        Assert.NotNull(detail);
        Assert.Contains("disabled", detail!, StringComparison.OrdinalIgnoreCase);

        // Assert: no secret leaked (nothing to leak here, but verify the payload is clean).
        Assert.DoesNotContain("personalAccessToken", await boardStatesResponse.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// (b) Missing PAT env var returns a missing-PAT connectivity error, not 500.
    /// </summary>
    [Fact]
    public async Task BoardStates_MissingPatEnvVar_Returns502ConnectivityError()
    {
        // Arrange: create an environment referencing a PAT env var that does not exist.
        const string nonexistentPatVar = "__BOARD_STATES_TEST_NONEXISTENT_PAT__";
        var profile = new
        {
            key = "ado.missing-pat",
            displayName = "Missing PAT ADO",
            enabled = true,
            provider = "AzureDevOpsBoards",
            organizationUrl = "https://dev.azure.com/example",
            project = "TestProject",
            tagPrefix = "agent",
            activeState = (string?)null,
            completedState = (string?)null,
            patEnvironmentVariable = nonexistentPatVar,
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/work-source-environments",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: query board-states — the PAT env var is not set.
        using var boardStatesResponse = await _client.GetAsync(
            "/api/webui/work-source-environments/ado.missing-pat/board-states"
        );

        // Assert: 502 Bad Gateway with the env var name in the detail, not 500.
        Assert.Equal(HttpStatusCode.BadGateway, boardStatesResponse.StatusCode);
        Assert.Equal("application/problem+json", boardStatesResponse.Content.Headers.ContentType?.MediaType);

        var problem = await ReadJsonAsync(boardStatesResponse);
        Assert.Equal(502, problem.GetProperty("status").GetInt32());
        Assert.Equal("Connectivity error.", problem.GetProperty("title").GetString());
        var detail = problem.GetProperty("detail").GetString();
        Assert.NotNull(detail);
        Assert.Contains(nonexistentPatVar, detail!);
        Assert.Contains("not set", detail!, StringComparison.OrdinalIgnoreCase);

        // Assert: no secret leaked.
        var body = await boardStatesResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("personalAccessToken", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (c) ADO HTTP error surfaces a connectivity error with the detail, not 500.
    /// </summary>
    [Fact]
    public async Task BoardStates_AdoHttpError_Returns502ConnectivityErrorWithDetail()
    {
        // Arrange: create an environment with a PAT env var that IS set (so we pass the PAT check).
        const string testPatVar = "BOARD_STATES_TEST_ADO_PAT";
        const string testPatValue = "test-pat-value-not-a-real-secret";
        Environment.SetEnvironmentVariable(testPatVar, testPatValue);
        try
        {
            var profile = new
            {
                key = "ado.http-error",
                displayName = "HTTP Error ADO",
                enabled = true,
                provider = "AzureDevOpsBoards",
                organizationUrl = "https://dev.azure.com/example",
                project = "TestProject",
                tagPrefix = "agent",
                activeState = (string?)null,
                completedState = (string?)null,
                patEnvironmentVariable = testPatVar,
            };

            using var createResponse = await _client.PostAsJsonAsync(
                "/api/webui/work-source-environments",
                profile
            );
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

            // Act: query board-states — the fake factory throws HttpRequestException.
            using var boardStatesResponse = await _client.GetAsync(
                "/api/webui/work-source-environments/ado.http-error/board-states"
            );

            // Assert: 502 Bad Gateway with HTTP error detail, not 500.
            Assert.Equal(HttpStatusCode.BadGateway, boardStatesResponse.StatusCode);
            Assert.Equal("application/problem+json", boardStatesResponse.Content.Headers.ContentType?.MediaType);

            var problem = await ReadJsonAsync(boardStatesResponse);
            Assert.Equal(502, problem.GetProperty("status").GetInt32());
            Assert.Equal("Connectivity error.", problem.GetProperty("title").GetString());
            var detail = problem.GetProperty("detail").GetString();
            Assert.NotNull(detail);
            Assert.Contains("Azure DevOps request failed", detail!);
            Assert.Contains("401", detail!);

            // Assert: the PAT value itself is not in the error payload.
            var body = await boardStatesResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(testPatValue, body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(testPatVar, null);
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static void DeleteDatabaseFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// WebApplicationFactory subclass that replaces IAzureDevOpsBoardsClientFactory
    /// with a fake that throws HttpRequestException on GetValidStatesAsync.
    /// </summary>
    private sealed class WebUiApiFactory(string databasePath) : SilentWebApplicationFactory
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
                services.RemoveAll<AgentControllerDbContext>();
                services.RemoveAll<DbContextOptions<AgentControllerDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AgentControllerDbContext>>();
                services.AddDbContext<AgentControllerDbContext>(options =>
                    options.UseSqlite($"Data Source={databasePath}")
                );

                // Replace the boards client factory with one that throws on GetValidStatesAsync.
                services.RemoveAll<IAzureDevOpsBoardsClientFactory>();
                services.AddSingleton<IAzureDevOpsBoardsClientFactory>(
                    _ => new FakeThrowingBoardsClientFactory()
                );
            });
        }
    }

    /// <summary>Factory whose client throws HttpRequestException on GetValidStatesAsync.</summary>
    private sealed class FakeThrowingBoardsClientFactory : IAzureDevOpsBoardsClientFactory
    {
        public IAzureDevOpsBoardsClient Create(WorkSourceEnvironmentProfile profile) =>
            new FakeThrowingBoardsClient();
    }

    private sealed class FakeThrowingBoardsClient : IAzureDevOpsBoardsClient
    {
        public Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(
            BoardsQueryParameters parameters, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClaimResult> TryClaimWorkItemAsync(
            ExternalWorkRef workRef, ClaimRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> UpdateWorkItemStatusAsync(
            ExternalWorkRef workRef, ExternalWorkStatus status, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddCommentAsync(
            ExternalWorkRef workRef, string comment, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
            string project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
            string organizationUrl, string project, string personalAccessToken,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef, int maxComments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseClaimWorkItemAsync(
            ReleaseClaimRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetValidStatesAsync(
            string project, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Request failed: HTTP 401 Unauthorized");
    }
}
