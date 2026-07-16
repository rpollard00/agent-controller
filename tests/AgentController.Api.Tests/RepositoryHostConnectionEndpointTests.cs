using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentController.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentController.Api.Tests;

/// <summary>
/// End-to-end coverage for the repository host connection web UI API.
/// Uses named-secret reference shape for PAT configuration.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields."
)]
public sealed class RepositoryHostConnectionEndpointTests : IAsyncLifetime
{
    private const string TestSecretName = "test-repo-host-ado-pat";

    private string _databasePath = null!;
    private WebUiApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-repo-host-{Guid.NewGuid():N}.db"
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

    [Fact]
    public async Task RepositoryHostConnectionEndpoints_SupportEveryVerbAndRedactCredentials()
    {
        var profile = new
        {
            key = " ADO.Repos ",
            displayName = " Main Azure DevOps Repos ",
            enabled = true,
            provider = "AzureDevOpsRepos",
            organizationUrl = "https://dev.azure.com/example/",
            project = "Agent Controller",
            personalAccessTokenReference = new
            {
                name = TestSecretName,
            },
        };

        // ─── POST: create ───
        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(
            "/api/webui/repository-host-connections/ado.repos",
            createResponse.Headers.Location?.ToString()
        );

        // ─── POST: duplicate ───
        using var duplicateResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            profile
        );
        await AssertProblemAsync(
            duplicateResponse,
            HttpStatusCode.Conflict,
            "Resource conflict."
        );

        // ─── GET: list ───
        using var listResponse = await _client.GetAsync("/api/webui/repository-host-connections");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = await listResponse.Content.ReadAsStringAsync();
        using var listJson = JsonDocument.Parse(listBody);
        Assert.Contains(
            listJson.RootElement.EnumerateArray(),
            connection => connection.GetProperty("key").GetString() == "ado.repos"
        );

        // ─── GET: by key (case-insensitive) ───
        using var getResponse = await _client.GetAsync(
            "/api/webui/repository-host-connections/ADO.REPOS"
        );
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadAsStringAsync();
        using var getJson = JsonDocument.Parse(getBody);
        Assert.Equal(
            TestSecretName,
            getJson.RootElement.GetProperty("personalAccessTokenReference").GetProperty("name").GetString()
        );

        // ─── PUT: update ───
        var update = new
        {
            key = "ado.repos",
            displayName = "Updated Azure DevOps Repos",
            enabled = false,
            provider = "AzureDevOpsRepos",
            organizationUrl = "https://dev.azure.com/example",
            project = "Agent Controller",
            personalAccessTokenReference = new
            {
                name = TestSecretName,
            },
        };
        using var updateResponse = await _client.PutAsJsonAsync(
            "/api/webui/repository-host-connections/ado.repos",
            update
        );
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updateBody = await updateResponse.Content.ReadAsStringAsync();
        using var updateJson = JsonDocument.Parse(updateBody);
        Assert.Equal(
            "Updated Azure DevOps Repos",
            updateJson.RootElement.GetProperty("displayName").GetString()
        );
        Assert.False(updateJson.RootElement.GetProperty("enabled").GetBoolean());

        // ─── DELETE ───
        using var deleteResponse = await _client.DeleteAsync(
            "/api/webui/repository-host-connections/ado.repos"
        );
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // ─── GET: missing after delete ───
        using var missingResponse = await _client.GetAsync(
            "/api/webui/repository-host-connections/ado.repos"
        );
        await AssertProblemAsync(
            missingResponse,
            HttpStatusCode.NotFound,
            "Resource not found."
        );
    }

    [Fact]
    public async Task RepositoryHostConnectionEndpoints_ValidationFailsOnMissingFields()
    {
        var invalidProfile = new
        {
            key = "",
            displayName = "",
            organizationUrl = "",
            project = "",
            personalAccessTokenReference = new
            {
                name = "",
            },
        };

        using var validationResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            invalidProfile
        );
        var validationProblem = await AssertProblemAsync(
            validationResponse,
            HttpStatusCode.BadRequest,
            "Validation failed."
        );
        Assert.True(validationProblem.GetProperty("errors").TryGetProperty("key", out _));
        Assert.True(validationProblem.GetProperty("errors").TryGetProperty("displayName", out _));
        Assert.True(
            validationProblem.GetProperty("errors").TryGetProperty("personalAccessTokenReference.name", out _)
        );
    }

    [Fact]
    public async Task RepositoryHostConnectionEndpoints_KeyMismatchOnUpdate_ReturnsValidationFailed()
    {
        // Create a valid connection first.
        var profile = new
        {
            key = "key.mismatch.test",
            displayName = "Key Mismatch Test",
            enabled = true,
            provider = "AzureDevOpsRepos",
            organizationUrl = "https://dev.azure.com/example",
            project = "TestProject",
            personalAccessTokenReference = new
            {
                name = TestSecretName,
            },
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Try to update with a different key.
        var update = new
        {
            key = "different.key",
            displayName = "Updated",
            enabled = true,
            provider = "AzureDevOpsRepos",
            organizationUrl = "https://dev.azure.com/example",
            project = "TestProject",
            personalAccessTokenReference = new
            {
                name = TestSecretName,
            },
        };
        using var updateResponse = await _client.PutAsJsonAsync(
            "/api/webui/repository-host-connections/key.mismatch.test",
            update
        );
        var problem = await AssertProblemAsync(
            updateResponse,
            HttpStatusCode.BadRequest,
            "Validation failed."
        );
        Assert.True(problem.GetProperty("errors").TryGetProperty("key", out _));

        // Cleanup.
        await _client.DeleteAsync("/api/webui/repository-host-connections/key.mismatch.test");
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedTitle
    )
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadJsonAsync(response);
        Assert.Equal((int)expectedStatus, problem.GetProperty("status").GetInt32());
        Assert.Equal(expectedTitle, problem.GetProperty("title").GetString());
        return problem;
    }

    private static void DeleteDatabaseFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

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
            });
        }
    }
}
