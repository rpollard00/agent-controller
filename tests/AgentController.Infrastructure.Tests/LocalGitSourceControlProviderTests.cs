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
/// Tests for <see cref="LocalGitSourceControlProvider"/> covering local paths,
/// <c>file://</c> URLs, and remote URL cloning behavior.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields.")]
public class LocalGitSourceControlProviderTests : IAsyncLifetime
{
    private string _tempRoot = null!;
    private string _sourceRepo = null!;
    private string _sourceRepoFileUrl = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "git-source-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        // Create a source git repository to clone from.
        _sourceRepo = Path.Combine(_tempRoot, "source-repo");
        Directory.CreateDirectory(_sourceRepo);

        // Initialize git repo with a commit. Use --initial-branch main so tests
        // that clone with --branch main can find the branch.
        await RunGitAsync(["init", "--initial-branch", "main"], _sourceRepo);
        await RunGitAsync(["config", "user.email", "test@example.com"], _sourceRepo);
        await RunGitAsync(["config", "user.name", "Test User"], _sourceRepo);

        // Create a file and commit so there's something to clone.
        var readme = Path.Combine(_sourceRepo, "README.md");
        await File.WriteAllTextAsync(readme, "# Test Repo\n\nTest file for clone tests.");
        await RunGitAsync(["add", "README.md"], _sourceRepo);
        await RunGitAsync(["commit", "-m", "Initial commit"], _sourceRepo);

        // Also create a branch
        await RunGitAsync(["branch", "feature-branch"], _sourceRepo);

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

    private static TestOptionsMonitor<AgentControllerOptions> CreateOptions(string runRoot = "/tmp/runs")
    {
        var options = new AgentControllerOptions
        {
            WorkerId = "test-worker",
            RunRoot = runRoot,
        };
        return new TestOptionsMonitor<AgentControllerOptions>(options);
    }

    private static LocalGitSourceControlProvider CreateProvider(string runRoot = "/tmp/runs")
    {
        return new LocalGitSourceControlProvider(
            CreateOptions(runRoot),
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

    // ── NormalizeCloneUrl tests ─────────────────────────────────────

    [Fact]
    public void NormalizeCloneUrl_PassthroughRemoteHttps()
    {
        var url = "https://dev.azure.com/org/project/_git/repo";
        var result = LocalGitSourceControlProvider.NormalizeCloneUrl(url);
        Assert.Equal(url, result);
    }

    [Fact]
    public void NormalizeCloneUrl_PassthroughRemoteGitAt()
    {
        var url = "git@github.com:user/repo.git";
        var result = LocalGitSourceControlProvider.NormalizeCloneUrl(url);
        Assert.Equal(url, result);
    }

    [Fact]
    public void NormalizeCloneUrl_PassthroughRemoteSsh()
    {
        var url = "ssh://git@github.com/user/repo.git";
        var result = LocalGitSourceControlProvider.NormalizeCloneUrl(url);
        Assert.Equal(url, result);
    }

    [Fact]
    public void NormalizeCloneUrl_PassthroughFileUrl()
    {
        var url = "file:///home/user/repo";
        var result = LocalGitSourceControlProvider.NormalizeCloneUrl(url);
        Assert.Equal(url, result);
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
        var result = LocalGitSourceControlProvider.NormalizeCloneUrl("~");
        Assert.Equal(home, result);
    }

    [Fact]
    public void NormalizeCloneUrl_PassthroughAbsoluteLocalPath()
    {
        var path = "/home/user/projects/repo";
        var result = LocalGitSourceControlProvider.NormalizeCloneUrl(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void NormalizeCloneUrl_PassthroughRelativePath()
    {
        // Relative paths are passed through — git will resolve relative to CWD.
        var path = "../relative/repo";
        var result = LocalGitSourceControlProvider.NormalizeCloneUrl(path);
        Assert.Equal(path, result);
    }

    [Fact]
    public void NormalizeCloneUrl_TrimsWhitespace()
    {
        var result = LocalGitSourceControlProvider.NormalizeCloneUrl("  /home/user/repo  ");
        Assert.Equal("/home/user/repo", result);
    }

    // ── Clone from local path ───────────────────────────────────────

    [Fact]
    public async Task CloneAsync_LocalPath_ClonesSuccessfully()
    {
        var provider = CreateProvider();
        var env = CreateEnvironment("run-local-path");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
            DefaultBranch = "main",
        };

        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);

        Assert.NotNull(checkout);
        Assert.Equal("test-repo", checkout.RepoKey);
        Assert.NotNull(checkout.CommitSha);
        Assert.NotEmpty(checkout.CommitSha);
        Assert.Equal("main", checkout.Branch);

        // Verify the clone exists and is a valid git repo.
        Assert.True(Directory.Exists(checkout.LocalPath));
        Assert.True(Directory.Exists(Path.Combine(checkout.LocalPath, ".git")));
        Assert.True(File.Exists(Path.Combine(checkout.LocalPath, "README.md")));
    }

    [Fact]
    public async Task CloneAsync_LocalPath_RespectsDefaultBranch()
    {
        var provider = CreateProvider();
        var env = CreateEnvironment("run-branch");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
            DefaultBranch = "feature-branch",
        };

        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);

        Assert.Equal("feature-branch", checkout.Branch);

        // Verify the branch is checked out.
        var branch = await GetCurrentBranchAsync(checkout.LocalPath);
        Assert.Equal("feature-branch", branch);
    }

