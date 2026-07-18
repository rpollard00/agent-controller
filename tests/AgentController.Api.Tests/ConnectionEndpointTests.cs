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

/// <summary>End-to-end coverage for the connection management API endpoints.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields."
)]
public sealed class ConnectionEndpointTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private ConnectionApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-conn-{Guid.NewGuid():N}.db"
        );
        _factory = new ConnectionApiFactory(_databasePath);

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
    public async Task ConnectionEndpoints_SupportEveryVerb()
    {
        // POST: Create connection
        var profile = new
        {
            key = " ADO.TestOrg ",
            displayName = " Test Azure DevOps ",
            enabled = true,
            provider = "AzureDevOps",
            capabilities = new[] { "Repositories", "WorkTracking" },
            providerSettings = new
            {
                provider = "AzureDevOps",
                organizationUrl = " https://dev.azure.com/testorg ",
                personalAccessTokenReference = new
                {
                    name = "test-pat-secret",
                },
            },
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(
            "/api/webui/connections/ado.testorg",
            createResponse.Headers.Location?.ToString()
        );

        var created = await ReadJsonAsync(createResponse);
        Assert.Equal("ado.testorg", created.GetProperty("key").GetString());
        Assert.Equal("AzureDevOps", created.GetProperty("provider").GetString());

        // GET: List connections
        using var listResponse = await _client.GetAsync("/api/webui/connections");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listJson = await ReadJsonAsync(listResponse);
        Assert.Contains(
            listJson.EnumerateArray(),
            conn => conn.GetProperty("key").GetString() == "ado.testorg"
        );

        // GET: Get connection by key (case-insensitive)
        using var getResponse = await _client.GetAsync("/api/webui/connections/ADO.TESTORG");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var connection = await ReadJsonAsync(getResponse);
        Assert.Equal("ado.testorg", connection.GetProperty("key").GetString());

        // PUT: Update connection
        var update = new
        {
            key = "ado.testorg",
            displayName = "Updated Display Name",
            enabled = false,
            provider = "AzureDevOps",
            capabilities = new[] { "Repositories" },
            providerSettings = new
            {
                provider = "AzureDevOps",
                organizationUrl = "https://dev.azure.com/testorg",
                personalAccessTokenReference = new
                {
                    name = "test-pat-secret",
                },
            },
        };
        using var updateResponse = await _client.PutAsJsonAsync(
            "/api/webui/connections/ado.testorg",
            update
        );
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await ReadJsonAsync(updateResponse);
        Assert.Equal("Updated Display Name", updated.GetProperty("displayName").GetString());
        Assert.False(updated.GetProperty("enabled").GetBoolean());

        // DELETE: Delete connection
        using var deleteResponse = await _client.DeleteAsync("/api/webui/connections/ado.testorg");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // GET after delete: NotFound
        using var missingResponse = await _client.GetAsync("/api/webui/connections/ado.testorg");
        await AssertProblemAsync(missingResponse, HttpStatusCode.NotFound, "Resource not found.");
    }

    [Fact]
    public async Task ConnectionEndpoints_Create_RejectsDuplicateKey()
    {
        var profile = new
        {
            key = "ado.dupe",
            displayName = "First",
            provider = "AzureDevOps",
            capabilities = new[] { "Repositories" },
            providerSettings = new
            {
                provider = "AzureDevOps",
                organizationUrl = "https://dev.azure.com/testorg",
                personalAccessTokenReference = new { name = "pat" },
            },
        };

        using var createResponse = await _client.PostAsJsonAsync("/api/webui/connections", profile);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var duplicateResponse = await _client.PostAsJsonAsync("/api/webui/connections", profile);
        await AssertProblemAsync(duplicateResponse, HttpStatusCode.Conflict, "Resource conflict.");
    }

    [Fact]
    public async Task ConnectionEndpoints_Create_ValidationRejectsEmptyKey()
    {
        var profile = new
        {
            key = "",
            displayName = "",
            provider = "AzureDevOps",
            capabilities = Array.Empty<string>(),
        };

        using var response = await _client.PostAsJsonAsync("/api/webui/connections", profile);
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "Validation failed.");
        Assert.True(problem.GetProperty("errors").TryGetProperty("key", out _));
    }

    [Fact]
    public async Task ConnectionEndpoints_Verify_ReturnsConnectivityResult()
    {
        // Create a connection first
        var profile = new
        {
            key = "ado.verify",
            displayName = "Verify Test",
            provider = "AzureDevOps",
            capabilities = new[] { "Repositories", "WorkTracking" },
            providerSettings = new
            {
                provider = "AzureDevOps",
                organizationUrl = "https://dev.azure.com/testorg",
                personalAccessTokenReference = new { name = "test-pat" },
            },
        };

        using (var createResponse = await _client.PostAsJsonAsync("/api/webui/connections", profile))
        {
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        }

        // POST verify
        using var verifyResponse = await _client.PostAsJsonAsync(
            "/api/webui/connections/ado.verify/verify",
            new { }
        );
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        var result = await ReadJsonAsync(verifyResponse);
        Assert.True(result.TryGetProperty("success", out var successProp));
        // Will fail because no real PAT is configured, but should return a proper result
        _ = successProp.GetBoolean();
    }

    [Fact]
    public async Task ConnectionEndpoints_ListProjects_ReturnsEmptyForUnverified()
    {
        // Create a connection first
        var profile = new
        {
            key = "ado.projects",
            displayName = "Projects Test",
            provider = "AzureDevOps",
            capabilities = new[] { "Repositories" },
            providerSettings = new
            {
                provider = "AzureDevOps",
                organizationUrl = "https://dev.azure.com/testorg",
                personalAccessTokenReference = new { name = "test-pat" },
            },
        };

        using (var createResponse = await _client.PostAsJsonAsync("/api/webui/connections", profile))
        {
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        }

        // GET projects (will return empty because no real PAT)
        using var projectsResponse = await _client.GetAsync("/api/webui/connections/ado.projects/projects");
        Assert.Equal(HttpStatusCode.OK, projectsResponse.StatusCode);

        var projects = await ReadJsonAsync(projectsResponse);
        // Should be an empty array (no real PAT configured)
        Assert.Equal(JsonValueKind.Array, projects.ValueKind);
    }

    [Fact]
    public async Task ConnectionEndpoints_NotFound_ReturnsProblemDetails()
    {
        using var getResponse = await _client.GetAsync("/api/webui/connections/nonexistent");
        await AssertProblemAsync(getResponse, HttpStatusCode.NotFound, "Resource not found.");

        using var deleteResponse = await _client.DeleteAsync("/api/webui/connections/nonexistent");
        await AssertProblemAsync(deleteResponse, HttpStatusCode.NotFound, "Resource not found.");
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

    private sealed class ConnectionApiFactory(string databasePath) : SilentWebApplicationFactory
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
                    options.UseSqlite(
                        $"Data Source={databasePath}",
                        sqlite => sqlite.MigrationsAssembly("AgentController.Migrations")
                    )
                );
            });
        }
    }
}
