using AgentController.Application;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Tests;

/// <summary>
/// Contract-level tests for the <see cref="IManagedSecretStore"/> port,
/// <see cref="SecretWriteResult"/>, <see cref="ResolvedSecret"/>,
/// and <see cref="ResolvedSecretsManifest"/> types.
/// Uses a fake in-memory secret store — no real implementations exercised.
/// </summary>
public sealed class IManagedSecretStoreTests
{
    // ─── IManagedSecretStore: resolve existing secret ───

    [Fact]
    public async Task ResolveAsync_ExistingEnvVarSecret_ReturnsValue()
    {
        // Arrange
        var store = new FakeSecretStore(new Dictionary<SecretReference, string>
        {
            [SecretReference.EnvironmentVariable("ADO_PAT")] = "fake-token-123",
        });

        // Act
        var value = await store.ResolveAsync(
            SecretReference.EnvironmentVariable("ADO_PAT"),
            CancellationToken.None
        );

        // Assert
        Assert.Equal("fake-token-123", value);
    }

    // ─── IManagedSecretStore: resolve missing secret returns null ───

    [Fact]
    public async Task ResolveAsync_MissingSecret_ReturnsNull()
    {
        // Arrange
        var store = new FakeSecretStore(new Dictionary<SecretReference, string>());

        // Act
        var value = await store.ResolveAsync(
            SecretReference.EnvironmentVariable("MISSING_VAR"),
            CancellationToken.None
        );

        // Assert
        Assert.Null(value);
    }

    // ─── IManagedSecretStore: resolve database-backed secret ───

    [Fact]
    public async Task ResolveAsync_DatabaseSecret_ReturnsValue()
    {
        // Arrange
        var store = new FakeSecretStore(new Dictionary<SecretReference, string>
        {
            [SecretReference.Database("guid-abc-123")] = "db-stored-token",
        });

        // Act
        var value = await store.ResolveAsync(
            SecretReference.Database("guid-abc-123"),
            CancellationToken.None
        );

        // Assert
        Assert.Equal("db-stored-token", value);
    }

    // ─── IManagedSecretStore: passes cancellation token ───

    [Fact]
    public async Task ResolveAsync_PassesCancellationToken()
    {
        // Arrange
        var store = new FakeSecretStore(new Dictionary<SecretReference, string>
        {
            [SecretReference.EnvironmentVariable("TOKEN")] = "value",
        });

        var cts = new CancellationTokenSource();

        // Act
        _ = await store.ResolveAsync(SecretReference.EnvironmentVariable("TOKEN"), cts.Token);

        // Assert
        Assert.True(store.ReceivedCancellation);
    }

    // ─── IManagedSecretStore: write secret succeeds ───

    [Fact]
    public async Task WriteAsync_NewSecret_ReturnsSuccess()
    {
        // Arrange
        var store = new FakeSecretStore(new Dictionary<SecretReference, string>());

        // Act
        var result = await store.WriteAsync(
            SecretReference.Database("new-guid"),
            "new-secret-value",
            CancellationToken.None
        );

        // Assert
        Assert.True(result.Success);
        Assert.Empty(result.Errors);

        // Verify the value was persisted in the fake store
        var resolved = await store.ResolveAsync(
            SecretReference.Database("new-guid"),
            CancellationToken.None
        );
        Assert.Equal("new-secret-value", resolved);
    }

    // ─── IManagedSecretStore: write secret updates existing value ───

    [Fact]
    public async Task WriteAsync_ExistingSecret_UpdatesValue()
    {
        // Arrange
        var store = new FakeSecretStore(new Dictionary<SecretReference, string>
        {
            [SecretReference.Database("guid-1")] = "old-value",
        });

        // Act
        var result = await store.WriteAsync(
            SecretReference.Database("guid-1"),
            "new-value",
            CancellationToken.None
        );

        // Assert
        Assert.True(result.Success);

        var resolved = await store.ResolveAsync(
            SecretReference.Database("guid-1"),
            CancellationToken.None
        );
        Assert.Equal("new-value", resolved);
    }

    // ─── IManagedSecretStore: write passes cancellation token ───

    [Fact]
    public async Task WriteAsync_PassesCancellationToken()
    {
        // Arrange
        var store = new FakeSecretStore(new Dictionary<SecretReference, string>());
        var cts = new CancellationTokenSource();

        // Act
        _ = await store.WriteAsync(
            SecretReference.EnvironmentVariable("X"),
            "val",
            cts.Token
        );

        // Assert
        Assert.True(store.ReceivedCancellation);
    }

    // ─── SecretWriteResult: success factory ───

    [Fact]
    public void SecretWriteResult_SuccessResult_HasCorrectDefaults()
    {
        // Act
        var result = SecretWriteResult.SuccessResult();

        // Assert
        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Null(result.Metadata);
    }

    // ─── SecretWriteResult: success with metadata ───

    [Fact]
    public void SecretWriteResult_SuccessResult_WithMetadata()
    {
        // Act
        var result = SecretWriteResult.SuccessResult(metadata: "v=2");

        // Assert
        Assert.True(result.Success);
        Assert.Equal("v=2", result.Metadata);
    }

