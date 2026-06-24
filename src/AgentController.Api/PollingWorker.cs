using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Json;

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
/// Also detects and recovers stale runs stuck in AwaitingResult past the
/// configured <see cref="AgentControllerOptions.StaleTimeoutSeconds"/>.
///
/// Seam: kept in the same host as the API for the prototype; a future split can
/// move this into a separate deployable without changing the domain or application contracts.
/// </summary>
public sealed partial class PollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentControllerOptions> _options;
    private readonly ILogger<PollingWorker> _logger;

    public PollingWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentControllerOptions> options,
        ILogger<PollingWorker> logger
    )
    {
        _scopeFactory = scopeFactory;
        _options = options;
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
            options.StaleTimeoutSeconds);

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
                await Task.Delay(
                    TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
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
        var agentRuntime = scope.ServiceProvider.GetRequiredService<IAgentRuntime>();
        var environmentProvider = scope.ServiceProvider.GetRequiredService<IEnvironmentProvider>();
        var sourceControlProvider = scope.ServiceProvider.GetRequiredService<ISourceControlProvider>();
        var environmentStore = scope.ServiceProvider.GetRequiredService<IEnvironmentStore>();
        var repositoryStore = scope.ServiceProvider.GetRequiredService<IRepositoryStore>();
        var repoConfig = scope.ServiceProvider.GetRequiredService<IOptions<Dictionary<string, RepositoryProfileOptions>>>();
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
                        agentRuntime,
                        runStore,
                        environmentProvider,
                        sourceControlProvider,
                        environmentStore,
                        repositoryStore,
                        repoConfig.Value,
                        options,
                        ct);
                }
                catch (Exception ex)
                {
                    Log.CandidateProcessingFailed(
                        _logger, ex, candidate.Id, candidate.Title);
                }
            }
        }

        // ── 3. Stale run recovery ────────────────────────────────────
        await RecoverStaleRunsAsync(lifecycle, options, ct);
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
        IAgentRuntime agentRuntime,
        IAgentRunStore runStore,
        IEnvironmentProvider environmentProvider,
        ISourceControlProvider sourceControlProvider,
        IEnvironmentStore environmentStore,
        IRepositoryStore repositoryStore,
        IReadOnlyDictionary<string, RepositoryProfileOptions> repoConfig,
        AgentControllerOptions options,
        CancellationToken ct)
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
        if (!await ValidateRepoKeyAsync(
            candidate, workSource, repoConfig, ct))
        {
            return;
        }

        // Claim the work item for exclusive execution
        var claim = new ClaimRequest
        {
            WorkerId = options.WorkerId,
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        var claimResult = await workSource.TryClaimAsync(candidate, claim, ct);

        if (!claimResult.Success)
        {
            Log.ClaimFailed(
                _logger,
                candidate.Id,
                claimResult.FailureReason ?? "unknown");
            return;
        }

        Log.CandidateClaimed(_logger, candidate.Id, candidate.Title);

        // Create the agent run (starts in Claimed state, records controller.claimed event)
        var run = await lifecycle.CreateRunForWorkItemAsync(
            candidate.Id,
            options.WorkerId,
            ct);

        Log.RunCreated(_logger, run.RunId, candidate.Id);

        // ── Advance through controller-owned lifecycle states ──────
        // Each state transition invokes the appropriate provider and
        // records lifecycle events. Failures at environment or clone
        // stages transition the run to Failed.

        RepositoryCheckout? checkout = null;
        EnvironmentHandle? envHandle = null;

        if (ct.IsCancellationRequested) return;

        // ── 1. EnvironmentProvisioning ─────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentProvisioning, ct);
        await AppendMilestoneEvent(lifecycle, run.RunId, ControllerEventTypes.EnvironmentProvisioning,
            "Environment provisioning started.", RunLifecycleState.EnvironmentProvisioning, ct);

        envHandle = await ProvisionEnvironmentAsync(
            run, candidate, environmentProvider, environmentStore, runStore, lifecycle, ct);

        if (envHandle is null)
        {
            // Environment provisioning failed — run is already transitioned to Failed.
            return;
        }

        // ── 2. EnvironmentReady ────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.EnvironmentReady, ct);
        await AppendMilestoneEvent(lifecycle, run.RunId, ControllerEventTypes.EnvironmentReady,
            "Environment provisioned and ready.", RunLifecycleState.EnvironmentReady, ct);

        if (ct.IsCancellationRequested) return;

        // ── 3. RepositoryCloning ───────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.RepositoryCloning, ct);
        await AppendMilestoneEvent(lifecycle, run.RunId, ControllerEventTypes.RepositoryCloning,
            "Repository cloning started.", RunLifecycleState.RepositoryCloning, ct);

        checkout = await CloneRepositoryAsync(
            run, candidate, envHandle, sourceControlProvider, repositoryStore, repoConfig, runStore, lifecycle, ct);

        if (checkout is null)
        {
            // Repository clone failed — run is already transitioned to Failed.
            return;
        }

        // ── 4. RepositoryReady ─────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.RepositoryReady, ct);
        await AppendMilestoneEvent(lifecycle, run.RunId, ControllerEventTypes.RepositoryReady,
            $"Repository cloned and ready at '{checkout.LocalPath}'.",
            RunLifecycleState.RepositoryReady, ct);

        if (ct.IsCancellationRequested) return;

        // ── 5. ContextInjected ─────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.ContextInjected, ct);
        await InjectContextAsync(
            run, candidate, envHandle, checkout, lifecycle, ct);
        await AppendMilestoneEvent(lifecycle, run.RunId, ControllerEventTypes.ContextInjected,
            "Context injected into run workspace.", RunLifecycleState.ContextInjected, ct);

        if (ct.IsCancellationRequested) return;

        // ── 6. AgentStarting ───────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.AgentStarting, ct);
        await AppendMilestoneEvent(lifecycle, run.RunId, ControllerEventTypes.AgentStarting,
            "Agent runtime start requested.", RunLifecycleState.AgentStarting, ct);

        await HandOffToRuntimeAsync(run, candidate, envHandle, checkout, agentRuntime, lifecycle, ct);

        // ── 7. AgentRunning ────────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.AgentRunning, ct);
        await AppendMilestoneEvent(lifecycle, run.RunId, ControllerEventTypes.AgentRunning,
            "Agent runtime is executing.", RunLifecycleState.AgentRunning, ct);

        // ── 8. AwaitingResult ──────────────────────────────────────
        await lifecycle.TransitionAsync(run.RunId, RunLifecycleState.AwaitingResult, ct);
        await AppendMilestoneEvent(lifecycle, run.RunId, ControllerEventTypes.AwaitingResult,
            "Run handed off to runtime, awaiting result.", RunLifecycleState.AwaitingResult, ct);

        Log.RunAdvanced(_logger, run.RunId, candidate.Id);
    }

    /// <summary>
    /// Validate the <c>repo:{key}</c> tag on a work candidate against configured
    /// repository profiles before claiming.
    ///
    /// <list type="bullet">
    ///   <item><b>Missing repo: tag (empty RepoKey):</b> treat as not-eligible.
    ///   Skip silently for local sources, post a clarifying comment for remote sources.</item>
    ///   <item><b>repo: tag present but no matching profile:</b> skip the item and
    ///   post a clarifying comment (e.g. "Skipped: no repository profile matches
    ///   the `repo:xxx` tag") so typos are visible on the board.</item>
    ///   <item><b>repo: tag matches a profile:</b> return true to allow the
    ///   candidate to proceed to claim.</item>
    /// </list>
    ///
    /// This validation runs <b>before</b> the claim attempt so we don't waste
    /// a claim on an item that can't be processed.
    /// </summary>
    private async Task<bool> ValidateRepoKeyAsync(
        WorkCandidate candidate,
        IWorkSource workSource,
        IReadOnlyDictionary<string, RepositoryProfileOptions> repoConfig,
        CancellationToken ct)
    {
        var repoKey = candidate.RepoKey;

        // No repo: tag at all — not eligible
        if (string.IsNullOrWhiteSpace(repoKey))
        {
            // Remote sources get a clarifying comment so the board owner sees
            // the item was discovered but skipped due to missing association.
            if (candidate.Source != "LocalFake" && candidate.Source != "LocalFile")
            {
                await PostRepoKeyCommentAsync(workSource, candidate,
                    "Skipped: work item has no `repo:` tag. Add a tag like `repo:example-service` " +
                    "to associate this item with a configured repository profile.", ct);
            }

            Log.CandidateSkippedNoRepoTag(_logger, candidate.Id, candidate.Title);
            return false;
        }

        // repo: tag present — check against configured profiles
        if (repoConfig.TryGetValue(repoKey, out _))
        {
            return true; // Profile found — proceed to claim
        }

        // No matching profile — post clarifying comment for remote sources
        if (candidate.Source != "LocalFake" && candidate.Source != "LocalFile")
        {
            await PostRepoKeyCommentAsync(workSource, candidate,
                $"Skipped: no repository profile matches the `repo:{repoKey}` tag. " +
                "Check for typos or add a matching profile to the 'repositories' configuration.", ct);
        }

        Log.CandidateSkippedNoProfile(_logger, candidate.Id, repoKey);
        return false;
    }

    /// <summary>
    /// Post a clarifying comment on a remote work item explaining why it was skipped.
    /// Best-effort: failures are logged but do not propagate.
    /// </summary>
    private static async Task PostRepoKeyCommentAsync(
        IWorkSource workSource,
        WorkCandidate candidate,
        string comment,
        CancellationToken ct)
    {
        var revision = candidate.SourceMetadata?.TryGetValue("revision", out var rev) == true
            ? rev
            : null;

        var workRef = new ExternalWorkRef
        {
            Source = candidate.Source,
            ExternalId = candidate.ExternalId,
            Url = candidate.ExternalUrl,
            Revision = revision,
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
        WorkCandidate candidate,
        IEnvironmentProvider environmentProvider,
        IEnvironmentStore environmentStore,
        IAgentRunStore runStore,
        IRunLifecycleService lifecycle,
        CancellationToken ct)
    {
        try
        {
            var spec = new EnvironmentSpec
            {
                RunId = run.RunId,
                Profile = "default",
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
                _logger, run.RunId, envHandle.RootPath, envHandle.ProviderType);

            return envHandle;
        }
        catch (Exception ex)
        {
            Log.EnvironmentProvisioningFailed(_logger, run.RunId, ex);

            await FailRunAsync(run, runStore, lifecycle, $"Environment provisioning failed: {ex.Message}", ct);
            return null;
        }
    }

    /// <summary>
    /// Clone the target repository into the provisioned environment by calling
    /// <see cref="ISourceControlProvider.CloneAsync"/>. Resolves the clone URL
    /// from the repository store first, then falls back to configuration options.
    /// On failure, transitions the run to <see cref="RunLifecycleState.Failed"/> and returns null.
    /// </summary>
    private async Task<RepositoryCheckout?> CloneRepositoryAsync(
        AgentRunHandle run,
        WorkCandidate candidate,
        EnvironmentHandle envHandle,
        ISourceControlProvider sourceControlProvider,
        IRepositoryStore repositoryStore,
        IReadOnlyDictionary<string, RepositoryProfileOptions> repoConfig,
        IAgentRunStore runStore,
        IRunLifecycleService lifecycle,
        CancellationToken ct)
    {
        try
        {
            var repoKey = candidate.RepoKey;
            if (string.IsNullOrWhiteSpace(repoKey))
            {
                throw new InvalidOperationException(
                    $"Cannot clone repository: work item '{candidate.Id}' has no repoKey.");
            }

            // Try the persistent store first (may contain cached profiles),
            // then fall back to configuration options.
            var repoProfile = await repositoryStore.GetByKeyAsync(repoKey, ct);

            string? cloneUrl;
            string defaultBranch;

            if (repoProfile is not null)
            {
                cloneUrl = repoProfile.CloneUrl;
                defaultBranch = repoProfile.DefaultBranch;
            }
            else if (repoConfig.TryGetValue(repoKey, out var configured))
            {
                cloneUrl = configured.CloneUrl;
                defaultBranch = configured.DefaultBranch;

                // Seed the profile into the store for future lookups
                try
                {
                    await repositoryStore.UpsertAsync(new RepositoryProfile
                    {
                        Key = repoKey,
                        CloneUrl = cloneUrl,
                        DefaultBranch = defaultBranch,
                        EnvironmentProfile = configured.EnvironmentProfile,
                        RuntimeProfile = configured.RuntimeProfile,
                        AllowedPaths = configured.AllowedPaths,
                    }, ct);
                }
                catch
                {
                    // Seeding is best-effort.
                }

                repoProfile = new RepositoryProfile
                {
                    Key = repoKey,
                    CloneUrl = cloneUrl,
                    DefaultBranch = defaultBranch,
                    EnvironmentProfile = configured.EnvironmentProfile,
                    RuntimeProfile = configured.RuntimeProfile,
                    AllowedPaths = configured.AllowedPaths,
                };
            }
            else
            {
                throw new InvalidOperationException(
                    $"Cannot clone repository '{repoKey}': no repository profile found. " +
                    "Ensure a matching key exists in the 'repositories' configuration section.");
            }

            var spec = new RepositorySpec
            {
                RepoKey = repoKey,
                CloneUrl = cloneUrl,
                DefaultBranch = defaultBranch,
                Profile = repoProfile,
            };

            var checkout = await sourceControlProvider.CloneAsync(spec, envHandle, ct);

            Log.RepositoryCloned(
                _logger, run.RunId, repoKey, checkout.LocalPath, checkout.Branch);

            return checkout;
        }
        catch (Exception ex)
        {
            Log.RepositoryCloneFailed(_logger, run.RunId, candidate.RepoKey, ex);

            await FailRunAsync(run, runStore, lifecycle, $"Repository clone failed: {ex.Message}", ct);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Write context files into the environment's context/ directory so the
    /// agent runtime has work-item metadata, acceptance criteria, and
    /// controller run configuration.
    /// This is best-effort: failures are logged but do not fail the run.
    /// </summary>
    private async Task InjectContextAsync(
        AgentRunHandle run,
        WorkCandidate candidate,
        EnvironmentHandle envHandle,
        RepositoryCheckout checkout,
        IRunLifecycleService lifecycle,
        CancellationToken ct)
    {
        var contextDir = Path.Combine(envHandle.RootPath, "context");

        try
        {
            Directory.CreateDirectory(contextDir);

            // ── work-item.md ───────────────────────────────────────
            var workItemMd = BuildWorkItemMarkdown(candidate);
            await File.WriteAllTextAsync(
                Path.Combine(contextDir, "work-item.md"), workItemMd, ct);

            // ── acceptance-criteria.md ─────────────────────────────
            if (candidate.AcceptanceCriteria is { Count: > 0 })
            {
                var acMd = BuildAcceptanceCriteriaMarkdown(candidate.AcceptanceCriteria);
                await File.WriteAllTextAsync(
                    Path.Combine(contextDir, "acceptance-criteria.md"), acMd, ct);
            }

            // ── controller-run.json ────────────────────────────────
            var runJson = JsonSerializer.Serialize(
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
                },
                JsonWriteOptions);
            await File.WriteAllTextAsync(
                Path.Combine(contextDir, "controller-run.json"), runJson, ct);

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
                JsonWriteOptions);
            await File.WriteAllTextAsync(
                Path.Combine(contextDir, "repository.json"), repoJson, ct);

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
        EnvironmentHandle envHandle,
        RepositoryCheckout checkout,
        IAgentRuntime agentRuntime,
        IRunLifecycleService lifecycle,
        CancellationToken ct)
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
                },
                RepoCheckout = checkout,
                EnvironmentHandle = envHandle,
                RuntimeProfile = "default",
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
                    },
                    ct);

                Log.RuntimeHandoffComplete(_logger, run.RunId, handle.RuntimeRunId);
            }
        }
        catch (Exception ex)
        {
            Log.RuntimeHandoffFailed(_logger, run.RunId, ex);

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
                ct);
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
        CancellationToken ct)
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
                new RuntimeFieldUpdate
                {
                    Error = reason,
                    FinishedAt = DateTimeOffset.UtcNow,
                },
                ct);
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
                ct);
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
        CancellationToken ct)
    {
        await lifecycle.AppendControllerEventAsync(
            runId,
            eventType,
            message,
            new Dictionary<string, object?>
            {
                ["state"] = state.ToString(),
            },
            ct);
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
        IReadOnlyDictionary<string, string> acceptanceCriteria)
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
    /// Find and recover runs that are stuck in <see cref="RunLifecycleState.AwaitingResult"/>
    /// past the configured stale timeout.
    /// </summary>
    private async Task RecoverStaleRunsAsync(
        IRunLifecycleService lifecycle,
        AgentControllerOptions options,
        CancellationToken ct)
    {
        var staleTimeout = TimeSpan.FromSeconds(options.StaleTimeoutSeconds);
        var staleRuns = await lifecycle.FindStaleRunsAsync(staleTimeout, ct);

        foreach (var staleRun in staleRuns)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await lifecycle.RecoverStaleRunAsync(staleRun.RunId, ct);
                Log.StaleRunRecovered(
                    _logger,
                    staleRun.RunId,
                    staleRun.LastHeartbeatAt);
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
                + "environment, or pi-materia work will be performed.")]
        public static partial void WorkerDisabled(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Polling worker started. WorkerId={WorkerId}, PollInterval={PollInterval}s, "
                + "MaxConcurrency={MaxConcurrency}, StaleTimeout={StaleTimeout}s")]
        public static partial void WorkerStarted(
            ILogger logger, string workerId, int pollInterval, int maxConcurrency, int staleTimeout);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Unhandled exception in polling cycle. Worker will retry after delay.")]
        public static partial void PollCycleError(ILogger logger, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Polling worker stopped.")]
        public static partial void WorkerStopped(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Concurrency limit reached (active={ActiveCount}, max={MaxConcurrency}). "
                + "Skipping work discovery.")]
        public static partial void ConcurrencyLimitReached(
            ILogger logger, int activeCount, int maxConcurrency);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Discovered {CandidateCount} eligible work candidate(s).")]
        public static partial void CandidatesDiscovered(ILogger logger, int candidateCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Skipping candidate {CandidateId} ({Title}): no `repo:` tag found.")]
        public static partial void CandidateSkippedNoRepoTag(
            ILogger logger, string candidateId, string title);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping candidate {CandidateId}: no repository profile matches `repo:{RepoKey}`.")]
        public static partial void CandidateSkippedNoProfile(
            ILogger logger, string candidateId, string repoKey);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to process work candidate {CandidateId} ({Title}).")]
        public static partial void CandidateProcessingFailed(
            ILogger logger, Exception ex, string candidateId, string title);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Could not claim candidate {CandidateId}: {Reason}")]
        public static partial void ClaimFailed(ILogger logger, string candidateId, string reason);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Claimed work candidate {CandidateId} ({Title}).")]
        public static partial void CandidateClaimed(ILogger logger, string candidateId, string title);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Upserted work candidate {CandidateId} from source {Source} into local persistence.")]
        public static partial void CandidateUpserted(ILogger logger, string candidateId, string source);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Created agent run {RunId} for work item {WorkItemId}.")]
        public static partial void RunCreated(ILogger logger, string runId, string workItemId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Run {RunId} advanced to AwaitingResult for work item {WorkItemId}.")]
        public static partial void RunAdvanced(ILogger logger, string runId, string workItemId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Runtime handoff complete for run {RunId}. Runtime ID: {RuntimeRunId}.")]
        public static partial void RuntimeHandoffComplete(
            ILogger logger, string runId, string runtimeRunId);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Runtime handoff failed for run {RunId}.")]
        public static partial void RuntimeHandoffFailed(
            ILogger logger, string runId, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Environment provisioned for run {RunId} at '{EnvPath}' (provider: {ProviderType}).")]
        public static partial void EnvironmentProvisioned(
            ILogger logger, string runId, string envPath, string providerType);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Environment provisioning failed for run {RunId}.")]
        public static partial void EnvironmentProvisioningFailed(
            ILogger logger, string runId, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Repository cloned for run {RunId}: '{RepoKey}' → '{LocalPath}' (branch: {Branch}).")]
        public static partial void RepositoryCloned(
            ILogger logger, string runId, string repoKey, string localPath, string branch);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Repository clone failed for run {RunId} (repo: {RepoKey}).")]
        public static partial void RepositoryCloneFailed(
            ILogger logger, string runId, string repoKey, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Context files written for run {RunId} in '{ContextDir}'.")]
        public static partial void ContextInjected(
            ILogger logger, string runId, string contextDir);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Context injection failed for run {RunId} in '{ContextDir}'. Run will continue.")]
        public static partial void ContextInjectionFailed(
            ILogger logger, string runId, string contextDir, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Recovered stale run {RunId} (last heartbeat: {LastHeartbeat}).")]
        public static partial void StaleRunRecovered(
            ILogger logger, string runId, DateTimeOffset? lastHeartbeat);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to recover stale run {RunId}.")]
        public static partial void StaleRecoveryFailed(ILogger logger, Exception ex, string runId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Recovered {StaleCount} stale run(s).")]
        public static partial void StaleRecoverySummary(ILogger logger, int staleCount);
    }
}
