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
/// End-to-end coverage for the secrets management delete endpoint:
/// DELETE /api/webui/secrets/{name} mapping to 204 / 404 / 409.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields."
)]
public sealed class WebUiSecretsEndpointTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private SecretsApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-secrets-{Guid.NewGuid():N}.db"
        );
        _factory = new SecretsApiFactory(_databasePath);

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
    public async Task DeleteSecret_UnreferencedSecret_Returns204_AndRemovesFromList()
    {
        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/secrets",
            new
            {
                name = "delete-me",
                payload = new
                {
                    type = "personal-access-token",
                    value = "value-that-must-never-leak",
                },
            }
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var deleteResponse = await _client.DeleteAsync("/api/webui/secrets/delete-me");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        using var listResponse = await _client.GetAsync("/api/webui/secrets");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var secrets = await ReadJsonAsync(listResponse);
        Assert.DoesNotContain(
            secrets.EnumerateArray(),
            secret => secret.GetProperty("name").GetString() == "delete-me"
        );

        // Deleting again reports the secret as missing.
        using var secondDelete = await _client.DeleteAsync("/api/webui/secrets/delete-me");
        await AssertProblemAsync(secondDelete, HttpStatusCode.NotFound, "Resource not found.");
    }

    [Fact]
    public async Task DeleteSecret_MissingName_Returns404()
    {
        using var deleteResponse = await _client.DeleteAsync(
            "/api/webui/secrets/no-such-secret"
        );

        await AssertProblemAsync(deleteResponse, HttpStatusCode.NotFound, "Resource not found.");
    }

    [Fact]
    public async Task DeleteSecret_InUseByConnection_Returns409_AndSecretRemains()
    {
        const string secretValue = "pat-value-that-must-never-leak";
        using (var createSecret = await _client.PostAsJsonAsync(
            "/api/webui/secrets",
            new
            {
                name = "pat-secret",
                payload = new
                {
                    type = "personal-access-token",
                    value = secretValue,
                },
            }
        ))
        {
            Assert.Equal(HttpStatusCode.Created, createSecret.StatusCode);
        }

        var connection = new
        {
            key = "ado-main",
            displayName = "Main Azure DevOps",
            enabled = true,
            provider = "AzureDevOps",
            capabilities = new[] { "Repositories" },
            providerSettings = new
            {
                provider = "AzureDevOps",
                organizationUrl = "https://dev.azure.com/testorg",
                personalAccessTokenReference = new { name = "pat-secret" },
            },
        };
        using (var createConnection = await _client.PostAsJsonAsync(
            "/api/webui/connections",
            connection
        ))
        {
            Assert.Equal(HttpStatusCode.Created, createConnection.StatusCode);
        }

        using var deleteResponse = await _client.DeleteAsync("/api/webui/secrets/pat-secret");
        Assert.Equal(HttpStatusCode.Conflict, deleteResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            deleteResponse.Content.Headers.ContentType?.MediaType
        );

        var body = await deleteResponse.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(409, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "Resource conflict.",
            problem.RootElement.GetProperty("title").GetString()
        );

        // The problem detail names the referencing connection.
        var detail = problem.RootElement.GetProperty("detail").GetString();
        Assert.Contains("pat-secret", detail, StringComparison.Ordinal);
        Assert.Contains("ado-main", detail, StringComparison.Ordinal);

        // The conflict response never leaks the secret value.
        Assert.DoesNotContain(secretValue, body, StringComparison.Ordinal);

        // The secret is still listed.
        using var listResponse = await _client.GetAsync("/api/webui/secrets");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var secrets = await ReadJsonAsync(listResponse);
        Assert.Contains(
            secrets.EnumerateArray(),
            secret => secret.GetProperty("name").GetString() == "pat-secret"
        );
    }

    [Fact]
    public async Task CreateSecretSsh_OmittedPassphrase_Returns400()
    {
        // POST an SSH-key secret without the passphrase property at all.
        using var response = await _client.PostAsJsonAsync(
            "/api/webui/secrets",
            new
            {
                name = "ssh-omitted-passphrase",
                payload = new
                {
                    type = "ssh-key",
                    privateKey = "ssh-private-key-content",
                    publicKey = "ssh-public-key-content",
                    // passphrase is intentionally omitted — should be rejected
                },
            }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateSecretSsh_ExplicitNullPassphrase_Succeeds()
    {
        // POST an SSH-key secret with passphrase explicitly set to null.
        using var response = await _client.PostAsJsonAsync(
            "/api/webui/secrets",
            new
            {
                name = "ssh-explicit-null",
                payload = new
                {
                    type = "ssh-key",
                    privateKey = "ssh-private-key-content",
                    publicKey = "ssh-public-key-content",
                    passphrase = (string?)null,
                },
            }
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateSecretSsh_ExplicitPassphraseValue_Succeeds()
    {
        // POST an SSH-key secret with an explicit passphrase value.
        using var response = await _client.PostAsJsonAsync(
            "/api/webui/secrets",
            new
            {
                name = "ssh-with-passphrase",
                payload = new
                {
                    type = "ssh-key",
                    privateKey = "ssh-private-key-content",
                    publicKey = "ssh-public-key-content",
                    passphrase = "my-secret-passphrase",
                },
            }
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateSecretVersionSsh_OmittedPassphrase_Returns400()
    {
        // First create an SSH-key secret with explicit null passphrase.
        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/secrets",
            new
            {
                name = "ssh-version-omitted-passphrase",
                payload = new
                {
                    type = "ssh-key",
                    privateKey = "ssh-private-key-content",
                    publicKey = "ssh-public-key-content",
                    passphrase = (string?)null,
                },
            }
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Now try to create a new version without passphrase.
        using var versionResponse = await _client.PostAsJsonAsync(
            "/api/webui/secrets/ssh-version-omitted-passphrase/versions",
            new
            {
                payload = new
                {
                    type = "ssh-key",
                    privateKey = "ssh-private-key-v2",
                    publicKey = "ssh-public-key-v2",
                    // passphrase is intentionally omitted — should be rejected
                },
            }
        );

        Assert.Equal(HttpStatusCode.BadRequest, versionResponse.StatusCode);
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

    private sealed class SecretsApiFactory(string databasePath) : SilentWebApplicationFactory
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
