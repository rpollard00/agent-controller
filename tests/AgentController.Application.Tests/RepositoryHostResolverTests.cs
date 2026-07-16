using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application.Tests;

/// <summary>
/// Tests for <see cref="RepositoryHostResolver"/> dispatch behaviour:
/// supported provider resolves the registered host; unsupported provider returns
/// a non-success result (for connectivity) or empty list (for enumeration) without throwing.
/// </summary>
public sealed class RepositoryHostResolverTests
{
    // ─── VerifyConnectivityAsync: supported provider dispatches to registered host ───

    [Fact]
    public async Task VerifyConnectivityAsync_SupportedProvider_DispatchesToRegisteredHost()
    {
        // Arrange: register a fake host for "TestProvider"
        var fakeHost = new FakeRepositoryHost(
            RepositoryHostConnectivityResult.SuccessResult(
                "FakeAuth",
                httpStatus: 200,
                payload: new Dictionary<string, object> { ["testKey"] = "testValue" }
            ),
            Array.Empty<HostRepository>()
        );

        var resolver = CreateResolver(
            fakeHost,
            new Dictionary<string, Type>
            {
                ["TestProvider"] = typeof(FakeRepositoryHost),
            }
        );

        var profile = new RepositoryHostConnectionProfile
        {
            Key = "test-host",
            DisplayName = "Test Host",
            Provider = "TestProvider",
        };

        // Act
        var result = await resolver.VerifyConnectivityAsync(profile, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("FakeAuth", result.AuthMechanism);
        Assert.Equal(200, result.HttpStatus);
        Assert.Empty(result.Errors);

        var payload = Assert.IsType<Dictionary<string, object>>(result.Payload);
        Assert.Equal("testValue", payload["testKey"]);
    }

    // ─── VerifyConnectivityAsync: unsupported provider returns non-success result ───

    [Fact]
    public async Task VerifyConnectivityAsync_UnsupportedProvider_ReturnsFailureWithoutThrowing()
    {
        // Arrange: register a host for "KnownProvider" only
        var resolver = CreateResolver(
            new FakeRepositoryHost(),
            new Dictionary<string, Type>
            {
                ["KnownProvider"] = typeof(FakeRepositoryHost),
            }
        );

        var profile = new RepositoryHostConnectionProfile
        {
            Key = "unknown-host",
            DisplayName = "Unknown Provider Host",
            Provider = "UnknownProvider",
        };

        // Act — must not throw
        var result = await resolver.VerifyConnectivityAsync(profile, CancellationToken.None);

        // Assert: non-success with clear unsupported-provider error
        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.Contains("UnknownProvider", result.Errors[0]);
        Assert.Contains(
            "not supported",
            result.Errors[0],
            StringComparison.OrdinalIgnoreCase
        );
    }

    // ─── VerifyConnectivityAsync: passes cancellation token to host ───

    [Fact]
    public async Task VerifyConnectivityAsync_PassesCancellationTokenToHost()
    {
        // Arrange
        var fakeHost = new FakeRepositoryHost(
            RepositoryHostConnectivityResult.SuccessResult("Test"),
            Array.Empty<HostRepository>()
        );

        var resolver = CreateResolver(
            fakeHost,
            new Dictionary<string, Type>
            {
                ["TestProvider"] = typeof(FakeRepositoryHost),
            }
        );

        var profile = new RepositoryHostConnectionProfile
        {
            Key = "test-host",
            Provider = "TestProvider",
        };

        var cts = new CancellationTokenSource();

        // Act
        var result = await resolver.VerifyConnectivityAsync(profile, cts.Token);

        // Assert: host was called with a non-default token
        Assert.True(result.Success);
        Assert.True(fakeHost.ReceivedCancellation);
    }

    // ─── VerifyConnectivityAsync: scoped host resolves through singleton resolver ───

    /// <summary>
    /// Regression guard: a scoped-registered host must resolve cleanly through the singleton
    /// RepositoryHostResolver without throwing captive-dependency errors.
    /// </summary>
    [Fact]
    public async Task VerifyConnectivityAsync_ScopedHost_Regression_ShouldNotThrowCaptiveDependencyError()
    {
        // Arrange: register FakeRepositoryHost as scoped (NOT singleton)
        var services = new ServiceCollection();
        services.AddScoped<FakeRepositoryHost>();
        var sp = services.BuildServiceProvider(validateScopes: true);

        var resolver = new RepositoryHostResolver(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new Dictionary<string, Type>
            {
                ["TestProvider"] = typeof(FakeRepositoryHost),
            }
        );

        var profile = new RepositoryHostConnectionProfile
        {
            Key = "test-host",
            Provider = "TestProvider",
        };

        // Act — must not throw InvalidOperationException about scoped service from root provider
        var result = await resolver.VerifyConnectivityAsync(profile, CancellationToken.None);

        // Assert: scoped host resolved and invoked successfully
        Assert.True(result.Success);
    }

    // ─── VerifyConnectivityAsync: failure from host is propagated ───

    [Fact]
    public async Task VerifyConnectivityAsync_HostReturnsFailure_PropagatesFailure()
    {
        // Arrange
        var fakeHost = new FakeRepositoryHost(
            RepositoryHostConnectivityResult.FailureResult(
                ["Connection refused"],
                authMechanism: "FakeAuth",
                httpStatus: 503
            ),
            Array.Empty<HostRepository>()
        );

        var resolver = CreateResolver(
            fakeHost,
            new Dictionary<string, Type>
            {
                ["TestProvider"] = typeof(FakeRepositoryHost),
            }
        );

        var profile = new RepositoryHostConnectionProfile
        {
            Key = "test-host",
            Provider = "TestProvider",
        };

        // Act
        var result = await resolver.VerifyConnectivityAsync(profile, CancellationToken.None);

        // Assert: failure propagated from the host
        Assert.False(result.Success);
        Assert.Equal("FakeAuth", result.AuthMechanism);
        Assert.Equal(503, result.HttpStatus);
        Assert.Single(result.Errors);
        Assert.Equal("Connection refused", result.Errors[0]);
    }

    // ─── ListRepositoriesAsync: supported provider dispatches to registered host ───

    [Fact]
    public async Task ListRepositoriesAsync_SupportedProvider_DispatchesToRegisteredHost()
    {
        // Arrange
        var expectedRepos = new[]
        {
            new HostRepository("repo-1", "Repo One", "main", "https://example.com/repo1.git", CloneTransportHint.HttpsPat),
            new HostRepository("repo-2", "Repo Two", "develop", "git@host:repo2.git", CloneTransportHint.Ssh),
        };

        var fakeHost = new FakeRepositoryHost(
            RepositoryHostConnectivityResult.SuccessResult("FakeAuth"),
            expectedRepos
        );

        var resolver = CreateResolver(
            fakeHost,
            new Dictionary<string, Type>
            {
                ["TestProvider"] = typeof(FakeRepositoryHost),
            }
        );

        var profile = new RepositoryHostConnectionProfile
        {
            Key = "test-host",
            Provider = "TestProvider",
        };

        // Act
        var result = await resolver.ListRepositoriesAsync(profile, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("repo-1", result[0].Id);
        Assert.Equal("Repo One", result[0].Name);
        Assert.Equal("main", result[0].DefaultBranch);
        Assert.Equal("https://example.com/repo1.git", result[0].RemoteUrl);
        Assert.Equal(CloneTransportHint.HttpsPat, result[0].CloneTransportHint);

        Assert.Equal("repo-2", result[1].Id);
        Assert.Equal(CloneTransportHint.Ssh, result[1].CloneTransportHint);
    }

    // ─── ListRepositoriesAsync: unsupported provider returns empty list ───

    [Fact]
    public async Task ListRepositoriesAsync_UnsupportedProvider_ReturnsEmptyListWithoutThrowing()
    {
        // Arrange
        var resolver = CreateResolver(
            new FakeRepositoryHost(),
            new Dictionary<string, Type>
            {
                ["KnownProvider"] = typeof(FakeRepositoryHost),
            }
        );

        var profile = new RepositoryHostConnectionProfile
        {
            Key = "unknown-host",
            Provider = "UnknownProvider",
        };

        // Act — must not throw
        var result = await resolver.ListRepositoriesAsync(profile, CancellationToken.None);

        // Assert: empty list for unsupported provider
        Assert.Empty(result);
    }

    // ─── ListRepositoriesAsync: passes cancellation token to host ───

    [Fact]
    public async Task ListRepositoriesAsync_PassesCancellationTokenToHost()
    {
        // Arrange
        var fakeHost = new FakeRepositoryHost(
            RepositoryHostConnectivityResult.SuccessResult("Test"),
            Array.Empty<HostRepository>()
        );

        var resolver = CreateResolver(
            fakeHost,
            new Dictionary<string, Type>
            {
                ["TestProvider"] = typeof(FakeRepositoryHost),
            }
        );

        var profile = new RepositoryHostConnectionProfile
        {
            Key = "test-host",
            Provider = "TestProvider",
        };

        var cts = new CancellationTokenSource();

        // Act
        var result = await resolver.ListRepositoriesAsync(profile, cts.Token);

        // Assert: host was called with a non-default token
        Assert.Empty(result);
        Assert.True(fakeHost.ReceivedCancellation);
    }

    // ─── HostRepository: record equality ───

    [Fact]
    public void HostRepository_RecordEquality()
    {
        var repoA = new HostRepository("id", "Name", "main", "https://example.com/repo.git", CloneTransportHint.HttpsPat);
        var repoB = new HostRepository("id", "Name", "main", "https://example.com/repo.git", CloneTransportHint.HttpsPat);
        var repoC = new HostRepository("id", "Name", "main", "https://example.com/repo.git", CloneTransportHint.Ssh);

        Assert.Equal(repoA, repoB);
        Assert.NotEqual(repoA, repoC);
    }

    // ─── CloneTransportHint: enum values ───

    [Fact]
    public void CloneTransportHint_EnumValues()
    {
        Assert.Equal(0, (int)CloneTransportHint.Unspecified);
        Assert.Equal(1, (int)CloneTransportHint.Ssh);
        Assert.Equal(2, (int)CloneTransportHint.HttpsPat);
    }

    // ─── Helpers ───

    private static RepositoryHostResolver CreateResolver(
        FakeRepositoryHost host,
        IReadOnlyDictionary<string, Type> hostTypes
    )
    {
        var services = new ServiceCollection();
        services.AddScoped<FakeRepositoryHost>(_ => host);
        var sp = services.BuildServiceProvider(validateScopes: true);
        return new RepositoryHostResolver(
            sp.GetRequiredService<IServiceScopeFactory>(),
            hostTypes
        );
    }

    /// <summary>
    /// Fake host that returns pre-configured results.
    /// </summary>
    private sealed class FakeRepositoryHost(
        RepositoryHostConnectivityResult? connectivityResult = null,
        IReadOnlyList<HostRepository>? repositories = null
    ) : IRepositoryHostConnection
    {
        public bool ReceivedCancellation { get; private set; }

        public Task<RepositoryHostConnectivityResult> VerifyConnectivityAsync(
            RepositoryHostConnectionProfile profile,
            CancellationToken cancellationToken
        )
        {
            ReceivedCancellation = cancellationToken.CanBeCanceled;
            return Task.FromResult(
                connectivityResult ?? RepositoryHostConnectivityResult.SuccessResult("Fake")
            );
        }

        public Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
            RepositoryHostConnectionProfile profile,
            CancellationToken cancellationToken
        )
        {
            ReceivedCancellation = cancellationToken.CanBeCanceled;
            return Task.FromResult(repositories ?? Array.Empty<HostRepository>());
        }
    }
}
