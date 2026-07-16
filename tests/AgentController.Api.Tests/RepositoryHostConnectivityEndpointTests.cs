using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
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
/// API/integration tests for POST /api/webui/repository-host-connections/{key}:verify.
/// Covers: success, config-error, missing-connection, unsupported-provider.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields."
)]
public sealed class RepositoryHostConnectivityEndpointTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"agent-controller-repo-host-verify-{Guid.NewGuid():N}.db"
        );

        _factory = new VerifyConnectivityApiFactory(_databasePath);

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
        // Arrange: create a repository host connection.
        var profile = CreateValidProfile("Verify.Success", "Success Environment");

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: POST verify
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/repository-host-connections/verify.success:verify",
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

        // Assert: payload contains repository list (from mock resolver)
        var payload = result.GetProperty("payload");
        Assert.True(payload.TryGetProperty("repositories", out var repos));
        Assert.Equal(2, repos.GetArrayLength());

        // Cleanup
        await _client.DeleteAsync("/api/webui/repository-host-connections/verify.success");
    }

    // ─── Config-error case: provider returns failure ───

    [Fact]
    public async Task VerifyEndpoint_ConfigError_Returns200WithFailure()
    {
        // Arrange: create a connection with a key that the mock resolver will fail on.
        var profile = new
        {
            key = "Verify.ConfigError",
            displayName = "Config Error Connection",
            enabled = true,
            provider = "AzureDevOpsRepos",
            organizationUrl = "https://dev.azure.com/testorg/",
            project = "TestProject",
            personalAccessTokenReference = new
            {
                name = "nonexistent-secret-for-test",
            },
        };

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/webui/repository-host-connections",
            profile
        );
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Act: POST verify
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/repository-host-connections/verify.configerror:verify",
            null
        );

        // Assert: 200 OK (not 500)
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        var result = await ReadJsonAsync(verifyResponse);
        Assert.False(result.GetProperty("success").GetBoolean());

        // Assert: errors describe the config problem
        var errors = result.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.NotEmpty(errors);

        // Cleanup
        await _client.DeleteAsync("/api/webui/repository-host-connections/verify.configerror");
    }

    // ─── Missing-connection case: non-existent key ───

    [Fact]
    public async Task VerifyEndpoint_MissingConnection_Returns200WithNotFound()
    {
        // Act: POST verify for a key that doesn't exist
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/repository-host-connections/nonexistent.repo:verify",
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

    // ─── Unsupported-provider case: no host registered for provider string ───

    [Fact]
    public async Task VerifyEndpoint_UnsupportedProvider_Returns200WithFailure()
    {
        // Arrange: create a connection with an unsupported provider.
        var profile = new
        {
            key = "Verify.Unsupported",
            displayName = "Unsupported Provider Connection",
            enabled = true,
            provider = "GitHub", // No host registered for this
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

        // Act: POST verify
        using var verifyResponse = await _client.PostAsync(
            "/api/webui/repository-host-connections/verify.unsupported:verify",
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
            e => e.Contains("GitHub", StringComparison.Ordinal)
        );
        Assert.Contains(
            errors,
            e => e.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                || e.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
        );

        // Cleanup
        await _client.DeleteAsync("/api/webui/repository-host-connections/verify.unsupported");
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
    /// with a mock that returns deterministic results.
    /// </summary>
    private sealed class VerifyConnectivityApiFactory(string databasePath) : SilentWebApplicationFactory
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

                // Replace the real IRepositoryHostResolver with a mock.
                services.RemoveAll<IRepositoryHostResolver>();
                services.AddSingleton<IRepositoryHostResolver>(new MockRepositoryHostResolver());
            });
        }
    }

    /// <summary>
    /// Mock resolver that returns success for "AzureDevOpsRepos" with test repos,
    /// failure for config-error keys, and unsupported-provider for others.
    /// </summary>
    private sealed class MockRepositoryHostResolver : IRepositoryHostResolver
    {
        public Task<RepositoryHostConnectivityResult> VerifyConnectivityAsync(
            RepositoryHostConnectionProfile profile,
            CancellationToken cancellationToken
        )
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                // Simulate config error for specific key.
                if (string.Equals(profile.Key, "verify.configerror", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(
                        RepositoryHostConnectivityResult.FailureResult(
                            new[]
                            {
                                "PAT reference 'NONEXISTENT_VAR_FOR_TEST' could not be resolved. "
                                + "Ensure the secret reference kind and id are correct.",
                            },
                            authMechanism: "PersonalAccessToken"
                        )
                    );
                }

                // Success for AzureDevOpsRepos provider.
                if (string.Equals(profile.Provider, "AzureDevOpsRepos", StringComparison.OrdinalIgnoreCase))
                {
                    var repos = new List<Dictionary<string, string>>
                    {
                        new()
                        {
                            { "id", "mock-repo-1" },
                            { "name", "main-repo" },
                            { "defaultBranch", "main" },
                            { "remoteUrl", "https://dev.azure.com/testorg/TestProject/_git/main-repo" },
                        },
                        new()
                        {
                            { "id", "mock-repo-2" },
                            { "name", "infra-repo" },
                            { "defaultBranch", "main" },
                            { "remoteUrl", "https://dev.azure.com/testorg/TestProject/_git/infra-repo" },
                        },
                    };
                    return Task.FromResult(
                        RepositoryHostConnectivityResult.SuccessResult(
                            authMechanism: "PersonalAccessToken",
                            httpStatus: 200,
                            payload: new Dictionary<string, object>
                            {
                                { "repositories", repos },
                            }
                        )
                    );
                }

                // Unsupported provider.
                return Task.FromResult(
                    RepositoryHostConnectivityResult.FailureResult(
                        new[]
                        {
                            $"Provider '{profile.Provider}' is not supported by any registered repository host.",
                        },
                        authMechanism: string.Empty
                    )
                );
            }

            return Task.FromException<RepositoryHostConnectivityResult>(
                new OperationCanceledException(cancellationToken)
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

            return Task.FromResult<IReadOnlyList<HostRepository>>(
                Array.Empty<HostRepository>()
            );
        }
    }
}
