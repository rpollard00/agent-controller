using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Domain.Secrets;
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
/// API/integration tests for POST /api/webui/connections/{key}/verify.
/// Covers: success, config-error, missing-connection, unsupported-provider.
/// Supersedes the legacy work-source-environments/{key}:verify endpoint tests.
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
    private string _kekPath = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-verify-{Guid.NewGuid():N}.db"
        );

        // Create a 32-byte KEK file for the Db secret provider validation.
        _kekPath = Path.Combine(Path.GetTempPath(), $"test-kek-{Guid.NewGuid():N}.key");
        File.WriteAllBytes(_kekPath, new byte[32]);

        _factory = new VerifyConnectivityApiFactory(_databasePath, _kekPath, TestSecretName, TestPatValue);

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
        DeleteDatabaseFile(_kekPath);
        return Task.CompletedTask;
    }

    // ─── Success case: returns 200 with provider-neutral shape, PAT not echoed ───

    [Fact]
    public async Task VerifyEndpoint_Success_Returns200WithResultShapeAndNoPat()
    {
        // Arrange: create a connection that the mock factory will serve.
        var profile = new
        {
            key = "verify-success",
            displayName = "Success Connection",
            enabled = true,
            provider = "AzureDevOps",
            capabilities = new[] { "Repositories", "WorkTracking" },
            providerSettings = new
            {
                provider = "AzureDevOps",
                organizationUrl = "https://dev.azure.com/testorg",
                personalAccessTokenReference = new
                {
                    name = TestSecretName,
                },
            },
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: POST verify
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/connections/verify-success/verify",
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
        // Arrange: create a connection referencing a secret that doesn't exist.
        var profile = new
        {
            key = "verify-configerror",
            displayName = "Config Error Connection",
            enabled = true,
            provider = "AzureDevOps",
            capabilities = new[] { "Repositories" },
            providerSettings = new
            {
                provider = "AzureDevOps",
                organizationUrl = "https://dev.azure.com/testorg",
                personalAccessTokenReference = new
                {
                    name = "nonexistent-secret-for-test",
                },
            },
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: POST verify (mock resolver checks secret name)
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/connections/verify-configerror/verify",
            null
        );

        // Assert: 200 OK (not 500)
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        var result = await ReadJsonAsync(verifyResponse);
        // The mock resolver returns failure for unknown secret references
        Assert.False(result.GetProperty("success").GetBoolean());

        // Assert: errors describe the config problem
        var errors = result.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.NotEmpty(errors);
    }

    // ─── Missing-connection case: non-existent key ───

    [Fact]
    public async Task VerifyEndpoint_MissingConnection_Returns200WithNotFound()
    {
        // Act: POST verify for a key that doesn't exist
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/connections/nonexistent-connection/verify",
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
        // Arrange: create a connection with an unsupported provider.
        // Note: providerSettings is null because only AzureDevOps has a registered
        // JsonDerivedType; other providers would need their own settings type.
        object? nullSettings = null;
        var profile = new
        {
            key = "verify-unsupported",
            displayName = "Unsupported Provider Connection",
            enabled = true,
            provider = "GitHubIssues", // No IConnection registered for this
            capabilities = new[] { "WorkTracking" },
            providerSettings = nullSettings,
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: POST verify
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/connections/verify-unsupported/verify",
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
                || e.Contains("not found", StringComparison.OrdinalIgnoreCase)
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static void DeleteDatabaseFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // ─── Test factory with mocked ADO connection ───

    /// <summary>
    /// WebApplicationFactory that replaces IConnection for AzureDevOps
    /// with a mock that returns a successful connectivity result with repos.
    /// </summary>
    private sealed class VerifyConnectivityApiFactory(
        string databasePath,
        string kekPath,
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
                            ["secrets:provider"] = "Db",
                            ["secrets:keyEncryptionKey:file:filePath"] = kekPath,
                            ["workSource:provider"] = "LocalFake",
                            ["workSource:connectionKey"] = "", // Clear appsettings fallback
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
                    options.UseSqlite(
                        $"Data Source={databasePath}",
                        sqlite => sqlite.MigrationsAssembly("AgentController.Migrations")
                    )
                );

                // Replace the real IConnectionResolver with a mock that returns
                // a successful connectivity result for AzureDevOps provider.
                services.RemoveAll<IConnectionResolver>();
                services.AddSingleton<IConnectionResolver>(new MockConnectionResolver());

                // Register in-memory named secret store with test secret.
                var secretStore = new InMemorySecretStore();
                secretStore.CreateAsync(testSecretName, testSecretValue, CancellationToken.None)
                    .GetAwaiter().GetResult();
                services.RemoveAll<Domain.Secrets.ISecretStore>();
                services.RemoveAll<Domain.Secrets.ISecretManager>();
                services.AddSingleton<Domain.Secrets.ISecretStore>(secretStore);
                services.AddSingleton<Domain.Secrets.ISecretManager>(secretStore);
            });
        }
    }

    /// <summary>
    /// Mock IConnectionResolver that returns a successful connectivity result
    /// for AzureDevOps provider with the known test secret, and failure otherwise.
    /// </summary>
    private sealed class MockConnectionResolver : IConnectionResolver
    {
        private const string KnownSecretName = "test-verify-ado-pat";

        public Task<ConnectionConnectivityResult> VerifyConnectivityAsync(
            ConnectionProfile profile,
            CancellationToken ct
        )
        {
            if (profile.Provider != "AzureDevOps")
            {
                return Task.FromResult(
                    ConnectionConnectivityResult.FailureResult(
                        [$"Connection operations are not supported for provider '{profile.Provider}'."]
                    )
                );
            }

            // Simulate PAT resolution failure for unknown secret references.
            var adoSettings = profile.ProviderSettings as AzureDevOpsConnectionSettings;
            if (adoSettings is not null &&
                adoSettings.PersonalAccessTokenReference.IsSpecified &&
                adoSettings.PersonalAccessTokenReference.Name != KnownSecretName)
            {
                return Task.FromResult(
                    ConnectionConnectivityResult.FailureResult(
                        [$"Secret '{adoSettings.PersonalAccessTokenReference.Name}' could not be resolved."],
                        authMechanism: "PersonalAccessToken"
                    )
                );
            }

            var payload = new Dictionary<string, object>
            {
                ["repositories"] = new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["id"] = "mock-repo-1",
                        ["name"] = "main-repo",
                        ["defaultBranch"] = "refs/heads/main",
                        ["remoteUrl"] = "https://dev.azure.com/testorg/TestProject/_git/main-repo",
                    },
                    new()
                    {
                        ["id"] = "mock-repo-2",
                        ["name"] = "infra-repo",
                        ["defaultBranch"] = "refs/heads/main",
                        ["remoteUrl"] = "https://dev.azure.com/testorg/TestProject/_git/infra-repo",
                    },
                },
            };

            return Task.FromResult(
                ConnectionConnectivityResult.SuccessResult(
                    "PersonalAccessToken",
                    200,
                    payload
                )
            );
        }

        public Task<IReadOnlyList<ConnectionProject>> ListProjectsAsync(
            ConnectionProfile profile,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<ConnectionProject>>(Array.Empty<ConnectionProject>());

        public Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
            ConnectionProfile profile,
            string project,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<HostRepository>>(Array.Empty<HostRepository>());
    }
}
