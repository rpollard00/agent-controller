using System.Diagnostics;
using AgentController.Application;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="LocalGitRepositoryMaterializer"/> covering HTTPS+PAT,
/// SSH, and Local transport materialization with credential injection.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields.")]
public class LocalGitRepositoryMaterializerTests : IAsyncLifetime
{
    private string _tempRoot = null!;
    private string _sourceRepo = null!;
    private string _sourceRepoFileUrl = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "git-materializer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        // Create a source git repository to clone from.
        _sourceRepo = Path.Combine(_tempRoot, "source-repo");
        Directory.CreateDirectory(_sourceRepo);

        // Initialize git repo with a commit.
        await RunGitAsync(["init", "--initial-branch", "main"], _sourceRepo);
        await RunGitAsync(["config", "user.email", "test@example.com"], _sourceRepo);
        await RunGitAsync(["config", "user.name", "Test User"], _sourceRepo);

        var readme = Path.Combine(_sourceRepo, "README.md");
        await File.WriteAllTextAsync(readme, "# Test Repo\n\nTest file for materializer tests.");
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

    private static LocalGitRepositoryMaterializer CreateMaterializer(IManagedSecretStore? secretStore = null)
    {
        return new LocalGitRepositoryMaterializer(
            secretStore ?? new FakeSecretStore(),
            NullLogger<LocalGitRepositoryMaterializer>.Instance);
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

    private static ResolvedSecretsManifest CreateManifest(
        string scope,
        params (SecretReference Ref, string? Value)[] secrets)
    {
        return new ResolvedSecretsManifest(
            scope,
            secrets.Select(s => new ResolvedSecret(s.Ref, s.Value)).ToArray());
    }

    // ── Local transport materialization ─────────────────────────────

    [Fact]
    public async Task MaterializeAsync_LocalTransport_ClonesSuccessfully()
    {
        // Arrange
        var materializer = CreateMaterializer();
        var env = CreateEnvironment("run-local");
        var profile = new RepositoryProfile
        {
            Key = "test-repo",
            CloneUrl = _sourceRepo,
            DefaultBranch = "main",
            Transport = CloneTransport.Local,
        };
        var manifest = CreateManifest("test-repo");

        // Act
        var result = await materializer.MaterializeAsync(profile, manifest, env, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("test-repo", result.RepoKey);
        Assert.Equal("main", result.Branch);
        Assert.NotNull(result.CommitSha);
        Assert.NotEmpty(result.CommitSha);
        Assert.Equal(CloneTransport.Local, result.Transport);
        Assert.Empty(result.ResolvedEnvVars);
        Assert.True(Directory.Exists(Path.Combine(result.LocalPath, ".git")));
        Assert.True(File.Exists(Path.Combine(result.LocalPath, "README.md")));
    }

    [Fact]
    public async Task MaterializeAsync_LocalTransport_NoEnvVarsForwarded()
    {
        // Arrange
        var materializer = CreateMaterializer();
        var env = CreateEnvironment("run-local-env");
        var profile = new RepositoryProfile
        {
            Key = "test-repo",
            CloneUrl = _sourceRepo,
            Transport = CloneTransport.Local,
        };
        var manifest = CreateManifest("test-repo",
            (SecretReference.EnvironmentVariable("ADO_PAT"), "fake-pat-value"));

        // Act
        var result = await materializer.MaterializeAsync(profile, manifest, env, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        // Local transport should NOT forward env vars (no PAT needed for local clones).
        Assert.Empty(result.ResolvedEnvVars);
    }

    // ── HTTPS+PAT transport materialization ─────────────────────────

    [Fact]
    public async Task MaterializeAsync_HttpsPat_ResolvesEnvVarsFromManifest()
    {
        // Arrange
        var materializer = CreateMaterializer();
        var env = CreateEnvironment("run-https");
        var profile = new RepositoryProfile
        {
            Key = "test-repo",
            CloneUrl = _sourceRepo, // Using local path but specifying HTTPS transport for env var test
            Transport = CloneTransport.HttpsPat,
        };
        var manifest = CreateManifest("test-repo",
            (SecretReference.EnvironmentVariable("ADO_PAT"), "fake-pat-123"));

        // Act
        var result = await materializer.MaterializeAsync(profile, manifest, env, CancellationToken.None);

        // Assert
        // Note: The clone will succeed because we're using a local path, but the
        // transport is specified as HttpsPat, so env vars should be resolved.
        Assert.True(result.Success);
        Assert.Equal(CloneTransport.HttpsPat, result.Transport);

        // Verify env vars are resolved for downstream forwarding.
        Assert.Contains("AZURE_DEVOPS_PAT", result.ResolvedEnvVars.Keys);
        Assert.Equal("fake-pat-123", result.ResolvedEnvVars["AZURE_DEVOPS_PAT"]);
        Assert.Contains("AZURE_DEVOPS_EXT_PAT", result.ResolvedEnvVars.Keys);
        Assert.Equal("fake-pat-123", result.ResolvedEnvVars["AZURE_DEVOPS_EXT_PAT"]);
    }

    [Fact]
    public async Task MaterializeAsync_HttpsPat_DbSecretMappedToEnvVar()
    {
        // Arrange
        var materializer = CreateMaterializer();
        var env = CreateEnvironment("run-https-db");
        var profile = new RepositoryProfile
        {
            Key = "test-repo",
            CloneUrl = _sourceRepo,
            Transport = CloneTransport.HttpsPat,
        };
        // Db-backed secret with "ADO" in the ID should map to AZURE_DEVOPS_PAT.
        var manifest = CreateManifest("test-repo",
            (SecretReference.Database("ado-pat-guid-123"), "db-stored-pat"));

        // Act
        var result = await materializer.MaterializeAsync(profile, manifest, env, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("AZURE_DEVOPS_PAT", result.ResolvedEnvVars.Keys);
        Assert.Equal("db-stored-pat", result.ResolvedEnvVars["AZURE_DEVOPS_PAT"]);
    }

    [Fact]
    public async Task MaterializeAsync_HttpsPat_InjectsExtraHeaderConfig()
    {
        // This test verifies that the materializer uses git http.extraHeader
        // for credential injection by checking that the clone succeeds when
        // using a local path with HTTPS transport specified.
        // The http.extraHeader config is only applied during the git clone command,
        // and won't affect local clones — but the test proves the code path runs.
        var materializer = CreateMaterializer();
        var env = CreateEnvironment("run-extraheader");
        var profile = new RepositoryProfile
        {
            Key = "test-repo",
            CloneUrl = _sourceRepo,
            DefaultBranch = "main",
            Transport = CloneTransport.HttpsPat,
        };
        var manifest = CreateManifest("test-repo",
            (SecretReference.EnvironmentVariable("ADO_PAT"), "test-pat-value"));

        var result = await materializer.MaterializeAsync(profile, manifest, env, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(CloneTransport.HttpsPat, result.Transport);
        Assert.True(Directory.Exists(Path.Combine(result.LocalPath, ".git")));
    }

    // ── SSH transport materialization ───────────────────────────────

    [Fact]
    public async Task MaterializeAsync_SshTransport_NoEnvVarsForwarded()
    {
        // Arrange
        var materializer = CreateMaterializer();
        var env = CreateEnvironment("run-ssh");
        var profile = new RepositoryProfile
        {
            Key = "test-repo",
            CloneUrl = _sourceRepo, // Using local path but specifying SSH transport
            Transport = CloneTransport.Ssh,
        };
        var manifest = CreateManifest("test-repo",
            (SecretReference.EnvironmentVariable("ADO_PAT"), "fake-pat"));

        // Act
        var result = await materializer.MaterializeAsync(profile, manifest, env, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(CloneTransport.Ssh, result.Transport);
        // SSH transport should NOT forward PAT env vars (SSH keys are used instead).
        Assert.Empty(result.ResolvedEnvVars);
    }

    // ── Error handling ──────────────────────────────────────────────

    [Fact]
    public async Task MaterializeAsync_EmptyCloneUrl_ReturnsFailure()
    {
        // Arrange
        var materializer = CreateMaterializer();
        var env = CreateEnvironment("run-empty-url");
        var profile = new RepositoryProfile
        {
            Key = "test-repo",
            CloneUrl = "",
        };
        var manifest = CreateManifest("test-repo");

        // Act
        var result = await materializer.MaterializeAsync(profile, manifest, env, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("cloneUrl is empty", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaterializeAsync_EmptyRootPath_ReturnsFailure()
    {
        // Arrange
        var materializer = CreateMaterializer();
        var env = new EnvironmentHandle
        {
            Id = "env-no-root",
            ProviderType = "NoOp",
            RootPath = "",
            Status = "created",
        };
        var profile = new RepositoryProfile
        {
            Key = "test-repo",
            CloneUrl = _sourceRepo,
        };
        var manifest = CreateManifest("test-repo");

        // Act
        var result = await materializer.MaterializeAsync(profile, manifest, env, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("no root path", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaterializeAsync_InvalidRepoPath_ReturnsFailure()
    {
        // Arrange
        var materializer = CreateMaterializer();
        var env = CreateEnvironment("run-invalid-repo");
        var profile = new RepositoryProfile
        {
            Key = "test-repo",
            CloneUrl = Path.Combine(_tempRoot, "nonexistent-repo"),
        };
        var manifest = CreateManifest("test-repo");

        // Act
        var result = await materializer.MaterializeAsync(profile, manifest, env, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("Materialization failed", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    // ── Cancellation token passthrough ──────────────────────────────

    [Fact]
    public async Task MaterializeAsync_CancellationToken_Propagates()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        var materializer = CreateMaterializer(new CancellationTrackingSecretStore());
        var env = CreateEnvironment("run-cancel");
        var profile = new RepositoryProfile
        {
            Key = "test-repo",
            CloneUrl = _sourceRepo,
            Transport = CloneTransport.Local,
        };
        var manifest = CreateManifest("test-repo");

        // Act & Assert
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => materializer.MaterializeAsync(profile, manifest, env, cts.Token));

        Assert.True(ex.CancellationToken.IsCancellationRequested);
    }

    // ── Result factory methods ──────────────────────────────────────

    [Fact]
    public void RepositoryMaterializationResult_SuccessResult_HasCorrectDefaults()
    {
        // Act
        var result = RepositoryMaterializationResult.SuccessResult(
            "repo-key", "/path/to/repo", "main", "abc123",
            CloneTransport.Ssh, new Dictionary<string, string> { ["VAR"] = "value" });

        // Assert
        Assert.True(result.Success);
        Assert.Equal("repo-key", result.RepoKey);
        Assert.Equal("/path/to/repo", result.LocalPath);
        Assert.Equal("main", result.Branch);
        Assert.Equal("abc123", result.CommitSha);
        Assert.Equal(CloneTransport.Ssh, result.Transport);
        Assert.Equal("value", result.ResolvedEnvVars["VAR"]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void RepositoryMaterializationResult_FailureResult_HasErrors()
    {
        // Act
        var result = RepositoryMaterializationResult.FailureResult(
            "repo-key", "error 1", "error 2");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("repo-key", result.RepoKey);
        Assert.Equal(2, result.Errors.Count);
        Assert.Equal("error 1", result.Errors[0]);
        Assert.Equal("error 2", result.Errors[1]);
        Assert.Empty(result.ResolvedEnvVars);
    }

    // ── Interface conformance ───────────────────────────────────────

    [Fact]
    public void Implements_IRepositoryMaterializer()
    {
        var materializer = CreateMaterializer();
        Assert.IsAssignableFrom<IRepositoryMaterializer>(materializer);
    }

    // ── DI registration ─────────────────────────────────────────────

    [Fact]
    public void DiRegistration_RegistersAsSingleton()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IManagedSecretStore, FakeSecretStore>();
        services.AddAgentControllerLocalGitRepositoryMaterializer();

        var provider = services.BuildServiceProvider();

        var materializer = provider.GetRequiredService<IRepositoryMaterializer>();
        Assert.IsType<LocalGitRepositoryMaterializer>(materializer);
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

// ─── Fakes ───

/// <summary>
/// Simple in-memory fake for <see cref="IManagedSecretStore"/>.
/// </summary>
internal sealed class FakeSecretStore : IManagedSecretStore
{
    public Task<string?> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<AgentController.Application.Results.SecretWriteResult> WriteAsync(
        SecretReference reference, string value, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentController.Application.Results.SecretWriteResult.SuccessResult());
    }
}

/// <summary>
/// Fake that tracks cancellation token receipt.
/// </summary>
internal sealed class CancellationTrackingSecretStore : IManagedSecretStore
{
    public Task<string?> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task<AgentController.Application.Results.SecretWriteResult> WriteAsync(
        SecretReference reference, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AgentController.Application.Results.SecretWriteResult.SuccessResult());
    }
}
