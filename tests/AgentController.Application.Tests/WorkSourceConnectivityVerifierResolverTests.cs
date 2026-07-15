using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application.Tests;

/// <summary>
/// Tests for <see cref="WorkSourceConnectivityVerifierResolver"/> dispatch behaviour:
/// supported provider resolves the registered verifier; unsupported provider returns
/// a non-success result without throwing.
/// </summary>
public sealed class WorkSourceConnectivityVerifierResolverTests
{
    // ─── Supported provider resolves registered verifier ───

    [Fact]
    public async Task VerifyAsync_SupportedProvider_DispatchesToRegisteredVerifier()
    {
        // Arrange: register a fake verifier for "TestProvider"
        var fakeVerifier = new FakeConnectivityVerifier(
            WorkSourceConnectivityResult.SuccessResult(
                "FakeAuth",
                httpStatus: 200,
                payload: new Dictionary<string, object> { ["testKey"] = "testValue" }
            )
        );

        var resolver = CreateResolver(
            fakeVerifier,
            new Dictionary<string, Type>
            {
                ["TestProvider"] = typeof(FakeConnectivityVerifier),
            }
        );

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "test-env",
            DisplayName = "Test Environment",
            Provider = "TestProvider",
        };

        // Act
        var result = await resolver.VerifyAsync(profile, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("FakeAuth", result.AuthMechanism);
        Assert.Equal(200, result.HttpStatus);
        Assert.Empty(result.Errors);

        var payload = Assert.IsType<Dictionary<string, object>>(result.Payload);
        Assert.Equal("testValue", payload["testKey"]);
    }

    // ─── Unsupported provider returns non-success result ───

    [Fact]
    public async Task VerifyAsync_UnsupportedProvider_ReturnsFailureWithoutThrowing()
    {
        // Arrange: register a verifier for "KnownProvider" only
        var resolver = CreateResolver(
            new FakeConnectivityVerifier(),
            new Dictionary<string, Type>
            {
                ["KnownProvider"] = typeof(FakeConnectivityVerifier),
            }
        );

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "unknown-env",
            DisplayName = "Unknown Provider Environment",
            Provider = "UnknownProvider",
        };

        // Act — must not throw
        var result = await resolver.VerifyAsync(profile, CancellationToken.None);

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

    // ─── Resolver passes cancellation token to verifier ───

    [Fact]
    public async Task VerifyAsync_PassesCancellationTokenToVerifier()
    {
        // Arrange
        var fakeVerifier = new FakeConnectivityVerifier(
            WorkSourceConnectivityResult.SuccessResult("Test")
        );

        var resolver = CreateResolver(
            fakeVerifier,
            new Dictionary<string, Type>
            {
                ["TestProvider"] = typeof(FakeConnectivityVerifier),
            }
        );

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "test-env",
            Provider = "TestProvider",
        };

        var cts = new CancellationTokenSource();

        // Act
        var result = await resolver.VerifyAsync(profile, cts.Token);

        // Assert: verifier was called with a non-default token
        Assert.True(result.Success);
        Assert.True(fakeVerifier.ReceivedCancellation);
    }

    // ─── Resolver returns failure from verifier ───

    [Fact]
    public async Task VerifyAsync_VerifierReturnsFailure_PropagatesFailure()
    {
        // Arrange
        var fakeVerifier = new FakeConnectivityVerifier(
            WorkSourceConnectivityResult.FailureResult(
                ["Connection refused"],
                authMechanism: "FakeAuth",
                httpStatus: 503
            )
        );

        var resolver = CreateResolver(
            fakeVerifier,
            new Dictionary<string, Type>
            {
                ["TestProvider"] = typeof(FakeConnectivityVerifier),
            }
        );

        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "test-env",
            Provider = "TestProvider",
        };

        // Act
        var result = await resolver.VerifyAsync(profile, CancellationToken.None);

        // Assert: failure propagated from the verifier
        Assert.False(result.Success);
        Assert.Equal("FakeAuth", result.AuthMechanism);
        Assert.Equal(503, result.HttpStatus);
        Assert.Single(result.Errors);
        Assert.Equal("Connection refused", result.Errors[0]);
    }

    // ─── Helpers ───

    private static WorkSourceConnectivityVerifierResolver CreateResolver(
        FakeConnectivityVerifier verifier,
        IReadOnlyDictionary<string, Type> verifierTypes
    )
    {
        var services = new ServiceCollection();
        services.AddScoped<FakeConnectivityVerifier>(_ => verifier);
        var sp = services.BuildServiceProvider(validateScopes: true);
        return new WorkSourceConnectivityVerifierResolver(
            sp.GetRequiredService<IServiceScopeFactory>(),
            verifierTypes
        );
    }

    /// <summary>
    /// Fake verifier that returns a pre-configured result.
    /// </summary>
    private sealed class FakeConnectivityVerifier(
        WorkSourceConnectivityResult? result = null
    ) : IWorkSourceConnectivityVerifier
    {
        public bool ReceivedCancellation { get; private set; }

        public Task<WorkSourceConnectivityResult> VerifyAsync(
            WorkSourceEnvironmentProfile profile,
            CancellationToken cancellationToken
        )
        {
            ReceivedCancellation = cancellationToken.CanBeCanceled;
            return Task.FromResult(result ?? WorkSourceConnectivityResult.SuccessResult("Fake"));
        }
    }
}
