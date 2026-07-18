using AgentController.Domain.Secrets;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Entities;
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
        await manager.CreateAsync("test-secret", new PersonalAccessTokenPayload { Value = "my-secret-value" }, CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var result = await store.ResolveAsync("test-secret", cancellationToken: CancellationToken.None);

        Assert.IsType<PersonalAccessTokenPayload>(result);
        Assert.Equal("my-secret-value", ((PersonalAccessTokenPayload)result!).Value);
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
        await manager.CreateAsync("versioned-secret", new PersonalAccessTokenPayload { Value = "value-v1" }, CancellationToken.None);
        await manager.CreateVersionAsync("versioned-secret", new PersonalAccessTokenPayload { Value = "value-v2" }, CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var v1 = await store.ResolveAsync("versioned-secret", version: 1, cancellationToken: CancellationToken.None);
        var v2 = await store.ResolveAsync("versioned-secret", version: 2, cancellationToken: CancellationToken.None);

        Assert.IsType<PersonalAccessTokenPayload>(v1);
        Assert.IsType<PersonalAccessTokenPayload>(v2);
        Assert.Equal("value-v1", ((PersonalAccessTokenPayload)v1!).Value);
        Assert.Equal("value-v2", ((PersonalAccessTokenPayload)v2!).Value);
    }

    [Fact]
    public async Task ResolveAsync_WithoutVersion_ResolvesLatest()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("latest-secret", new PersonalAccessTokenPayload { Value = "old-value" }, CancellationToken.None);
        await manager.CreateVersionAsync("latest-secret", new PersonalAccessTokenPayload { Value = "new-value" }, CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var result = await store.ResolveAsync("latest-secret", cancellationToken: CancellationToken.None);

        Assert.IsType<PersonalAccessTokenPayload>(result);
        Assert.Equal("new-value", ((PersonalAccessTokenPayload)result!).Value);
    }

    [Fact]
    public async Task ResolveAsync_NonExistentVersion_ReturnsNull()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("single-version", new PersonalAccessTokenPayload { Value = "value" }, CancellationToken.None);

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
        var result = await manager.CreateAsync("new-secret-1", new PersonalAccessTokenPayload { Value = "secret-value" }, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_ReturnsFalse()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("dup-secret-1", new PersonalAccessTokenPayload { Value = "value1" }, CancellationToken.None);

        using var scope2 = CreateScope();
        var manager2 = scope2.ServiceProvider.GetRequiredService<ISecretManager>();
        var result = await manager2.CreateAsync("dup-secret-1", new PersonalAccessTokenPayload { Value = "value2" }, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task CreateAsync_CreatesVersion1()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("create-v1-1", new PersonalAccessTokenPayload { Value = "initial-value" }, CancellationToken.None);

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
        await manager.CreateAsync("version-test-1", new PersonalAccessTokenPayload { Value = "v1-value" }, CancellationToken.None);

        var newVersion = await manager.CreateVersionAsync("version-test-1", new PersonalAccessTokenPayload { Value = "v2-value" }, CancellationToken.None);

        Assert.Equal(2, newVersion);
    }

    [Fact]
    public async Task CreateVersionAsync_NonExistentSecret_ReturnsNull()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        var result = await manager.CreateVersionAsync("no-such-secret-1", new PersonalAccessTokenPayload { Value = "value" }, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateVersionAsync_MonotonicallyIncrements()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("mono-test-1", new PersonalAccessTokenPayload { Value = "v1" }, CancellationToken.None);

        var v2 = await manager.CreateVersionAsync("mono-test-1", new PersonalAccessTokenPayload { Value = "v2" }, CancellationToken.None);
        var v3 = await manager.CreateVersionAsync("mono-test-1", new PersonalAccessTokenPayload { Value = "v3" }, CancellationToken.None);

        Assert.Equal(2, v2);
        Assert.Equal(3, v3);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSecretsOrderedByName()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("bravo-1", new PersonalAccessTokenPayload { Value = "val-b" }, CancellationToken.None);
        await manager.CreateAsync("alpha-1", new PersonalAccessTokenPayload { Value = "val-a" }, CancellationToken.None);
        await manager.CreateAsync("charlie-1", new PersonalAccessTokenPayload { Value = "val-c" }, CancellationToken.None);

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
        await manager.CreateAsync("meta-test-1", new PersonalAccessTokenPayload { Value = "v1" }, CancellationToken.None);
        await Task.Delay(50); // Ensure timestamp difference
        await manager.CreateVersionAsync("meta-test-1", new PersonalAccessTokenPayload { Value = "v2" }, CancellationToken.None);

        var secrets = await manager.ListAsync(CancellationToken.None);

        var secret = secrets.Single(s => s.Name == "meta-test-1");
        Assert.Equal(2, secret.LatestVersion);
        Assert.True(secret.UpdatedAt >= secret.CreatedAt);
        Assert.Equal(Domain.Secrets.SecretType.PersonalAccessToken, secret.SecretType);
    }

    [Fact]
    public async Task ListVersionsAsync_ExistingSecret_ReturnsVersionsOrdered()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("list-ver-test-1", new PersonalAccessTokenPayload { Value = "v1" }, CancellationToken.None);
        await manager.CreateVersionAsync("list-ver-test-1", new PersonalAccessTokenPayload { Value = "v2" }, CancellationToken.None);
        await manager.CreateVersionAsync("list-ver-test-1", new PersonalAccessTokenPayload { Value = "v3" }, CancellationToken.None);

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
    // DeleteAsync
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task DeleteAsync_ExistingSecret_ReturnsTrue()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("delete-existing-1", new PersonalAccessTokenPayload { Value = "value" }, CancellationToken.None);

        var result = await manager.DeleteAsync("delete-existing-1", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task DeleteAsync_UnknownName_ReturnsFalse()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        var result = await manager.DeleteAsync("no-such-secret-3", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSecretRowAndCascadesAllVersions()
    {
        using (var scope = CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
            await manager.CreateAsync("delete-cascade-1", new PersonalAccessTokenPayload { Value = "v1" }, CancellationToken.None);
            await manager.CreateVersionAsync("delete-cascade-1", new PersonalAccessTokenPayload { Value = "v2" }, CancellationToken.None);
            await manager.CreateVersionAsync("delete-cascade-1", new PersonalAccessTokenPayload { Value = "v3" }, CancellationToken.None);

            var deleted = await manager.DeleteAsync("delete-cascade-1", CancellationToken.None);
            Assert.True(deleted);
        }

        // Verify against the raw tables with a fresh context: the NamedSecrets
        // row is gone and every SecretVersions row cascaded away.
        using var verifyScope = CreateScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();

        Assert.False(await context.NamedSecrets.AnyAsync(s => s.Name == "delete-cascade-1"));
        Assert.False(
            await context.SecretVersions.AnyAsync(v => v.NamedSecret!.Name == "delete-cascade-1")
        );
    }

    [Fact]
    public async Task DeleteAsync_DeletedSecret_ResolveReturnsNullAndListOmitsIt()
    {
        using (var scope = CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
            await manager.CreateAsync("delete-resolve-1", new PersonalAccessTokenPayload { Value = "value" }, CancellationToken.None);
            await manager.CreateAsync("delete-resolve-2", new PersonalAccessTokenPayload { Value = "value" }, CancellationToken.None);

            Assert.True(await manager.DeleteAsync("delete-resolve-1", CancellationToken.None));
        }

        using var verifyScope = CreateScope();
        var store = verifyScope.ServiceProvider.GetRequiredService<ISecretStore>();
        var manager2 = verifyScope.ServiceProvider.GetRequiredService<ISecretManager>();

        Assert.Null(
            await store.ResolveAsync("delete-resolve-1", cancellationToken: CancellationToken.None)
        );

        var secrets = await manager2.ListAsync(CancellationToken.None);
        Assert.DoesNotContain(secrets, s => s.Name == "delete-resolve-1");
        Assert.Contains(secrets, s => s.Name == "delete-resolve-2");
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
                await manager.CreateAsync("reboot-secret", new PersonalAccessTokenPayload { Value = "persisted-value-abc123" }, CancellationToken.None);
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

                Assert.IsType<PersonalAccessTokenPayload>(result);
                Assert.Equal("persisted-value-abc123", ((PersonalAccessTokenPayload)result!).Value);
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
        await manager.CreateAsync("e2e-test-1", new PersonalAccessTokenPayload { Value = secretValue }, CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var result = await store.ResolveAsync("e2e-test-1", cancellationToken: CancellationToken.None);

        Assert.IsType<PersonalAccessTokenPayload>(result);
        Assert.Equal(secretValue, ((PersonalAccessTokenPayload)result!).Value);
    }

    [Fact]
    public async Task EndToEnd_MultipleVersions_EachResolvesCorrectly()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();
        await manager.CreateAsync("multi-ver-1", new PersonalAccessTokenPayload { Value = "first" }, CancellationToken.None);
        await manager.CreateVersionAsync("multi-ver-1", new PersonalAccessTokenPayload { Value = "second" }, CancellationToken.None);
        await manager.CreateVersionAsync("multi-ver-1", new PersonalAccessTokenPayload { Value = "third" }, CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var v1 = await store.ResolveAsync("multi-ver-1", version: 1, cancellationToken: CancellationToken.None);
        var v2 = await store.ResolveAsync("multi-ver-1", version: 2, cancellationToken: CancellationToken.None);
        var v3 = await store.ResolveAsync("multi-ver-1", version: 3, cancellationToken: CancellationToken.None);
        var latest = await store.ResolveAsync("multi-ver-1", cancellationToken: CancellationToken.None);

        Assert.IsType<PersonalAccessTokenPayload>(v1);
        Assert.IsType<PersonalAccessTokenPayload>(v2);
        Assert.IsType<PersonalAccessTokenPayload>(v3);
        Assert.IsType<PersonalAccessTokenPayload>(latest);
        Assert.Equal("first", ((PersonalAccessTokenPayload)v1!).Value);
        Assert.Equal("second", ((PersonalAccessTokenPayload)v2!).Value);
        Assert.Equal("third", ((PersonalAccessTokenPayload)v3!).Value);
        Assert.Equal("third", ((PersonalAccessTokenPayload)latest!).Value);
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
    // Typed SSH-key persistence
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateAsync_SshKeyPayload_StoresAndResolves()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        var payload = new SshKeyPayload
        {
            PrivateKey = "ssh-private-key-content-abc",
            PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5aaaa",
            Passphrase = null,
        };

        await manager.CreateAsync("ssh-test-key", payload, CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var result = await store.ResolveAsync("ssh-test-key", cancellationToken: CancellationToken.None);

        Assert.IsType<SshKeyPayload>(result);
        var sshResult = (SshKeyPayload)result!;
        Assert.Equal("ssh-private-key-content-abc", sshResult.PrivateKey);
        Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5aaaa", sshResult.PublicKey);
        Assert.Null(sshResult.Passphrase);
    }

    [Fact]
    public async Task CreateAsync_SshKeyPayload_WithPassphrase()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        var payload = new SshKeyPayload
        {
            PrivateKey = "encrypted-private-key",
            PublicKey = "ssh-rsa AAAAB3NzaC1yc2E...",
            Passphrase = "my-secret-passphrase",
        };

        await manager.CreateAsync("ssh-with-passphrase", payload, CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
        var result = await store.ResolveAsync("ssh-with-passphrase", cancellationToken: CancellationToken.None);

        Assert.IsType<SshKeyPayload>(result);
        var sshResult = (SshKeyPayload)result!;
        Assert.Equal("encrypted-private-key", sshResult.PrivateKey);
        Assert.Equal("ssh-rsa AAAAB3NzaC1yc2E...", sshResult.PublicKey);
        Assert.Equal("my-secret-passphrase", sshResult.Passphrase);
    }

    [Fact]
    public async Task CreateAsync_SshKeyPayload_SecretInfoReturnsSshType()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        await manager.CreateAsync("ssh-type-check", new SshKeyPayload
        {
            PrivateKey = "pk",
            PublicKey = "ssh-ed25519 abc",
            Passphrase = null,
        }, CancellationToken.None);

        using var scope2 = CreateScope();
        var manager2 = scope2.ServiceProvider.GetRequiredService<ISecretManager>();
        var secrets = await manager2.ListAsync(CancellationToken.None);

        var secret = secrets.Single(s => s.Name == "ssh-type-check");
        Assert.Equal(Domain.Secrets.SecretType.SshKey, secret.SecretType);
    }

    [Fact]
    public async Task ListVersionsAsync_SshKey_ReturnsPublicKey()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        await manager.CreateAsync("ssh-pubkey", new SshKeyPayload
        {
            PrivateKey = "private-key-data",
            PublicKey = "ssh-ed25519 AAAAC3N...public-key",
            Passphrase = null,
        }, CancellationToken.None);

        using var scope2 = CreateScope();
        var manager2 = scope2.ServiceProvider.GetRequiredService<ISecretManager>();
        var versions = await manager2.ListVersionsAsync("ssh-pubkey", CancellationToken.None);

        Assert.NotNull(versions);
        Assert.Single(versions);
        Assert.Equal(Domain.Secrets.SecretType.SshKey, versions[0].SecretType);
        Assert.Equal("ssh-ed25519 AAAAC3N...public-key", versions[0].PublicKey);
    }

    [Fact]
    public async Task ListVersionsAsync_Pat_ReturnsNullPublicKey()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        await manager.CreateAsync("pat-pubkey", new PersonalAccessTokenPayload { Value = "token" }, CancellationToken.None);

        using var scope2 = CreateScope();
        var manager2 = scope2.ServiceProvider.GetRequiredService<ISecretManager>();
        var versions = await manager2.ListVersionsAsync("pat-pubkey", CancellationToken.None);

        Assert.NotNull(versions);
        Assert.Single(versions);
        Assert.Equal(Domain.Secrets.SecretType.PersonalAccessToken, versions[0].SecretType);
        Assert.Null(versions[0].PublicKey);
    }

    [Fact]
    public async Task ListVersionsAsync_SshKey_MultipleVersions_AllHavePublicKeys()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        await manager.CreateAsync("ssh-multi", new SshKeyPayload
        {
            PrivateKey = "pk-v1",
            PublicKey = "public-key-v1",
            Passphrase = null,
        }, CancellationToken.None);

        await manager.CreateVersionAsync("ssh-multi", new SshKeyPayload
        {
            PrivateKey = "pk-v2",
            PublicKey = "public-key-v2",
            Passphrase = "passphrase-v2",
        }, CancellationToken.None);

        using var scope2 = CreateScope();
        var manager2 = scope2.ServiceProvider.GetRequiredService<ISecretManager>();
        var versions = await manager2.ListVersionsAsync("ssh-multi", CancellationToken.None);

        Assert.NotNull(versions);
        Assert.Equal(2, versions.Count);
        Assert.Equal("public-key-v1", versions[0].PublicKey);
        Assert.Equal("public-key-v2", versions[1].PublicKey);
        Assert.Equal(Domain.Secrets.SecretType.SshKey, versions[0].SecretType);
        Assert.Equal(Domain.Secrets.SecretType.SshKey, versions[1].SecretType);
    }

    [Fact]
    public async Task CreateVersionAsync_TypeMismatch_Throws()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        // Create a PAT secret.
        await manager.CreateAsync("type-mismatch", new PersonalAccessTokenPayload { Value = "pat-value" }, CancellationToken.None);

        // Attempt to add an SSH-key version — should throw.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreateVersionAsync("type-mismatch", new SshKeyPayload
            {
                PrivateKey = "pk",
                PublicKey = "ssh-ed25519 abc",
                Passphrase = null,
            }, CancellationToken.None));

        Assert.Contains("ssh-key", ex.Message);
        Assert.Contains("personal-access-token", ex.Message);
    }

    [Fact]
    public async Task CreateVersionAsync_ReverseTypeMismatch_Throws()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        // Create an SSH secret.
        await manager.CreateAsync("reverse-mismatch", new SshKeyPayload
        {
            PrivateKey = "pk",
            PublicKey = "ssh-ed25519 abc",
            Passphrase = null,
        }, CancellationToken.None);

        // Attempt to add a PAT version — should throw.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreateVersionAsync("reverse-mismatch", new PersonalAccessTokenPayload { Value = "pat" }, CancellationToken.None));

        Assert.Contains("personal-access-token", ex.Message);
        Assert.Contains("ssh-key", ex.Message);
    }

    [Fact]
    public async Task SshKey_RoundTripPersistence_AfterReboot_ResolvesCorrectly()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"ssh-reboot-test-{Guid.NewGuid():N}.db");
        var kek = CreateTestKek();
        try
        {
            // Phase 1: create schema and an SSH key secret.
            var connection1 = new SqliteConnection($"Data Source={tempDbPath}");
            connection1.Open();
            using (var scope1 = CreateScopeWithConnection(connection1, kek))
            {
                var ctx1 = scope1.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
                ctx1.Database.EnsureCreated();

                var manager = scope1.ServiceProvider.GetRequiredService<ISecretManager>();
                await manager.CreateAsync("reboot-ssh", new SshKeyPayload
                {
                    PrivateKey = "persistent-private-key",
                    PublicKey = "ssh-ed25519 persistent-public",
                    Passphrase = "persistent-passphrase",
                }, CancellationToken.None);
            }
            connection1.Dispose();

            // Phase 2: "after reboot" — fresh connection.
            var connection2 = new SqliteConnection($"Data Source={tempDbPath}");
            connection2.Open();
            using (var scope2 = CreateScopeWithConnection(connection2, kek))
            {
                var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
                var result = await store.ResolveAsync("reboot-ssh", cancellationToken: CancellationToken.None);

                Assert.IsType<SshKeyPayload>(result);
                var sshResult = (SshKeyPayload)result!;
                Assert.Equal("persistent-private-key", sshResult.PrivateKey);
                Assert.Equal("ssh-ed25519 persistent-public", sshResult.PublicKey);
                Assert.Equal("persistent-passphrase", sshResult.Passphrase);

                // Also verify metadata shows SSH type with public key.
                var manager = scope2.ServiceProvider.GetRequiredService<ISecretManager>();
                var versions = await manager.ListVersionsAsync("reboot-ssh", CancellationToken.None);
                Assert.NotNull(versions);
                Assert.Equal(Domain.Secrets.SecretType.SshKey, versions[0].SecretType);
                Assert.Equal("ssh-ed25519 persistent-public", versions[0].PublicKey);
            }
            connection2.Dispose();
        }
        finally
        {
            if (File.Exists(tempDbPath))
            {
                File.Delete(tempDbPath);
            }
        }
    }

    [Fact]
    public async Task SshKey_PinnedVersion_ResolvesCorrectVersion()
    {
        using var scope = CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISecretManager>();

        await manager.CreateAsync("ssh-pinned", new SshKeyPayload
        {
            PrivateKey = "pk-v1",
            PublicKey = "pub-v1",
            Passphrase = null,
        }, CancellationToken.None);

        await manager.CreateVersionAsync("ssh-pinned", new SshKeyPayload
        {
            PrivateKey = "pk-v2",
            PublicKey = "pub-v2",
            Passphrase = "p2",
        }, CancellationToken.None);

        using var scope2 = CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();

        var v1 = await store.ResolveAsync("ssh-pinned", version: 1, cancellationToken: CancellationToken.None);
        var v2 = await store.ResolveAsync("ssh-pinned", version: 2, cancellationToken: CancellationToken.None);
        var latest = await store.ResolveAsync("ssh-pinned", cancellationToken: CancellationToken.None);

        Assert.IsType<SshKeyPayload>(v1);
        Assert.IsType<SshKeyPayload>(v2);
        Assert.IsType<SshKeyPayload>(latest);

        Assert.Equal("pk-v1", ((SshKeyPayload)v1!).PrivateKey);
        Assert.Null(((SshKeyPayload)v1!).Passphrase);

        Assert.Equal("pk-v2", ((SshKeyPayload)v2!).PrivateKey);
        Assert.Equal("p2", ((SshKeyPayload)v2!).Passphrase);

        Assert.Equal("pk-v2", ((SshKeyPayload)latest!).PrivateKey);
    }

    // ═══════════════════════════════════════════════════════════
    // Legacy PAT format compatibility
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task LegacyPatValue_RawStringEncryptedByOldProvider_ResolvesCorrectly()
    {
        // The old DbNamedSecretProvider encrypted the raw PAT value string directly,
        // not as a JSON {"value":"..."} envelope. After migration backfills SecretType
        // to "personal-access-token", legacy rows must still resolve correctly.
        //
        // This test simulates that scenario by writing a version row with a raw
        // encrypted PAT string (bypassing the new CreateAsync which always uses JSON).

        var tempDbPath = Path.Combine(Path.GetTempPath(), $"legacy-pat-test-{Guid.NewGuid():N}.db");
        var kek = CreateTestKek();
        try
        {
            // Phase 1: create schema and seed a legacy PAT version (raw encrypted string).
            var connection1 = new SqliteConnection($"Data Source={tempDbPath}");
            connection1.Open();
            using (var scope1 = CreateScopeWithConnection(connection1, kek))
            {
                var ctx1 = scope1.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
                ctx1.Database.EnsureCreated();

                // Simulate the old provider's behavior: encrypt a raw PAT string.
                var encryption = new AesGcmEnvelopeEncryption(new TestKeyEncryptionKeySource(kek));
                var legacyPatValue = "ghp_legacyTokenValue123!@#";
                var (encryptedValue, nonce, wrappedDek) = encryption.Encrypt(legacyPatValue);

                // Insert a NamedSecret with PAT type and a version containing the raw encrypted value.
                var now = DateTimeOffset.UtcNow;
                var secretEntity = new NamedSecretEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "legacy-pat-secret",
                    SecretType = Domain.Secrets.SecretType.PersonalAccessToken,
                    CreatedAt = now,
                };
                ctx1.NamedSecrets.Add(secretEntity);
                ctx1.SecretVersions.Add(new SecretVersionEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    NamedSecretId = secretEntity.Id,
                    VersionNumber = 1,
                    EncryptedValue = encryptedValue,
                    Nonce = nonce,
                    WrappedDek = wrappedDek,
                    CreatedAt = now,
                });
                await ctx1.SaveChangesAsync();
            }
            connection1.Dispose();

            // Phase 2: resolve through the new provider — must work compatibly.
            var connection2 = new SqliteConnection($"Data Source={tempDbPath}");
            connection2.Open();
            using (var scope2 = CreateScopeWithConnection(connection2, kek))
            {
                var store = scope2.ServiceProvider.GetRequiredService<ISecretStore>();
                var result = await store.ResolveAsync("legacy-pat-secret", cancellationToken: CancellationToken.None);

                Assert.NotNull(result);
                Assert.IsType<PersonalAccessTokenPayload>(result);
                Assert.Equal("ghp_legacyTokenValue123!@#", ((PersonalAccessTokenPayload)result!).Value);

                // Also verify ListVersionsAsync works.
                var manager = scope2.ServiceProvider.GetRequiredService<ISecretManager>();
                var versions = await manager.ListVersionsAsync("legacy-pat-secret", CancellationToken.None);
                Assert.NotNull(versions);
                Assert.Single(versions);
                Assert.Equal(Domain.Secrets.SecretType.PersonalAccessToken, versions[0].SecretType);
                Assert.Null(versions[0].PublicKey); // PAT secrets have no public key
            }
            connection2.Dispose();
        }
        finally
        {
            if (File.Exists(tempDbPath))
            {
                File.Delete(tempDbPath);
            }
        }
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

    public Task<SecretPayload?> ResolveAsync(string name, int? version = null, CancellationToken cancellationToken = default)
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

    public Task<bool> CreateAsync(string name, SecretPayload payload, CancellationToken cancellationToken = default)
        => _inner.CreateAsync(name, payload, cancellationToken);

    public Task<int?> CreateVersionAsync(string name, SecretPayload payload, CancellationToken cancellationToken = default)
        => _inner.CreateVersionAsync(name, payload, cancellationToken);

    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(name, cancellationToken);

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
