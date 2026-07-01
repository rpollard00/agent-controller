using System.Diagnostics;
using System.Text.Json;
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
/// Integration tests for PollingWorker rework consumption end-to-end.
///
/// Verifies that a Pending ReworkCycle materialized by the feedback worker
/// is correctly consumed by the polling worker:
/// - Clone targets the prior PR branch (not default branch)
/// - rework-context.md is written into the environment context directory
/// - controller-run.json contains the rework block
/// - The ReworkCycle is marked Consumed after successful context injection
/// </summary>
public class ReworkConsumptionTests : IAsyncLifetime
{
    private string _tempRoot = null!;
    private string _tempRepoPath = null!;
    private string _tempRunRoot = null!;
    private string _reworkBranch = "feature/rework-test-branch";
    private string _reworkBranchSha = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"agent-rework-e2e-{Guid.NewGuid():N}");
        _tempRepoPath = Path.Combine(_tempRoot, "test-repo");
        _tempRunRoot = Path.Combine(_tempRoot, "runs");

        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_tempRepoPath);
        Directory.CreateDirectory(_tempRunRoot);

        // Initialize a git repository with main and a feature branch
        // (simulating the prior PR branch that rework should clone onto).
        await RunGitAsync(_tempRepoPath, ["init", "--initial-branch=main"], TimeSpan.FromSeconds(10));
        await RunGitAsync(_tempRepoPath, ["config", "user.email", "test@example.com"], TimeSpan.FromSeconds(5));
        await RunGitAsync(_tempRepoPath, ["config", "user.name", "Test User"], TimeSpan.FromSeconds(5));

        // Initial commit on main
        await File.WriteAllTextAsync(Path.Combine(_tempRepoPath, "README.md"), "# Test Repo");
        await RunGitAsync(_tempRepoPath, ["add", "README.md"], TimeSpan.FromSeconds(5));
        await RunGitAsync(_tempRepoPath, ["commit", "-m", "Initial commit"], TimeSpan.FromSeconds(5));

        // Create the feature branch (simulating the prior PR branch)
        await RunGitAsync(_tempRepoPath, ["checkout", "-b", _reworkBranch], TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(Path.Combine(_tempRepoPath, "feature.txt"), "Feature content");
        await RunGitAsync(_tempRepoPath, ["add", "feature.txt"], TimeSpan.FromSeconds(5));
        await RunGitAsync(_tempRepoPath, ["commit", "-m", "Add feature"], TimeSpan.FromSeconds(5));

        // Get the commit SHA for the rework branch
        _reworkBranchSha = (await RunGitAsyncCapture(_tempRepoPath, ["rev-parse", "HEAD"], TimeSpan.FromSeconds(5))).Trim();
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
        catch { /* best-effort */ }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A Pending ReworkCycle drives the full happy path:
    /// 1. Clone targets the prior PR branch (not main)
    /// 2. rework-context.md is written with the correct schema
    /// 3. controller-run.json contains the rework block
    /// 4. The ReworkCycle is marked Consumed after injection
    /// </summary>
    [Fact]
    public async Task PendingReworkCycle_DrivesCloneOntoPriorBranch_WritesContext_MarksConsumed()
    {
        var dbPath = Path.Combine(_tempRoot, $"test-rework-{Guid.NewGuid():N}.db");

        // ── 1. Build configuration ──────────────────────────────────
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agentController:workerId"] = "test-rework-worker",
                ["agentController:pollIntervalSeconds"] = "10",
                ["agentController:maxConcurrentRuns"] = "1",
                ["agentController:staleTimeoutSeconds"] = "300",
                ["agentController:runRoot"] = _tempRunRoot,
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
                ["localWork:definitions:0:title"] = "Rework Test: Fix review feedback",
                ["localWork:definitions:0:body"] = "Address review feedback from the prior PR.",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
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

        services.AddSingleton<IServiceScopeFactory, SimpleScopeFactory>();

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // ── 3. Run database migrations ──────────────────────────────
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // ── 4. Trigger LocalFileWorkSource initialization and seed a Pending ReworkCycle ──
        // LocalFileWorkSource.EnsureInitializedAsync is called lazily on first method call.
        // We call FindEligibleAsync to trigger it, then use the work item IDs to seed the cycle.
        await using (var seedScope = scopeFactory.CreateAsyncScope())
        {
            var workSource = seedScope.ServiceProvider.GetRequiredService<IWorkSource>();
            var seedCycleStore = seedScope.ServiceProvider.GetRequiredService<IReworkCycleStore>();

            // Trigger EnsureInitializedAsync which upserts configured work items into the store
            var eligibleCandidates = await workSource.FindEligibleAsync(
                new WorkQuery { MaxResults = 10 }, CancellationToken.None);

            Assert.NotEmpty(eligibleCandidates);
            var workItemId = eligibleCandidates[0].Id;

            var feedbackBundle = new List<ReviewThread>
            {
                new ReviewThread
                {
                    ThreadId = "thread-001",
                    Author = "reviewer@example.com",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                    Status = ReviewThreadStatus.Active,
                    FilePath = "feature.txt",
                    StartLine = 1,
                    EndLine = 1,
                    IsFileLevel = false,
                    Comments = new List<ReviewThreadComment>
                    {
                        new ReviewThreadComment
                        {
                            Author = "reviewer@example.com",
                            Body = "Please add more detail to this feature.",
                            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                            IsReply = false,
                        },
                    },
                },
            };

            var feedbackBundleJson = JsonSerializer.Serialize(feedbackBundle);
            var feedbackBundleId = "test-bundle-hash-" + Guid.NewGuid().ToString("N")[..8];

            var cycle = await seedCycleStore.CreateAsync(
                workItemId: workItemId,
                cycleNumber: 1,
                priorRunId: "run-prior-001",
                branchName: _reworkBranch,
                pullRequestUrl: "https://example.com/pr/42",
                baseCommitSha: _reworkBranchSha,
                feedbackBundleJson: feedbackBundleJson,
                feedbackBundleId: feedbackBundleId,
                cancellationToken: CancellationToken.None);

            Assert.NotNull(cycle);
            Assert.Equal(ReworkCycleStatus.Pending, cycle.Status);
            Assert.Equal(_reworkBranch, cycle.BranchName);
            Assert.Equal(workItemId, cycle.WorkItemId);
        }

        // ── 5. Create PollingWorker and run a poll cycle ────────────
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<AgentControllerOptions>>();
        var workSourceMonitor = provider.GetRequiredService<IOptionsMonitor<WorkSourceOptionsView>>();
        var logger = provider.GetRequiredService<ILogger<PollingWorker>>();

        var worker = new PollingWorker(scopeFactory, optionsMonitor, workSourceMonitor, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.RunPollCycleForTestingAsync(cts.Token);

        // Wait for mock runtime to complete
        await Task.Delay(TimeSpan.FromSeconds(1));

        // ── 6. Verify the run was created and progressed ────────────
        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
        var runStore = verifyScope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var cycleStore = verifyScope.ServiceProvider.GetRequiredService<IReworkCycleStore>();
        var workItemStore = verifyScope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        var workItems = await workItemStore.ListAsync(
            new ListWorkItemsQuery { MaxResults = 10 }, CancellationToken.None);
        Assert.NotEmpty(workItems);
        var workItem = workItems[0];

        var runs = await runStore.ListAsync(
            new ListRunsQuery { MaxResults = 10 }, CancellationToken.None);
        Assert.NotEmpty(runs);
        var run = runs[0];
        Assert.Equal(workItem.Id, run.WorkItemId);

        // The run should have progressed past context injection
        Assert.True(
            run.Status >= RunLifecycleState.ContextInjected,
            $"Run should have progressed past context injection, but was: {run.Status}. Error: {run.Error}");

        // ── 7. Verify ReworkCycle was marked Consumed ───────────────
        var pendingAfter = await cycleStore.GetPendingForWorkItemAsync(workItem.Id, CancellationToken.None);
        Assert.Null(pendingAfter);

        var consumedCycles = await cycleStore.ListConsumedAsync(CancellationToken.None);
        Assert.NotEmpty(consumedCycles);

        var consumedCycle = consumedCycles.First(c => c.WorkItemId == workItem.Id);
        Assert.Equal(ReworkCycleStatus.Consumed, consumedCycle.Status);
        Assert.NotNull(consumedCycle.ConsumedAt);
        Assert.Equal(run.RunId, consumedCycle.NewRunId);

        // ── 8. Verify rework-context.md was written ─────────────────
        // AgentRunHandle.EnvironmentId is not set by the worker; look up via DbContext.
        var envEntity = await verifyDb.Environments
            .FirstOrDefaultAsync(e => e.RunId == run.RunId);
        Assert.NotNull(envEntity);

        var contextDir = Path.Combine(envEntity!.RootPath, "context");
        var reworkContextPath = Path.Combine(contextDir, "rework-context.md");

        Assert.True(File.Exists(reworkContextPath),
            $"rework-context.md should exist at {reworkContextPath}");

        var reworkContextContent = await File.ReadAllTextAsync(reworkContextPath);

        // Verify the markdown schema
        Assert.True(reworkContextContent.Contains("# Rework Context"), "Missing '# Rework Context' header");
        Assert.True(reworkContextContent.Contains("You are continuing an EXISTING PR. Do not open a new one."),
            "Missing preamble instruction");
        Assert.True(reworkContextContent.Contains("## Prior Run"), "Missing '## Prior Run' section");
        Assert.True(reworkContextContent.Contains(_reworkBranch),
            $"Missing branch reference '{_reworkBranch}'");
        Assert.True(reworkContextContent.Contains("run-prior-001"), "Missing prior run ID");
        Assert.True(reworkContextContent.Contains("Cycle"), "Missing cycle section");
        Assert.True(reworkContextContent.Contains("## Review Threads"), "Missing '## Review Threads' section");
        Assert.True(reworkContextContent.Contains("feature.txt"), "Missing file path from thread");
        Assert.True(reworkContextContent.Contains("Please add more detail to this feature."),
            "Missing comment body from thread");

        // ── 9. Verify controller-run.json contains the rework block ─
        var controllerRunPath = Path.Combine(contextDir, "controller-run.json");
        Assert.True(File.Exists(controllerRunPath),
            $"controller-run.json should exist at {controllerRunPath}");

        var controllerRunContent = await File.ReadAllTextAsync(controllerRunPath);
        var controllerRunJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(controllerRunContent);
        Assert.NotNull(controllerRunJson);
        Assert.True(controllerRunJson!.ContainsKey("rework"),
            "controller-run.json should contain a 'rework' block");

        var reworkBlock = controllerRunJson!["rework"];
        Assert.Equal(1, reworkBlock.GetProperty("cycleNumber").GetInt32());
        Assert.Equal("run-prior-001", reworkBlock.GetProperty("priorRunId").GetString());
        Assert.Equal(_reworkBranch, reworkBlock.GetProperty("branchName").GetString());

        // ── 10. Verify the repository was cloned onto the rework branch ─
        var repoJsonPath = Path.Combine(contextDir, "repository.json");
        Assert.True(File.Exists(repoJsonPath),
            $"repository.json should exist at {repoJsonPath}");

        var repoJsonContent = await File.ReadAllTextAsync(repoJsonPath);
        var repoJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(repoJsonContent);
        Assert.NotNull(repoJson);
        Assert.Equal(_reworkBranch, repoJson!["defaultBranch"].GetString());
    }

    /// <summary>
    /// When no Pending ReworkCycle exists, the happy path is untouched:
    /// clone targets the default branch, no rework-context.md is written,
    /// and no rework block appears in controller-run.json.
    /// </summary>
    [Fact]
    public async Task NoPendingReworkCycle_HappyPathUntouched_NoReworkArtifacts()
    {
        var dbPath = Path.Combine(_tempRoot, $"test-no-rework-{Guid.NewGuid():N}.db");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agentController:workerId"] = "test-no-rework-worker",
                ["agentController:pollIntervalSeconds"] = "10",
                ["agentController:maxConcurrentRuns"] = "1",
                ["agentController:staleTimeoutSeconds"] = "300",
                ["agentController:runRoot"] = _tempRunRoot,
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
                ["localWork:definitions:0:title"] = "Happy Path: No rework needed",
                ["localWork:definitions:0:body"] = "This work item has no rework cycle.",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
                ["localWork:definitions:0:priority"] = "1",
                ["localWork:definitions:0:status"] = "New",
                ["repositories:test-repo:cloneUrl"] = _tempRepoPath,
                ["repositories:test-repo:defaultBranch"] = "main",
                ["repositories:test-repo:environmentProfile"] = "local-default",
                ["repositories:test-repo:runtimeProfile"] = "pi-materia-default",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddAgentControllerOptions(config);
        services.AddAgentControllerDbContext(config);
        services.AddAgentControllerRepositories();
        services.AddAgentControllerLifecycleService();
        services.AddAgentControllerNoOpProviders();
        services.AddAgentControllerLocalFileWorkSource();
        services.AddAgentControllerLocalGitSourceControl();
        services.AddAgentControllerLocalWorkspaceEnvironment();
        services.AddAgentControllerMockPiMateriaRuntime();
        services.AddSingleton<IServiceScopeFactory, SimpleScopeFactory>();

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // Do NOT seed any ReworkCycle

        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<AgentControllerOptions>>();
        var workSourceMonitor = provider.GetRequiredService<IOptionsMonitor<WorkSourceOptionsView>>();
        var logger = provider.GetRequiredService<ILogger<PollingWorker>>();

        var worker = new PollingWorker(scopeFactory, optionsMonitor, workSourceMonitor, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.RunPollCycleForTestingAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Verify the run progressed
        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
        var runStore = verifyScope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var cycleStore = verifyScope.ServiceProvider.GetRequiredService<IReworkCycleStore>();

        var runs = await runStore.ListAsync(
            new ListRunsQuery { MaxResults = 10 }, CancellationToken.None);

        Assert.NotEmpty(runs);
        var run = runs[0];
        Assert.True(
            run.Status >= RunLifecycleState.ContextInjected,
            $"Run should have progressed past context injection, but was: {run.Status}. Error: {run.Error}");

        // Verify no rework cycle was consumed
        var consumedCycles = await cycleStore.ListConsumedAsync(CancellationToken.None);
        Assert.Empty(consumedCycles);

        // Verify no rework-context.md was written
        var envEntity = await verifyDb.Environments
            .FirstOrDefaultAsync(e => e.RunId == run.RunId);
        Assert.NotNull(envEntity);

        var contextDir = Path.Combine(envEntity!.RootPath, "context");
        var reworkContextPath = Path.Combine(contextDir, "rework-context.md");
        Assert.False(File.Exists(reworkContextPath),
            "rework-context.md should NOT exist when there is no rework cycle.");

        // Verify the repository was cloned onto main (default branch)
        var repoJsonPath = Path.Combine(contextDir, "repository.json");
        Assert.True(File.Exists(repoJsonPath));

        var repoJsonContent = await File.ReadAllTextAsync(repoJsonPath);
        var repoJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(repoJsonContent);
        Assert.NotNull(repoJson);
        Assert.Equal("main", repoJson!["defaultBranch"].GetString());
    }

    /// <summary>
    /// Verify lifecycle events include the full chain through context injection
    /// when a rework cycle is consumed.
    /// </summary>
    [Fact]
    public async Task PendingReworkCycle_LifecycleEventsIncludeFullChain()
    {
        var dbPath = Path.Combine(_tempRoot, $"test-rework-events-{Guid.NewGuid():N}.db");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agentController:workerId"] = "test-rework-events-worker",
                ["agentController:pollIntervalSeconds"] = "10",
                ["agentController:maxConcurrentRuns"] = "1",
                ["agentController:staleTimeoutSeconds"] = "300",
                ["agentController:runRoot"] = _tempRunRoot,
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
                ["localWork:definitions:0:title"] = "Rework Events Test",
                ["localWork:definitions:0:body"] = "Verify lifecycle events for rework consumption.",
                ["localWork:definitions:0:tags:0"] = "agent-ready",
                ["localWork:definitions:0:priority"] = "1",
                ["localWork:definitions:0:status"] = "New",
                ["repositories:test-repo:cloneUrl"] = _tempRepoPath,
                ["repositories:test-repo:defaultBranch"] = "main",
                ["repositories:test-repo:environmentProfile"] = "local-default",
                ["repositories:test-repo:runtimeProfile"] = "pi-materia-default",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddAgentControllerOptions(config);
        services.AddAgentControllerDbContext(config);
        services.AddAgentControllerRepositories();
        services.AddAgentControllerLifecycleService();
        services.AddAgentControllerNoOpProviders();
        services.AddAgentControllerLocalFileWorkSource();
        services.AddAgentControllerLocalGitSourceControl();
        services.AddAgentControllerLocalWorkspaceEnvironment();
        services.AddAgentControllerMockPiMateriaRuntime();
        services.AddSingleton<IServiceScopeFactory, SimpleScopeFactory>();

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentControllerDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // Seed a Pending ReworkCycle (trigger work source init first)
        await using (var seedScope = scopeFactory.CreateAsyncScope())
        {
            var workSource = seedScope.ServiceProvider.GetRequiredService<IWorkSource>();
            var cycleStore = seedScope.ServiceProvider.GetRequiredService<IReworkCycleStore>();

            var eligibleCandidates = await workSource.FindEligibleAsync(
                new WorkQuery { MaxResults = 10 }, CancellationToken.None);
            var workItemId = eligibleCandidates[0].Id;

            var feedbackBundle = new List<ReviewThread>
            {
                new ReviewThread
                {
                    ThreadId = "thread-events-001",
                    Author = "reviewer@example.com",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
                    Status = ReviewThreadStatus.Active,
                    FilePath = "README.md",
                    StartLine = 1,
                    EndLine = 1,
                    IsFileLevel = false,
                    Comments = new List<ReviewThreadComment>
                    {
                        new ReviewThreadComment
                        {
                            Author = "reviewer@example.com",
                            Body = "Update the README.",
                            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
                            IsReply = false,
                        },
                    },
                },
            };

            await cycleStore.CreateAsync(
                workItemId, 1, "run-prior-events", _reworkBranch,
                "https://example.com/pr/99", _reworkBranchSha,
                JsonSerializer.Serialize(feedbackBundle),
                "test-bundle-events-" + Guid.NewGuid().ToString("N")[..8],
                CancellationToken.None);
        }

        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<AgentControllerOptions>>();
        var workSourceMonitor = provider.GetRequiredService<IOptionsMonitor<WorkSourceOptionsView>>();
        var logger = provider.GetRequiredService<ILogger<PollingWorker>>();

        var worker = new PollingWorker(scopeFactory, optionsMonitor, workSourceMonitor, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.RunPollCycleForTestingAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Verify lifecycle events
        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var runStore = verifyScope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var eventStore = verifyScope.ServiceProvider.GetRequiredService<ILifecycleEventStore>();

        var runs = await runStore.ListAsync(
            new ListRunsQuery { MaxResults = 10 }, CancellationToken.None);
        Assert.NotEmpty(runs);
        var run = runs[0];

        var events = await eventStore.ListByRunIdAsync(run.RunId, CancellationToken.None);

        // Verify the full lifecycle chain
        Assert.Contains(events, e => e.EventType == "controller.claimed");
        Assert.Contains(events, e => e.EventType == "controller.environment_provisioning");
        Assert.Contains(events, e => e.EventType == "controller.environment_ready");
        Assert.Contains(events, e => e.EventType == "controller.repository_cloning");
        Assert.Contains(events, e => e.EventType == "controller.repository_ready");
        Assert.Contains(events, e => e.EventType == "controller.context_injected");
        Assert.Contains(events, e => e.EventType == "controller.agent_starting");
    }

    // ── Helpers ─────────────────────────────────────────────────────

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
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stdErr}");
        }
    }

    private static async Task<string> RunGitAsyncCapture(string workingDir, string[] args, TimeSpan timeout)
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
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stdErr}");
        }

        return await stdOutTask;
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
