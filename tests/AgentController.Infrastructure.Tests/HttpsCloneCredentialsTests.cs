using AgentController.Domain;
using AgentController.Domain.Secrets;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

public sealed class HttpsCloneCredentialsTests
{
    [Fact]
    public async Task Resolver_UsesPinnedVersionAndRequiresPatPayload()
    {
        var store = new RecordingSecretStore(
            new PersonalAccessTokenPayload { Value = "version-two-token" }
        );
        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(store);
        await using var provider = services.BuildServiceProvider();
        var resolver = new RepositoryCloneCredentialResolver(
            provider.GetRequiredService<IServiceScopeFactory>()
        );

        var value = await resolver.ResolvePersonalAccessTokenAsync(
            SecretReference.ByNameAndVersion("clone-pat", 2),
            CancellationToken.None
        );

        Assert.Equal("version-two-token", value);
        Assert.Equal("clone-pat", store.ResolvedName);
        Assert.Equal(2, store.ResolvedVersion);
    }

    [Fact]
    public async Task Resolver_RejectsNonPatPayloadWithoutExposingItsValue()
    {
        const string privateKey = "private-key-must-not-leak";
        var store = new RecordingSecretStore(
            new SshKeyPayload { PrivateKey = privateKey, PublicKey = "ssh-ed25519 public" }
        );
        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(store);
        await using var provider = services.BuildServiceProvider();
        var resolver = new RepositoryCloneCredentialResolver(
            provider.GetRequiredService<IServiceScopeFactory>()
        );

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            resolver.ResolvePersonalAccessTokenAsync(
                SecretReference.ByName("clone-key"),
                CancellationToken.None
            )
        );

