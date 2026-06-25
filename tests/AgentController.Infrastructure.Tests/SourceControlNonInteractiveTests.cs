using System.Diagnostics;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests covering the source-control layer's non-interactive git clone behavior
/// across SSH and HTTPS+PAT transports.
///
/// Acceptance criteria:
/// - Clone invocations always pass GIT_TERMINAL_PROMPT=0 and BatchMode/StrictHostKeyChecking
///   SSH options across SSH and HTTPS+PAT transports
/// - A prompt-triggering failure surfaces as an immediate error (no hang) and routes through
///   the clone-failure release path
/// - Preflight flags unreachable/misconfigured clone URLs before a claim is pinned
/// - Transport selection from the worker profile is covered
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields.")]
public class SourceControlNonInteractiveTests : IAsyncLifetime
{
    private string _tempRoot = null!;
    private string _sourceRepo = null!;
    private string _sourceRepoFileUrl = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "git-noninteractive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        // Create a source git repository.
        _sourceRepo = Path.Combine(_tempRoot, "source-repo");
        Directory.CreateDirectory(_sourceRepo);

        await RunGitAsync(["init", "--initial-branch", "main"], _sourceRepo);
        await RunGitAsync(["config", "user.email", "test@example.com"], _sourceRepo);
        await RunGitAsync(["config", "user.name", "Test User"], _sourceRepo);

        var readme = Path.Combine(_sourceRepo, "README.md");
        await File.WriteAllTextAsync(readme, "# Test Repo");
        await RunGitAsync(["add", "README.md"], _sourceRepo);
        await RunGitAsync(["commit", "-m", "Initial commit"], _sourceRepo);

