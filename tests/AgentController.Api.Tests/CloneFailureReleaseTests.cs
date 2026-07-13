using System.Diagnostics;
using System.Globalization;
using AgentController.Api;
using AgentController.Application;
using AgentController.Application.Abstractions;
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
/// Integration tests for the clone-failure release path and preflight behavior in PollingWorker.
///
/// The preflight check (CheckClonePreflightAsync) runs BEFORE the claim attempt.
/// When preflight fails, the candidate is skipped without creating a run.
///
/// When preflight passes but the clone fails, the worker:
/// - Transitions the run to Failed
/// - Releases the ADO claim (strips agent-active/agent-worker tags)
/// - Destroys the workspace
/// - Frees the concurrency slot
///
/// Tests cover:
/// - Preflight failure skips candidates without pinning claims
/// - Preflight flags unreachable/misconfigured clone URLs
/// - Preflight passes for valid repos (allowing the full pipeline to proceed)
/// - Concurrency slot is freed after terminal states
/// - The full pipeline works end-to-end when preflight passes
/// </summary>
public class CloneFailureReleaseTests : IAsyncLifetime
{
    private string _tempRoot = null!;
    private string _tempDbPath = null!;
    private string _tempRunRoot = null!;

    public Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"agent-clone-fail-{Guid.NewGuid():N}");
        _tempDbPath = Path.Combine(_tempRoot, "test.db");
        _tempRunRoot = Path.Combine(_tempRoot, "runs");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_tempRunRoot);
        return Task.CompletedTask;
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
        { /* best-effort */
        }
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // Preflight failure: candidate skipped without claim
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When preflight fails (unreachable HTTPS URL), the candidate is skipped
    /// without creating a run or pinning a claim.
    /// </summary>
    [Fact]
    public async Task PreflightFailure_UnreachableHttpsUrl_CandidateSkippedWithoutClaim()
    {
        var config = BuildConfig(
            _tempDbPath,
            _tempRunRoot,
            repoCloneUrl: "https://this-host-does-not-exist-12345.example.com/repo.git",
            repoTransport: "HttpsPat"
        );

        var (scopeFactory, worker) = BuildWorkerAndScope(config);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await worker.RunPollCycleForTestingAsync(cts.Token);

        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var runStore = verifyScope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var runs = await runStore.ListAsync(
            new ListRunsQuery { MaxResults = 10 },
            CancellationToken.None
        );

        Assert.Empty(runs);
    }

    /// <summary>
    /// When preflight fails (non-existent local path), the candidate is skipped.
    /// </summary>
    [Fact]
    public async Task PreflightFailure_NonExistentLocalPath_CandidateSkipped()
    {
        var config = BuildConfig(
            _tempDbPath,
            _tempRunRoot,
            repoCloneUrl: Path.Combine(_tempRoot, "does-not-exist-for-preflight"),
            repoTransport: "Local"
        );

        var (scopeFactory, worker) = BuildWorkerAndScope(config);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.RunPollCycleForTestingAsync(cts.Token);

        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var runStore = verifyScope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var runs = await runStore.ListAsync(
            new ListRunsQuery { MaxResults = 10 },
            CancellationToken.None
        );

        Assert.Empty(runs);
    }

    /// <summary>
    /// When preflight fails (empty clone URL), the candidate is skipped.
    /// </summary>
    [Fact]
    public async Task PreflightFailure_EmptyCloneUrl_CandidateSkipped()
    {
        var config = BuildConfig(
            _tempDbPath,
            _tempRunRoot,
            repoCloneUrl: "",
            repoTransport: "Local"
        );

        var (scopeFactory, worker) = BuildWorkerAndScope(config);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.RunPollCycleForTestingAsync(cts.Token);

        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var runStore = verifyScope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var runs = await runStore.ListAsync(
            new ListRunsQuery { MaxResults = 10 },
            CancellationToken.None
        );

        Assert.Empty(runs);
    }

    /// <summary>
    /// When preflight fails (non-git directory), git ls-remote catches it
    /// before any claim is made.
    /// </summary>
    [Fact]
    public async Task PreflightFailure_NonGitDirectory_CandidateSkipped()
    {
        // Create a directory that exists but is NOT a git repo.
        // Preflight's git ls-remote probe will fail for this.
        var fakeRepoPath = Path.Combine(_tempRoot, "not-a-git-repo");
        Directory.CreateDirectory(fakeRepoPath);
        await File.WriteAllTextAsync(Path.Combine(fakeRepoPath, "readme.txt"), "not a repo");

        var config = BuildConfig(
            _tempDbPath,
            _tempRunRoot,
            repoCloneUrl: fakeRepoPath,
            repoTransport: "Local"
        );

        var (scopeFactory, worker) = BuildWorkerAndScope(config);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.RunPollCycleForTestingAsync(cts.Token);

        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var runStore = verifyScope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var runs = await runStore.ListAsync(
            new ListRunsQuery { MaxResults = 10 },
            CancellationToken.None
        );

        // Preflight's git ls-remote catches non-git directories before claim.
        Assert.Empty(runs);
    }

    // ═══════════════════════════════════════════════════════════════
    // Preflight passes: full pipeline proceeds end-to-end
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When preflight passes (valid local git repo), the full pipeline proceeds:
    /// claim → environment provisioning → clone → context injection → runtime handoff.
    /// This proves the preflight correctly allows valid repos through.
    /// </summary>
    [Fact]
    public async Task PreflightPass_ValidRepo_FullPipelineProceeds()
    {
        var validRepo = Path.Combine(_tempRoot, "valid-repo-pipeline");
        Directory.CreateDirectory(validRepo);
        await RunGitAsync(validRepo, ["init", "--initial-branch=main"], TimeSpan.FromSeconds(10));
        await RunGitAsync(
            validRepo,
            ["config", "user.email", "test@example.com"],
            TimeSpan.FromSeconds(5)
        );
        await RunGitAsync(validRepo, ["config", "user.name", "Test User"], TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(Path.Combine(validRepo, "README.md"), "# Valid Repo");
        await RunGitAsync(validRepo, ["add", "README.md"], TimeSpan.FromSeconds(5));
        await RunGitAsync(validRepo, ["commit", "-m", "Initial commit"], TimeSpan.FromSeconds(5));

        var config = BuildConfig(
            _tempDbPath,
            _tempRunRoot,
            repoCloneUrl: validRepo,
            repoTransport: "Local"
        );

        var (scopeFactory, worker) = BuildWorkerAndScope(config);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.RunPollCycleForTestingAsync(cts.Token);

        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var runStore = verifyScope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var runs = await runStore.ListAsync(
            new ListRunsQuery { MaxResults = 10 },
            CancellationToken.None
        );

        Assert.NotEmpty(runs);
        var run = runs[0];

        // The run should have progressed past clone (preflight passed, clone succeeded)
        Assert.True(
            run.Status >= RunLifecycleState.RepositoryReady,
            $"Run should have progressed past clone, but is in state: {run.Status}. Error: {run.Error}"
        );

        // Verify lifecycle events show the full pipeline
        var eventStore = verifyScope.ServiceProvider.GetRequiredService<ILifecycleEventStore>();
        var events = await eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);

        Assert.Contains(events, e => e.EventType == "controller.claimed");
        Assert.Contains(events, e => e.EventType == "controller.environment_provisioning");
        Assert.Contains(events, e => e.EventType == "controller.repository_cloning");
        Assert.Contains(events, e => e.EventType == "controller.repository_ready");
    }

    // ═══════════════════════════════════════════════════════════════
    // Concurrency slot management
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// After a run reaches a terminal state (Failed), the concurrency slot is freed.
    /// CountActiveAsync should not count terminal runs.
    /// </summary>
    [Fact]
    public async Task TerminalRun_ConcurrencySlotFreed()
    {
        // Create a valid repo so the pipeline can proceed
        var validRepo = Path.Combine(_tempRoot, "valid-repo-concurrency");
        Directory.CreateDirectory(validRepo);
        await RunGitAsync(validRepo, ["init", "--initial-branch=main"], TimeSpan.FromSeconds(10));
        await RunGitAsync(
            validRepo,
            ["config", "user.email", "test@example.com"],
            TimeSpan.FromSeconds(5)
        );
        await RunGitAsync(validRepo, ["config", "user.name", "Test User"], TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(Path.Combine(validRepo, "README.md"), "# Valid Repo");
        await RunGitAsync(validRepo, ["add", "README.md"], TimeSpan.FromSeconds(5));
        await RunGitAsync(validRepo, ["commit", "-m", "Initial commit"], TimeSpan.FromSeconds(5));

        var config = BuildConfig(
            _tempDbPath,
            _tempRunRoot,
            repoCloneUrl: validRepo,
            repoTransport: "Local",
            maxConcurrentRuns: 1
        );

        var (scopeFactory, worker) = BuildWorkerAndScope(config);

        // Run the poll cycle — the run will progress to AwaitingResult
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.RunPollCycleForTestingAsync(cts1.Token);

        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var runStore = verifyScope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var runs = await runStore.ListAsync(
            new ListRunsQuery { MaxResults = 10 },
            CancellationToken.None
        );

        Assert.NotEmpty(runs);
        var run = runs[0];

        // Force the run to a terminal state to test concurrency slot freeing
        await runStore.UpdateRuntimeFieldsAsync(
            run.RunId,
            new RuntimeFieldUpdate
            {
                Error = "Simulated failure for concurrency test",
                FinishedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None
        );

        var lifecycle = verifyScope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
        await lifecycle.TransitionAsync(
            run.RunId,
            RunLifecycleState.Failed,
            CancellationToken.None
        );

        // The Failed state is terminal, so CountActiveAsync should not count it
        var activeCount = await runStore.CountActiveAsync(CancellationToken.None);
        Assert.Equal(0, activeCount);
    }

    // ═══════════════════════════════════════════════════════════════
    // Clone-failure release path verification (via lifecycle service)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Verify that the RunLifecycleService correctly transitions runs to Failed
    /// and records the failure event. This is the application-layer contract
    /// that the PollingWorker uses when clone fails.
    /// </summary>
    [Fact]
    public async Task CloneFailurePath_LifecycleService_TransitionsToFailed()
    {
        // Build a minimal DI container with the lifecycle service
        var services = new ServiceCollection();
        services.AddSilentLogging();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["persistence:provider"] = "Sqlite",
                    ["persistence:connectionString"] = $"Data Source={_tempDbPath}",
                }
            )
            .Build();

        services.AddAgentControllerOptions(config);
        services.AddAgentControllerDbContext(config);
        services.AddAgentControllerRepositories();
        services.AddAgentControllerLifecycleService();
        services.AddAgentControllerNoOpProviders();
        services.AddSingleton<IServiceScopeFactory, SimpleScopeFactory>();

        var provider = services.BuildServiceProvider();

        // Ensure database is created
        await using (
            var initScope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope()
        )
        {
            var db = initScope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        await using var scope = provider
            .GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
        var runStore = scope.ServiceProvider.GetRequiredService<IAgentRunStore>();

        // Create a work item
        var wi = await workItemStore.CreateAsync(
            new CreateWorkItemRequest { RepoKey = "test-repo", Title = "Test" },
            CancellationToken.None
        );

        // Create a run
        var run = await lifecycle.CreateRunForWorkItemAsync(
            wi.Id,
            "test-worker",
            CancellationToken.None
        );

        // Advance through states to RepositoryCloning
        await lifecycle.TransitionAsync(
            run.RunId,
            RunLifecycleState.EnvironmentProvisioning,
            CancellationToken.None
        );
        await lifecycle.TransitionAsync(
            run.RunId,
            RunLifecycleState.EnvironmentReady,
            CancellationToken.None
        );
        await lifecycle.TransitionAsync(
            run.RunId,
            RunLifecycleState.RepositoryCloning,
            CancellationToken.None
        );

        // Simulate clone failure: transition to Failed
        await lifecycle.TransitionAsync(
            run.RunId,
            RunLifecycleState.Failed,
            CancellationToken.None
        );

        // Record the failure event
        await lifecycle.AppendControllerEventAsync(
            run.RunId,
            "controller.failed",
            "Repository clone failed: git clone failed for repository 'test-repo'",
            new Dictionary<string, object?>
            {
                ["runId"] = run.RunId,
                ["state"] = RunLifecycleState.Failed.ToString(),
            },
            EventSeverity.Error,
            CancellationToken.None
        );

        // Verify the run is in Failed state
        var updatedRun = await runStore.GetByIdAsync(run.RunId, CancellationToken.None);
        Assert.NotNull(updatedRun);
        Assert.Equal(RunLifecycleState.Failed, updatedRun!.Status);

        // Verify the failure event was recorded
        var eventStore = scope.ServiceProvider.GetRequiredService<ILifecycleEventStore>();
        var events = await eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);
        Assert.Contains(events, e => e.EventType == "controller.failed");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static IConfiguration BuildConfig(
        string dbPath,
        string runRoot,
        string repoCloneUrl,
        string repoTransport,
        int maxConcurrentRuns = 2
    )
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["agentController:workerId"] = "test-clone-fail-worker",
                    ["agentController:pollIntervalSeconds"] = "10",
                    ["agentController:maxConcurrentRuns"] = maxConcurrentRuns.ToString(
                        CultureInfo.InvariantCulture
                    ),
                    ["agentController:staleTimeoutSeconds"] = "300",
                    ["agentController:runRoot"] = runRoot,
                    ["agentController:retainSuccessfulRuns"] = "true",
                    ["agentController:retainFailedRuns"] = "true",
                    ["agentController:workerEnabled"] = "true",
                    ["persistence:provider"] = "Sqlite",
                    ["persistence:connectionString"] = $"Data Source={dbPath}",
                    ["workSource:provider"] = "LocalFile",
                    ["sourceControl:provider"] = "LocalGit",
                    ["environmentProvider:provider"] = "LocalWorkspace",
                    ["runtime:provider"] = "MockPiMateria",
                    ["runtime:defaultMateriaLoadout"] = "success-pr",
                    ["localWork:definitions:0:repoKey"] = "test-repo",
                    ["localWork:definitions:0:title"] = "Test: clone failure",
                    ["localWork:definitions:0:body"] = "Test work item for clone failure path.",
                    ["localWork:definitions:0:tags:0"] = "agent-ready",
                    ["localWork:definitions:0:priority"] = "1",
                    ["localWork:definitions:0:status"] = "New",
                    ["repositories:test-repo:cloneUrl"] = repoCloneUrl,
                    ["repositories:test-repo:transport"] = repoTransport,
                    ["repositories:test-repo:defaultBranch"] = "main",
                    ["repositories:test-repo:environmentProfile"] = "local-default",
                    ["repositories:test-repo:runtimeProfile"] = "pi-materia-default",
                }
            )
            .Build();
    }

    private static (IServiceScopeFactory ScopeFactory, PollingWorker Worker) BuildWorkerAndScope(
        IConfiguration config
    )
    {
        var services = new ServiceCollection();

        services.AddSilentLogging();

        services.AddAgentControllerOptions(config);
        services.AddAgentControllerDbContext(config);
        services.AddAgentControllerRepositories();
        services.AddAgentControllerLifecycleService();
        services.AddAgentControllerNoOpProviders();

        services.AddApplicationHandlers();

        // Wire real providers
        services.AddAgentControllerLocalFileWorkSource();
        services.AddAgentControllerLocalGitSourceControl();
        services.AddAgentControllerLocalWorkspaceEnvironment();
        services.AddAgentControllerMockPiMateriaRuntime();

        services.AddSingleton<IServiceScopeFactory, SimpleScopeFactory>();

        var provider = services.BuildServiceProvider();

        // Ensure database is created
#pragma warning disable xUnit1031 // Test methods should not use blocking operations
        using (var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            db.Database.EnsureCreatedAsync().Wait();
        }
#pragma warning restore xUnit1031

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<AgentControllerOptions>>();
        var workSourceMonitor = provider.GetRequiredService<
            IOptionsMonitor<WorkSourceOptionsView>
        >();
        var logger = provider.GetRequiredService<ILogger<PollingWorker>>();

        var worker = new PollingWorker(scopeFactory, optionsMonitor, workSourceMonitor, logger);

        return (scopeFactory, worker);
    }

    private static async Task RunGitAsync(string workingDir, string[] args, TimeSpan timeout)
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
        using var cts = new CancellationTokenSource(timeout);
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        if (process.ExitCode != 0)
        {
            var stdErr = await stdErrTask;
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stdErr}"
            );
        }
    }

    /// <summary>
    /// Simple IServiceScopeFactory that wraps a root IServiceProvider.
    /// </summary>
    private sealed class SimpleScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;

        public SimpleScopeFactory(IServiceProvider provider)
        {
            _inner = (IServiceScopeFactory)provider;
        }

        public IServiceScope CreateScope() => _inner.CreateScope();
    }
}