        Assert.Contains("not a personal-access-token", exception.Message);
        Assert.DoesNotContain(privateKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AskPassCredentials_KeepPatOutOfHelperAndCleanUpPrivateFiles()
    {
        const string personalAccessToken = "pat-must-not-appear-in-helper";
        string directoryPath;

        using (var credentials = GitAskPassCredentials.Create(personalAccessToken))
        {
            var tokenPath = Assert.IsType<string>(
                credentials.Environment[GitAskPassCredentials.TokenFileEnvironmentVariable]
            );
            var askPassPath = Assert.IsType<string>(credentials.Environment["GIT_ASKPASS"]);
            directoryPath = Path.GetDirectoryName(tokenPath)!;

            Assert.Equal(personalAccessToken, File.ReadAllText(tokenPath));
            Assert.DoesNotContain(
                personalAccessToken,
                File.ReadAllText(askPassPath),
                StringComparison.Ordinal
            );
            Assert.Equal("force", credentials.Environment["GIT_ASKPASS_REQUIRE"]);

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(tokenPath)
                );
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(askPassPath)
                );
            }
        }

        Assert.False(Directory.Exists(directoryPath));
    }

    [Fact]
    public async Task CloneAsync_ResolvesConnectionPatAndDoesNotLeakCredentials()
    {
        const string personalAccessToken = "managed-pat-must-not-leak";
        const string embeddedPassword = "embedded-password-must-not-leak";
        var cloneUrl = $"https://embedded-user:{embeddedPassword}@127.0.0.1:1/repo.git";
        var store = new RecordingSecretStore(
            new PersonalAccessTokenPayload { Value = personalAccessToken }
        );
        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(store);
        await using var serviceProvider = services.BuildServiceProvider();
        var credentialResolver = new RepositoryCloneCredentialResolver(
            serviceProvider.GetRequiredService<IServiceScopeFactory>()
        );
        var logger = new RecordingLogger<LocalGitSourceControlProvider>();
        var provider = new LocalGitSourceControlProvider(
            new FixedOptionsMonitor<AgentControllerOptions>(new AgentControllerOptions()),
            logger,
            credentialResolver
        );
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"https-clone-credentials-test-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(tempRoot);

        try
        {
            var repository = new RepositoryProfile
            {
                Key = "private-repo",
                CloneUrl = cloneUrl,
                RepositoryHostConnectionKey = "primary-host",
            };
            var connection = new ConnectionProfile
            {
                Key = "primary-host",
                ProviderSettings = new AzureDevOpsConnectionSettings
                {
                    PersonalAccessTokenReference = SecretReference.ByNameAndVersion("clone-pat", 4),
                },
            };
            var spec = new RepositorySpec
            {
                RepoKey = repository.Key,
                CloneUrl = repository.CloneUrl,
                Profile = repository,
                RepositoryConnection = connection,
            };
            var environment = new EnvironmentHandle { RootPath = tempRoot, Status = "created" };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.CloneAsync(spec, environment, CancellationToken.None)
            );

            Assert.Equal("clone-pat", store.ResolvedName);
            Assert.Equal(4, store.ResolvedVersion);
            Assert.DoesNotContain(personalAccessToken, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(embeddedPassword, exception.Message, StringComparison.Ordinal);

            var logs = string.Join(Environment.NewLine, logger.Messages);
            Assert.DoesNotContain(personalAccessToken, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(embeddedPassword, logs, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best effort test cleanup.
            }
        }
    }

    [Fact]
    public async Task Preflight_MissingPinnedPat_ReturnsActionableCredentialFailure()
    {
        var store = new RecordingSecretStore(null);
        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(store);
        await using var serviceProvider = services.BuildServiceProvider();
        var provider = new LocalGitSourceControlProvider(
            new FixedOptionsMonitor<AgentControllerOptions>(new AgentControllerOptions()),
            new RecordingLogger<LocalGitSourceControlProvider>(),
            new RepositoryCloneCredentialResolver(
                serviceProvider.GetRequiredService<IServiceScopeFactory>()
            )
        );
        var reference = SecretReference.ByNameAndVersion("expired-clone-pat", 7);
        var repository = new RepositoryProfile
        {
            Key = "private-repo",
            CloneUrl = "https://example.test/owner/repo.git",
            RepositoryHostConnectionKey = "primary-host",
        };
        var connection = new ConnectionProfile
        {
            Key = "primary-host",
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                PersonalAccessTokenReference = reference,
            },
        };

        var result = await provider.CheckClonePreflightAsync(
            new RepositorySpec
            {
                RepoKey = repository.Key,
                CloneUrl = repository.CloneUrl,
                Profile = repository,
                RepositoryConnection = connection,
            },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Equal(ClonePreflightFailureCode.CredentialNotFound, result.FailureCode);
        Assert.Equal(reference, result.CredentialReference);
        Assert.Contains("version 7", result.Reason, StringComparison.Ordinal);
        Assert.Equal("expired-clone-pat", store.ResolvedName);
        Assert.Equal(7, store.ResolvedVersion);
    }

    [Fact]
    public async Task Preflight_ResolvesPatBeforeCredentialedRemoteProbe()
    {
        const string personalAccessToken = "preflight-pat-must-not-leak";
        var store = new RecordingSecretStore(
            new PersonalAccessTokenPayload { Value = personalAccessToken }
        );
        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(store);
        await using var serviceProvider = services.BuildServiceProvider();
        var logger = new RecordingLogger<LocalGitSourceControlProvider>();
        var provider = new LocalGitSourceControlProvider(
            new FixedOptionsMonitor<AgentControllerOptions>(new AgentControllerOptions()),
            logger,
            new RepositoryCloneCredentialResolver(
                serviceProvider.GetRequiredService<IServiceScopeFactory>()
            )
        );
        var reference = SecretReference.ByNameAndVersion("clone-pat", 3);
        var repository = new RepositoryProfile
        {
            Key = "private-repo",
            CloneUrl = "https://127.0.0.1:1/owner/repo.git",
            RepositoryHostConnectionKey = "primary-host",
        };
        var connection = new ConnectionProfile
        {
            Key = "primary-host",
            ProviderSettings = new AzureDevOpsConnectionSettings
            {
                PersonalAccessTokenReference = reference,
            },
        };

        var result = await provider.CheckClonePreflightAsync(
            new RepositorySpec
            {
                RepoKey = repository.Key,
                CloneUrl = repository.CloneUrl,
                Profile = repository,
                RepositoryConnection = connection,
            },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Equal(ClonePreflightFailureCode.RemoteUnreachable, result.FailureCode);
        Assert.Equal(reference, result.CredentialReference);
        Assert.Equal("clone-pat", store.ResolvedName);
        Assert.Equal(3, store.ResolvedVersion);
        Assert.DoesNotContain(personalAccessToken, result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            personalAccessToken,
            string.Join(Environment.NewLine, logger.Messages),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void RemoveCredentialsFromCloneUrl_StripsUserInfo()
    {
        var result = LocalGitSourceControlProvider.RemoveCredentialsFromCloneUrl(
            "https://user:secret@example.test/owner/repo.git"
        );

        Assert.Equal("https://example.test/owner/repo.git", result);
        Assert.DoesNotContain("user", result, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result, StringComparison.Ordinal);
    }

    private sealed class RecordingSecretStore(SecretPayload? payload) : ISecretStore
    {
        public string? ResolvedName { get; private set; }

        public int? ResolvedVersion { get; private set; }

        public Task<SecretPayload?> ResolveAsync(
            string name,
            int? version = null,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedName = name;
            ResolvedVersion = version;
            return Task.FromResult<SecretPayload?>(payload);
        }
    }

    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose() { }
    }
}
