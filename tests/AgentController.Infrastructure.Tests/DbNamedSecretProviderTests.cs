using AgentController.Domain.Secrets;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for the DB-backed named secret provider with envelope encryption.
/// </summary>
public sealed class DbNamedSecretProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly byte[] _testKek;

    public DbNamedSecretProviderTests()
    {
        // Use a shared in-memory SQLite connection so all tests share the same database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        // Create the schema once.
        using var setupContext = new AgentControllerDbContext(
            new DbContextOptionsBuilder<AgentControllerDbContext>()
                .UseSqlite(_connection)
                .Options);
        setupContext.Database.EnsureCreated();

        // Deterministic 32-byte KEK for testing.
        _testKek = new byte[32];
        for (int i = 0; i < 32; i++) _testKek[i] = (byte)i;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private IServiceScope CreateScope()
    {
        return CreateScopeWithConnection(_connection, _testKek);
    }

    private static byte[] CreateTestKek()
    {
        var kek = new byte[32];
        for (int i = 0; i < 32; i++) kek[i] = (byte)i;
        return kek;
    }

    private static IServiceScope CreateScopeWithConnection(SqliteConnection connection, byte[] kek)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AgentControllerDbContext>(options =>
            options.UseSqlite(connection));
        services.AddSingleton<IKeyEncryptionKeySource>(_ =>
            new TestKeyEncryptionKeySource(kek));
        services.AddScoped<ISecretStore, DbNamedSecretProvider>();
        services.AddScoped<ISecretManager, DbNamedSecretProvider>();

        var provider = services.BuildServiceProvider();
        return provider.CreateScope();
    }

    // ═══════════════════════════════════════════════════════════
    // ISecretStore (read path)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveAsync_ExistingSecret_ReturnsPlaintext()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("test-secret", "my-secret-value", CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var result = await store.ResolveAsync("test-secret", cancellationToken: CancellationToken.None);

        Assert.Equal("my-secret-value", result);
    }

    [Fact]
    public async Task ResolveAsync_NonExistentSecret_ReturnsNull()
    {
        using var scope = CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var result = await store.ResolveAsync("non-existent", cancellationToken: CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_WithVersion_ResolvesSpecificVersion()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("versioned-secret", "value-v1", CancellationToken.None);
        await manager.CreateVersionAsync("versioned-secret", "value-v2", CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var v1 = await store.ResolveAsync("versioned-secret", version: 1, cancellationToken: CancellationToken.None);
        var v2 = await store.ResolveAsync("versioned-secret", version: 2, cancellationToken: CancellationToken.None);

        Assert.Equal("value-v1", v1);
        Assert.Equal("value-v2", v2);
    }

    [Fact]
    public async Task ResolveAsync_WithoutVersion_ResolvesLatest()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("latest-secret", "old-value", CancellationToken.None);
        await manager.CreateVersionAsync("latest-secret", "new-value", CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var result = await store.ResolveAsync("latest-secret", cancellationToken: CancellationToken.None);

        Assert.Equal("new-value", result);
    }

    [Fact]
    public async Task ResolveAsync_NonExistentVersion_ReturnsNull()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("single-version", "value", CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var result = await store.ResolveAsync("single-version", version: 99, cancellationToken: CancellationToken.None);

        Assert.Null(result);
    }

    // ═══════════════════════════════════════════════════════════
    // ISecretManager (admin path)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateAsync_NewSecret_ReturnsTrue()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        var result = await manager.CreateAsync("new-secret-1", "secret-value", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_ReturnsFalse()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("dup-secret-1", "value1", CancellationToken.None);

        using var scope2 = CreateScope();
        var manager2 = scope2.ServiceProvider.GetRequiredService<ISecretManager>();
        var result = await manager2.CreateAsync("dup-secret-1", "value2", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task CreateAsync_CreatesVersion1()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("create-v1-1", "initial-value", CancellationToken.None);

        var versions = await manager.ListVersionsAsync("create-v1-1", CancellationToken.None);

        Assert.NotNull(versions);
        Assert.Single(versions);
        Assert.Equal(1, versions[0].Version);
    }

    [Fact]
    public async Task CreateVersionAsync_ExistingSecret_ReturnsNewVersion()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("version-test-1", "v1-value", CancellationToken.None);

        var newVersion = await manager.CreateVersionAsync("version-test-1", "v2-value", CancellationToken.None);

        Assert.Equal(2, newVersion);
    }

    [Fact]
    public async Task CreateVersionAsync_NonExistentSecret_ReturnsNull()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        var result = await manager.CreateVersionAsync("no-such-secret-1", "value", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateVersionAsync_MonotonicallyIncrements()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("mono-test-1", "v1", CancellationToken.None);

        var v2 = await manager.CreateVersionAsync("mono-test-1", "v2", CancellationToken.None);
        var v3 = await manager.CreateVersionAsync("mono-test-1", "v3", CancellationToken.None);

        Assert.Equal(2, v2);
        Assert.Equal(3, v3);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSecretsOrderedByName()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("bravo-1", "val-b", CancellationToken.None);
        await manager.CreateAsync("alpha-1", "val-a", CancellationToken.None);
        await manager.CreateAsync("charlie-1", "val-c", CancellationToken.None);

        var secrets = await manager.ListAsync(CancellationToken.None);

        Assert.Equal(3, secrets.Count);
        Assert.Equal("alpha-1", secrets[0].Name);
        Assert.Equal("bravo-1", secrets[1].Name);
        Assert.Equal("charlie-1", secrets[2].Name);
    }

    [Fact]
    public async Task ListAsync_SecretInfo_HasCorrectMetadata()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("meta-test-1", "v1", CancellationToken.None);
        await Task.Delay(50); // Ensure timestamp difference
        await manager.CreateVersionAsync("meta-test-1", "v2", CancellationToken.None);

        var secrets = await manager.ListAsync(CancellationToken.None);

        var secret = secrets.Single(s => s.Name == "meta-test-1");
        Assert.Equal(2, secret.LatestVersion);
        Assert.True(secret.UpdatedAt >= secret.CreatedAt);
    }

    [Fact]
    public async Task ListVersionsAsync_ExistingSecret_ReturnsVersionsOrdered()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("list-ver-test-1", "v1", CancellationToken.None);
        await manager.CreateVersionAsync("list-ver-test-1", "v2", CancellationToken.None);
        await manager.CreateVersionAsync("list-ver-test-1", "v3", CancellationToken.None);

        var versions = await manager.ListVersionsAsync("list-ver-test-1", CancellationToken.None);

        Assert.NotNull(versions);
        Assert.Equal(3, versions.Count);
        Assert.Equal(1, versions[0].Version);
        Assert.Equal(2, versions[1].Version);
        Assert.Equal(3, versions[2].Version);
    }

    [Fact]
    public async Task ListVersionsAsync_NonExistentSecret_ReturnsNull()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        var result = await manager.ListVersionsAsync("no-such-secret-2", CancellationToken.None);

        Assert.Null(result);
    }

    // ═══════════════════════════════════════════════════════════
    // Round-trip persistence through a fresh DbContext (reboot simulation)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task RoundTripPersistence_FreshDbContextAfterReboot_ReturnsPlaintext()
    {
        // Arrange: use a file-based temp SQLite DB so it survives connection close/reopen
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"secrets-reboot-test-{Guid.NewGuid():N}.db");
        var kek = CreateTestKek();
        try
        {
            // ── Phase 1: "first run" — create schema, create the secret, persist ──
            var connection1 = new SqliteConnection($"Data Source={tempDbPath}");
            connection1.Open();
            using (var scope1 = CreateScopeWithConnection(connection1, kek))
            {
                // Simulate startup migration/table creation
                var ctx1 = scope1.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
                ctx1.Database.EnsureCreated();

                var manager = scope1.ServiceProvider.GetRequiredService<ISecretManager>();
                await manager.CreateAsync("reboot-secret", "persisted-value-abc123", CancellationToken.None);
            }
            // Dispose the scope (and its DbContext), then close the connection entirely
            connection1.Dispose();

            // ── Phase 2: "after reboot" — fresh connection, fresh DbContext ──
            var connection2 = new SqliteConnection($"Data Source={tempDbPath}");
            connection2.Open();
            using (var scope2 = CreateScopeWithConnection(connection2, kek))
            {
                var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
                var result = await store.ResolveAsync("reboot-secret", cancellationToken: CancellationToken.None);

                Assert.Equal("persisted-value-abc123", result);
            }
            connection2.Dispose();
        }
        finally
        {
            // Cleanup temp file
            if (File.Exists(tempDbPath))
            {
                File.Delete(tempDbPath);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // End-to-end: encrypt then decrypt
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task EndToEnd_EncryptThenDecrypt_RoundTripsValue()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        var secretValue = "super-secret-token-abc123!@#";
        await manager.CreateAsync("e2e-test-1", secretValue, CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var result = await store.ResolveAsync("e2e-test-1", cancellationToken: CancellationToken.None);

        Assert.Equal(secretValue, result);
    }

    [Fact]
    public async Task EndToEnd_MultipleVersions_EachResolvesCorrectly()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("multi-ver-1", "first", CancellationToken.None);
        await manager.CreateVersionAsync("multi-ver-1", "second", CancellationToken.None);
        await manager.CreateVersionAsync("multi-ver-1", "third", CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var v1 = await store.ResolveAsync("multi-ver-1", version: 1, cancellationToken: CancellationToken.None);
        var v2 = await store.ResolveAsync("multi-ver-1", version: 2, cancellationToken: CancellationToken.None);
        var v3 = await store.ResolveAsync("multi-ver-1", version: 3, cancellationToken: CancellationToken.None);
        var latest = await store.ResolveAsync("multi-ver-1", cancellationToken: CancellationToken.None);

        Assert.Equal("first", v1);
        Assert.Equal("second", v2);
        Assert.Equal("third", v3);
        Assert.Equal("third", latest);
    }

    // ═══════════════════════════════════════════════════════════
    // AesGcmEnvelopeEncryption unit tests (no DB)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void AesGcmEnvelopeEncryption_EncryptThenDecrypt_RoundTrips()
    {
        var kekSource = new TestKeyEncryptionKeySource(_testKek);
        var encryption = new AesGcmEnvelopeEncryption(kekSource);

        var (encryptedValue, nonce, wrappedDek) = encryption.Encrypt("test-secret-value");
        var decrypted = encryption.Decrypt(encryptedValue, nonce, wrappedDek);

        Assert.Equal("test-secret-value", decrypted);
    }

    [Fact]
    public void AesGcmEnvelopeEncryption_EncryptThenDecrypt_EmptyString()
    {
        var kekSource = new TestKeyEncryptionKeySource(_testKek);
        var encryption = new AesGcmEnvelopeEncryption(kekSource);

        var (encryptedValue, nonce, wrappedDek) = encryption.Encrypt("");
        var decrypted = encryption.Decrypt(encryptedValue, nonce, wrappedDek);

        Assert.Equal("", decrypted);
    }

    [Fact] // Debug: print sizes to verify
    public void AesGcmEnvelopeEncryption_Encrypt_SizesAreCorrect()
    {
        var kekSource = new TestKeyEncryptionKeySource(_testKek);
        var encryption = new AesGcmEnvelopeEncryption(kekSource);
        var plaintext = "hello"; // 5 bytes

        var (encryptedValue, nonce, wrappedDek) = encryption.Encrypt(plaintext);

        // encryptedValue = ciphertext (5) + tag (16) = 21
        Assert.Equal(21, encryptedValue.Length);
        // nonce = 12
        Assert.Equal(12, nonce.Length);
        // wrappedDek = dekNonce (12) + wrappedCiphertext (32) + wrappedTag (16) = 60
        Assert.Equal(60, wrappedDek.Length);

        var decrypted = encryption.Decrypt(encryptedValue, nonce, wrappedDek);
        Assert.Equal(plaintext, decrypted);
    }

    // ═══════════════════════════════════════════════════════════
    // KEK validation (fail fast)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_InvalidKekLength_Throws()
    {
        var badKek = new byte[16]; // Too short for AES-256
        var badSource = new TestKeyEncryptionKeySource(badKek);

        Assert.Throws<InvalidOperationException>(() =>
            new AesGcmEnvelopeEncryption(badSource));
    }

    [Fact]
    public void Constructor_NullKek_Throws()
    {
        var nullSource = new TestKeyEncryptionKeySource(null);

        Assert.Throws<InvalidOperationException>(() =>
            new AesGcmEnvelopeEncryption(nullSource));
    }
}

/// <summary>
/// Wrapper that keeps the IServiceScope alive for the lifetime of the service.
/// </summary>
internal sealed class ScopedSecretStore : ISecretStore, IDisposable
{
    private readonly ISecretStore _inner;
    private readonly IServiceScope _scope;

    public ScopedSecretStore(ISecretStore inner, IServiceScope scope)
    {
        _inner = inner;
        _scope = scope;
    }

    public Task<string?> ResolveAsync(string name, int? version = null, CancellationToken cancellationToken = default)
        => _inner.ResolveAsync(name, version, cancellationToken);

    public void Dispose() => _scope.Dispose();
}

internal sealed class ScopedSecretManager : ISecretManager, IDisposable
{
    private readonly ISecretManager _inner;
    private readonly IServiceScope _scope;

    public ScopedSecretManager(ISecretManager inner, IServiceScope scope)
    {
        _inner = inner;
        _scope = scope;
    }

    public Task<bool> CreateAsync(string name, string value, CancellationToken cancellationToken = default)
        => _inner.CreateAsync(name, value, cancellationToken);

    public Task<int?> CreateVersionAsync(string name, string value, CancellationToken cancellationToken = default)
        => _inner.CreateVersionAsync(name, value, cancellationToken);

    public Task<IReadOnlyList<SecretInfo>> ListAsync(CancellationToken cancellationToken = default)
        => _inner.ListAsync(cancellationToken);

    public Task<IReadOnlyList<SecretVersionInfo>?> ListVersionsAsync(string name, CancellationToken cancellationToken = default)
        => _inner.ListVersionsAsync(name, cancellationToken);

    public void Dispose() => _scope.Dispose();
}

/// <summary>
/// Test double for IKeyEncryptionKeySource that returns a fixed KEK.
/// </summary>
internal sealed class TestKeyEncryptionKeySource : IKeyEncryptionKeySource
{
    private readonly byte[]? _key;

    public TestKeyEncryptionKeySource(byte[]? key)
    {
        _key = key;
    }

    public byte[] GetKey()
    {
        if (_key == null)
        {
            throw new InvalidOperationException("KEK is not configured.");
        }
        return _key;
    }
}
