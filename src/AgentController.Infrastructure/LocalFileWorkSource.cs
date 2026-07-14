using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// A local work source that reads deterministic work item definitions from the
/// <c>localWork</c> configuration section. On first use, definitions are validated
/// and upserted into the persistence store via <see cref="IWorkItemStore"/>.
/// Subsequent queries discover eligible items from the store using the same
/// eligibility filtering as <see cref="LocalFakeWorkSource"/>.
///
/// This enables declarative, file-based work item seeding without requiring
/// API calls or Azure DevOps integration, making the controller exerciseable
/// for end-to-end local development runs.
///
/// Registered as a singleton via
/// <see cref="AgentControllerServiceCollectionExtensions.AddAgentControllerLocalFileWorkSource"/>.
/// Because <see cref="IWorkItemStore"/> is scoped (EF Core), each method creates its
/// own <see cref="IServiceScope"/> to resolve a fresh store instance per operation.
/// </summary>
internal sealed partial class LocalFileWorkSource : IWorkSource, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<WorkSourceOptions> _workSourceOptions;
    private readonly IOptionsMonitor<LocalWorkOptions> _localWorkOptions;
    private readonly ILogger<LocalFileWorkSource> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public LocalFileWorkSource(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<WorkSourceOptions> workSourceOptions,
        IOptionsMonitor<LocalWorkOptions> localWorkOptions,
        ILogger<LocalFileWorkSource> logger)
    {
        _scopeFactory = scopeFactory;
        _workSourceOptions = workSourceOptions;
        _localWorkOptions = localWorkOptions;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
        WorkQuery query,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var workOpts = _workSourceOptions.CurrentValue;

        // Merge configured filters into the query, letting caller overrides win
        // where explicitly provided (same pattern as LocalFakeWorkSource).
        // Note: EligibleStates/EligibleTags/ExcludedTags removed in favor of
        // CompletedStates + TagPrefix model; caller overrides take precedence.
        var effectiveQuery = query with
        {
            States = query.States is { Count: > 0 }
                ? query.States
                : null,

            Tags = query.Tags is { Count: > 0 }
                ? query.Tags
                : null,

            ExcludedTags = query.ExcludedTags is { Count: > 0 }
                ? query.ExcludedTags
                : null,
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        return await store.FindEligibleAsync(effectiveQuery, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ClaimResult> TryClaimAsync(
        WorkCandidate candidate,
        ClaimRequest claim,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        // Delegate to the persistence store's atomic claim logic.
        var result = await store.TryClaimAsync(candidate.Id, claim, cancellationToken);

        // If the claim succeeded and we have an active-state configured,
        // update the status to reflect the claim.
        if (result.Success)
        {
            var activeState = _workSourceOptions.CurrentValue.ActiveState;
            if (!string.IsNullOrWhiteSpace(activeState))
            {
                await store.UpdateStatusAsync(candidate.Id, activeState, cancellationToken);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public Task UpdateStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken)
    {
        // Local file source has no external system to push status updates to.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken)
    {
        // Local file source has no external work item system to comment on.
        // Comments are recorded through lifecycle events instead.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        ExternalWorkRef workRef,
        int maxComments,
        CancellationToken cancellationToken)
    {
        // Local file source has no external work item system to fetch comments from.
        return Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());
    }

    /// <inheritdoc/>
    public Task ReleaseClaimAsync(
        ReleaseClaimRequest request,
        CancellationToken cancellationToken)
    {
        // Local file source has no external system to release claims on.
        // The local persistence store handles lease expiry automatically.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<ReworkReactivateResult> ReactivateForReworkAsync(
        ReworkReactivateRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        // Fetch current work item from the local store.
        var candidate = await store.GetByIdAsync(request.WorkItemId, cancellationToken);
        if (candidate is null)
        {
            return new ReworkReactivateResult
            {
                Success = false,
                FailureReason = $"Work item '{request.WorkItemId}' not found in local store.",
            };
        }

        // Determine target state: ActiveState from options.
        var options = _workSourceOptions.CurrentValue;
        if (!string.IsNullOrWhiteSpace(options.ActiveState))
        {
            await store.UpdateStatusAsync(request.WorkItemId, options.ActiveState, cancellationToken);
        }

        // Ensure agent-ready; remove agent lifecycle exclusion tags.
        // Aligns with the ADO path: strips agent-active, agent-failed,
        // agent-needs-human, and any agent-worker:{id} tags.
        var tags = candidate.Tags
            .Where(t => t != WorkSourceOptions.TagActive() &&
                        t != WorkSourceOptions.TagFailed() &&
                        t != WorkSourceOptions.TagNeedsHuman() &&
                        !t.StartsWith("agent-worker:", StringComparison.Ordinal))
            .ToList();

        if (!tags.Contains(WorkSourceOptions.TagReady()))
        {
            tags.Add(WorkSourceOptions.TagReady());
        }

        // Upsert with updated tags (idempotent against local state).
        await store.UpsertAsync(candidate with
        {
            Tags = tags,
        }, cancellationToken);

        // Comment is a no-op for local file source (no external system).
        return new ReworkReactivateResult { Success = true };
    }

    /// <summary>
    /// Lazily upsert work item definitions from configuration into the persistence
    /// store. Thread-safe; initialization happens exactly once across all callers.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var definitions = _localWorkOptions.CurrentValue.Definitions;
            if (definitions.Count == 0)
            {
                Log.NoLocalWorkDefinitions(_logger);
                _initialized = true;
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

            var upserted = 0;
            var skipped = 0;

            foreach (var def in definitions)
            {
                ct.ThrowIfCancellationRequested();

                // Validate required fields
                if (string.IsNullOrWhiteSpace(def.RepoKey))
                {
                    Log.SkippingDefinitionMissingRepoKey(
                        _logger, def.Title ?? "(no title)");
                    skipped++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(def.Title))
                {
                    Log.SkippingDefinitionMissingTitle(_logger, def.RepoKey);
                    skipped++;
                    continue;
                }

                // Derive stable externalId when not explicitly provided
                var externalId = !string.IsNullOrWhiteSpace(def.ExternalId)
                    ? def.ExternalId
                    : DeriveExternalId(def);

                var candidate = new WorkCandidate
                {
                    Source = "LocalFile",
                    ExternalId = externalId,
                    RepoKey = def.RepoKey,
                    Title = def.Title,
                    Description = def.Body ?? def.Description,
                    AcceptanceCriteria = def.AcceptanceCriteria,
                    Priority = def.Priority,
                    Status = def.Status,
                    Tags = def.Tags,
                };

                await store.UpsertAsync(candidate, ct);
                upserted++;

                Log.DefinitionUpserted(_logger, def.Title, externalId);
            }

            Log.InitializationComplete(
                _logger, upserted, skipped, definitions.Count);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Derives a stable external identifier from definition content so
    /// repeated upserts across controller restarts are idempotent.
    /// Uses SHA-256 over key fields and returns the first 12 hex chars
    /// prefixed with "local-".
    /// </summary>
    internal static string DeriveExternalId(LocalWorkItemDefinition def)
    {
        var content = $"{def.RepoKey}|{def.Title}|{def.Body ?? def.Description ?? ""}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"local-{Convert.ToHexStringLower(hash)[..12]}";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _initLock.Dispose();
    }

    /// <summary>
    /// Source-generated high-performance logger methods.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "No local work definitions configured in 'localWork:definitions'. " +
                      "The controller will not discover any work items from the LocalFile source.")]
        public static partial void NoLocalWorkDefinitions(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping local work definition: missing repoKey. Title: {Title}")]
        public static partial void SkippingDefinitionMissingRepoKey(
            ILogger logger, string title);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping local work definition: missing title. RepoKey: {RepoKey}")]
        public static partial void SkippingDefinitionMissingTitle(
            ILogger logger, string repoKey);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Upserted local work definition '{Title}' with externalId {ExternalId}")]
        public static partial void DefinitionUpserted(
            ILogger logger, string title, string externalId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "LocalFile work source initialization complete: {UpsertedCount} upserted, " +
                      "{SkippedCount} skipped (of {TotalCount} total definitions).")]
        public static partial void InitializationComplete(
            ILogger logger, int upsertedCount, int skippedCount, int totalCount);
    }
}
