using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentController.Api;

/// <summary>
/// Background polling worker that discovers, claims, and orchestrates agent runs
/// through the controller-owned portion of the lifecycle.
///
/// Invokes <see cref="IEnvironmentProvider"/> at
/// <see cref="RunLifecycleState.EnvironmentProvisioning"/>,
/// <see cref="ISourceControlProvider"/> at
/// <see cref="RunLifecycleState.RepositoryCloning"/>, writes context files at
/// <see cref="RunLifecycleState.ContextInjected"/>, and calls
/// <see cref="IAgentRuntime.StartAsync"/> at
/// <see cref="RunLifecycleState.AgentStarting"/> to hand off to the runtime.
///
/// When a real runtime (e.g. <see cref="MockPiMateriaRuntime"/>) is registered,
/// it emits events that drive the run through to completion. When the no-op
/// runtime is registered, the worker records lifecycle events and stops at
/// <see cref="RunLifecycleState.AwaitingResult"/> as before.
///
/// Also detects and recovers stale runs stuck in AwaitingResult or AgentRunning
/// past the configured <see cref="AgentControllerOptions.StaleTimeoutSeconds"/>.
///
/// Seam: kept in the same host as the API for the prototype; a future split can
/// move this into a separate deployable without changing the domain or application contracts.
/// </summary>
public sealed partial class PollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentControllerOptions> _options;
    private readonly IOptionsMonitor<WorkSourceOptionsView> _workSourceOptions;
    private readonly ILogger<PollingWorker> _logger;

    public PollingWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentControllerOptions> options,
        IOptionsMonitor<WorkSourceOptionsView> workSourceOptions,
        ILogger<PollingWorker> logger
    )
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _workSourceOptions = workSourceOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;

        if (!options.WorkerEnabled)
        {
            Log.WorkerDisabled(_logger);

            // Sleep indefinitely until requested to stop, keeping the host alive.
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when the host shuts down.
            }

            return;
        }

        Log.WorkerStarted(
            _logger,
            options.WorkerId,
            options.PollIntervalSeconds,
            options.MaxConcurrentRuns,
            options.StaleTimeoutSeconds
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Expected shutdown.
                break;
            }
            catch (Exception ex)
            {
                Log.PollCycleError(_logger, ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Log.WorkerStopped(_logger);
    }

    /// <summary>
    /// Execute a single poll cycle: check concurrency, discover and claim work,
    /// advance new runs through controller states, and recover stale runs.
    /// </summary>
    private async Task PollCycleAsync(CancellationToken ct)
    {
        // Each poll cycle gets its own DI scope so scoped services (EF Core DbContext,
        // stores, lifecycle service) share a single unit-of-work.
        await using var scope = _scopeFactory.CreateAsyncScope();

        var workSource = scope.ServiceProvider.GetRequiredService<IWorkSource>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IRunLifecycleService>();
        var runStore = scope.ServiceProvider.GetRequiredService<IAgentRunStore>();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        var executionProviderResolver =
            scope.ServiceProvider.GetRequiredService<IExecutionProviderResolver>();
        var sourceControlProvider =
            scope.ServiceProvider.GetRequiredService<ISourceControlProvider>();
        var environmentStore = scope.ServiceProvider.GetRequiredService<IEnvironmentStore>();
        var profileResolver = scope.ServiceProvider.GetRequiredService<IManagedProfileResolver>();
        var reworkCycleStore = scope.ServiceProvider.GetRequiredService<IReworkCycleStore>();
        var options = _options.CurrentValue;

        // ── 1. Check concurrency ─────────────────────────────────────
        var activeCount = await runStore.CountActiveAsync(ct);
        var availableSlots = options.MaxConcurrentRuns - activeCount;

        if (availableSlots <= 0)
        {
            Log.ConcurrencyLimitReached(_logger, activeCount, options.MaxConcurrentRuns);
        }
        else
        {
            // ── 2. Discover eligible work ────────────────────────────
            // AzureDevOpsBoardsWorkSource resolves all enabled managed environments;
            // other sources ignore the provider-specific managed profile set.
            var query = new WorkQuery { MaxResults = availableSlots };
            var candidates = await workSource.FindEligibleAsync(query, ct);

            if (candidates.Count > 0)
            {
                Log.CandidatesDiscovered(_logger, candidates.Count);
            }

            foreach (var candidate in candidates)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await ProcessCandidateAsync(
                        candidate,
                        workSource,
                        lifecycle,
                        workItemStore,
                        runStore,
                        executionProviderResolver,
                        sourceControlProvider,
                        environmentStore,
                        profileResolver,
                        reworkCycleStore,
                        options,
                        ct
                    );
                }
                catch (Exception ex)
                {
                    Log.CandidateProcessingFailed(_logger, ex, candidate.Id, candidate.Title);
                }
            }
        }

        // ── 3. Stale run recovery ────────────────────────────────────
        // StaleTimeout is a non-retryable failure (goes straight to NeedsHuman).
        await RecoverStaleRunsAsync(lifecycle, options, ct);

        // ── 4. Evaluate failed runs for retry ────────────────────────
        // Check recently-failed runs for retryable errors (keepalive-stall,
        // process-exit-without-terminal) and kick off fresh runs if under threshold.
        await EvaluateFailedRunsForRetryAsync(lifecycle, runStore, options, ct);

        // ── 5. Advance Claimed runs (including retry runs) ───────────
        // Retry runs created by EvaluateFailedRunsForRetryAsync sit in Claimed
        // state. Process them through the full controller lifecycle so they
        // actually execute instead of remaining stuck.
        try
        {
            await ProcessClaimedRunsAsync(
                lifecycle,
                runStore,
                workItemStore,
                executionProviderResolver,
                sourceControlProvider,
                environmentStore,
                profileResolver,
                options,
                ct
            );
        }
        catch (Microsoft.Extensions.Options.OptionsValidationException)
        {
            // Repository profile validation failed — skip claimed run processing.
            // The validation error will be surfaced by ProcessCandidateAsync
            // when it runs (or already was in a previous cycle).
        }
    }

    /// <summary>
    /// Claim a work candidate, create an AgentRun, and advance it through
    /// all controller-owned lifecycle states up to <see cref="RunLifecycleState.AwaitingResult"/>.
    ///
    /// At each key milestone the worker invokes the real infrastructure provider:
    /// <list type="number">
    ///   <item><b>EnvironmentProvisioning</b>: calls <see cref="IEnvironmentProvider.CreateAsync"/>,
    ///       persists the environment record, and links it to the run.</item>
    ///   <item><b>RepositoryCloning</b>: resolves the repository profile clone URL,
    ///       calls <see cref="ISourceControlProvider.CloneAsync"/> into the environment's
    ///       workspace directory.</item>
    ///   <item><b>ContextInjected</b>: writes work-item context files into the
    ///       environment's context/ directory.</item>
    ///   <item><b>AgentStarting</b>: calls <see cref="IAgentRuntime.StartAsync"/> with
    ///       the full environment handle, repository checkout, and context metadata.</item>
    /// </list>
    ///
    /// When no-op providers are registered, the worker still records lifecycle
    /// events and stops at <see cref="RunLifecycleState.AwaitingResult"/>.
    /// When real providers (e.g. <see cref="MockPiMateriaRuntime"/>,
    /// <see cref="LocalWorkspaceEnvironmentProvider"/>,
    /// <see cref="LocalGitSourceControlProvider"/>) are registered, real work
    /// is performed and the run can complete end-to-end without Azure DevOps.
    /// </summary>
    private async Task ProcessCandidateAsync(
        WorkCandidate candidate,
        IWorkSource workSource,
        IRunLifecycleService lifecycle,
        IWorkItemStore workItemStore,
        IAgentRunStore runStore,
        IExecutionProviderResolver executionProviderResolver,
        ISourceControlProvider sourceControlProvider,
        IEnvironmentStore environmentStore,
        IManagedProfileResolver profileResolver,
        IReworkCycleStore reworkCycleStore,
        AgentControllerOptions options,
        CancellationToken ct
    )
    {
        // ── Upsert remote candidates into local persistence ────────
        // Azure DevOps Boards (and future remote sources) return
        // WorkCandidate objects that must exist in IWorkItemStore
        // before CreateRunForWorkItemAsync can find them by ID.
        if (candidate.Source != "LocalFake" && candidate.Source != "LocalFile")
        {
            candidate = await workItemStore.UpsertAsync(candidate, ct);
            Log.CandidateUpserted(_logger, candidate.Id, candidate.Source);
        }

        // ── Validate repo:{key} tag against repository profiles ────
        // Resolve the repo key from the work item tags against configured
        // repository profiles. If no profile matches, skip the item and
        // post a clarifying comment so typos are visible on the board.
        // A missing repo: tag is treated as not-eligible (skip silently).
        var resolvedProfiles = await ResolveProfilesAsync(
            candidate,
            workSource,
            profileResolver,
            ct
        );
        if (resolvedProfiles is null)
        {
            return;
        }

        var environmentProvider = executionProviderResolver.ResolveEnvironmentProvider(
            resolvedProfiles.RuntimeEnvironment
        );
        var agentRuntime = executionProviderResolver.ResolveAgentRuntime(
            resolvedProfiles.RuntimeEnvironment
        );

        // ── Run clone preflight before claiming ────────────────────
        // Validate clone-readiness before committing to a claim so
        // misconfiguration surfaces early instead of as a silent hang.
        // The preflight checks: URL parseable, transport prerequisites,
        // and a non-interactive git ls-remote probe.
        var preflightSpec = BuildPreflightSpec(resolvedProfiles.Repository);
        var preflightResult = await sourceControlProvider.CheckClonePreflightAsync(
            preflightSpec,
            ct
        );

        if (!preflightResult.Success)
        {
            Log.CandidateSkippedPreflight(
                _logger,
                candidate.Id,
                candidate.Title,
                preflightResult.Transport,
                preflightResult.Reason
            );

            // Post a clarifying comment for remote sources so the board
            // owner sees the concrete failure reason.
            if (candidate.Source != "LocalFake" && candidate.Source != "LocalFile")
            {
                await PostRepoKeyCommentAsync(
                    workSource,
                    candidate,
                    $"Skipped: clone preflight failed (transport: {preflightResult.Transport}). "
                        + $"{preflightResult.Reason}",
                    ct
                );
            }

            return;
        }

        Log.CandidatePreflightPassed(_logger, candidate.Id, preflightResult.Transport);

        // Claim the work item for exclusive execution
        var claim = new ClaimRequest
        {
            WorkerId = options.WorkerId,
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        var claimResult = await workSource.TryClaimAsync(candidate, claim, ct);

        if (!claimResult.Success)
        {
            Log.ClaimFailed(_logger, candidate.Id, claimResult.FailureReason ?? "unknown");
            return;
        }

        Log.CandidateClaimed(_logger, candidate.Id, candidate.Title);

        // Lifecycle log: claim acquired with full context (before run creation)
        Log.ClaimAcquired(_logger, options.WorkerId, candidate.Id, candidate.Title);

        // Create the agent run (starts in Claimed state, records controller.claimed event)
        var run = await lifecycle.CreateRunForWorkItemAsync(candidate.Id, options.WorkerId, ct);

        Log.RunCreated(_logger, run.RunId, candidate.Id);

        // ── Claim-time ReworkCycle lookup ──────────────────────────
        // Single seam: after the run is created, check for a Pending
        // ReworkCycle materialized by the feedback worker. If present,
        // build a ReworkContext to thread through the happy path.
        // If null, the happy path is completely untouched.
        ReworkContext? reworkContext = null;
        var pendingCycle = await reworkCycleStore.GetPendingForWorkItemAsync(candidate.Id, ct);

        if (pendingCycle is not null)
        {
            var feedbackBundle = JsonSerializer.Deserialize<IReadOnlyList<ReviewThread>>(
                pendingCycle.FeedbackBundleJson,
                JsonReadOptions
            );

            reworkContext = new ReworkContext
            {
                CycleNumber = pendingCycle.CycleNumber,
                PriorRunId = pendingCycle.PriorRunId,
                BranchName = pendingCycle.BranchName,
                PullRequestUrl = pendingCycle.PullRequestUrl,
                BaseCommitSha = pendingCycle.BaseCommitSha,
                FeedbackBundle = feedbackBundle ?? Array.Empty<ReviewThread>(),
            };

            Log.ReworkCycleFound(
                _logger,
                run.RunId,
                candidate.Id,
                pendingCycle.Id,
                pendingCycle.CycleNumber,
                reworkContext.FeedbackBundle.Count
            );
        }

        // ── Advance through controller-owned lifecycle states ──────
        // Each state transition invokes the appropriate provider and
        // records lifecycle events. Failures at environment or clone
        // stages transition the run to Failed.

        RepositoryCheckout? checkout = null;
        EnvironmentHandle? envHandle = null;

        if (ct.IsCancellationRequested)
            return;

        // ── 1. EnvironmentProvisioning ─────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentProvisioning, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.EnvironmentProvisioning,
            "Environment provisioning started.",
            RunLifecycleState.EnvironmentProvisioning,
            ct
        );

        envHandle = await ProvisionEnvironmentAsync(
            run,
            resolvedProfiles.RuntimeEnvironment,
            environmentProvider,
            environmentStore,
            runStore,
            lifecycle,
            ct
        );

        if (envHandle is null)
        {
            // Environment provisioning failed — run is already transitioned to Failed.
            // Release the ADO claim and destroy the workspace to free concurrency.
            await ReleaseClaimAndCleanupAsync(
                run,
                candidate,
                workSource,
                environmentProvider,
                envHandle,
                "Environment provisioning failed.",
                lifecycle,
                ct
            );
            return;
        }

        // ── 2. EnvironmentReady ────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentReady, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.EnvironmentReady,
            "Environment provisioned and ready.",
            RunLifecycleState.EnvironmentReady,
            ct
        );

        if (ct.IsCancellationRequested)
            return;

        // ── 3. RepositoryCloning ───────────────────────────────────
        var transportForLog = resolvedProfiles.Repository.Transport;

        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.RepositoryCloning, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.RepositoryCloning,
            "Repository cloning started.",
            RunLifecycleState.RepositoryCloning,
            ct
        );

        Log.CloneStarting(_logger, run.RunId, candidate.RepoKey ?? "(unknown)", transportForLog);

        checkout = await CloneRepositoryAsync(
            run,
            candidate,
            resolvedProfiles.Repository,
            envHandle,
            sourceControlProvider,
            runStore,
            lifecycle,
            reworkContext,
            ct
        );

        if (checkout is null)
        {
            // Repository clone failed — run is already transitioned to Failed.
            // Release the ADO claim and destroy the workspace to free concurrency.
            await ReleaseClaimAndCleanupAsync(
                run,
                candidate,
                workSource,
                environmentProvider,
                envHandle,
                "Repository clone failed.",
                lifecycle,
                ct
            );
            return;
        }

        // ── 4. RepositoryReady ─────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.RepositoryReady, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.RepositoryReady,
            $"Repository cloned and ready at '{checkout.LocalPath}'.",
            RunLifecycleState.RepositoryReady,
            ct
        );

        // Persist the HEAD commit SHA from the clone so ReworkCycle.BaseCommitSha
        // can be sourced from the prior run later.
        if (!string.IsNullOrWhiteSpace(checkout.CommitSha))
        {
            try
            {
                await runStore.UpdateRuntimeFieldsAsync(
                    run.RunId,
                    new RuntimeFieldUpdate { CommitSha = checkout.CommitSha },
                    ct
                );
            }
            catch
            {
                // Best-effort — the SHA is also captured in controller-run.json.
            }
        }

        Log.WorkspaceReady(_logger, run.RunId, envHandle.RootPath, checkout.LocalPath);

        if (ct.IsCancellationRequested)
            return;

        // ── 5. ContextInjected ─────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.ContextInjected, ct);

        // Fetch discussion comments for ADO-sourced work items so the agent
        // can see clarifications and ongoing discussion context.
        var comments = await FetchCommentsAsync(workSource, candidate, ct);

        await InjectContextAsync(
            run,
            candidate,
            envHandle,
            checkout,
            lifecycle,
            comments,
            reworkContext,
            ct
        );

        // Mark the pending ReworkCycle as Consumed after successful context injection.
        // This closes the audit chain and prevents re-injection of the same bundle on retry.
        if (pendingCycle is not null)
        {
            await reworkCycleStore.MarkConsumedAsync(pendingCycle.Id, run.RunId, ct);
            Log.ReworkCycleConsumed(_logger, run.RunId, pendingCycle.Id, pendingCycle.CycleNumber);
        }

        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.ContextInjected,
            "Context injected into run workspace.",
            RunLifecycleState.ContextInjected,
            ct
        );

        if (ct.IsCancellationRequested)
            return;

        // Lifecycle log: runtime dispatch to agent starting
        Log.RuntimeDispatchStarting(_logger, run.RunId, candidate.Id, agentRuntime.GetType().Name);

        // ── 6. AgentStarting ───────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.AgentStarting, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.AgentStarting,
            "Agent runtime start requested.",
            RunLifecycleState.AgentStarting,
            ct
        );

        await HandOffToRuntimeAsync(
            run,
            candidate,
            resolvedProfiles.RuntimeEnvironment,
            envHandle,
            checkout,
            agentRuntime,
            lifecycle,
            reworkContext,
            ct
        );

        // ── 7. AgentRunning ────────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.AgentRunning, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.AgentRunning,
            "Agent runtime is executing.",
            RunLifecycleState.AgentRunning,
            ct
        );

        // ── 8. AwaitingResult ──────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.AwaitingResult, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.AwaitingResult,
            "Run handed off to runtime, awaiting result.",
            RunLifecycleState.AwaitingResult,
            ct
        );

        Log.RunAdvanced(_logger, run.RunId, candidate.Id);
    }

    /// <summary>
    /// Resolves a candidate's repository and associated environments before claiming it.
    /// Managed profiles are considered alongside appsettings fallback profiles.
    /// </summary>
    private async Task<ResolvedControllerProfiles?> ResolveProfilesAsync(
        WorkCandidate candidate,
        IWorkSource workSource,
        IManagedProfileResolver profileResolver,
        CancellationToken ct
    )
    {
        var repoKey = candidate.RepoKey;

        if (string.IsNullOrWhiteSpace(repoKey))
        {
            if (candidate.Source != "LocalFake" && candidate.Source != "LocalFile")
            {
                await PostRepoKeyCommentAsync(
                    workSource,
                    candidate,
                    "Skipped: work item has no `repo:` tag. Add a tag like `repo:example-service` "
                        + "to associate this item with a configured repository profile.",
                    ct
                );
            }

            Log.CandidateSkippedNoRepoTag(_logger, candidate.Id, candidate.Title);
            return null;
        }

        var resolved = await profileResolver.ResolveForRepositoryAsync(repoKey, ct);
        if (resolved is not null)
        {
            return resolved;
        }

        if (candidate.Source != "LocalFake" && candidate.Source != "LocalFile")
        {
            await PostRepoKeyCommentAsync(
                workSource,
                candidate,
                $"Skipped: no repository profile matches the `repo:{repoKey}` tag. "
                    + "Check for typos or add a matching managed or appsettings profile.",
                ct
            );
        }

        Log.CandidateSkippedNoProfile(_logger, candidate.Id, repoKey);
        return null;
    }

    private static RepositorySpec BuildPreflightSpec(RepositoryProfile repository)
    {
        return new RepositorySpec
        {
            RepoKey = repository.Key,
            CloneUrl = repository.CloneUrl,
            DefaultBranch = repository.DefaultBranch,
            Transport = repository.Transport,
            Profile = repository,
        };
    }

    /// <summary>
    /// Post a clarifying comment on a remote work item explaining why it was skipped.
    /// Best-effort: failures are logged but do not propagate.
    /// </summary>
    private static async Task PostRepoKeyCommentAsync(
        IWorkSource workSource,
        WorkCandidate candidate,
        string comment,
        CancellationToken ct
    )
    {
        var revision =
            candidate.SourceMetadata?.TryGetValue("revision", out var rev) == true ? rev : null;

        var workRef = new ExternalWorkRef
        {
            Source = candidate.Source,
            ExternalId = candidate.ExternalId,
            Url = candidate.ExternalUrl,
            Revision = revision,
            EnvironmentKey = GetAzureDevOpsEnvironmentKey(candidate.SourceMetadata),
        };

        try
        {
            await workSource.AddCommentAsync(workRef, comment, ct);
        }
        catch
        {
            // Best-effort: comment posting failure is not fatal.
            // The item is still skipped regardless.
        }
    }

    /// <summary>
    /// Provision an execution environment by calling <see cref="IEnvironmentProvider.CreateAsync"/>,
    /// persisting the environment record, and linking it to the run.
    /// On failure, transitions the run to <see cref="RunLifecycleState.Failed"/> and returns null.
    /// </summary>
    private async Task<EnvironmentHandle?> ProvisionEnvironmentAsync(
        AgentRunHandle run,
        RuntimeEnvironmentProfile runtimeEnvironment,
        IEnvironmentProvider environmentProvider,
        IEnvironmentStore environmentStore,
        IAgentRunStore runStore,
        IRunLifecycleService lifecycle,
        CancellationToken ct
    )
    {
        try
        {
            var spec = new EnvironmentSpec
            {
                RunId = run.RunId,
                Profile = runtimeEnvironment.Key,
                RootPath = runtimeEnvironment.EnvironmentSettings.WorkspaceRoot,
                RuntimeEnvironmentProfile = runtimeEnvironment,
            };

            var envHandle = await environmentProvider.CreateAsync(spec, ct);

            // Persist the environment record in the store
            var createEnvRequest = new CreateEnvironmentRequest
            {
                RunId = run.RunId,
                ProviderType = envHandle.ProviderType,
                RootPath = envHandle.RootPath,
                Status = envHandle.Status,
            };

            await environmentStore.CreateAsync(createEnvRequest, ct);

            Log.EnvironmentProvisioned(
                _logger,
                run.RunId,
                envHandle.RootPath,
                envHandle.ProviderType
            );

            return envHandle;
        }
        catch (Exception ex)
        {
            Log.EnvironmentProvisioningFailed(_logger, run.RunId, ex);

            await FailRunAsync(
                run,
                runStore,
                lifecycle,
                $"[environment_provisioning_failed] Environment provisioning failed: {ex.Message}",
                ct
            );
            return null;
        }
    }

    /// <summary>
    /// Clones the already-resolved repository into the provisioned environment.
    /// On failure, transitions the run to <see cref="RunLifecycleState.Failed"/> and returns null.
    /// </summary>
    private async Task<RepositoryCheckout?> CloneRepositoryAsync(
        AgentRunHandle run,
        WorkCandidate candidate,
        RepositoryProfile repository,
        EnvironmentHandle envHandle,
        ISourceControlProvider sourceControlProvider,
        IAgentRunStore runStore,
        IRunLifecycleService lifecycle,
        ReworkContext? reworkContext,
        CancellationToken ct
    )
    {
        try
        {
            var repoKey = repository.Key;
            var cloneUrl = repository.CloneUrl;
            var defaultBranch = repository.DefaultBranch;

            // Override default branch when rework context specifies the prior PR branch.
            var effectiveBranch = reworkContext?.BranchName ?? defaultBranch;

            // Guard: if we are overriding the branch for rework, verify it exists on origin.
            // Fail loud with [rework_branch_missing] — no silent fallback to main.
            if (reworkContext?.BranchName is not null && reworkContext.BranchName != defaultBranch)
            {
                var (exitCode, stdErr) = await CheckBranchExistsOnRemoteAsync(
                    cloneUrl,
                    reworkContext.BranchName,
                    ct
                );
                if (exitCode != 0)
                {
                    var errorDetail = stdErr.Length > 0 ? $"\n{stdErr.Trim()}" : string.Empty;
                    throw new InvalidOperationException(
                        $"[rework_branch_missing] Branch '{reworkContext.BranchName}' does not exist on remote "
                            + $"for repository '{repoKey}'. Cannot proceed with rework clone.{errorDetail}"
                    );
                }

                Log.ReworkBranchVerified(_logger, run.RunId, reworkContext.BranchName);
            }

            var spec = new RepositorySpec
            {
                RepoKey = repoKey,
                CloneUrl = cloneUrl,
                DefaultBranch = effectiveBranch,
                Transport = repository.Transport,
                Profile = repository,
            };

            var checkout = await sourceControlProvider.CloneAsync(spec, envHandle, ct);

            Log.RepositoryCloned(_logger, run.RunId, repoKey, checkout.LocalPath, checkout.Branch);

            return checkout;
        }
        catch (Exception ex)
        {
            Log.RepositoryCloneFailed(_logger, run.RunId, candidate.RepoKey, ex);

            await FailRunAsync(
                run,
                runStore,
                lifecycle,
                $"[repository_clone_failed] Repository clone failed: {ex.Message}",
                ct
            );
            return null;
        }
    }

    /// <summary>
    /// Probe the remote repository with <c>git ls-remote</c> to verify a branch exists.
    /// Used as a preflight guard before cloning onto a rework branch override.
    /// Returns (exitCode, stderr) — exitCode 0 means the branch was found.
    /// </summary>
    private static async Task<(int ExitCode, string StdErr)> CheckBranchExistsOnRemoteAsync(
        string cloneUrl,
        string branchName,
        CancellationToken cancellationToken
    )
    {
        // Expand ~ for local paths (git ls-remote won't expand shell globs).
        if (cloneUrl.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            cloneUrl = Path.Combine(home, cloneUrl[2..]);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.StartInfo.ArgumentList.Add("ls-remote");
        process.StartInfo.ArgumentList.Add("--exit-code");
        process.StartInfo.ArgumentList.Add("--heads");
        process.StartInfo.ArgumentList.Add(cloneUrl);
        process.StartInfo.ArgumentList.Add($"refs/heads/{branchName}");

        // Harden against interactive prompts (mirrors LocalGitSourceControlProvider).
        process.StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.EnvironmentVariables["GIT_SSH_COMMAND"] =
            "ssh -o BatchMode=yes -o StrictHostKeyChecking=no";

        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(cts.Token);
            var stdErr = await process.StandardError.ReadToEndAsync(CancellationToken.None);
            return (process.ExitCode, stdErr ?? string.Empty);
        }
        catch (OperationCanceledException)
            when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            { /* best effort */
            }
            return (-1, $"git ls-remote timed out after 30s.");
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Write context files into the environment's context/ directory so the
    /// agent runtime has work-item metadata, acceptance criteria, discussion
    /// comments, and controller run configuration.
    /// This is best-effort: failures are logged but do not fail the run.
    /// </summary>
    private async Task InjectContextAsync(
        AgentRunHandle run,
        WorkCandidate candidate,
        EnvironmentHandle envHandle,
        RepositoryCheckout checkout,
        IRunLifecycleService lifecycle,
        IReadOnlyList<WorkItemComment> comments,
        ReworkContext? reworkContext,
        CancellationToken ct
    )
    {
        var contextDir = Path.Combine(envHandle.RootPath, "context");

        try
        {
            Directory.CreateDirectory(contextDir);

            // ── work-item.md ───────────────────────────────────────
            var workItemMd = BuildWorkItemMarkdown(candidate);
            await File.WriteAllTextAsync(Path.Combine(contextDir, "work-item.md"), workItemMd, ct);

            // ── acceptance-criteria.md ─────────────────────────────
            if (candidate.AcceptanceCriteria is { Count: > 0 })
            {
                var acMd = BuildAcceptanceCriteriaMarkdown(candidate.AcceptanceCriteria);
                await File.WriteAllTextAsync(
                    Path.Combine(contextDir, "acceptance-criteria.md"),
                    acMd,
                    ct
                );
            }

            // ── comments.md ────────────────────────────────────────
            if (comments is { Count: > 0 })
            {
                var commentsMd = BuildCommentsMarkdown(comments);
                await File.WriteAllTextAsync(
                    Path.Combine(contextDir, "comments.md"),
                    commentsMd,
                    ct
                );
            }

            // ── rework-context.md ──────────────────────────────────
            if (reworkContext is not null)
            {
                var reworkMd = BuildReworkContextMarkdown(reworkContext);
                await File.WriteAllTextAsync(
                    Path.Combine(contextDir, "rework-context.md"),
                    reworkMd,
                    ct
                );
            }

            // ── controller-run.json ────────────────────────────────
            var runJson = BuildControllerRunJson(run, candidate, checkout, reworkContext);
            await File.WriteAllTextAsync(
                Path.Combine(contextDir, "controller-run.json"),
                runJson,
                ct
            );

            // ── repository.json ────────────────────────────────────
            var repoJson = JsonSerializer.Serialize(
                new
                {
                    key = checkout.RepoKey,
                    cloneUrl = "<resolved>", // clone URL resolved by source-control provider
                    localPath = checkout.LocalPath,
                    defaultBranch = checkout.Branch,
                    commitSha = checkout.CommitSha,
                },
                JsonWriteOptions
            );
            await File.WriteAllTextAsync(Path.Combine(contextDir, "repository.json"), repoJson, ct);

            Log.ContextInjected(_logger, run.RunId, contextDir);
        }
        catch (Exception ex)
        {
            // Context injection is best-effort — failures are logged but
            // do not prevent the run from proceeding.
            Log.ContextInjectionFailed(_logger, run.RunId, contextDir, ex);
        }
    }

    /// <summary>
    /// Hand off the run to the agent runtime by calling <see cref="IAgentRuntime.StartAsync"/>
    /// with the full environment handle, repository checkout, and context metadata.
    /// Records the runtime handoff result and the runtime-assigned run ID.
    /// </summary>
    private async Task HandOffToRuntimeAsync(
        AgentRunHandle run,
        WorkCandidate candidate,
        RuntimeEnvironmentProfile runtimeEnvironment,
        EnvironmentHandle envHandle,
        RepositoryCheckout checkout,
        IAgentRuntime agentRuntime,
        IRunLifecycleService lifecycle,
        ReworkContext? reworkContext,
        CancellationToken ct
    )
    {
        try
        {
            var spec = new AgentRunSpec
            {
                RunId = run.RunId,
                WorkRef = new ExternalWorkRef
                {
                    Source = candidate.Source,
                    ExternalId = candidate.ExternalId,
                    Url = candidate.ExternalUrl,
                    EnvironmentKey = GetAzureDevOpsEnvironmentKey(candidate.SourceMetadata),
                },
                RepoCheckout = checkout,
                EnvironmentHandle = envHandle,
                RuntimeProfile = runtimeEnvironment.Key,
                RuntimeEnvironmentProfile = runtimeEnvironment,
                ExecutionKind = reworkContext is not null
                    ? ExecutionKind.Rework
                    : ExecutionKind.NewWork,
            };

            var handle = await agentRuntime.StartAsync(spec, ct);

            if (!string.IsNullOrWhiteSpace(handle.RuntimeRunId))
            {
                await lifecycle.AppendControllerEventAsync(
                    run.RunId,
                    ControllerEventTypes.AgentStarting,
                    $"Runtime handoff complete. Runtime assigned ID: {handle.RuntimeRunId}.",
                    new Dictionary<string, object?>
                    {
                        ["runtimeRunId"] = handle.RuntimeRunId,
                        ["runtimeStatus"] = handle.Status.ToString(),
                        ["runtimeProfile"] = runtimeEnvironment.Key,
                        ["runtimeProvider"] = runtimeEnvironment.RuntimeProvider,
                    },
                    ct
                );

                Log.RuntimeHandoffComplete(_logger, run.RunId, handle.RuntimeRunId);
            }
        }
        catch (Exception ex)
        {
            Log.RuntimeHandoffFailed(_logger, run.RunId, ex);
            Log.RuntimeDispatchFailed(_logger, run.RunId, candidate.Id, ex.Message);

            // Runtime start failure is non-fatal for the poll cycle.
            // Record the failure so the run can be diagnosed, but don't
            // crash the worker.
            await lifecycle.AppendControllerEventAsync(
                run.RunId,
                ControllerEventTypes.Failed,
                $"Runtime handoff failed: {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["error"] = ex.Message,
                    ["runtimeType"] = agentRuntime.GetType().Name,
                },
                EventSeverity.Error,
                ct
            );
        }
    }

    /// <summary>
    /// Transition a run to <see cref="RunLifecycleState.Failed"/> and record
    /// the failure reason as a controller event and on the run record.
    /// Best-effort: swallows exceptions during the transition to avoid
    /// masking the original failure.
    /// </summary>
    private static async Task FailRunAsync(
        AgentRunHandle run,
        IAgentRunStore runStore,
        IRunLifecycleService lifecycle,
        string reason,
        CancellationToken ct
    )
    {
        try
        {
            await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.Failed, ct);
        }
        catch (InvalidOperationException)
        {
            // Run may have already been transitioned. Don't mask the original error.
        }

        // Set the error on the run record for diagnostics.
        try
        {
            await runStore.UpdateRuntimeFieldsAsync(
                run.RunId,
                new RuntimeFieldUpdate { Error = reason, FinishedAt = DateTimeOffset.UtcNow },
                ct
            );
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            await lifecycle.AppendControllerEventAsync(
                run.RunId,
                ControllerEventTypes.Failed,
                reason,
                new Dictionary<string, object?>
                {
                    ["runId"] = run.RunId,
                    ["state"] = RunLifecycleState.Failed.ToString(),
                },
                EventSeverity.Error,
                ct
            );
        }
        catch
        {
            // Best-effort logging.
        }
    }

    /// <summary>
    /// Strip agent-controlled tags from the ADO work item (without reverting its state),
    /// destroy the workspace, and log the release. Called when a pre-agent setup
    /// step (environment provisioning, clone) fails so the concurrency slot is
    /// freed immediately.
    ///
    /// Does NOT revert the ADO state — the work item stays in its claimed state
    /// (e.g. "Approved") so it is NOT re-discovered by the polling worker.
    /// The run is left in Failed state; EvaluateFailedRunsForRetryAsync will
    /// evaluate it for retry (incrementing RunAttempt via the existing
    /// RunAttempt/PreviousRunId/MaxRunAttempts chain) or escalate to NeedsHuman.
    ///
    /// Does NOT add an agent-failed tag — a bad runtime environment should not
    /// dirty the external record.
    /// </summary>
    private async Task ReleaseClaimAndCleanupAsync(
        AgentRunHandle run,
        WorkCandidate candidate,
        IWorkSource workSource,
        IEnvironmentProvider environmentProvider,
        EnvironmentHandle? envHandle,
        string reason,
        IRunLifecycleService lifecycle,
        CancellationToken ct
    )
    {
        // 1. Strip agent-controlled tags WITHOUT reverting the ADO state.
        // TargetState = null means "leave state unchanged" — the work item
        // stays in its claimed state so it is NOT re-discovered by FindEligibleAsync.
        // The Failed run will be evaluated by EvaluateFailedRunsForRetryAsync
        // which handles retry (with RunAttempt increment) or escalation.
        if (
            candidate.Source != "LocalFake"
            && candidate.Source != "LocalFile"
            && !string.IsNullOrWhiteSpace(candidate.ExternalId)
        )
        {
            var revision =
                candidate.SourceMetadata?.TryGetValue("revision", out var rev) == true ? rev : null;

            var releaseRequest = new ReleaseClaimRequest
            {
                WorkRef = new ExternalWorkRef
                {
                    Source = candidate.Source,
                    ExternalId = candidate.ExternalId,
                    Url = candidate.ExternalUrl,
                    Revision = revision,
                    EnvironmentKey = GetAzureDevOpsEnvironmentKey(candidate.SourceMetadata),
                },
                WorkerId = _options.CurrentValue.WorkerId,
                TargetState = null, // Do NOT revert state — let retry/escalation handle it
                Reason = reason,
            };

            try
            {
                await workSource.ReleaseClaimAsync(releaseRequest, ct);
                Log.ClaimTagsStripped(_logger, run.RunId, candidate.Id);
            }
            catch (Exception ex)
            {
                // Best-effort: claim release failure is logged but not fatal.
                Log.ClaimReleaseFailed(_logger, run.RunId, candidate.Id, ex);
            }
        }

        // 2. Destroy the workspace
        if (envHandle is not null && !string.IsNullOrWhiteSpace(envHandle.RootPath))
        {
            try
            {
                await environmentProvider.DestroyAsync(envHandle, ct);
                Log.WorkspaceDestroyed(_logger, run.RunId, envHandle.RootPath);
            }
            catch (Exception ex)
            {
                // Best-effort: workspace destruction failure is logged but not fatal.
                Log.WorkspaceDestroyFailed(_logger, run.RunId, envHandle.RootPath, ex);
            }
        }

        // 3. Log the release as a controller event
        try
        {
            await lifecycle.AppendControllerEventAsync(
                run.RunId,
                ControllerEventTypes.ClaimReleased,
                $"Claim tags stripped due to setup failure (state preserved for retry/escalation): {reason}",
                new Dictionary<string, object?> { ["reason"] = reason, ["stateReverted"] = false },
                EventSeverity.Warning,
                ct
            );
        }
        catch
        {
            // Best-effort logging.
        }
    }

    /// <summary>
    /// Append a milestone controller event for a lifecycle state transition.
    /// </summary>
    private static async Task AppendMilestoneEvent(
        IRunLifecycleService lifecycle,
        string runId,
        string eventType,
        string message,
        RunLifecycleState state,
        CancellationToken ct
    )
    {
        await lifecycle.AppendControllerEventAsync(
            runId,
            eventType,
            message,
            new Dictionary<string, object?> { ["state"] = state.ToString() },
            ct
        );
    }

    /// <summary>
    /// Build a Markdown representation of the work item for the context directory.
    /// </summary>
    private static string BuildWorkItemMarkdown(WorkCandidate candidate)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# ");
        sb.AppendLine(candidate.Title);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(candidate.ExternalId))
        {
            sb.Append("**External ID:** ");
            sb.AppendLine(candidate.ExternalId);
        }

        if (!string.IsNullOrWhiteSpace(candidate.ExternalUrl))
        {
            sb.Append("**URL:** ");
            sb.AppendLine(candidate.ExternalUrl);
        }

        sb.Append("**Repository:** ");
        sb.AppendLine(candidate.RepoKey);

        if (candidate.Priority.HasValue)
        {
            sb.Append("**Priority:** ");
            sb.Append(candidate.Priority.Value);
            sb.AppendLine();
        }

        if (candidate.Tags is { Count: > 0 })
        {
            sb.Append("**Tags:** ");
            sb.AppendLine(string.Join(", ", candidate.Tags));
        }

        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(candidate.Description))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(candidate.Description);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build a Markdown representation of acceptance criteria for the context directory.
    /// </summary>
    private static string BuildAcceptanceCriteriaMarkdown(
        IReadOnlyDictionary<string, string> acceptanceCriteria
    )
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Acceptance Criteria");
        sb.AppendLine();

        foreach (var (key, value) in acceptanceCriteria)
        {
            sb.Append("**");
            sb.Append(key);
            sb.Append(":** ");
            sb.AppendLine(value);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Fetch discussion comments for a work candidate from the work source.
    /// Only fetches for remote sources (e.g. Azure DevOps Boards) that support
    /// comment retrieval. Returns an empty list for local sources.
    /// </summary>
    private static async Task<IReadOnlyList<WorkItemComment>> FetchCommentsAsync(
        IWorkSource workSource,
        WorkCandidate candidate,
        CancellationToken ct
    )
    {
        // Only fetch comments for remote sources that support it.
        if (candidate.Source == "LocalFake" || candidate.Source == "LocalFile")
        {
            return Array.Empty<WorkItemComment>();
        }

        var revision =
            candidate.SourceMetadata?.TryGetValue("revision", out var rev) == true ? rev : null;

        var workRef = new ExternalWorkRef
        {
            Source = candidate.Source,
            ExternalId = candidate.ExternalId,
            Url = candidate.ExternalUrl,
            Revision = revision,
            EnvironmentKey = GetAzureDevOpsEnvironmentKey(candidate.SourceMetadata),
        };

        // Use a reasonable default for max comments.
        // The actual bound is configurable via workSource:maxComments in the future.
        const int maxComments = 50;

        try
        {
            return await workSource.GetCommentsAsync(workRef, maxComments, ct);
        }
        catch
        {
            // Best-effort: missing comments do not block the run.
            return Array.Empty<WorkItemComment>();
        }
    }

    private static string? GetAzureDevOpsEnvironmentKey(
        IReadOnlyDictionary<string, string>? metadata
    )
    {
        return metadata?.TryGetValue("azureDevOpsEnvironmentKey", out var key) == true ? key : null;
    }

    /// <summary>
    /// Build a Markdown representation of work item comments for the context directory.
    /// Each comment includes the author, timestamp, and text content.
    /// </summary>
    private static string BuildCommentsMarkdown(IReadOnlyList<WorkItemComment> comments)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Discussion Comments");
        sb.AppendLine();

        for (int i = 0; i < comments.Count; i++)
        {
            var comment = comments[i];

            // Header with author and timestamp
            var headerParts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(comment.Author))
            {
                headerParts.Add(comment.Author);
            }
            if (comment.PostedAt.HasValue)
            {
                headerParts.Add(
                    comment.PostedAt.Value.ToString(
                        "yyyy-MM-dd HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture
                    )
                );
            }

            if (headerParts.Count > 0)
            {
                sb.Append("### ");
                sb.AppendLine(string.Join(" · ", headerParts));
                sb.AppendLine();
            }

            sb.AppendLine(comment.Text);
            sb.AppendLine();

            // Separator between comments (except after the last one)
            if (i < comments.Count - 1)
            {
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build a Markdown representation of the rework context for the context directory.
    /// Schema: preamble instructing the agent to continue an existing PR, prior run metadata,
    /// then review threads sorted by file:line with full reply chains.
    /// </summary>
    private static string BuildReworkContextMarkdown(ReworkContext rework)
    {
        var sb = new System.Text.StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // ── Preamble ───────────────────────────────────────────────
        sb.AppendLine("# Rework Context");
        sb.AppendLine();
        sb.AppendLine("**You are continuing an EXISTING PR. Do not open a new one.**");
        sb.AppendLine(
            "Address the review feedback below, then push your changes to the existing branch."
        );
        sb.AppendLine();

        // ── Prior run metadata ─────────────────────────────────────
        sb.AppendLine("## Prior Run");
        sb.AppendLine();
        sb.Append("- **Cycle:** ");
        sb.AppendLine(rework.CycleNumber.ToString(inv));
        sb.Append("- **Prior Run ID:** ");
        sb.AppendLine(rework.PriorRunId);
        sb.Append("- **Branch:** ");
        sb.AppendLine(rework.BranchName);
        sb.Append("- **Pull Request:** ");
        sb.AppendLine(rework.PullRequestUrl);
        sb.Append("- **Base Commit:** ");
        sb.AppendLine(rework.BaseCommitSha);
        sb.AppendLine();

        // ── Review threads sorted by file:line ─────────────────────
        sb.AppendLine("## Review Threads");
        sb.AppendLine();

        var sorted = rework
            .FeedbackBundle.OrderBy(t => t.FilePath ?? string.Empty)
            .ThenBy(t => t.StartLine)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var thread = sorted[i];

            // Thread header with location
            var locationParts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(thread.FilePath))
            {
                locationParts.Add(thread.FilePath);
            }
            if (thread.StartLine.HasValue)
            {
                locationParts.Add(thread.StartLine.Value.ToString(inv));
            }
            if (locationParts.Count > 0)
            {
                sb.Append("### Thread ");
                sb.Append((i + 1).ToString(inv));
                sb.Append(" \u2014 ");
                sb.Append('`');
                sb.Append(string.Join(":", locationParts));
                sb.Append('`');
                sb.AppendLine();
            }
            else
            {
                sb.Append("### Thread ");
                sb.Append((i + 1).ToString(inv));
                sb.AppendLine(" \u2014 PR-level");
            }
            sb.AppendLine();

            // Thread metadata
            sb.Append("- **Author:** ");
            sb.AppendLine(thread.Author);
            sb.Append("- **Status:** ");
            sb.AppendLine(thread.Status.ToString());
            sb.Append("- **Created:** ");
            sb.AppendLine(thread.CreatedAt.ToString("yyyy-MM-dd HH:mm", inv));
            if (!thread.IsFileLevel && thread.StartLine.HasValue && thread.EndLine.HasValue)
            {
                sb.Append("- **Range:** ");
                sb.Append(thread.StartLine.Value.ToString(inv));
                sb.Append('-');
                sb.AppendLine(thread.EndLine.Value.ToString(inv));
            }
            sb.AppendLine();

            // Full reply chain
            foreach (var comment in thread.Comments)
            {
                var replyPrefix = comment.IsReply ? "> " : "";
                sb.Append(replyPrefix);
                sb.Append("**");
                sb.Append(comment.Author);
                sb.Append("** (");
                sb.Append(comment.CreatedAt.ToString("yyyy-MM-dd HH:mm", inv));
                sb.AppendLine("):");
                sb.AppendLine();
                sb.Append(replyPrefix);
                sb.AppendLine(comment.Body);
                sb.AppendLine();
            }

            // Separator between threads (except after the last one)
            if (i < sorted.Count - 1)
            {
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build the controller-run.json payload, optionally including a rework block
    /// when a <see cref="ReworkContext"/> is present.
    /// </summary>
    private static string BuildControllerRunJson(
        AgentRunHandle run,
        WorkCandidate candidate,
        RepositoryCheckout checkout,
        ReworkContext? rework
    )
    {
        var reworkBlock = rework is not null
            ? new
            {
                cycleNumber = rework.CycleNumber,
                priorRunId = rework.PriorRunId,
                branchName = rework.BranchName,
                pullRequestUrl = rework.PullRequestUrl,
                baseCommitSha = rework.BaseCommitSha,
                feedbackBundle = rework
                    .FeedbackBundle.Select(t => new
                    {
                        threadId = t.ThreadId,
                        author = t.Author,
                        createdAt = t.CreatedAt,
                        status = t.Status,
                        filePath = t.FilePath,
                        startLine = t.StartLine,
                        endLine = t.EndLine,
                        isFileLevel = t.IsFileLevel,
                        comments = t
                            .Comments.Select(c => new
                            {
                                author = c.Author,
                                body = c.Body,
                                createdAt = c.CreatedAt,
                                isReply = c.IsReply,
                            })
                            .ToArray(),
                    })
                    .ToArray(),
            }
            : null;

        return JsonSerializer.Serialize(
            new
            {
                runId = run.RunId,
                workItemId = candidate.Id,
                externalId = candidate.ExternalId,
                externalUrl = candidate.ExternalUrl,
                source = candidate.Source,
                repoKey = candidate.RepoKey,
                repoPath = checkout.LocalPath,
                branch = checkout.Branch,
                commitSha = checkout.CommitSha,
                clonedAt = checkout.ClonedAt,
                startedAt = run.StartedAt,
                rework = reworkBlock,
            },
            JsonWriteOptions
        );
    }

    /// <summary>
    /// Find and recover runs that are stuck in <see cref="RunLifecycleState.AwaitingResult"/>
    /// or <see cref="RunLifecycleState.AgentRunning"/> past the configured stale timeout.
    /// StaleTimeout is a non-retryable failure and goes straight to NeedsHuman (not retried).
    /// </summary>
    private async Task RecoverStaleRunsAsync(
        IRunLifecycleService lifecycle,
        AgentControllerOptions options,
        CancellationToken ct
    )
    {
        var staleTimeout = TimeSpan.FromSeconds(options.StaleTimeoutSeconds);
        var staleRuns = await lifecycle.FindStaleRunsAsync(staleTimeout, ct);

        foreach (var staleRun in staleRuns)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                // StaleTimeout is a non-retryable failure — goes straight to NeedsHuman.
                // The RecoverStaleRunWithRetryAsync method handles the classification.
                var retryRun = await lifecycle.RecoverStaleRunWithRetryAsync(
                    staleRun.RunId,
                    options.WorkerId,
                    options.MaxRunAttempts,
                    ct
                );

                if (retryRun is not null)
                {
                    // A retry run was created — advance it through the lifecycle.
                    Log.StaleRunRetried(
                        _logger,
                        retryRun.RunId,
                        staleRun.RunId,
                        retryRun.RunAttempt
                    );
                }
                else
                {
                    // Escalated to NeedsHuman.
                    Log.StaleRunRecovered(_logger, staleRun.RunId, staleRun.LastHeartbeatAt);
                }
            }
            catch (Exception ex)
            {
                Log.StaleRecoveryFailed(_logger, ex, staleRun.RunId);
            }
        }

        if (staleRuns.Count > 0)
        {
            Log.StaleRecoverySummary(_logger, staleRuns.Count);
        }
    }

    /// <summary>
    /// Evaluate failed runs for retry eligibility. When a run fails with a
    /// retryable error (keepalive-stall, process-exit-without-terminal), the
    /// controller kicks off a fresh run for the same work item from scratch,
    /// up to the configured MaxRunAttempts threshold.
    /// </summary>
    private async Task EvaluateFailedRunsForRetryAsync(
        IRunLifecycleService lifecycle,
        IAgentRunStore runStore,
        AgentControllerOptions options,
        CancellationToken ct
    )
    {
        // Find recently failed runs that haven't been evaluated for retry yet.
        // We look for runs in Failed state that were updated within the last poll cycle.
        var allRuns = await runStore.ListAsync(
            new ListRunsQuery { Status = RunLifecycleState.Failed, MaxResults = 100 },
            ct
        );

        foreach (var failedRun in allRuns)
        {
            if (ct.IsCancellationRequested)
                break;

            // Check if this run has already been evaluated for retry.
            // A run is considered evaluated if a retry run was created for it
            // (i.e., another run exists with PreviousRunId == this run's ID)
            // or if it has been escalated to NeedsHuman.
            var retryRuns = await runStore.ListAsync(new ListRunsQuery { MaxResults = 100 }, ct);
            var hasRetryRun = retryRuns.Any(r => r.PreviousRunId == failedRun.RunId);
            if (hasRetryRun)
            {
                // Already has a retry run — skip.
                continue;
            }

            // Check if the failure is recent (within the last poll cycle).
            // This prevents re-evaluating old failed runs on every poll.
            var timeSinceFailure = DateTimeOffset.UtcNow - failedRun.UpdatedAt;
            if (timeSinceFailure.TotalSeconds > options.PollIntervalSeconds * 2)
            {
                // Too old — this run was likely evaluated in a previous cycle.
                continue;
            }

            try
            {
                var retryRun = await lifecycle.EvaluateRetryAsync(
                    failedRun.RunId,
                    options.WorkerId,
                    options.MaxRunAttempts,
                    ct
                );

                if (retryRun is not null)
                {
                    Log.RetryRunScheduled(
                        _logger,
                        retryRun.RunId,
                        failedRun.RunId,
                        retryRun.RunAttempt,
                        options.MaxRunAttempts
                    );
                }
            }
            catch (Exception ex)
            {
                Log.RetryEvaluationFailed(_logger, ex, failedRun.RunId);
            }
        }
    }

    /// <summary>
    /// Find runs in Claimed state and advance them through the full controller
    /// lifecycle. This handles retry runs created by EvaluateFailedRunsForRetryAsync
    /// which sit in Claimed state waiting to be processed.
    ///
    /// For each Claimed run, looks up the associated work item, builds a
    /// WorkCandidate, and advances through environment provisioning, repository
    /// cloning, context injection, and runtime handoff.
    /// </summary>
    private async Task ProcessClaimedRunsAsync(
        IRunLifecycleService lifecycle,
        IAgentRunStore runStore,
        IWorkItemStore workItemStore,
        IExecutionProviderResolver executionProviderResolver,
        ISourceControlProvider sourceControlProvider,
        IEnvironmentStore environmentStore,
        IManagedProfileResolver profileResolver,
        AgentControllerOptions options,
        CancellationToken ct
    )
    {
        // Find runs stuck in Claimed state (retry runs from EvaluateFailedRunsForRetryAsync).
        var claimedRuns = await runStore.ListAsync(
            new ListRunsQuery { Status = RunLifecycleState.Claimed, MaxResults = 10 },
            ct
        );

        if (claimedRuns.Count == 0)
            return;

        Log.ClaimedRunsDiscovered(_logger, claimedRuns.Count);

        foreach (var run in claimedRuns)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await AdvanceRunThroughLifecycleAsync(
                    run,
                    workItemStore,
                    runStore,
                    executionProviderResolver,
                    sourceControlProvider,
                    environmentStore,
                    profileResolver,
                    lifecycle,
                    ct
                );
            }
            catch (Exception ex)
            {
                Log.ClaimedRunAdvanceFailed(_logger, ex, run.RunId);
            }
        }
    }

    /// <summary>
    /// Advance an existing run (already in Claimed state) through the full
    /// controller-owned lifecycle up to AwaitingResult.
    /// </summary>
    private async Task AdvanceRunThroughLifecycleAsync(
        AgentRunHandle run,
        IWorkItemStore workItemStore,
        IAgentRunStore runStore,
        IExecutionProviderResolver executionProviderResolver,
        ISourceControlProvider sourceControlProvider,
        IEnvironmentStore environmentStore,
        IManagedProfileResolver profileResolver,
        IRunLifecycleService lifecycle,
        CancellationToken ct
    )
    {
        // Look up the work item to build a WorkCandidate for the lifecycle steps.
        var workItem = await workItemStore.GetByIdAsync(run.WorkItemId!, ct);
        if (workItem is null)
        {
            Log.WorkItemNotFoundForRun(_logger, run.RunId, run.WorkItemId);
            return;
        }

        var candidate = new WorkCandidate
        {
            Id = workItem.Id,
            Title = workItem.Title ?? "",
            ExternalId = workItem.ExternalId,
            ExternalUrl = workItem.ExternalUrl,
            Source = workItem.Source ?? "LocalFake",
            RepoKey = workItem.RepoKey,
            Priority = workItem.Priority,
            SourceMetadata = workItem.SourceMetadata,
        };

        var resolvedProfiles = await profileResolver.ResolveForRepositoryAsync(
            candidate.RepoKey,
            ct
        );
        if (resolvedProfiles is null)
        {
            Log.CandidateSkippedNoProfile(_logger, candidate.Id, candidate.RepoKey ?? "(unknown)");
            return;
        }

        var environmentProvider = executionProviderResolver.ResolveEnvironmentProvider(
            resolvedProfiles.RuntimeEnvironment
        );
        var agentRuntime = executionProviderResolver.ResolveAgentRuntime(
            resolvedProfiles.RuntimeEnvironment
        );

        RepositoryCheckout? checkout = null;
        EnvironmentHandle? envHandle = null;

        if (ct.IsCancellationRequested)
            return;

        // ── 1. EnvironmentProvisioning ─────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentProvisioning, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.EnvironmentProvisioning,
            "Environment provisioning started.",
            RunLifecycleState.EnvironmentProvisioning,
            ct
        );

        envHandle = await ProvisionEnvironmentAsync(
            run,
            resolvedProfiles.RuntimeEnvironment,
            environmentProvider,
            environmentStore,
            runStore,
            lifecycle,
            ct
        );

        if (envHandle is null)
        {
            // Environment provisioning failed — run is already transitioned to Failed.
            // The ADO tags were already stripped by the previous ReleaseClaimAndCleanupAsync
            // call (which preserved the ADO state). No workspace to destroy.
            return;
        }

        // ── 2. EnvironmentReady ────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentReady, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.EnvironmentReady,
            "Environment provisioned and ready.",
            RunLifecycleState.EnvironmentReady,
            ct
        );

        if (ct.IsCancellationRequested)
            return;

        // ── 3. RepositoryCloning ───────────────────────────────────
        var transportForLog = resolvedProfiles.Repository.Transport;

        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.RepositoryCloning, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.RepositoryCloning,
            "Repository cloning started.",
            RunLifecycleState.RepositoryCloning,
            ct
        );

        Log.CloneStarting(_logger, run.RunId, candidate.RepoKey ?? "(unknown)", transportForLog);

        checkout = await CloneRepositoryAsync(
            run,
            candidate,
            resolvedProfiles.Repository,
            envHandle,
            sourceControlProvider,
            runStore,
            lifecycle,
            null,
            ct
        );

        if (checkout is null)
        {
            // Repository clone failed — run is already transitioned to Failed.
            if (!string.IsNullOrWhiteSpace(envHandle.RootPath))
            {
                try
                {
                    await environmentProvider.DestroyAsync(envHandle, ct);
                }
                catch
                { /* best-effort */
                }
            }
            return;
        }

        // ── 4. RepositoryReady ─────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.RepositoryReady, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.RepositoryReady,
            $"Repository cloned and ready at '{checkout.LocalPath}'.",
            RunLifecycleState.RepositoryReady,
            ct
        );

        // Persist the HEAD commit SHA from the clone so ReworkCycle.BaseCommitSha
        // can be sourced from the prior run later.
        if (!string.IsNullOrWhiteSpace(checkout.CommitSha))
        {
            try
            {
                await runStore.UpdateRuntimeFieldsAsync(
                    run.RunId,
                    new RuntimeFieldUpdate { CommitSha = checkout.CommitSha },
                    ct
                );
            }
            catch
            {
                // Best-effort — the SHA is also captured in controller-run.json.
            }
        }

        Log.WorkspaceReady(_logger, run.RunId, envHandle.RootPath, checkout.LocalPath);

        if (ct.IsCancellationRequested)
            return;

        // ── 5. ContextInjected ─────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.ContextInjected, ct);

        await InjectContextAsync(
            run,
            candidate,
            envHandle,
            checkout,
            lifecycle,
            Array.Empty<WorkItemComment>(),
            null,
            ct
        );
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.ContextInjected,
            "Context injected into run workspace.",
            RunLifecycleState.ContextInjected,
            ct
        );

        if (ct.IsCancellationRequested)
            return;

        // ── 6. AgentStarting ───────────────────────────────────────
        Log.RuntimeDispatchStarting(_logger, run.RunId, candidate.Id, agentRuntime.GetType().Name);
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.AgentStarting, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.AgentStarting,
            "Agent runtime start requested.",
            RunLifecycleState.AgentStarting,
            ct
        );

        await HandOffToRuntimeAsync(
            run,
            candidate,
            resolvedProfiles.RuntimeEnvironment,
            envHandle,
            checkout,
            agentRuntime,
            lifecycle,
            null,
            ct
        );

        // ── 7. AgentRunning ────────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.AgentRunning, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.AgentRunning,
            "Agent runtime is executing.",
            RunLifecycleState.AgentRunning,
            ct
        );

        // ── 8. AwaitingResult ──────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.AwaitingResult, ct);
        await AppendMilestoneEvent(
            lifecycle,
            run.RunId,
            ControllerEventTypes.AwaitingResult,
            "Run handed off to runtime, awaiting result.",
            RunLifecycleState.AwaitingResult,
            ct
        );

        Log.RunAdvanced(_logger, run.RunId, candidate.Id);
    }

    /// <summary>
    /// Run a single poll cycle synchronously from test code.
    /// Exposed as internal for integration-style tests that wire the full
    /// local-only flow (LocalFile work source + local repo + mock runtime).
    /// </summary>
    internal async Task RunPollCycleForTestingAsync(CancellationToken ct = default)
    {
        await PollCycleAsync(ct);
    }

    /// <summary>
    /// Source-generated high-performance logger methods.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Polling worker is disabled (WorkerEnabled=false). No Azure DevOps, source control, "
                + "environment, or pi-materia work will be performed."
        )]
        public static partial void WorkerDisabled(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Polling worker started. WorkerId={WorkerId}, PollInterval={PollInterval}s, "
                + "MaxConcurrency={MaxConcurrency}, StaleTimeout={StaleTimeout}s"
        )]
        public static partial void WorkerStarted(
            ILogger logger,
            string workerId,
            int pollInterval,
            int maxConcurrency,
            int staleTimeout
        );

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Unhandled exception in polling cycle. Worker will retry after delay."
        )]
        public static partial void PollCycleError(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Polling worker stopped.")]
        public static partial void WorkerStopped(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Concurrency limit reached (active={ActiveCount}, max={MaxConcurrency}). "
                + "Skipping work discovery."
        )]
        public static partial void ConcurrencyLimitReached(
            ILogger logger,
            int activeCount,
            int maxConcurrency
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Discovered {CandidateCount} eligible work candidate(s)."
        )]
        public static partial void CandidatesDiscovered(ILogger logger, int candidateCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Skipping candidate {CandidateId} ({Title}): no `repo:` tag found."
        )]
        public static partial void CandidateSkippedNoRepoTag(
            ILogger logger,
            string candidateId,
            string title
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping candidate {CandidateId}: no repository profile matches `repo:{RepoKey}`."
        )]
        public static partial void CandidateSkippedNoProfile(
            ILogger logger,
            string candidateId,
            string repoKey
        );

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to process work candidate {CandidateId} ({Title})."
        )]
        public static partial void CandidateProcessingFailed(
            ILogger logger,
            Exception ex,
            string candidateId,
            string title
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Could not claim candidate {CandidateId}: {Reason}"
        )]
        public static partial void ClaimFailed(ILogger logger, string candidateId, string reason);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Claimed work candidate {CandidateId} ({Title})."
        )]
        public static partial void CandidateClaimed(
            ILogger logger,
            string candidateId,
            string title
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Upserted work candidate {CandidateId} from source {Source} into local persistence."
        )]
        public static partial void CandidateUpserted(
            ILogger logger,
            string candidateId,
            string source
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Created agent run {RunId} for work item {WorkItemId}."
        )]
        public static partial void RunCreated(ILogger logger, string runId, string workItemId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[rework] Pending ReworkCycle found — runId={RunId}, workItemId={WorkItemId}, "
                + "cycleId={CycleId}, cycleNumber={CycleNumber}, threadCount={ThreadCount}"
        )]
        public static partial void ReworkCycleFound(
            ILogger logger,
            string runId,
            string workItemId,
            string cycleId,
            int cycleNumber,
            int threadCount
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[rework] ReworkCycle consumed — runId={RunId}, cycleId={CycleId}, cycleNumber={CycleNumber}"
        )]
        public static partial void ReworkCycleConsumed(
            ILogger logger,
            string runId,
            string cycleId,
            int cycleNumber
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[rework] Branch '{BranchName}' verified on remote for run {RunId}."
        )]
        public static partial void ReworkBranchVerified(
            ILogger logger,
            string runId,
            string branchName
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Run {RunId} advanced to AwaitingResult for work item {WorkItemId}."
        )]
        public static partial void RunAdvanced(ILogger logger, string runId, string workItemId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Runtime handoff complete for run {RunId}. Runtime ID: {RuntimeRunId}."
        )]
        public static partial void RuntimeHandoffComplete(
            ILogger logger,
            string runId,
            string runtimeRunId
        );

        [LoggerMessage(Level = LogLevel.Error, Message = "Runtime handoff failed for run {RunId}.")]
        public static partial void RuntimeHandoffFailed(ILogger logger, string runId, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Environment provisioned for run {RunId} at '{EnvPath}' (provider: {ProviderType})."
        )]
        public static partial void EnvironmentProvisioned(
            ILogger logger,
            string runId,
            string envPath,
            string providerType
        );

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Environment provisioning failed for run {RunId}."
        )]
        public static partial void EnvironmentProvisioningFailed(
            ILogger logger,
            string runId,
            Exception ex
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Repository cloned for run {RunId}: '{RepoKey}' → '{LocalPath}' (branch: {Branch})."
        )]
        public static partial void RepositoryCloned(
            ILogger logger,
            string runId,
            string repoKey,
            string localPath,
            string branch
        );

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Repository clone failed for run {RunId} (repo: {RepoKey})."
        )]
        public static partial void RepositoryCloneFailed(
            ILogger logger,
            string runId,
            string repoKey,
            Exception ex
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Context files written for run {RunId} in '{ContextDir}'."
        )]
        public static partial void ContextInjected(ILogger logger, string runId, string contextDir);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Context injection failed for run {RunId} in '{ContextDir}'. Run will continue."
        )]
        public static partial void ContextInjectionFailed(
            ILogger logger,
            string runId,
            string contextDir,
            Exception ex
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Recovered stale run {RunId} (last heartbeat: {LastHeartbeat})."
        )]
        public static partial void StaleRunRecovered(
            ILogger logger,
            string runId,
            DateTimeOffset? lastHeartbeat
        );

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to recover stale run {RunId}.")]
        public static partial void StaleRecoveryFailed(ILogger logger, Exception ex, string runId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Recovered {StaleCount} stale run(s)."
        )]
        public static partial void StaleRecoverySummary(ILogger logger, int staleCount);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Released ADO claim for run {RunId} (work item {WorkItemId}). "
                + "Stripped agent-active/agent-worker tags, reverted to '{TargetState}'."
        )]
        public static partial void ClaimReleased(
            ILogger logger,
            string runId,
            string workItemId,
            string targetState
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Stripped agent tags for run {RunId} (work item {WorkItemId}). "
                + "ADO state preserved for retry/escalation evaluation."
        )]
        public static partial void ClaimTagsStripped(
            ILogger logger,
            string runId,
            string workItemId
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to release ADO claim for run {RunId} (work item {WorkItemId})."
        )]
        public static partial void ClaimReleaseFailed(
            ILogger logger,
            string runId,
            string workItemId,
            Exception ex
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Destroyed workspace for run {RunId} at '{WorkspacePath}'."
        )]
        public static partial void WorkspaceDestroyed(
            ILogger logger,
            string runId,
            string workspacePath
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to destroy workspace for run {RunId} at '{WorkspacePath}'."
        )]
        public static partial void WorkspaceDestroyFailed(
            ILogger logger,
            string runId,
            string workspacePath,
            Exception ex
        );

        // ── Lifecycle logging (Claimed → AgentStarting gap) ────────

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[lifecycle] Claim acquired — workerId={WorkerId}, workItemId={WorkItemId}, title='{Title}'."
        )]
        public static partial void ClaimAcquired(
            ILogger logger,
            string workerId,
            string workItemId,
            string title
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping candidate {CandidateId} ({Title}): clone preflight failed "
                + "(transport={Transport}): {Reason}"
        )]
        public static partial void CandidateSkippedPreflight(
            ILogger logger,
            string candidateId,
            string title,
            CloneTransport transport,
            string reason
        );

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Clone preflight passed for candidate {CandidateId} (transport={Transport})."
        )]
        public static partial void CandidatePreflightPassed(
            ILogger logger,
            string candidateId,
            CloneTransport transport
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[lifecycle] Source-control clone starting — runId={RunId}, repoKey={RepoKey}, transport={Transport}."
        )]
        public static partial void CloneStarting(
            ILogger logger,
            string runId,
            string repoKey,
            CloneTransport transport
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[lifecycle] Workspace ready — runId={RunId}, envRoot='{EnvRoot}', repoPath='{RepoPath}'."
        )]
        public static partial void WorkspaceReady(
            ILogger logger,
            string runId,
            string envRoot,
            string repoPath
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "[lifecycle] Runtime dispatch to agent starting — runId={RunId}, workItemId={WorkItemId}, runtimeType={RuntimeType}."
        )]
        public static partial void RuntimeDispatchStarting(
            ILogger logger,
            string runId,
            string workItemId,
            string runtimeType
        );

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "[lifecycle] Runtime dispatch FAILED — runId={RunId}, workItemId={WorkItemId}, reason='{Reason}'."
        )]
        public static partial void RuntimeDispatchFailed(
            ILogger logger,
            string runId,
            string workItemId,
            string reason
        );

        // ── Run-level retry logging ─────────────────────────────────

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Stale run recovered with retry — retryRunId={RetryRunId}, "
                + "staleRunId={StaleRunId}, attempt={Attempt}"
        )]
        public static partial void StaleRunRetried(
            ILogger logger,
            string retryRunId,
            string staleRunId,
            int attempt
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Retry run scheduled — retryRunId={RetryRunId}, "
                + "previousRunId={PreviousRunId}, attempt={Attempt}/{MaxAttempts}"
        )]
        public static partial void RetryRunScheduled(
            ILogger logger,
            string retryRunId,
            string previousRunId,
            int attempt,
            int maxAttempts
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to evaluate retry for run {RunId}."
        )]
        public static partial void RetryEvaluationFailed(
            ILogger logger,
            Exception ex,
            string runId
        );

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Discovered {Count} run(s) in Claimed state awaiting lifecycle advance."
        )]
        public static partial void ClaimedRunsDiscovered(ILogger logger, int count);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to advance Claimed run {RunId} through lifecycle."
        )]
        public static partial void ClaimedRunAdvanceFailed(
            ILogger logger,
            Exception ex,
            string runId
        );

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Work item {WorkItemId} not found for Claimed run {RunId}."
        )]
        public static partial void WorkItemNotFoundForRun(
            ILogger logger,
            string runId,
            string? workItemId
        );
    }
}
