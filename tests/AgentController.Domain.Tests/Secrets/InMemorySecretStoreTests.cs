using AgentController.Domain.Secrets;

namespace AgentController.Domain.Tests.Secrets;

public class InMemorySecretStoreTests
{
    // ---- SSH-key payload creation and resolution ----

    [Fact]
    public async Task CreateAsync_WithSshKeyPayload_StoresAndResolvesLatest()
    {
        var store = new InMemorySecretStore();
        var payload = new SshKeyPayload
        {
            PrivateKey = "-----BEGIN PRIVATE KEY-----\nprivate\n-----END PRIVATE KEY-----",
            PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI test-key",
            Passphrase = null,
        };

        var created = await store.CreateAsync("my-ssh-key", payload);
        Assert.True(created);

        var resolved = await store.ResolveAsync("my-ssh-key");
        Assert.NotNull(resolved);
        var sshPayload = Assert.IsType<SshKeyPayload>(resolved);
        Assert.Equal(payload.PrivateKey, sshPayload.PrivateKey);
        Assert.Equal(payload.PublicKey, sshPayload.PublicKey);
        Assert.Null(sshPayload.Passphrase);
    }

    [Fact]
    public async Task CreateAsync_WithSshKeyPayloadAndPassphrase_ResolvesPassphrase()
    {
        var store = new InMemorySecretStore();
        var payload = new SshKeyPayload
        {
            PrivateKey = "-----BEGIN ENCRYPTED PRIVATE KEY-----\nencrypted\n-----END ENCRYPTED PRIVATE KEY-----",
            PublicKey = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQ test-key",
            Passphrase = "hunter2",
        };

        var created = await store.CreateAsync("my-encrypted-key", payload);
        Assert.True(created);

        var resolved = await store.ResolveAsync("my-encrypted-key");
        Assert.NotNull(resolved);
        var sshPayload = Assert.IsType<SshKeyPayload>(resolved);
        Assert.Equal(payload.PrivateKey, sshPayload.PrivateKey);
        Assert.Equal(payload.PublicKey, sshPayload.PublicKey);
        Assert.Equal("hunter2", sshPayload.Passphrase);
    }

    [Fact]
    public async Task ResolveAsync_SshKeyByExactVersion_ReturnsPinnedPayload()
    {
        var store = new InMemorySecretStore();

        // V1
        await store.CreateAsync("deploy-key", new SshKeyPayload
        {
            PrivateKey = "private-v1",
            PublicKey = "public-v1",
            Passphrase = null,
        });

        // V2
        await store.CreateVersionAsync("deploy-key", new SshKeyPayload
        {
            PrivateKey = "private-v2",
            PublicKey = "public-v2",
            Passphrase = "pass-v2",
        });

        // Resolve latest — should be V2
        var latest = await store.ResolveAsync("deploy-key");
        Assert.NotNull(latest);
        var latestSsh = Assert.IsType<SshKeyPayload>(latest);
        Assert.Equal("private-v2", latestSsh.PrivateKey);
        Assert.Equal("public-v2", latestSsh.PublicKey);
        Assert.Equal("pass-v2", latestSsh.Passphrase);

        // Resolve pinned V1
        var v1 = await store.ResolveAsync("deploy-key", version: 1);
        Assert.NotNull(v1);
        var v1Ssh = Assert.IsType<SshKeyPayload>(v1);
        Assert.Equal("private-v1", v1Ssh.PrivateKey);
        Assert.Equal("public-v1", v1Ssh.PublicKey);
        Assert.Null(v1Ssh.Passphrase);

        // Resolve pinned V2
        var v2 = await store.ResolveAsync("deploy-key", version: 2);
        Assert.NotNull(v2);
        var v2Ssh = Assert.IsType<SshKeyPayload>(v2);
        Assert.Equal("private-v2", v2Ssh.PrivateKey);
    }

    [Fact]
    public async Task ResolveAsync_SshKeyNonexistentVersion_ReturnsNull()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("key", new SshKeyPayload
        {
            PrivateKey = "private",
            PublicKey = "public",
        });

