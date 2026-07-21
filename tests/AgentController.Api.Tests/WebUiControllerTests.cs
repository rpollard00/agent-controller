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

/// <summary>End-to-end coverage for the managed-profile web UI API.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields."
)]
public sealed class WebUiControllerTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private WebUiApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-webui-{Guid.NewGuid():N}.db"
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
    public async Task RepositoryEndpoints_SupportEveryVerbAndProblemDetails()
    {
        var profile = new
        {
            key = " Web.Repo ",
            cloneUrl = " https://example.test/org/repo.git ",
            defaultBranch = " main ",
            transport = "httpsPat",
            allowedPaths = new[] { " src/AgentController.Api ", "tests" },
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repositories",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(
            "/api/webui/repositories/web.repo",
            createResponse.Headers.Location?.ToString()
        );
        var created = await ReadJsonAsync(createResponse);
        Assert.Equal("web.repo", created.GetProperty("key").GetString());
        Assert.Equal("src/AgentController.Api", created.GetProperty("allowedPaths")[0].GetString());

        using var duplicateResponse = await _client.PostAsJsonAsync(
            "/api/webui/repositories",
            profile
        );
        await AssertProblemAsync(duplicateResponse, HttpStatusCode.Conflict, "Resource conflict.");

        using var listResponse = await _client.GetAsync("/api/webui/repositories");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var repositories = await ReadJsonAsync(listResponse);
        Assert.Contains(
            repositories.EnumerateArray(),
            repository => repository.GetProperty("key").GetString() == "web.repo"
        );

        using var getResponse = await _client.GetAsync("/api/webui/repositories/WEB.REPO");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var repository = await ReadJsonAsync(getResponse);
        Assert.Equal("web.repo", repository.GetProperty("key").GetString());

        using var transportResponse = await _client.GetAsync(
            "/api/webui/repositories/WEB.REPO/clone-transport"
        );
        Assert.Equal(HttpStatusCode.OK, transportResponse.StatusCode);
        var transport = await ReadJsonAsync(transportResponse);
        Assert.Equal("httpsPat", transport.GetProperty("transport").GetString());
        Assert.False(transport.GetProperty("isReady").GetBoolean());
        Assert.Equal(
            "missingRepositoryHostConnection",
            transport.GetProperty("blockingIssues")[0].GetProperty("code").GetString()
        );

        var update = new
        {
            key = "web.repo",
            cloneUrl = "https://example.test/org/repo.git",
            defaultBranch = "develop",
            transport = "httpsPat",
            allowedPaths = new[] { "src" },
        };
        using var updateResponse = await _client.PutAsJsonAsync(
            "/api/webui/repositories/web.repo",
            update
        );
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await ReadJsonAsync(updateResponse);
        Assert.Equal("develop", updated.GetProperty("defaultBranch").GetString());

        using var validationResponse = await _client.PostAsJsonAsync(
            "/api/webui/repositories",
            new
            {
                key = "",
                cloneUrl = "",
                defaultBranch = "",
            }
        );
        var validationProblem = await AssertProblemAsync(
            validationResponse,
            HttpStatusCode.BadRequest,
            "Validation failed."
        );
        Assert.True(validationProblem.GetProperty("errors").TryGetProperty("key", out _));

        using var deleteResponse = await _client.DeleteAsync("/api/webui/repositories/web.repo");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var missingResponse = await _client.GetAsync("/api/webui/repositories/web.repo");
        await AssertProblemAsync(missingResponse, HttpStatusCode.NotFound, "Resource not found.");

        using var missingTransportResponse = await _client.GetAsync(
            "/api/webui/repositories/web.repo/clone-transport"
        );
        await AssertProblemAsync(
            missingTransportResponse,
            HttpStatusCode.NotFound,
            "Resource not found."
        );
    }

    [Fact]
    public async Task RepositoryEndpoints_RoundTripSshKeyReferenceAndRejectPatReference()
    {
        using (var createSshKey = await _client.PostAsJsonAsync(
            "/api/webui/secrets",
            new
            {
                name = "repository-deploy-key",
                payload = new
                {
                    type = "ssh-key",
                    privateKey = "private-key-material",
                    publicKey = "public-key-material",
                    passphrase = (string?)null,
                },
            }
        ))
        {
            Assert.Equal(HttpStatusCode.Created, createSshKey.StatusCode);
        }

        var profile = new
        {
            key = "ssh.repo",
            cloneUrl = "git@example.test:ssh.repo.git",
            defaultBranch = "main",
            transport = "ssh",
            sshKeyReference = new { name = "repository-deploy-key", version = 1 },
        };
        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repositories",
            profile
        );

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadJsonAsync(createResponse);
        Assert.Equal(
            "repository-deploy-key",
            created.GetProperty("sshKeyReference").GetProperty("name").GetString()
        );
        Assert.Equal(
            1,
            created.GetProperty("sshKeyReference").GetProperty("version").GetInt32()
        );

        using var getResponse = await _client.GetAsync("/api/webui/repositories/ssh.repo");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var persisted = await ReadJsonAsync(getResponse);
        Assert.Equal(
            "repository-deploy-key",
            persisted.GetProperty("sshKeyReference").GetProperty("name").GetString()
        );

        using var transportResponse = await _client.GetAsync(
            "/api/webui/repositories/ssh.repo/clone-transport"
        );
        Assert.Equal(HttpStatusCode.OK, transportResponse.StatusCode);
        var transport = await ReadJsonAsync(transportResponse);
        Assert.Equal("ssh", transport.GetProperty("transport").GetString());
        Assert.Equal("sshKey", transport.GetProperty("credentialSource").GetString());
        Assert.Equal(
            "repository-deploy-key",
            transport.GetProperty("credentialReference").GetProperty("name").GetString()
        );
        Assert.Equal(
            1,
            transport.GetProperty("credentialReference").GetProperty("version").GetInt32()
        );
        Assert.True(transport.GetProperty("isReady").GetBoolean());
        Assert.Empty(transport.GetProperty("blockingIssues").EnumerateArray());

        using (var createPat = await _client.PostAsJsonAsync(
            "/api/webui/secrets",
            new
            {
                name = "connection-pat",
                payload = new
                {
                    type = "personal-access-token",
                    value = "write-only-value",
                },
            }
        ))
        {
            Assert.Equal(HttpStatusCode.Created, createPat.StatusCode);
        }

        using var invalidResponse = await _client.PostAsJsonAsync(
            "/api/webui/repositories",
            new
            {
                key = "invalid-ssh.repo",
                cloneUrl = "git@example.test:invalid-ssh.repo.git",
                defaultBranch = "main",
                transport = "ssh",
                sshKeyReference = new { name = "connection-pat", version = 1 },
            }
        );
        var problem = await AssertProblemAsync(
            invalidResponse,
            HttpStatusCode.BadRequest,
            "Validation failed."
        );
        Assert.True(
            problem.GetProperty("errors").TryGetProperty("sshKeyReference", out _)
        );
    }

    [Fact]
    public async Task WorkSourceEnvironmentEndpoints_SupportEveryVerbAndRedactCredentials()
    {
        var profile = new
        {
            key = " ADO.Main ",
            displayName = " Main Azure DevOps ",
            enabled = true,
            provider = "AzureDevOpsBoards",
            connectionKey = "azuredevops-example",
            project = "Agent Controller",
            completedStates = new[] { "Resolved", "Removed" },
            tagPrefix = "agent",
            activeState = "Active",
            completedState = "Resolved",
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/work-source-environments",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(
            "/api/webui/work-source-environments/ado.main",
            createResponse.Headers.Location?.ToString()
        );

        using var duplicateResponse = await _client.PostAsJsonAsync(
            "/api/webui/work-source-environments",
            profile
        );
        await AssertProblemAsync(
            duplicateResponse,
            HttpStatusCode.Conflict,
            "Resource conflict."
        );

        using var listResponse = await _client.GetAsync("/api/webui/work-source-environments");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = await listResponse.Content.ReadAsStringAsync();
        using var listJson = JsonDocument.Parse(listBody);
        Assert.Contains(
            listJson.RootElement.EnumerateArray(),
            environment => environment.GetProperty("key").GetString() == "ado.main"
        );

        using var getResponse = await _client.GetAsync(
            "/api/webui/work-source-environments/ADO.MAIN"
        );
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = await getResponse.Content.ReadAsStringAsync();
        using var getJson = JsonDocument.Parse(getBody);
        Assert.True(getJson.RootElement.TryGetProperty("connectionKey", out var connKeyProp));
        Assert.Equal("azuredevops-example", connKeyProp.GetString());
        Assert.False(getJson.RootElement.TryGetProperty("organizationUrl", out _));
        Assert.False(getJson.RootElement.TryGetProperty("personalAccessToken", out _));

        var update = new
        {
            key = "ado.main",
            displayName = "Updated Azure DevOps",
            enabled = false,
            provider = "AzureDevOpsBoards",
            connectionKey = "azuredevops-example",
            project = "Agent Controller",
            completedStates = new[] { "Resolved" },
            tagPrefix = "agent",
            activeState = "Active",
            completedState = "Done",
        };
        using var updateResponse = await _client.PutAsJsonAsync(
            "/api/webui/work-source-environments/ado.main",
            update
        );
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updateBody = await updateResponse.Content.ReadAsStringAsync();
        using var updateJson = JsonDocument.Parse(updateBody);
        Assert.Equal(
            "Updated Azure DevOps",
            updateJson.RootElement.GetProperty("displayName").GetString()
        );
        Assert.False(updateJson.RootElement.GetProperty("enabled").GetBoolean());

        using var deleteResponse = await _client.DeleteAsync(
            "/api/webui/work-source-environments/ado.main"
        );
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var missingResponse = await _client.GetAsync(
            "/api/webui/work-source-environments/ado.main"
        );
        await AssertProblemAsync(
            missingResponse,
            HttpStatusCode.NotFound,
            "Resource not found."
        );
    }

    [Fact]
    public async Task RuntimeEnvironmentEndpoints_SupportEveryVerbAndRedactCredentialValues()
    {
        const string credentialValue = "runtime-secret-that-must-never-be-returned";
        const string credentialVariable = "WEBUI_TEST_RUNTIME_TOKEN";
        Environment.SetEnvironmentVariable(credentialVariable, credentialValue);
        try
        {
            var profile = new
            {
                key = " Runtime-Local ",
                displayName = " Local Runtime ",
                enabled = true,
                environmentProvider = "localworkspace",
                environmentSettings = new { workspaceRoot = "/tmp/agent-workspaces" },
                runtimeProvider = "mockpimateria",
                runtimeSettings = new
                {
                    piExecutablePath = (string?)null,
                    controllerBaseUrl = (string?)null,
                    ptyWrapperPath = (string?)null,
                    ptyWrapperArgs = (string?)null,
                    loadouts = new { newWork = "ADO-Build-NewWork", rework = "ADO-Build-Rework" },
                    forwardEnvironmentVariables = new Dictionary<string, string>
                    {
                        ["RUNTIME_TOKEN"] = credentialVariable,
                    },
                    environmentVariableValues = new Dictionary<string, string>
                    {
                        ["RUNTIME_TOKEN"] = credentialValue,
                    },
                },
            };

            using var createResponse = await _client.PostAsJsonAsync(
                "/api/webui/runtime-environments",
                profile
            );
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            Assert.Equal(
                "/api/webui/runtime-environments/runtime-local",
                createResponse.Headers.Location?.ToString()
            );
            await AssertCredentialRedactedAsync(createResponse, credentialValue);

            using var duplicateResponse = await _client.PostAsJsonAsync(
                "/api/webui/runtime-environments",
                profile
            );
            await AssertProblemAsync(
                duplicateResponse,
                HttpStatusCode.Conflict,
                "Resource conflict."
            );
            await AssertCredentialRedactedAsync(duplicateResponse, credentialValue);

            using var listResponse = await _client.GetAsync("/api/webui/runtime-environments");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            var listBody = await AssertCredentialRedactedAsync(listResponse, credentialValue);
            using var listJson = JsonDocument.Parse(listBody);
            Assert.Contains(
                listJson.RootElement.EnumerateArray(),
                environment => environment.GetProperty("key").GetString() == "runtime-local"
            );

            using var getResponse = await _client.GetAsync(
                "/api/webui/runtime-environments/RUNTIME-LOCAL"
            );
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var getBody = await AssertCredentialRedactedAsync(getResponse, credentialValue);
            using var getJson = JsonDocument.Parse(getBody);
            var runtimeSettings = getJson.RootElement.GetProperty("runtimeSettings");
            // Loadouts remain a user-level, profile-specific control and are persisted.
            Assert.Equal(
                "ADO-Build-NewWork",
                runtimeSettings.GetProperty("loadouts").GetProperty("newWork").GetString()
            );
            // Controller-owned process settings are accepted for compatibility but not
            // persisted per-profile, so stale stored overrides cannot alter execution.
            Assert.Null(runtimeSettings.GetProperty("piExecutablePath").GetString());
            Assert.Null(runtimeSettings.GetProperty("controllerBaseUrl").GetString());
            Assert.Null(runtimeSettings.GetProperty("ptyWrapperPath").GetString());
            Assert.Null(runtimeSettings.GetProperty("ptyWrapperArgs").GetString());
            Assert.False(
                runtimeSettings.GetProperty("forwardEnvironmentVariables").EnumerateObject().Any()
            );
            Assert.False(runtimeSettings.TryGetProperty("environmentVariableValues", out _));

            var update = new
            {
                key = "runtime-local",
                displayName = "Updated Local Runtime",
                enabled = false,
                environmentProvider = "LocalWorkspace",
                environmentSettings = new { workspaceRoot = "/tmp/updated-workspaces" },
                runtimeProvider = "MockPiMateria",
                runtimeSettings = new
                {
                    piExecutablePath = (string?)null,
                    controllerBaseUrl = (string?)null,
                    ptyWrapperPath = (string?)null,
                    ptyWrapperArgs = (string?)null,
                    loadouts = new { newWork = "updated-new-work", rework = "updated-rework" },
                    forwardEnvironmentVariables = new Dictionary<string, string>
                    {
                        ["RUNTIME_TOKEN"] = credentialVariable,
                    },
                    environmentVariableValues = new Dictionary<string, string>
                    {
                        ["RUNTIME_TOKEN"] = credentialValue,
                    },
                },
            };
            using var updateResponse = await _client.PutAsJsonAsync(
                "/api/webui/runtime-environments/runtime-local",
                update
            );
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            var updateBody = await AssertCredentialRedactedAsync(updateResponse, credentialValue);
            using var updateJson = JsonDocument.Parse(updateBody);
            Assert.Equal(
                "Updated Local Runtime",
                updateJson.RootElement.GetProperty("displayName").GetString()
            );
            Assert.False(updateJson.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal(
                "updated-new-work",
                updateJson.RootElement
                    .GetProperty("runtimeSettings")
                    .GetProperty("loadouts")
                    .GetProperty("newWork")
                    .GetString()
            );

            using var deleteResponse = await _client.DeleteAsync(
                "/api/webui/runtime-environments/runtime-local"
            );
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

            using var missingResponse = await _client.GetAsync(
                "/api/webui/runtime-environments/runtime-local"
            );
            await AssertProblemAsync(
                missingResponse,
                HttpStatusCode.NotFound,
                "Resource not found."
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
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

    private static async Task<string> AssertCredentialRedactedAsync(
        HttpResponseMessage response,
        string credentialValue
    )
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(credentialValue, body, StringComparison.Ordinal);
        return body;
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
                    options.UseSqlite(
                        $"Data Source={databasePath}",
                        sqlite => sqlite.MigrationsAssembly("AgentController.Migrations")
                    )
                );
            });
        }
    }
}