    // ── Clone from file:// URL ──────────────────────────────────────

    [Fact]
    public async Task CloneAsync_FileUrl_ClonesSuccessfully()
    {
        var provider = CreateProvider();
        var env = CreateEnvironment("run-file-url");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepoFileUrl,
            DefaultBranch = "main",
        };

        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);

        Assert.NotNull(checkout);
        Assert.NotNull(checkout.CommitSha);
        Assert.NotEmpty(checkout.CommitSha);

        // Verify the clone exists.
        Assert.True(Directory.Exists(checkout.LocalPath));
        Assert.True(Directory.Exists(Path.Combine(checkout.LocalPath, ".git")));
    }

    // ── Clone into pre-existing env path (like LocalWorkspaceEnvironmentProvider) ─

    [Fact]
    public async Task CloneAsync_CreatesRepoSubdirectoryInEnvironment()
    {
        // Simulate what LocalWorkspaceEnvironmentProvider does: create the
        // environment root path but NOT the repo/ subdirectory. The source
        // control provider creates repo/ via git clone.
        var runId = "run-dir-test";
        var envRoot = Path.Combine(_tempRoot, "envs", runId);
        Directory.CreateDirectory(envRoot);

        var provider = CreateProvider();
        var env = new EnvironmentHandle
        {
            Id = $"local-{runId}",
            ProviderType = "LocalWorkspace",
            RootPath = envRoot,
            Status = "created",
        };

        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
        };

        var checkout = await provider.CloneAsync(spec, env, CancellationToken.None);

        // The clone should be at {envRoot}/repo/
        Assert.Equal(Path.Combine(envRoot, "repo"), checkout.LocalPath);
        Assert.True(Directory.Exists(Path.Combine(envRoot, "repo", ".git")));
    }

    // ── Error handling ──────────────────────────────────────────────

    [Fact]
    public async Task CloneAsync_EmptyCloneUrl_Throws()
    {
        var provider = CreateProvider();
        var env = CreateEnvironment("run-empty-url");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = "",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CloneAsync(spec, env, CancellationToken.None));

        Assert.Contains("cloneUrl is empty", ex.Message);
    }

    [Fact]
    public async Task CloneAsync_EmptyRootPath_Throws()
    {
        var provider = CreateProvider();
        var env = new EnvironmentHandle
        {
            Id = "env-no-root",
            ProviderType = "NoOp",
            RootPath = "",
            Status = "created",
        };
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = _sourceRepo,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CloneAsync(spec, env, CancellationToken.None));

        Assert.Contains("no root path", ex.Message);
    }

    [Fact]
    public async Task CloneAsync_InvalidRepoPath_Throws()
    {
        var provider = CreateProvider();
        var env = CreateEnvironment("run-invalid");
        var spec = new RepositorySpec
        {
            RepoKey = "test-repo",
            CloneUrl = Path.Combine(_tempRoot, "nonexistent-repo"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CloneAsync(spec, env, CancellationToken.None));

        Assert.Contains("git clone failed", ex.Message);
    }

    // ── GetStatusAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_ReturnsNotExists()
    {
        var provider = CreateProvider();
        var scRef = new SourceControlRef
        {
            Provider = "LocalGit",
            RepoKey = "some-repo",
        };

        var status = await provider.GetStatusAsync(scRef, CancellationToken.None);

        Assert.False(status.Exists);
        Assert.Null(status.PullRequestUrl);
        Assert.Null(status.PullRequestStatus);
    }

    // ── Interface conformance ───────────────────────────────────────

    [Fact]
    public void Implements_ISourceControlProvider()
    {
        var provider = new LocalGitSourceControlProvider(
            CreateOptions(),
            NullLogger<LocalGitSourceControlProvider>.Instance);
        Assert.IsAssignableFrom<ISourceControlProvider>(provider);
    }

    // ── DI registration ─────────────────────────────────────────────

    [Fact]
    public void DiRegistration_RegistersAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agentController:workerId"] = "test",
                ["agentController:runRoot"] = "/tmp/runs",
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddAgentControllerOptions(config);
        services.AddAgentControllerLocalGitSourceControl();

        var provider = services.BuildServiceProvider();

        // Verify we can resolve the provider.
        var scp = provider.GetRequiredService<ISourceControlProvider>();
        Assert.IsType<LocalGitSourceControlProvider>(scp);
    }

    [Fact]
    public void DiRegistration_OverrideReplacesNoOp()
    {
        // NoOp registered first, then LocalGit — last wins.
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agentController:workerId"] = "test",
                ["agentController:runRoot"] = "/tmp/runs",
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddAgentControllerOptions(config);

        // Register no-op first
        services.AddSingleton<ISourceControlProvider, NoOpSourceControlProvider>();
        // Then override with local git
        services.AddSingleton<ISourceControlProvider, LocalGitSourceControlProvider>();

        var provider = services.BuildServiceProvider();

        var scp = provider.GetRequiredService<ISourceControlProvider>();
        Assert.IsType<LocalGitSourceControlProvider>(scp);
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

    private static async Task<string> GetCurrentBranchAsync(string repoPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --abbrev-ref HEAD",
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return output.Trim();
    }
}

/// <summary>
/// Simple <see cref="IOptionsMonitor{TOptions}"/> implementation for tests
/// that returns a fixed options value.
/// </summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> where T : class
{
    public T CurrentValue { get; }

    public TestOptionsMonitor(T value)
    {
        CurrentValue = value;
    }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null!;
}