        var result = await store.ResolveAsync("key", version: 99);
        Assert.Null(result);
    }

    // ---- PAT payload creation and resolution (regression) ----

    [Fact]
    public async Task CreateAsync_WithPatPayload_StoresAndResolvesLatest()
    {
        var store = new InMemorySecretStore();
        var created = await store.CreateAsync("my-pat", (PersonalAccessTokenPayload)"pat-value-123");
        Assert.True(created);

        var resolved = await store.ResolveAsync("my-pat");
        Assert.NotNull(resolved);
        var patPayload = Assert.IsType<PersonalAccessTokenPayload>(resolved);
        Assert.Equal("pat-value-123", patPayload.Value);
    }

    [Fact]
    public async Task ResolveAsync_PatByExactVersion_ReturnsPinnedPayload()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("pat", (PersonalAccessTokenPayload)"v1");

        await store.CreateVersionAsync("pat", (PersonalAccessTokenPayload)"v2");
        await store.CreateVersionAsync("pat", (PersonalAccessTokenPayload)"v3");

        var v2 = await store.ResolveAsync("pat", version: 2);
        Assert.NotNull(v2);
        Assert.Equal("v2", Assert.IsType<PersonalAccessTokenPayload>(v2).Value);

        var v3 = await store.ResolveAsync("pat", version: 3);
        Assert.NotNull(v3);
        Assert.Equal("v3", Assert.IsType<PersonalAccessTokenPayload>(v3).Value);
    }

    [Fact]
    public async Task ResolveAsync_PatNonexistentVersion_ReturnsNull()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("pat", (PersonalAccessTokenPayload)"v1");

        var result = await store.ResolveAsync("pat", version: 99);
        Assert.Null(result);
    }

    // ---- Public-key-only metadata exposure ----

    [Fact]
    public async Task ListVersionsAsync_SshKey_ExposesPublicKeyButNotPrivateKeyOrPassphrase()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("ssh-key", new SshKeyPayload
        {
            PrivateKey = "---PRIVATE---",
            PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI public-key",
            Passphrase = "secret-passphrase",
        });

        var versions = await store.ListVersionsAsync("ssh-key");
        Assert.NotNull(versions);
        var info = Assert.Single(versions);

        Assert.Equal(SecretType.SshKey, info.SecretType);
        Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI public-key", info.PublicKey);

        // Private key and passphrase must NOT leak into metadata.
        // We can only verify by absense of such properties on SecretVersionInfo.
        // The record shape guarantees this at compile time.
    }

    [Fact]
    public async Task ListVersionsAsync_Pat_PublicKeyIsNull()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("pat", (PersonalAccessTokenPayload)"supersecret");

        var versions = await store.ListVersionsAsync("pat");
        Assert.NotNull(versions);
        var info = Assert.Single(versions);

        Assert.Equal(SecretType.PersonalAccessToken, info.SecretType);
        Assert.Null(info.PublicKey);
    }

    [Fact]
    public async Task ListVersionsAsync_SshKeyMultipleVersions_EachExposesPublicKey()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("key", new SshKeyPayload
        {
            PrivateKey = "private-v1",
            PublicKey = "public-v1",
        });
        await store.CreateVersionAsync("key", new SshKeyPayload
        {
            PrivateKey = "private-v2",
            PublicKey = "public-v2",
        });
        await store.CreateVersionAsync("key", new SshKeyPayload
        {
            PrivateKey = "private-v3",
            PublicKey = "public-v3",
        });

        var versions = await store.ListVersionsAsync("key");
        Assert.NotNull(versions);
        Assert.Equal(3, versions.Count);

        Assert.Equal("public-v1", versions[0].PublicKey);
        Assert.Equal("public-v2", versions[1].PublicKey);
        Assert.Equal("public-v3", versions[2].PublicKey);

        // Secret type is consistent across versions
        Assert.All(versions, v => Assert.Equal(SecretType.SshKey, v.SecretType));
    }

    // ---- SecretInfo metadata ----

    [Fact]
    public async Task ListAsync_ReturnsSecretTypeInMetadata()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("pat-secret", (PersonalAccessTokenPayload)"pat");
        await store.CreateAsync("ssh-secret", new SshKeyPayload
        {
            PrivateKey = "priv",
            PublicKey = "pub",
        });

        var infos = await store.ListAsync();
        Assert.Equal(2, infos.Count);

        var patInfo = Assert.Single(infos, i => i.Name == "pat-secret");
        Assert.Equal(SecretType.PersonalAccessToken, patInfo.SecretType);

        var sshInfo = Assert.Single(infos, i => i.Name == "ssh-secret");
        Assert.Equal(SecretType.SshKey, sshInfo.SecretType);
    }

    // ---- Type-mismatch enforcement ----

    [Fact]
    public async Task CreateVersionAsync_PatPayloadOnSshKeySecret_ThrowsInvalidOperationException()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("ssh-key", new SshKeyPayload
        {
            PrivateKey = "private",
            PublicKey = "public",
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateVersionAsync("ssh-key", (PersonalAccessTokenPayload)"should-not-work"));

        Assert.Contains("personal-access-token", ex.Message);
        Assert.Contains("ssh-key", ex.Message);
        Assert.Contains("immutable", ex.Message);
    }

    [Fact]
    public async Task CreateVersionAsync_SshKeyPayloadOnPatSecret_ThrowsInvalidOperationException()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("my-pat", (PersonalAccessTokenPayload)"original");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateVersionAsync("my-pat", new SshKeyPayload
            {
                PrivateKey = "private",
                PublicKey = "public",
            }));

        Assert.Contains("ssh-key", ex.Message);
        Assert.Contains("personal-access-token", ex.Message);
        Assert.Contains("immutable", ex.Message);
    }

    [Fact]
    public async Task CreateVersionAsync_MatchingTypeOnPatSecret_Succeeds()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("pat", (PersonalAccessTokenPayload)"v1");

        var version = await store.CreateVersionAsync("pat", (PersonalAccessTokenPayload)"v2");
        Assert.NotNull(version);
        Assert.Equal(2, version);

        var resolved = await store.ResolveAsync("pat");
        Assert.NotNull(resolved);
        Assert.Equal("v2", Assert.IsType<PersonalAccessTokenPayload>(resolved).Value);
    }

    [Fact]
    public async Task CreateVersionAsync_MatchingTypeOnSshKeySecret_Succeeds()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("key", new SshKeyPayload
        {
            PrivateKey = "private-v1",
            PublicKey = "public-v1",
        });

        var version = await store.CreateVersionAsync("key", new SshKeyPayload
        {
            PrivateKey = "private-v2",
            PublicKey = "public-v2",
            Passphrase = "new-passphrase",
        });
        Assert.NotNull(version);
        Assert.Equal(2, version);

        var resolved = await store.ResolveAsync("key");
        Assert.NotNull(resolved);
        var sshPayload = Assert.IsType<SshKeyPayload>(resolved);
        Assert.Equal("private-v2", sshPayload.PrivateKey);
        Assert.Equal("public-v2", sshPayload.PublicKey);
        Assert.Equal("new-passphrase", sshPayload.Passphrase);
    }

    // ---- Standard lifecycle: Create / List / Delete ----

    [Fact]
    public async Task CreateAsync_DuplicateName_ReturnsFalse()
    {
        var store = new InMemorySecretStore();
        Assert.True(await store.CreateAsync("dup", (PersonalAccessTokenPayload)"v1"));
        Assert.False(await store.CreateAsync("dup", (PersonalAccessTokenPayload)"v2"));
    }

    [Fact]
    public async Task CreateVersionAsync_NonexistentSecret_ReturnsNull()
    {
        var store = new InMemorySecretStore();
        var result = await store.CreateVersionAsync("nope", (PersonalAccessTokenPayload)"v1");
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_ExistingSecret_RemovesIt()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("to-delete", (PersonalAccessTokenPayload)"v1");
        Assert.True(await store.DeleteAsync("to-delete"));

        var resolved = await store.ResolveAsync("to-delete");
        Assert.Null(resolved);
    }

    [Fact]
    public async Task DeleteAsync_NonexistentSecret_ReturnsFalse()
    {
        var store = new InMemorySecretStore();
        Assert.False(await store.DeleteAsync("nope"));
    }

    [Fact]
    public async Task ListAsync_ReturnsSecretsOrderedByName()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("z-secret", (PersonalAccessTokenPayload)"z");
        await store.CreateAsync("a-secret", (PersonalAccessTokenPayload)"a");
        await store.CreateAsync("m-secret", (PersonalAccessTokenPayload)"m");

        var list = await store.ListAsync();
        Assert.Equal(3, list.Count);
        Assert.Equal("a-secret", list[0].Name);
        Assert.Equal("m-secret", list[1].Name);
        Assert.Equal("z-secret", list[2].Name);
    }

    [Fact]
    public async Task ListVersionsAsync_NonexistentSecret_ReturnsNull()
    {
        var store = new InMemorySecretStore();
        var versions = await store.ListVersionsAsync("nope");
        Assert.Null(versions);
    }

    [Fact]
    public async Task ListVersionsAsync_ReturnsVersionsOldestFirst()
    {
        var store = new InMemorySecretStore();
        await store.CreateAsync("pat", (PersonalAccessTokenPayload)"v1");
        await store.CreateVersionAsync("pat", (PersonalAccessTokenPayload)"v2");
        await store.CreateVersionAsync("pat", (PersonalAccessTokenPayload)"v3");

        var versions = await store.ListVersionsAsync("pat");
        Assert.NotNull(versions);
        Assert.Equal(3, versions.Count);
        Assert.Equal(1, versions[0].Version);
        Assert.Equal(2, versions[1].Version);
        Assert.Equal(3, versions[2].Version);
    }

    [Fact]
    public async Task ResolveAsync_NoVersions_ReturnsNull()
    {
        // Should not happen under normal use, but test defensive behavior.
        var store = new InMemorySecretStore();
        var result = await store.ResolveAsync("nonexistent");
        Assert.Null(result);
    }

    // ---- Cancellation ----

    [Fact]
    public async Task ResolveAsync_Cancelled_ThrowsOperationCanceledException()
    {
        var store = new InMemorySecretStore();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ResolveAsync("anything", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task CreateAsync_Cancelled_ThrowsOperationCanceledException()
    {
        var store = new InMemorySecretStore();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CreateAsync("x", (PersonalAccessTokenPayload)"v1", cts.Token));
    }

    [Fact]
    public async Task CreateVersionAsync_Cancelled_ThrowsOperationCanceledException()
    {
        var store = new InMemorySecretStore();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CreateVersionAsync("x", (PersonalAccessTokenPayload)"v1", cts.Token));
    }
}
