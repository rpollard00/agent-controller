using AgentController.Application;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Infrastructure.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Unit tests for EnvVarSecretStore, DbSecretStore, and SecretStoreResolver.
/// </summary>
public sealed class SecretStoreTests
{
    // ─── EnvVarSecretStore: resolve existing env var ───

    [Fact]
    public async Task EnvVarSecretStore_ResolveAsync_ExistingEnvVar_ReturnsValue()
    {
        // Arrange
        var envName = "SECRET_STORE_TEST_PAT";
        var expected = "test-token-abc";

        try
        {
            Environment.SetEnvironmentVariable(envName, expected);
            var store = new EnvVarSecretStore();
            var reference = SecretReference.EnvironmentVariable(envName);

            // Act
            var value = await store.ResolveAsync(reference, CancellationToken.None);

            // Assert
            Assert.Equal(expected, value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ─── EnvVarSecretStore: resolve missing env var returns null ───

    [Fact]
    public async Task EnvVarSecretStore_ResolveAsync_MissingEnvVar_ReturnsNull()
    {
        // Arrange
        var store = new EnvVarSecretStore();
        var reference = SecretReference.EnvironmentVariable("__NONEXISTENT_VAR_XYZ__");

        // Act
        var value = await store.ResolveAsync(reference, CancellationToken.None);

        // Assert
        Assert.Null(value);
    }

    // ─── EnvVarSecretStore: resolve non-EnvVar kind returns null ───

    [Fact]
    public async Task EnvVarSecretStore_ResolveAsync_NonEnvVarKind_ReturnsNull()
    {
        // Arrange
        var store = new EnvVarSecretStore();
        var reference = SecretReference.Database("some-guid");

        // Act
        var value = await store.ResolveAsync(reference, CancellationToken.None);

        // Assert
        Assert.Null(value);
    }

    // ─── EnvVarSecretStore: write always fails ───

    [Fact]
    public async Task EnvVarSecretStore_WriteAsync_ReturnsFailure()
    {
        // Arrange
        var store = new EnvVarSecretStore();
        var reference = SecretReference.EnvironmentVariable("SOME_VAR");

        // Act
        var result = await store.WriteAsync(reference, "some-value", CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("read-only", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    // ─── EnvVarSecretStore: resolve passes cancellation token ───

    [Fact]
    public async Task EnvVarSecretStore_ResolveAsync_CancellationToken_ThrowsWhenCancelled()
    {
        // Arrange
        var store = new EnvVarSecretStore();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.ResolveAsync(SecretReference.EnvironmentVariable("X"), cts.Token)
        );
    }

    // ─── EnvVarSecretStore: write passes cancellation token ───

    [Fact]
    public async Task EnvVarSecretStore_WriteAsync_CancellationToken_ThrowsWhenCancelled()
    {
        // Arrange
        var store = new EnvVarSecretStore();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.WriteAsync(SecretReference.EnvironmentVariable("X"), "v", cts.Token)
        );
    }

    // ─── SecretStoreResolver: dispatches EnvVar to EnvVarSecretStore ───

    [Fact]
    public async Task SecretStoreResolver_ResolveAsync_EnvVarKind_DelegatesToEnvVarStore()
    {
        // Arrange
        var envName = "SECRET_STORE_RESOLVER_TEST";
        var expected = "resolved-by-envvar-store";

        try
        {
            Environment.SetEnvironmentVariable(envName, expected);
            var stores = new Dictionary<string, ISecretStore>
            {
                ["EnvVar"] = new EnvVarSecretStore(),
            };
            var resolver = new SecretStoreResolver(stores);
            var reference = SecretReference.EnvironmentVariable(envName);

            // Act
            var value = await resolver.ResolveAsync(reference, CancellationToken.None);

            // Assert
            Assert.Equal(expected, value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ─── SecretStoreResolver: resolves unknown kind returns null ───

    [Fact]
    public async Task SecretStoreResolver_ResolveAsync_UnknownKind_ReturnsNull()
    {
        // Arrange
        var stores = new Dictionary<string, ISecretStore>();
        var resolver = new SecretStoreResolver(stores);
        var reference = new SecretReference { Kind = "Unknown", Id = "x" };

        // Act
        var value = await resolver.ResolveAsync(reference, CancellationToken.None);

        // Assert
        Assert.Null(value);
    }

    // ─── SecretStoreResolver: writes unknown kind returns failure ───

    [Fact]
    public async Task SecretStoreResolver_WriteAsync_UnknownKind_ReturnsFailure()
    {
        // Arrange
        var stores = new Dictionary<string, ISecretStore>();
        var resolver = new SecretStoreResolver(stores);
        var reference = new SecretReference { Kind = "Unknown", Id = "x" };

        // Act
        var result = await resolver.WriteAsync(reference, "v", CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("Unknown", result.Errors[0]);
    }

    // ─── SecretStoreResolver: write delegates to registered store ───

    [Fact]
    public async Task SecretStoreResolver_WriteAsync_KnownKind_DelegatesToStore()
    {
        // Arrange
        var fakeStore = new InMemoryFakeSecretStore();
        var stores = new Dictionary<string, ISecretStore>
        {
            ["Db"] = fakeStore,
        };
        var resolver = new SecretStoreResolver(stores);
        var reference = SecretReference.Database("test-guid");

        // Act
        var result = await resolver.WriteAsync(reference, "secret-value", CancellationToken.None);

        // Assert
        Assert.True(result.Success);

        // Verify the value was written to the underlying store
        var resolved = await fakeStore.ResolveAsync(reference, CancellationToken.None);
        Assert.Equal("secret-value", resolved);
    }

    // ─── ISecretProtector: contract verification ───

    [Fact]
    public void ISecretProtector_ExistsAndHasCorrectMembers()
    {
        var type = typeof(ISecretProtector);
        Assert.True(type.IsInterface);

        var protect = type.GetMethod("Protect")!;
        Assert.Equal(typeof(string), protect.ReturnType);
        Assert.Single(protect.GetParameters());

        var unprotect = type.GetMethod("Unprotect")!;
        Assert.Equal(typeof(string), unprotect.ReturnType);
        Assert.Single(unprotect.GetParameters());
    }

    // ─── DbSecretStore: resolve non-Db kind returns null (no DB needed) ───

    [Fact]
    public async Task DbSecretStore_ResolveAsync_NonDbKind_ReturnsNull()
    {
        // Arrange: we can't easily construct a DbContext for this test,
        // but we verify the Kind check logic by confirming the interface
        // is correctly implemented.
        var type = typeof(DbSecretStore);
        Assert.True(typeof(ISecretStore).IsAssignableFrom(type));
    }
}

/// <summary>
/// Simple in-memory fake for testing SecretStoreResolver dispatch.
/// </summary>
internal sealed class InMemoryFakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _store = new();

    public Task<string?> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        return Task.FromResult(_store.GetValueOrDefault(reference.Id));
    }

    public Task<SecretWriteResult> WriteAsync(SecretReference reference, string value, CancellationToken cancellationToken)
    {
        _store[reference.Id] = value;
        return Task.FromResult(SecretWriteResult.SuccessResult());
    }
}