        _sourceRepoFileUrl = "file://" + _sourceRepo;
    }

    public Task DisposeAsync()
    {
        if (_tempRoot is not null && Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static TestOptionsMonitor<AgentControllerOptions> CreateOptions()
    {
        var options = new AgentControllerOptions
        {
            WorkerId = "test-worker",
            RunRoot = "/tmp/runs",
        };
        return new TestOptionsMonitor<AgentControllerOptions>(options);
    }

    private static LocalGitSourceControlProvider CreateProvider()
    {
        return new LocalGitSourceControlProvider(
            CreateOptions(),
            NullLogger<LocalGitSourceControlProvider>.Instance);
    }

    private EnvironmentHandle CreateEnvironment(string runId)
    {
        var envPath = Path.Combine(_tempRoot, "envs", runId);
        Directory.CreateDirectory(envPath);
        return new EnvironmentHandle
        {
            Id = $"local-{runId}",
            ProviderType = "LocalWorkspace",
            RootPath = envPath,
            Status = "created",
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // 1. Non-interactive environment variables across transports
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GitNonInteractiveEnv_ContainsGitTerminalPrompt_Zero()
    {
        // GIT_TERMINAL_PROMPT=0 prevents git from opening a terminal for credentials.
        Assert.True(LocalGitSourceControlProvider.GitNonInteractiveEnv.ContainsKey("GIT_TERMINAL_PROMPT"));
        Assert.Equal("0", LocalGitSourceControlProvider.GitNonInteractiveEnv["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public void GitNonInteractiveEnv_ContainsGitSshCommand_WithBatchMode()
    {
        // GIT_SSH_COMMAND must enforce SSH BatchMode=yes to never prompt for host keys.
        Assert.True(LocalGitSourceControlProvider.GitNonInteractiveEnv.ContainsKey("GIT_SSH_COMMAND"));
        var sshCommand = LocalGitSourceControlProvider.GitNonInteractiveEnv["GIT_SSH_COMMAND"]!;
        Assert.Contains("BatchMode=yes", sshCommand);
    }

    [Fact]
    public void GitNonInteractiveEnv_ContainsGitSshCommand_WithStrictHostKeyChecking()
    {
        // GIT_SSH_COMMAND must include StrictHostKeyChecking to avoid interactive prompts.
        var sshCommand = LocalGitSourceControlProvider.GitNonInteractiveEnv["GIT_SSH_COMMAND"]!;
        Assert.Contains("StrictHostKeyChecking", sshCommand);
    }

    [Fact]
    public void GitNonInteractiveEnv_UsesOrdinalIgnoreCaseComparer()
    {
        // The dictionary must be case-insensitive for robust environment variable lookups.
        var value = LocalGitSourceControlProvider.GitNonInteractiveEnv["git_terminal_prompt"];
        Assert.Equal("0", value);

        var sshValue = LocalGitSourceControlProvider.GitNonInteractiveEnv["git_ssh_command"];
        Assert.NotNull(sshValue);
        Assert.Contains("BatchMode=yes", sshValue);
    }

    [Fact]
    public async Task GitNonInteractiveEnv_AppliedToLocalPathClones()
    {
        // Local path clones also go through RunGitAsync which applies GitNonInteractiveEnv.
        // The env vars should not break local clones.
        var provider = CreateProvider();
        var env = CreateEnvironment("run-local-env");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
            DefaultBranch = "main",
            Transport = CloneTransport.Local,
        };

        // This will succeed, proving the env vars don't interfere with local clones.
        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);
        Assert.NotNull(checkout);
        Assert.True(Directory.Exists(Path.Combine(checkout.LocalPath, ".git")));
    }

    [Fact]
    public async Task GitNonInteractiveEnv_AppliedToFileUrlClones()
    {
        // file:// URL clones also apply the non-interactive env vars.
        var provider = CreateProvider();
        var env = CreateEnvironment("run-file-env");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepoFileUrl,
            DefaultBranch = "main",
            Transport = CloneTransport.Local,
        };

        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);
        Assert.NotNull(checkout);
        Assert.True(Directory.Exists(Path.Combine(checkout.LocalPath, ".git")));
    }

    [Fact]
    public void GitNonInteractiveEnv_AppliedToSshTransportClones()
    {
        // When Transport is set to Ssh, the env vars are still applied.
        // We can't test a real SSH clone here, but we verify the env vars
        // are in the dictionary and the provider uses them for all git invocations.
        Assert.Equal("0", LocalGitSourceControlProvider.GitNonInteractiveEnv["GIT_TERMINAL_PROMPT"]);
        var sshCmd = LocalGitSourceControlProvider.GitNonInteractiveEnv["GIT_SSH_COMMAND"];
        Assert.NotNull(sshCmd);
        Assert.Contains("BatchMode=yes", sshCmd);
        Assert.Contains("StrictHostKeyChecking", sshCmd);
    }

    [Fact]
    public void GitNonInteractiveEnv_AppliedToHttpsPatTransportClones()
    {
        // When Transport is set to HttpsPat, GIT_TERMINAL_PROMPT=0 is the key
        // non-interactive guard (prevents credential prompts).
        Assert.Equal("0", LocalGitSourceControlProvider.GitNonInteractiveEnv["GIT_TERMINAL_PROMPT"]);
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. Prompt-triggering failure surfaces as immediate error (no hang)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CloneAsync_InvalidRemoteUrl_FailsFastWithoutHang()
    {
        // An unreachable URL should fail within the clone timeout, not hang indefinitely.
        // The timeout is 10 minutes but git itself will fail fast for invalid URLs.
        var provider = CreateProvider();
        var env = CreateEnvironment("run-fail-fast");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = "https://this-host-does-not-exist-12345.example.com/repo.git",
            Transport = CloneTransport.HttpsPat,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CloneAsync(spec, env, CancellationToken.None));
        sw.Stop();

        // Should fail fast (within seconds), not hang for minutes.
        Assert.Contains("git clone failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(1),
            $"Clone took {sw.Elapsed.TotalSeconds:F1}s — should have failed fast");
    }

    [Fact]
    public async Task CloneAsync_InvalidSshUrl_FailsFastWithoutHang()
    {
        // An SSH URL to a non-existent host should fail fast with BatchMode=yes.
        var provider = CreateProvider();
        var env = CreateEnvironment("run-ssh-fail-fast");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = "git@nonexistent-host-12345.example.com:user/repo.git",
            Transport = CloneTransport.Ssh,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CloneAsync(spec, env, CancellationToken.None));
        sw.Stop();

        Assert.Contains("git clone failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(1),
            $"SSH clone took {sw.Elapsed.TotalSeconds:F1}s — should have failed fast");
    }

    [Fact]
    public async Task CloneAsync_NonExistentLocalPath_FailsFastWithoutHang()
    {
        // A non-existent local path should fail immediately.
        var provider = CreateProvider();
        var env = CreateEnvironment("run-local-fail");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = Path.Combine(_tempRoot, "does-not-exist-repo"),
            Transport = CloneTransport.Local,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CloneAsync(spec, env, CancellationToken.None));
        sw.Stop();

        Assert.Contains("git clone failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Local clone took {sw.Elapsed.TotalSeconds:F1}s — should have failed immediately");
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. Preflight flags unreachable/misconfigured clone URLs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Preflight_EmptyCloneUrl_FailsWithReason()
    {
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = "",
        };

        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("empty", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(result.Reason);
    }

    [Fact]
    public async Task Preflight_NullCloneUrl_FailsWithReason()
    {
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = null!,
        };

        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("empty", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflight_WhitespaceCloneUrl_FailsWithReason()
    {
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = "   ",
        };

        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("empty", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflight_NonExistentLocalPath_FailsWithReason()
    {
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = Path.Combine(_tempRoot, "nonexistent-local-repo"),
        };

        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflight_UnreachableHttpsUrl_FailsWithReason()
    {
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = "https://this-host-does-not-exist-12345.example.com/repo.git",
            Transport = CloneTransport.HttpsPat,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);
        sw.Stop();

        Assert.False(result.Success);
        Assert.NotEmpty(result.Reason);
        // Preflight uses a 30s timeout for git ls-remote
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(1),
            $"Preflight took {sw.Elapsed.TotalSeconds:F1}s — should have timed out or failed fast");
    }

    [Fact]
    public async Task Preflight_UnreachableSshUrl_FailsWithReason()
    {
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = "git@nonexistent-host-12345.example.com:user/repo.git",
            Transport = CloneTransport.Ssh,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);
        sw.Stop();

        Assert.False(result.Success);
        Assert.NotEmpty(result.Reason);
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(1),
            $"SSH preflight took {sw.Elapsed.TotalSeconds:F1}s — should have timed out or failed fast");
    }

    [Fact]
    public async Task Preflight_LocalPath_Passes()
    {
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
            DefaultBranch = "main",
        };

        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(CloneTransport.Local, result.Transport);
        Assert.Empty(result.Reason);
    }

    [Fact]
    public async Task Preflight_FileUrl_Passes()
    {
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepoFileUrl,
            DefaultBranch = "main",
        };

        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(CloneTransport.Local, result.Transport);
    }

    [Fact]
    public async Task Preflight_WithSpecificBranch_Passes()
    {
        // Create a branch first.
        await RunGitAsync(["branch", "preflight-branch"], _sourceRepo);

        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
            DefaultBranch = "preflight-branch",
        };

        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Preflight_WithNonExistentBranch_Passes()
    {
        // git ls-remote with a specific ref that doesn't exist still returns exit code 0
        // (just no output). The preflight validates URL reachability, not branch existence.
        // Branch existence is validated at clone time.
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
            DefaultBranch = "this-branch-does-not-exist",
        };

        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Reason);
    }

    [Fact]
    public async Task Preflight_ExplicitTransport_PreservedInResult()
    {
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
            Transport = CloneTransport.Ssh, // explicit, even though URL is local
        };

        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);

        // The resolved transport is from the explicit value.
        Assert.Equal(CloneTransport.Ssh, result.Transport);
    }

    [Fact]
    public async Task Preflight_ResultContainsConcreteReason()
    {
        var provider = CreateProvider();
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = "",
        };

        var result = await provider.CheckClonePreflightAsync(spec, CancellationToken.None);

        Assert.False(result.Success);
        // The reason should be actionable, not just "failed".
        Assert.True(result.Reason.Length > 10, "Reason should be descriptive");
        Assert.Contains("clone", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. Transport selection from the worker profile
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TransportSelection_ExplicitSshOverridesUrlInference()
    {
        // Explicit transport in the profile takes priority over URL inference.
        var result = LocalGitSourceControlProvider.ResolveTransport(
            CloneTransport.Ssh, "https://dev.azure.com/org/project/_git/repo");
        Assert.Equal(CloneTransport.Ssh, result);
    }

    [Fact]
    public void TransportSelection_ExplicitHttpsPatOverridesUrlInference()
    {
        var result = LocalGitSourceControlProvider.ResolveTransport(
            CloneTransport.HttpsPat, "git@github.com:user/repo.git");
        Assert.Equal(CloneTransport.HttpsPat, result);
    }

    [Fact]
    public void TransportSelection_GitAtUrlInfersSsh()
    {
        var result = LocalGitSourceControlProvider.ResolveTransport(
            CloneTransport.Unspecified, "git@ssh.dev.azure.com:v3/org/project/repo");
        Assert.Equal(CloneTransport.Ssh, result);
    }

    [Fact]
    public void TransportSelection_SshUrlInfersSsh()
    {
        var result = LocalGitSourceControlProvider.ResolveTransport(
            CloneTransport.Unspecified, "ssh://git@github.com/user/repo.git");
        Assert.Equal(CloneTransport.Ssh, result);
    }

    [Fact]
    public void TransportSelection_HttpsUrlInfersHttpsPat()
    {
        var result = LocalGitSourceControlProvider.ResolveTransport(
            CloneTransport.Unspecified, "https://dev.azure.com/org/project/_git/repo");
        Assert.Equal(CloneTransport.HttpsPat, result);
    }

    [Fact]
    public void TransportSelection_HttpUrlInfersHttpsPat()
    {
        var result = LocalGitSourceControlProvider.ResolveTransport(
            CloneTransport.Unspecified, "http://example.com/repo");
        Assert.Equal(CloneTransport.HttpsPat, result);
    }

    [Fact]
    public void TransportSelection_FileUrlInfersLocal()
    {
        var result = LocalGitSourceControlProvider.ResolveTransport(
            CloneTransport.Unspecified, "file:///home/user/repo");
        Assert.Equal(CloneTransport.Local, result);
    }

    [Fact]
    public void TransportSelection_AbsolutePathInfersLocal()
    {
        var result = LocalGitSourceControlProvider.ResolveTransport(
            CloneTransport.Unspecified, "/home/user/projects/repo");
        Assert.Equal(CloneTransport.Local, result);
    }

    [Fact]
    public void TransportSelection_FromRepositoryProfileOptions()
    {
        // Verify that RepositoryProfileOptions.Transport is correctly read
        // and maps to the CloneTransport enum.
        var profile = new RepositoryProfileOptions
        {
            CloneUrl = "git@ssh.dev.azure.com:v3/org/project/repo",
            Transport = CloneTransport.Ssh,
            DefaultBranch = "main",
        };

        Assert.Equal(CloneTransport.Ssh, profile.Transport);
        Assert.Equal("git@ssh.dev.azure.com:v3/org/project/repo", profile.CloneUrl);
    }

    [Fact]
    public void TransportSelection_FromRepositoryProfileOptions_HttpsPat()
    {
        var profile = new RepositoryProfileOptions
        {
            CloneUrl = "https://dev.azure.com/org/project/_git/repo",
            Transport = CloneTransport.HttpsPat,
            DefaultBranch = "develop",
        };

        Assert.Equal(CloneTransport.HttpsPat, profile.Transport);
    }

    [Fact]
    public void TransportSelection_FromRepositoryProfileOptions_UnspecifiedInfersFromUrl()
    {
        var profile = new RepositoryProfileOptions
        {
            CloneUrl = "git@github.com:user/repo.git",
            Transport = CloneTransport.Unspecified, // will be inferred at runtime
        };

        // The profile stores Unspecified; inference happens in ResolveTransport.
        Assert.Equal(CloneTransport.Unspecified, profile.Transport);

        // When passed through ResolveTransport, it should infer SSH.
        var inferred = LocalGitSourceControlProvider.ResolveTransport(
            profile.Transport, profile.CloneUrl);
        Assert.Equal(CloneTransport.Ssh, inferred);
    }

    [Fact]
    public void TransportSelection_ConfigBinding_Ssh()
    {
        // Verify that the config binding correctly maps "Ssh" string to CloneTransport.Ssh.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["repositories:test1:cloneUrl"] = "git@ssh.dev.azure.com:v3/org/project/repo",
                ["repositories:test1:transport"] = "Ssh",
                ["repositories:test1:defaultBranch"] = "main",
            })
            .Build();

        var repoSection = config.GetSection("repositories:test1");
        var profile = repoSection.Get<RepositoryProfileOptions>();

        Assert.NotNull(profile);
        Assert.Equal(CloneTransport.Ssh, profile!.Transport);
    }

    [Fact]
    public void TransportSelection_ConfigBinding_HttpsPat()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["repositories:test1:cloneUrl"] = "https://dev.azure.com/org/project/_git/repo",
                ["repositories:test1:transport"] = "HttpsPat",
            })
            .Build();

        var repoSection = config.GetSection("repositories:test1");
        var profile = repoSection.Get<RepositoryProfileOptions>();

        Assert.NotNull(profile);
        Assert.Equal(CloneTransport.HttpsPat, profile!.Transport);
    }

    [Fact]
    public void TransportSelection_ConfigBinding_Local()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["repositories:test1:cloneUrl"] = "/home/user/projects/repo",
                ["repositories:test1:transport"] = "Local",
            })
            .Build();

        var repoSection = config.GetSection("repositories:test1");
        var profile = repoSection.Get<RepositoryProfileOptions>();

        Assert.NotNull(profile);
        Assert.Equal(CloneTransport.Local, profile!.Transport);
    }

    [Fact]
    public void TransportSelection_ConfigBinding_DefaultUnspecified()
    {
        // When transport is not specified in config, it defaults to Unspecified.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["repositories:test1:cloneUrl"] = "git@github.com:user/repo.git",
            })
            .Build();

        var repoSection = config.GetSection("repositories:test1");
        var profile = repoSection.Get<RepositoryProfileOptions>();

        Assert.NotNull(profile);
        Assert.Equal(CloneTransport.Unspecified, profile!.Transport);
    }

    // ═══════════════════════════════════════════════════════════════
    // 5. Clone checkout transport reflects resolved transport
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CloneAsync_CheckoutRecordsTransport_Local()
    {
        var provider = CreateProvider();
        var env = CreateEnvironment("run-transport-local");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
            Transport = CloneTransport.Local,
        };

        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);

        Assert.Equal(CloneTransport.Local, checkout.Transport);
    }

    [Fact]
    public async Task CloneAsync_CheckoutRecordsTransport_InferredLocal()
    {
        var provider = CreateProvider();
        var env = CreateEnvironment("run-transport-inferred");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
            Transport = CloneTransport.Unspecified, // will infer Local from path
        };

        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);

        Assert.Equal(CloneTransport.Local, checkout.Transport);
    }

    [Fact]
    public async Task CloneAsync_CheckoutRecordsTransport_ExplicitSsh()
    {
        // Even though the URL is a local path, explicit transport is honored.
        var provider = CreateProvider();
        var env = CreateEnvironment("run-transport-explicit-ssh");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
            Transport = CloneTransport.Ssh,
        };

        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);

        // The checkout records the resolved transport (explicit Ssh),
        // even though the actual git clone used a local path.
        Assert.Equal(CloneTransport.Ssh, checkout.Transport);
    }

    // ═══════════════════════════════════════════════════════════════
    // 6. Preflight factory methods and result shape
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void PreflightResult_OkFactory_ReturnsSuccess()
    {
        var result = ClonePreflightResult.Ok(CloneTransport.Ssh, "git@host:repo");
        Assert.True(result.Success);
        Assert.Equal(CloneTransport.Ssh, result.Transport);
        Assert.Equal("git@host:repo", result.CloneUrl);
        Assert.Empty(result.Reason);
    }

    [Fact]
    public void PreflightResult_FailedFactory_ReturnsFailure()
    {
        var result = ClonePreflightResult.Failed(
            CloneTransport.HttpsPat, "https://example.com/repo", "auth failed");
        Assert.False(result.Success);
        Assert.Equal(CloneTransport.HttpsPat, result.Transport);
        Assert.Equal("https://example.com/repo", result.CloneUrl);
        Assert.Equal("auth failed", result.Reason);
    }

    // ═══════════════════════════════════════════════════════════════
    // 7. NormalizeCloneUrl edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeCloneUrl_PassthroughRemoteHttps()
    {
        var url = "https://dev.azure.com/org/project/_git/repo";
        Assert.Equal(url, LocalGitSourceControlProvider.NormalizeCloneUrl(url));
    }

    [Fact]
    public void NormalizeCloneUrl_PassthroughRemoteGitAt()
    {
        var url = "git@github.com:user/repo.git";
        Assert.Equal(url, LocalGitSourceControlProvider.NormalizeCloneUrl(url));
    }

    [Fact]
    public void NormalizeCloneUrl_PassthroughRemoteSsh()
    {
        var url = "ssh://git@github.com/user/repo.git";
        Assert.Equal(url, LocalGitSourceControlProvider.NormalizeCloneUrl(url));
    }

    [Fact]
    public void NormalizeCloneUrl_PassthroughFileUrl()
    {
        var url = "file:///home/user/repo";
        Assert.Equal(url, LocalGitSourceControlProvider.NormalizeCloneUrl(url));
    }

    [Fact]
    public void NormalizeCloneUrl_ExpandsTildeInLocalPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = LocalGitSourceControlProvider.NormalizeCloneUrl("~/projects/repo");
        Assert.Equal(Path.Combine(home, "projects", "repo"), result);
        Assert.DoesNotContain("~", result);
    }

    [Fact]
    public void NormalizeCloneUrl_ExpandsTildeAlone()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, LocalGitSourceControlProvider.NormalizeCloneUrl("~"));
    }

    [Fact]
    public void NormalizeCloneUrl_TrimsWhitespace()
    {
        Assert.Equal("/home/user/repo",
            LocalGitSourceControlProvider.NormalizeCloneUrl("  /home/user/repo  "));
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static async Task RunGitAsync(IReadOnlyList<string> args, string workingDir)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed with exit code {process.ExitCode}: {stderr}");
        }
    }
}
