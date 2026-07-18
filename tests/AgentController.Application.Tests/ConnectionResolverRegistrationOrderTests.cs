using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application.Tests;

/// <summary>
/// Regression tests for the deferred ConnectionRegistry snapshot behavior.
/// Guards against the bug where ConnectionRegistry.Build() was called at
/// registration time, capturing the provider list before all providers
/// (including AzureDevOps) had been registered.
/// </summary>
public sealed class ConnectionResolverRegistrationOrderTests
{
    /// <summary>
    /// Verify that when AddConnectionResolver() is called before AddConnection&lt;T&gt;(),
    /// the resolved IConnectionResolver can still handle an AzureDevOps connection operation.
    ///
    /// This exercises the real DI container wiring — the registry snapshot must be captured
    /// at first resolution time (inside the singleton factory lambda), not at registration time.
    /// </summary>
    [Fact]
    public async Task ConnectionResolver_AzureDevOps_RoutesThroughResolver_WhenRegisteredAfterResolver()
    {
        // Arrange: build the service collection with the same ordering as production.
        // AddApplicationHandlers() calls AddConnectionResolver() which registers the
        // IConnectionResolver singleton. The provider is registered AFTER that.
        var services = new ServiceCollection();
        services.AddApplicationHandlers();

        // Register a stub AzureDevOps connection AFTER the resolver is wired.
        // This is the critical ordering: resolver first, provider second.
        services.AddConnection<StubAzureDevOpsConnection>("AzureDevOps");

        using var serviceProvider = services.BuildServiceProvider();

        // Act: resolve the resolver and test connectivity for an AzureDevOps profile.
        var resolver = serviceProvider.GetRequiredService<IConnectionResolver>();
        var profile = new ConnectionProfile
        {
            Key = "test-ado",
            DisplayName = "Test Azure DevOps",
            Provider = "AzureDevOps",
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                OrganizationUrl = "https://dev.azure.com/testorg",
            },
        };

        var result = await resolver.VerifyConnectivityAsync(profile, CancellationToken.None);

        // Assert: the operation should succeed (not return "not supported" error).
        Assert.True(result.Success,
            $"Expected successful connectivity but got errors: {string.Join(", ", result.Errors)}");
        Assert.DoesNotContain(
            result.Errors,
            e => e.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verify that an unregistered provider returns a clear "not supported" failure
    /// rather than throwing. This ensures the resolver's fallback path works correctly.
    /// </summary>
    [Fact]
    public async Task ConnectionResolver_UnregisteredProvider_ReturnsFailureResult()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddApplicationHandlers();
        // Intentionally NOT registering a connection for "UnknownProvider".

        using var serviceProvider = services.BuildServiceProvider();
        var resolver = serviceProvider.GetRequiredService<IConnectionResolver>();
        var profile = new ConnectionProfile
        {
            Key = "test-unknown",
            DisplayName = "Test Unknown",
            Provider = "UnknownProvider",
        };

        // Act
        var result = await resolver.VerifyConnectivityAsync(profile, CancellationToken.None);

        // Assert: should return a failure with a clear error message, not throw.
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("UnknownProvider"));
    }

    /// <summary>
    /// Stub IConnection implementation that always returns a successful connectivity result.
    /// Used to verify the resolver routes to the correct provider without needing real infrastructure.
    /// </summary>
    private sealed class StubAzureDevOpsConnection : IConnection
    {
        public Task<ConnectionConnectivityResult> VerifyConnectivityAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                ConnectionConnectivityResult.SuccessResult("TestStub", 200));
        }

        public Task<IReadOnlyList<ConnectionProject>> ListProjectsAsync(
            ConnectionProfile profile,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ConnectionProject>>(
                new[] { new ConnectionProject("proj-1", "Test Project") });
        }

        public Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
            ConnectionProfile profile,
            string project,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<HostRepository>>(Array.Empty<HostRepository>());
        }
    }
}
