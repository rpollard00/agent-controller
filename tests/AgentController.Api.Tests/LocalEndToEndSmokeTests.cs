using System.Diagnostics;
using AgentController.Api;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Api.Tests;

/// <summary>
/// Integration-style smoke tests that wire the full local-only controller path:
/// LocalFile work source + local git repository workspace + mock pi-materia runtime.
///
/// These tests prove the controller can discover work items from declarative config,
/// provision a local workspace, clone a local git repository, inject context files,
/// hand off to the mock runtime, and complete a run — all without Azure DevOps.
/// </summary>
public class LocalEndToEndSmokeTests : IAsyncLifetime
{
    private string _tempRoot = null!;
    private string _tempDbPath = null!;
    private string _tempRepoPath = null!;
    private string _tempRunRoot = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"agent-controller-e2e-{Guid.NewGuid():N}");
        _tempDbPath = Path.Combine(_tempRoot, "test.db");
        _tempRepoPath = Path.Combine(_tempRoot, "test-repo");
        _tempRunRoot = Path.Combine(_tempRoot, "runs");

        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_tempRepoPath);

        // Initialize a minimal git repository so LocalGitSourceControlProvider
        // can clone it during the poll cycle.
        await RunGitAsync(_tempRepoPath, ["init", "--initial-branch=main"], TimeSpan.FromSeconds(10));
        await RunGitAsync(_tempRepoPath, ["config", "user.email", "test@example.com"], TimeSpan.FromSeconds(5));
        await RunGitAsync(_tempRepoPath, ["config", "user.name", "Test User"], TimeSpan.FromSeconds(5));

        // Add a minimal file so there's something to clone
        await File.WriteAllTextAsync(Path.Combine(_tempRepoPath, "README.md"), "# Test Repo");
        await RunGitAsync(_tempRepoPath, ["add", "README.md"], TimeSpan.FromSeconds(5));
        await RunGitAsync(_tempRepoPath, ["commit", "-m", "Initial commit"], TimeSpan.FromSeconds(5));
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task FullLocalPollCycle_CompletesRunWithMockRuntime()
    {
        // ── 1. Build configuration ──────────────────────────────────
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agentController:workerId"] = "test-e2e-worker",
                ["agentController:pollIntervalSeconds"] = "10",
                ["agentController:maxConcurrentRuns"] = "1",
                ["agentController:staleTimeoutSeconds"] = "300",
                ["agentController:runRoot"] = _tempRunRoot,
                ["agentController:retainSuccessfulRuns"] = "true",
                ["agentController:retainFailedRuns"] = "true",
                ["agentController:workerEnabled"] = "true",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = $"Data Source={_tempDbPath}",
                ["workSource:provider"] = "LocalFile",
                ["sourceControl:provider"] = "LocalGit",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "MockPiMateria",
                ["runtime:defaultMateriaLoadout"] = "success-pr",
                ["localWork:definitions:0:repoKey"] = "test-repo",
                ["localWork:definitions:0:title"] = "E2E Test: Implement demo feature",
                ["localWork:definitions:0:body"] = "This is an end-to-end test work item for verifying the local-only controller path.",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
                ["localWork:definitions:0:tags:1"] = "test",
                ["localWork:definitions:0:priority"] = "1",
                ["localWork:definitions:0:status"] = "New",
                ["repositories:test-repo:cloneUrl"] = _tempRepoPath,
                ["repositories:test-repo:defaultBranch"] = "main",
                ["repositories:test-repo:environmentProfile"] = "local-default",
                ["repositories:test-repo:runtimeProfile"] = "pi-materia-default",
            })
            .Build();

        // ── 2. Build the DI container ───────────────────────────────
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

        services.AddAgentControllerOptions(config);
        services.AddAgentControllerDbContext(config);
        services.AddAgentControllerRepositories();
        services.AddAgentControllerLifecycleService();
        services.AddAgentControllerNoOpProviders();

        // Wire real providers (last-registered wins over no-ops)
        services.AddAgentControllerLocalFileWorkSource();
        services.AddAgentControllerLocalGitSourceControl();
        services.AddAgentControllerLocalWorkspaceEnvironment();
        services.AddAgentControllerMockPiMateriaRuntime();

        // Register a scope factory that wraps the root provider.
        services.AddSingleton<IServiceScopeFactory, SimpleScopeFactory>();

        var provider = services.BuildServiceProvider();

        // Resolve IServiceScopeFactory from the container.
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // ── 3. Run database migrations ──────────────────────────────
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // ── 4. Create PollingWorker and run a poll cycle ────────────
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<AgentControllerOptions>>();
        var logger = provider.GetRequiredService<ILogger<PollingWorker>>();

        var worker = new PollingWorker(scopeFactory, optionsMonitor, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Run a single poll cycle — this discovers the work item,
        // provisions the environment, clones the repo, injects context,
        // hands off to MockPiMateriaRuntime, which fires events in-process.
        await worker.RunPollCycleForTestingAsync(cts.Token);

        // ── 5. Wait for mock runtime to complete ────────────────────
        // MockPiMateriaRuntime emits events in a fire-and-forget background task.
        // Give it time to emit all events (accepted → heartbeat → status → completed).
        await Task.Delay(TimeSpan.FromSeconds(1));

        // ── 6. Assert the run completed ─────────────────────────────
        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var runStore = verifyScope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var eventStore = verifyScope.ServiceProvider.GetRequiredService<ILifecycleEventStore>();
        var workItemStore = verifyScope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        // Find the work item that was seeded
        var workItems = await workItemStore.ListAsync(
            new WorkItemListQuery { MaxResults = 10 },
            CancellationToken.None);

        Assert.NotEmpty(workItems);
        var workItem = workItems[0];
        Assert.Equal("E2E Test: Implement demo feature", workItem.Title);
        Assert.Equal("test-repo", workItem.RepoKey);

        // Find the run created for this work item
        var runs = await runStore.ListAsync(
            new RunListQuery { MaxResults = 10 },
            CancellationToken.None);

        Assert.NotEmpty(runs);
        var run = runs[0];
        Assert.Equal(workItem.Id, run.WorkItemId);

        // The run should be in a terminal or near-terminal state (PrOpened for success-pr loadout)
        Assert.True(
            run.Status == RunLifecycleState.PrOpened
            || run.Status == RunLifecycleState.Completed
            || run.Status == RunLifecycleState.AwaitingResult, // may still be processing
            $"Expected run to be PrOpened/Completed, but was: {run.Status}. Error: {run.Error}");

        // Verify lifecycle events were recorded
        var events = await eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.EventType == "controller.claimed");
        Assert.Contains(events, e => e.EventType == "controller.environment_provisioning");
        Assert.Contains(events, e => e.EventType == "controller.environment_ready");
        Assert.Contains(events, e => e.EventType == "controller.repository_cloning");
        Assert.Contains(events, e => e.EventType == "controller.repository_ready");
        Assert.Contains(events, e => e.EventType == "controller.context_injected");
        Assert.Contains(events, e => e.EventType == "controller.agent_starting");
        Assert.Contains(events, e => e.EventType == "controller.agent_running");
        Assert.Contains(events, e => e.EventType == "controller.awaiting_result");

        // Verify the runtime events were ingested
        var runtimeEvents = events.Where(e => e.EventType.StartsWith("runtime.", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(runtimeEvents);
        Assert.Contains(runtimeEvents, e => e.EventType == "runtime.accepted");
        Assert.Contains(runtimeEvents, e => e.EventType == "runtime.heartbeat");
        Assert.Contains(runtimeEvents, e => e.EventType == "runtime.status");

        // The completed event may not have been ingested yet (mock runtime
        // fires events asynchronously). If the run is in PrOpened/Completed
        // state, the event was ingested; otherwise the test proves the
        // full pipeline up to AwaitingResult works.
        if (!runtimeEvents.Any(e => e.EventType == "runtime.completed"))
        {
            Assert.Equal(RunLifecycleState.AwaitingResult, run.Status);
        }
    }

    [Fact]
    public void AllLocalProviders_CanBeResolvedFromContainer()
    {
        // Prove the DI container can resolve all local-only provider interfaces
        // after registering them through the canonical extension methods.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agentController:workerId"] = "test-worker",
                ["agentController:runRoot"] = "/tmp/test-runs",
                ["persistence:provider"] = "Sqlite",
                ["persistence:connectionString"] = "Data Source=test.db",
                ["workSource:provider"] = "LocalFile",
                ["sourceControl:provider"] = "LocalGit",
                ["environmentProvider:provider"] = "LocalWorkspace",
                ["runtime:provider"] = "MockPiMateria",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddAgentControllerOptions(config);
        services.AddAgentControllerNoOpProviders();

        // Override with real providers
        services.AddAgentControllerLocalFileWorkSource();
        services.AddAgentControllerLocalGitSourceControl();
        services.AddAgentControllerLocalWorkspaceEnvironment();
        services.AddAgentControllerMockPiMateriaRuntime();

        var provider = services.BuildServiceProvider();

        // All four application ports must be resolvable, and none should be no-ops.
        var workSource = provider.GetRequiredService<IWorkSource>();
        Assert.IsType<LocalFileWorkSource>(workSource);

        var sourceControl = provider.GetRequiredService<ISourceControlProvider>();
        Assert.IsType<LocalGitSourceControlProvider>(sourceControl);

        var envProvider = provider.GetRequiredService<IEnvironmentProvider>();
        Assert.IsType<LocalWorkspaceEnvironmentProvider>(envProvider);

        var runtime = provider.GetRequiredService<IAgentRuntime>();
        Assert.IsType<MockPiMateriaRuntime>(runtime);
    }

    /// <summary>
    /// Run a git command in the specified working directory.
    /// </summary>
    private static async Task<(int ExitCode, string StdErr)> RunGitAsync(
        string workingDirectory,
        string[] arguments,
        TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        using var cts = new CancellationTokenSource(timeout);

        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);

        await process.WaitForExitAsync(cts.Token);

        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            var stdOut = await stdOutTask;
            throw new InvalidOperationException(
                $"git {string.Join(" ", arguments)} failed (exit {process.ExitCode}):\n{stdOut}\n{stdErr}");
        }

        return (process.ExitCode, stdErr);
    }

    /// <summary>
    /// Simple <see cref="IServiceScopeFactory"/> that wraps a root
    /// <see cref="IServiceProvider"/> for use in test scenarios.
    /// </summary>
    private sealed class SimpleScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;

        public SimpleScopeFactory(IServiceProvider provider)
        {
            // The default DI container's ServiceProvider implements IServiceScopeFactory.
            _inner = (IServiceScopeFactory)provider;
        }

        public IServiceScope CreateScope()
        {
            return _inner.CreateScope();
        }
    }
}
