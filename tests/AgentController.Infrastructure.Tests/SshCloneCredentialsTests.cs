using System.Diagnostics;
using AgentController.Domain;
using AgentController.Domain.Secrets;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

public sealed class SshCloneCredentialsTests
{
    [Fact]
    public async Task Resolver_UsesPinnedVersionAndRequiresSshKeyPayload()
    {
        var payload = new SshKeyPayload
        {
            PrivateKey = "private-key-value",
            PublicKey = "ssh-ed25519 public-key-value",
        };
        var store = new RecordingSecretStore(payload);
        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(store);
        await using var provider = services.BuildServiceProvider();
        var resolver = new RepositoryCloneCredentialResolver(
            provider.GetRequiredService<IServiceScopeFactory>()
        );

        var result = await resolver.ResolveSshKeyAsync(
            SecretReference.ByNameAndVersion("clone-key", 3),
            CancellationToken.None
        );

        Assert.Same(payload, result);
        Assert.Equal("clone-key", store.ResolvedName);
        Assert.Equal(3, store.ResolvedVersion);
    }

    [Fact]
    public async Task Resolver_RejectsNonSshPayloadWithoutExposingItsValue()
    {
        const string personalAccessToken = "pat-must-not-leak";
        var store = new RecordingSecretStore(
            new PersonalAccessTokenPayload { Value = personalAccessToken }
        );
        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(store);
        await using var provider = services.BuildServiceProvider();
        var resolver = new RepositoryCloneCredentialResolver(
            provider.GetRequiredService<IServiceScopeFactory>()
        );

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            resolver.ResolveSshKeyAsync(SecretReference.ByName("clone-key"), CancellationToken.None)
        );

        Assert.Contains("not an SSH-key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(personalAccessToken, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_RejectsNonSshCredentialBeforeRemoteProbe()
    {
        const string personalAccessToken = "wrong-type-pat-must-not-leak";
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
        var reference = SecretReference.ByNameAndVersion("deploy-key", 2);
        var repository = new RepositoryProfile
        {
            Key = "ssh-repo",
            CloneUrl = "git@example.test:owner/repo.git",
            SshKeyReference = reference,
        };

        var result = await provider.CheckClonePreflightAsync(
            new RepositorySpec
            {
                RepoKey = repository.Key,
                CloneUrl = repository.CloneUrl,
                Profile = repository,
            },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Equal(ClonePreflightFailureCode.CredentialTypeMismatch, result.FailureCode);
        Assert.Equal(RepositoryCloneCredentialSource.SshKey, result.CredentialSource);
        Assert.Equal(reference, result.CredentialReference);
        Assert.DoesNotContain(personalAccessToken, result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            personalAccessToken,
            string.Join(Environment.NewLine, logger.Messages),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task SshCredentials_MaterializeRestrictiveFilesAndCleanUp()
    {
        const string privateKey = "private-key-must-not-appear-in-command";
        string directoryPath;

        using (
            var credentials = await GitSshCredentials.CreateAsync(
                new SshKeyPayload
                {
                    PrivateKey = privateKey,
                    PublicKey = "ssh-ed25519 public-key-value",
                },
                CancellationToken.None
            )
        )
        {
            var keyPath = Assert.IsType<string>(
                credentials.Environment[GitSshCredentials.KeyFileEnvironmentVariable]
            );
            var knownHostsPath = Assert.IsType<string>(
                credentials.Environment[GitSshCredentials.KnownHostsFileEnvironmentVariable]
            );
            var sshCommand = Assert.IsType<string>(credentials.Environment["GIT_SSH_COMMAND"]);
            directoryPath = Path.GetDirectoryName(keyPath)!;

            Assert.Equal(privateKey, File.ReadAllText(keyPath));
            Assert.Equal("ssh-ed25519 public-key-value", File.ReadAllText($"{keyPath}.pub"));
            Assert.Empty(File.ReadAllText(knownHostsPath));
            Assert.DoesNotContain(privateKey, sshCommand, StringComparison.Ordinal);

            var wrapperPath = Path.Combine(
                directoryPath,
                OperatingSystem.IsWindows() ? "ssh-command.cmd" : "ssh-command.sh"
            );
            var wrapper = File.ReadAllText(wrapperPath);
            Assert.DoesNotContain(privateKey, wrapper, StringComparison.Ordinal);
            Assert.Contains("IdentitiesOnly=yes", wrapper, StringComparison.Ordinal);
            Assert.Contains("BatchMode=yes", wrapper, StringComparison.Ordinal);
            Assert.Contains("StrictHostKeyChecking=accept-new", wrapper, StringComparison.Ordinal);
            Assert.Contains("UserKnownHostsFile", wrapper, StringComparison.Ordinal);

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(directoryPath)
                );
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(keyPath)
                );
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode($"{keyPath}.pub")
                );
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(knownHostsPath)
                );
            }
        }