    // ─── SecretWriteResult: failure factory ───

    [Fact]
    public void SecretWriteResult_FailureResult_HasErrors()
    {
        // Act
        var result = SecretWriteResult.FailureResult("disk full", "timeout");

        // Assert
        Assert.False(result.Success);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal("disk full", result.Errors[0]);
        Assert.Equal("timeout", result.Errors[1]);
    }

    // ─── ResolvedSecret: record equality ───

    [Fact]
    public void ResolvedSecret_RecordEquality()
    {
        var reference = SecretReference.EnvironmentVariable("PAT");
        var a = new ResolvedSecret(reference, "token-value");
        var b = new ResolvedSecret(reference, "token-value");
        var c = new ResolvedSecret(reference, "different-value");
        var d = new ResolvedSecret(SecretReference.Database("guid"), "token-value");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    // ─── ResolvedSecret: null value is allowed ───

    [Fact]
    public void ResolvedSecret_AllowsNullValue()
    {
        var secret = new ResolvedSecret(SecretReference.EnvironmentVariable("MISSING"), null);

        Assert.Null(secret.Value);
        Assert.Equal("EnvVar", secret.Reference.Kind);
        Assert.Equal("MISSING", secret.Reference.Id);
    }

    // ─── ResolvedSecretsManifest: record equality ───

    [Fact]
    public void ResolvedSecretsManifest_RecordEquality()
    {
        var secrets = new[]
        {
            new ResolvedSecret(SecretReference.EnvironmentVariable("PAT"), "token"),
        };

        var a = new ResolvedSecretsManifest("ado-repos", secrets);
        var b = new ResolvedSecretsManifest("ado-repos", secrets);
        var c = new ResolvedSecretsManifest("github-repos", secrets);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ─── ResolvedSecretsManifest: empty secrets list ───

    [Fact]
    public void ResolvedSecretsManifest_AllowsEmptySecrets()
    {
        var manifest = new ResolvedSecretsManifest("empty-scope", Array.Empty<ResolvedSecret>());

        Assert.Equal("empty-scope", manifest.Scope);
        Assert.Empty(manifest.Secrets);
    }

    // ─── ResolvedSecretsManifest: multiple secrets ───

    [Fact]
    public void ResolvedSecretsManifest_MultipleSecrets()
    {
        var secrets = new[]
        {
            new ResolvedSecret(SecretReference.EnvironmentVariable("PAT"), "pat-value"),
            new ResolvedSecret(SecretReference.Database("guid-1"), "db-value"),
        };

        var manifest = new ResolvedSecretsManifest("multi-scope", secrets);

        Assert.Equal("multi-scope", manifest.Scope);
        Assert.Equal(2, manifest.Secrets.Count);
        Assert.Equal("pat-value", manifest.Secrets[0].Value);
        Assert.Equal("db-value", manifest.Secrets[1].Value);
    }

    // ─── ResolvedSecretsManifest: with-expression does not mutate original ───

    [Fact]
    public void ResolvedSecretsManifest_WithExpressionDoesNotMutateOriginal()
    {
        var secrets = new[]
        {
            new ResolvedSecret(SecretReference.EnvironmentVariable("PAT"), "token"),
        };

        var original = new ResolvedSecretsManifest("scope-1", secrets);
        var updated = original with { Scope = "scope-2" };

        Assert.Equal("scope-1", original.Scope);
        Assert.Equal("scope-2", updated.Scope);
        Assert.NotEqual(original, updated);
    }

    // ─── FakeSecretStore: write failure simulation ───

    [Fact]
    public async Task WriteAsync_ReadOnlyStore_ReturnsFailure()
    {
        // Arrange: read-only store always fails writes
        var store = new ReadOnlyFakeSecretStore();

        // Act
        var result = await store.WriteAsync(
            SecretReference.Database("guid"),
            "value",
            CancellationToken.None
        );

        // Assert
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }
}

// ─── Fakes ───

/// <summary>
/// In-memory fake implementation of <see cref="IManagedSecretStore"/> for contract tests.
/// </summary>
internal sealed class FakeSecretStore(Dictionary<SecretReference, string> initialData) : IManagedSecretStore
{
    private readonly Dictionary<SecretReference, string> _store = initialData;
    public bool ReceivedCancellation { get; private set; }

    public Task<string?> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        ReceivedCancellation = cancellationToken.CanBeCanceled;
        return Task.FromResult(_store.GetValueOrDefault(reference));
    }

    public Task<SecretWriteResult> WriteAsync(SecretReference reference, string value, CancellationToken cancellationToken)
    {
        ReceivedCancellation = cancellationToken.CanBeCanceled;
        _store[reference] = value;
        return Task.FromResult(SecretWriteResult.SuccessResult());
    }
}

/// <summary>
/// Read-only fake that always fails writes — tests failure path of <see cref="SecretWriteResult"/>.
/// </summary>
internal sealed class ReadOnlyFakeSecretStore : IManagedSecretStore
{
    public Task<string?> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<SecretWriteResult> WriteAsync(SecretReference reference, string value, CancellationToken cancellationToken)
    {
        return Task.FromResult(SecretWriteResult.FailureResult("store is read-only"));
    }
}
