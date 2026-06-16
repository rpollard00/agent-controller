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
/// Tests for <see cref="LocalWorkspaceEnvironmentProvider"/> covering workspace
/// directory creation, command execution, and workspace destruction.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "IAsyncLifetime.DisposeAsync disposes all owned fields.")]
public class LocalWorkspaceEnvironmentProviderTests : IAsyncLifetime
{
    private string _tempRoot = null!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "env-provider-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (_tempRoot is not null && Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }

        return Task.CompletedTask;
    }

    private static TestOptionsMonitor<AgentControllerOptions> CreateOptions(string runRoot)
    {
        var options = new AgentControllerOptions
        {
            WorkerId = "test-worker",
            RunRoot = runRoot,
            RetainSuccessfulRuns = true,
            RetainFailedRuns = true,
        };
        return new TestOptionsMonitor<AgentControllerOptions>(options);
    }

    private LocalWorkspaceEnvironmentProvider CreateProvider(string? runRoot = null)
    {
        return new LocalWorkspaceEnvironmentProvider(
            CreateOptions(runRoot ?? _tempRoot),
            NullLogger<LocalWorkspaceEnvironmentProvider>.Instance);
    }

    // ── CreateAsync tests ───────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_CreatesWorkspaceDirectory()
    {
        var provider = CreateProvider();
        var spec = new EnvironmentSpec
        {
            RunId = "run-create-1",
            Profile = "default",
        };

        var handle = await provider.CreateAsync(spec, CancellationToken.None);

        Assert.NotNull(handle);
        Assert.Contains("run-create-1", handle.Id);
        Assert.Equal("LocalWorkspace", handle.ProviderType);
        Assert.Equal("created", handle.Status);

        // Verify the root directory exists.
        Assert.True(Directory.Exists(handle.RootPath));

        // Verify subdirectories exist.
        Assert.True(Directory.Exists(Path.Combine(handle.RootPath, "repo")));
        Assert.True(Directory.Exists(Path.Combine(handle.RootPath, "context")));
        Assert.True(Directory.Exists(Path.Combine(handle.RootPath, "logs")));
        Assert.True(Directory.Exists(Path.Combine(handle.RootPath, "artifacts")));
        Assert.True(Directory.Exists(Path.Combine(handle.RootPath, "result")));

        // Verify the root is under runRoot.
        Assert.StartsWith(_tempRoot, handle.RootPath);
    }

    [Fact]
    public async Task CreateAsync_MultipleRunsCreateSeparateDirectories()
    {
        var provider = CreateProvider();

        var handle1 = await provider.CreateAsync(
            new EnvironmentSpec { RunId = "run-a" },
            CancellationToken.None);

        var handle2 = await provider.CreateAsync(
            new EnvironmentSpec { RunId = "run-b" },
            CancellationToken.None);

        Assert.NotEqual(handle1.RootPath, handle2.RootPath);
        Assert.True(Directory.Exists(handle1.RootPath));
        Assert.True(Directory.Exists(handle2.RootPath));
    }

    [Fact]
    public async Task CreateAsync_DoesNotCreateNestedDirectoriesInRepo()
    {
        // The repo/ directory is the target for git clone; the env provider
        // only creates it as a placeholder. No nested subdirectories inside repo/.
        var provider = CreateProvider();
        var spec = new EnvironmentSpec { RunId = "run-check-repo" };

        var handle = await provider.CreateAsync(spec, CancellationToken.None);

        var repoDir = Path.Combine(handle.RootPath, "repo");
        Assert.True(Directory.Exists(repoDir));
        Assert.Empty(Directory.GetDirectories(repoDir));
        Assert.Empty(Directory.GetFiles(repoDir));
    }

    // ── ExpandTilde tests ───────────────────────────────────────────

    [Fact]
    public void ExpandTilde_ExpandsTildeSlash()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = LocalWorkspaceEnvironmentProvider.ExpandTilde("~/projects/repo");

        Assert.Equal(Path.Combine(home, "projects", "repo"), result);
        Assert.DoesNotContain("~", result);
    }

    [Fact]
    public void ExpandTilde_ExpandsTildeAlone()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = LocalWorkspaceEnvironmentProvider.ExpandTilde("~");

        Assert.Equal(home, result);
    }

    [Fact]
    public void ExpandTilde_PassthroughNonTilde()
    {
        var path = "/tmp/runs";
        var result = LocalWorkspaceEnvironmentProvider.ExpandTilde(path);

        Assert.Equal("/tmp/runs", result);
    }

    [Fact]
    public void ExpandTilde_ReturnsEmptyForEmpty()
    {
        var result = LocalWorkspaceEnvironmentProvider.ExpandTilde("");
        Assert.Equal("", result);
    }

    [Fact]
    public void ExpandTilde_ReturnsWhitespaceForWhitespace()
    {
        var result = LocalWorkspaceEnvironmentProvider.ExpandTilde("   ");
        Assert.Equal("   ", result);
    }

    // ── ExecuteAsync tests ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RunsSimpleCommand()
    {
        var provider = CreateProvider();
        var handle = await provider.CreateAsync(
            new EnvironmentSpec { RunId = "run-exec-1" },
            CancellationToken.None);

        var cmd = new CommandSpec
        {
            Command = "echo",
            Arguments = new List<string> { "hello", "world" },
        };

        var result = await provider.ExecuteAsync(handle, cmd, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello world", result.StdOut);
        Assert.False(result.TimedOut);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsWorkingDirectory()
    {
        var provider = CreateProvider();
        var handle = await provider.CreateAsync(
            new EnvironmentSpec { RunId = "run-exec-wd" },
            CancellationToken.None);

        // Create a file in the context directory.
        var contextDir = Path.Combine(handle.RootPath, "context");
        var testFile = Path.Combine(contextDir, "test.txt");
        await File.WriteAllTextAsync(testFile, "hello");

        // List files in the context directory from the context/ working directory.
        var cmd = new CommandSpec
        {
            Command = "ls",
            Arguments = new List<string> { "test.txt" },
            WorkingDirectory = "context",
        };

        var result = await provider.ExecuteAsync(handle, cmd, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("test.txt", result.StdOut);
    }

    [Fact]
    public async Task ExecuteAsync_EnvironmentVariablePassedToProcess()
    {
        var provider = CreateProvider();
        var handle = await provider.CreateAsync(
            new EnvironmentSpec { RunId = "run-exec-env" },
            CancellationToken.None);

        var cmd = new CommandSpec
        {
            Command = "sh",
            Arguments = new List<string> { "-c", "echo $TEST_VAR" },
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["TEST_VAR"] = "env-value-42",
            },
        };

        var result = await provider.ExecuteAsync(handle, cmd, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("env-value-42", result.StdOut);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCommand_Throws()
    {
        var provider = CreateProvider();
        var handle = await provider.CreateAsync(
            new EnvironmentSpec { RunId = "run-exec-err" },
            CancellationToken.None);

        var cmd = new CommandSpec { Command = "" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExecuteAsync(handle, cmd, CancellationToken.None));

        Assert.Contains("command is empty", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NonexistentWorkingDirectory_Throws()
    {
        var provider = CreateProvider();
        var handle = await provider.CreateAsync(
            new EnvironmentSpec { RunId = "run-exec-badwd" },
            CancellationToken.None);

        var cmd = new CommandSpec
        {
            Command = "echo",
            Arguments = ["test"],
            WorkingDirectory = "nonexistent",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExecuteAsync(handle, cmd, CancellationToken.None));

        Assert.Contains("working directory does not exist", ex.Message);
    }

    // ── DestroyAsync tests ──────────────────────────────────────────

    [Fact]
    public async Task DestroyAsync_RemovesWorkspaceDirectory()
    {
        var provider = CreateProvider();
        var handle = await provider.CreateAsync(
            new EnvironmentSpec { RunId = "run-destroy-1" },
            CancellationToken.None);

        Assert.True(Directory.Exists(handle.RootPath));

        await provider.DestroyAsync(handle, CancellationToken.None);

        Assert.False(Directory.Exists(handle.RootPath));
    }

    [Fact]
    public async Task DestroyAsync_EmptyRootPath_NoOp()
    {
        var provider = CreateProvider();
        var handle = new EnvironmentHandle
        {
            Id = "env-empty",
            ProviderType = "LocalWorkspace",
            RootPath = "",
            Status = "created",
        };

        // Should not throw.
        await provider.DestroyAsync(handle, CancellationToken.None);
    }

    [Fact]
    public async Task DestroyAsync_NonexistentPath_NoOp()
    {
        var provider = CreateProvider();
        var handle = new EnvironmentHandle
        {
            Id = "env-nonexistent",
            ProviderType = "LocalWorkspace",
            RootPath = Path.Combine(_tempRoot, "does-not-exist"),
            Status = "created",
        };

        // Should not throw.
        await provider.DestroyAsync(handle, CancellationToken.None);
    }

    [Fact]
    public async Task DestroyAsync_Idempotent()
    {
        var provider = CreateProvider();
        var handle = await provider.CreateAsync(
            new EnvironmentSpec { RunId = "run-destroy-idem" },
            CancellationToken.None);

        Assert.True(Directory.Exists(handle.RootPath));

        await provider.DestroyAsync(handle, CancellationToken.None);
        Assert.False(Directory.Exists(handle.RootPath));

        // Second call should not throw.
        await provider.DestroyAsync(handle, CancellationToken.None);
    }

    // ── Interface conformance ───────────────────────────────────────

    [Fact]
    public void Implements_IEnvironmentProvider()
    {
        var provider = new LocalWorkspaceEnvironmentProvider(
            CreateOptions(_tempRoot),
            NullLogger<LocalWorkspaceEnvironmentProvider>.Instance);
        Assert.IsAssignableFrom<IEnvironmentProvider>(provider);
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
                ["agentController:runRoot"] = _tempRoot,
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddAgentControllerOptions(config);
        services.AddAgentControllerLocalWorkspaceEnvironment();

        var provider = services.BuildServiceProvider();

        var ep = provider.GetRequiredService<IEnvironmentProvider>();
        Assert.IsType<LocalWorkspaceEnvironmentProvider>(ep);
    }

    [Fact]
    public void DiRegistration_OverrideReplacesNoOp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agentController:workerId"] = "test",
                ["agentController:runRoot"] = _tempRoot,
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddAgentControllerOptions(config);

        services.AddSingleton<IEnvironmentProvider, NoOpEnvironmentProvider>();
        services.AddSingleton<IEnvironmentProvider, LocalWorkspaceEnvironmentProvider>();

        var provider = services.BuildServiceProvider();

        var ep = provider.GetRequiredService<IEnvironmentProvider>();
        Assert.IsType<LocalWorkspaceEnvironmentProvider>(ep);
    }
}
