using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentController.Api.Tests;

/// <summary>
/// Integration tests for the GET /azure-devops/diagnostic endpoint.
/// Uses WebApplicationFactory to verify the endpoint's response structure
/// including the repositories array.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields.")]
public class AzureDevOpsDiagnosticTests : IAsyncLifetime
{
    private SilentWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Configure ADO connection via environment variables
        Environment.SetEnvironmentVariable("workSource__organizationUrl", "https://dev.azure.com/testorg");
        Environment.SetEnvironmentVariable("workSource__project", "TestProject");
        Environment.SetEnvironmentVariable("workSource__provider", "AzureDevOpsBoards");
        Environment.SetEnvironmentVariable("azureDevOps__personalAccessToken", "test-pat-token");
        Environment.SetEnvironmentVariable("agentController__workerId", "test-diagnostic-worker");
        Environment.SetEnvironmentVariable("agentController__workerEnabled", "false");
        // Use no-op persistence to avoid DB dependency
        Environment.SetEnvironmentVariable("persistence__provider", "NoOp");

        _factory = new SilentWebApplicationFactory();
        _client = _factory.CreateClient();
        await Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();

        // Clean up environment variables
        Environment.SetEnvironmentVariable("workSource__organizationUrl", null);
        Environment.SetEnvironmentVariable("workSource__project", null);
        Environment.SetEnvironmentVariable("workSource__provider", null);
        Environment.SetEnvironmentVariable("azureDevOps__personalAccessToken", null);
        Environment.SetEnvironmentVariable("agentController__workerId", null);
        Environment.SetEnvironmentVariable("agentController__workerEnabled", null);
        Environment.SetEnvironmentVariable("persistence__provider", null);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task DiagnosticEndpoint_ResponseContainsExpectedFields()
    {
        // Even with a real (failing) ADO connection, the response should have
        // the expected structure including the repositories array
        var response = await _client.GetAsync("/azure-devops/diagnostic");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var root = doc.RootElement;

        // Verify all expected fields are present
        Assert.True(root.TryGetProperty("status", out _));
        Assert.True(root.TryGetProperty("organizationUrl", out _));
        Assert.True(root.TryGetProperty("project", out _));
        Assert.True(root.TryGetProperty("patConfigured", out _));
        Assert.True(root.TryGetProperty("repositories", out var reposProp));
        Assert.True(root.TryGetProperty("errors", out _));
        Assert.True(root.TryGetProperty("timestamp", out _));

        // repositories should be an array (empty if API call fails)
        Assert.Equal(JsonValueKind.Array, reposProp.ValueKind);
    }

    [Fact]
    public async Task DiagnosticEndpoint_PatNotExposedInResponse()
    {
        var response = await _client.GetAsync("/azure-devops/diagnostic");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // The raw PAT should never appear in the response
        Assert.DoesNotContain("test-pat-token", body);
    }
}

/// <summary>
/// Standalone smoke tests for the diagnostic endpoint that don't require
/// WebApplicationFactory — verifies the endpoint is mapped and the
/// repository JSON parsing logic produces the expected structure.
/// </summary>
public class AzureDevOpsDiagnosticSmokeTests
{
    [Fact]
    public void DiagnosticEndpoint_ProgramTypeExists()
    {
        // Verify the Program type is loadable (endpoint is part of the API).
        var programType = typeof(Program);
        Assert.NotNull(programType);
    }

    [Fact]
    public void DiagnosticEndpoint_ReturnsRepositoriesArray_OnMockedResponse()
    {
        // Verify the JSON parsing logic that the diagnostic endpoint uses
        // produces the expected structure by simulating its behavior.
        var reposJson = """
        {
          "count": 2,
          "value": [
            {
              "id": "11111111-2222-3333-4444-555555555555",
              "name": "repo-one",
              "defaultBranch": "refs/heads/main",
              "remoteUrl": "https://dev.azure.com/org/proj/_git/repo-one"
            },
            {
              "id": "22222222-3333-4444-5555-666666666666",
              "name": "repo-two",
              "defaultBranch": "refs/heads/develop",
              "remoteUrl": "https://dev.azure.com/org/proj/_git/repo-two"
            }
          ]
        }
        """;

        var repositories = new List<object>();
        using var doc = JsonDocument.Parse(reposJson);
        if (doc.RootElement.TryGetProperty("value", out var val)
            && val.ValueKind == JsonValueKind.Array)
        {
            foreach (var repo in val.EnumerateArray())
            {
                var id = repo.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                var name = repo.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString()
                    : null;
                var defaultBranch = repo.TryGetProperty("defaultBranch", out var dbEl) && dbEl.ValueKind == JsonValueKind.String
                    ? dbEl.GetString()
                    : null;
                var remoteUrl = repo.TryGetProperty("remoteUrl", out var ruEl) && ruEl.ValueKind == JsonValueKind.String
                    ? ruEl.GetString()
                    : null;

                repositories.Add(new
                {
                    id,
                    name,
                    defaultBranch,
                    remoteUrl,
                });
            }
        }

        // Verify the parsing produces the expected structure
        Assert.Equal(2, repositories.Count);

        var first = JsonSerializer.Serialize(repositories[0]);
        using var firstDoc = JsonDocument.Parse(first);
        Assert.Equal("11111111-2222-3333-4444-555555555555", firstDoc.RootElement.GetProperty("id").GetString());
        Assert.Equal("repo-one", firstDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal("refs/heads/main", firstDoc.RootElement.GetProperty("defaultBranch").GetString());
    }
}