        Assert.False(Directory.Exists(directoryPath));
    }

    [Fact]
    public async Task SshCredentials_LoadPassphraseProtectedKeyIntoEphemeralAgent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string passphrase = "agent-passphrase-must-not-leak";
        var fixtureDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ssh-credential-fixture-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(fixtureDirectory);
        var fixtureKeyPath = Path.Combine(fixtureDirectory, "fixture-key");

        try
        {
            await RunProcessAsync(
                "ssh-keygen",
                ["-q", "-t", "ed25519", "-N", passphrase, "-C", string.Empty, "-f", fixtureKeyPath]
            );
            var privateKey = await File.ReadAllTextAsync(fixtureKeyPath);
            var publicKey = await File.ReadAllTextAsync($"{fixtureKeyPath}.pub");
            string materialDirectory;

            using (
                var credentials = await GitSshCredentials.CreateAsync(
                    new SshKeyPayload
                    {
                        PrivateKey = privateKey,
                        PublicKey = publicKey,
                        Passphrase = passphrase,
                    },
                    CancellationToken.None
                )
            )
            {
                var materializedKeyPath = Assert.IsType<string>(
                    credentials.Environment[GitSshCredentials.KeyFileEnvironmentVariable]
                );
                var agentSocket = Assert.IsType<string>(credentials.Environment["SSH_AUTH_SOCK"]);
                materialDirectory = Path.GetDirectoryName(materializedKeyPath)!;

                Assert.True(File.Exists(agentSocket));
                Assert.False(File.Exists(Path.Combine(materialDirectory, "passphrase")));
                Assert.False(File.Exists(Path.Combine(materialDirectory, "ssh-askpass.sh")));
                Assert.DoesNotContain(
                    passphrase,
                    File.ReadAllText(Path.Combine(materialDirectory, "ssh-command.sh")),
                    StringComparison.Ordinal
                );
            }

            Assert.False(Directory.Exists(materialDirectory));
        }
        finally
        {
            try
            {
                Directory.Delete(fixtureDirectory, recursive: true);
            }
            catch
            {
                // Best effort test cleanup.
            }
        }
    }

    [Fact]
    public async Task CloneAsync_ResolvesStoredSshKeyWithoutLeakingMaterial()
    {
        const string privateKey = "stored-private-key-must-not-leak";
        var store = new RecordingSecretStore(
            new SshKeyPayload
            {
                PrivateKey = privateKey,
                PublicKey = "ssh-ed25519 public-key-value",
                Passphrase = null,
            }
        );
        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(store);
        await using var serviceProvider = services.BuildServiceProvider();
        var resolver = new RepositoryCloneCredentialResolver(
            serviceProvider.GetRequiredService<IServiceScopeFactory>()
        );
        var logger = new RecordingLogger<LocalGitSourceControlProvider>();
        var provider = new LocalGitSourceControlProvider(
            new FixedOptionsMonitor<AgentControllerOptions>(new AgentControllerOptions()),
            logger,
            resolver
        );
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ssh-clone-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var repository = new RepositoryProfile
            {
                Key = "private-repo",
                CloneUrl = "ssh://git@127.0.0.1:1/owner/repo.git",
                SshKeyReference = SecretReference.ByNameAndVersion("clone-key", 5),
            };
            var spec = new RepositorySpec
            {
                RepoKey = repository.Key,
                CloneUrl = repository.CloneUrl,
                Profile = repository,
            };
            var environment = new EnvironmentHandle { RootPath = tempRoot, Status = "created" };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.CloneAsync(spec, environment, CancellationToken.None)
            );

            Assert.Equal("clone-key", store.ResolvedName);
            Assert.Equal(5, store.ResolvedVersion);
            Assert.DoesNotContain(privateKey, exception.Message, StringComparison.Ordinal);

            var logs = string.Join(Environment.NewLine, logger.Messages);
            Assert.DoesNotContain(privateKey, logs, StringComparison.Ordinal);
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

    private static async Task RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(standardOutput, standardError);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} failed with exit code {process.ExitCode}."
            );
        }
    }

    private sealed class RecordingSecretStore(SecretPayload payload) : ISecretStore
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
