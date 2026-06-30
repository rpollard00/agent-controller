using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// A local work source backed by persisted fake <see cref="WorkCandidate"/> items
/// stored via <see cref="IWorkItemStore"/>. Queries eligible local work, honors
/// configured eligible/excluded tags and states, claims unleased or expired items,
/// and updates local work status.
///
/// Avoids Azure DevOps assumptions and remote source-control behavior.
///
/// Registered as a singleton via <see cref="AddAgentControllerLocalFakeWorkSource"/>.
/// Because <see cref="IWorkItemStore"/> is scoped (EF Core), each method creates its
/// own <see cref="IServiceScope"/> to resolve a fresh store instance per operation.
/// </summary>
internal sealed class LocalFakeWorkSource : IWorkSource
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<WorkSourceOptions> _options;

    public LocalFakeWorkSource(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<WorkSourceOptions> options)
    {
        _scopeFactory = scopeFactory;
        _options = options;
    }

    public async Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
        WorkQuery query,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        // Merge configured filters into the query, letting caller overrides win
        // where explicitly provided.
        var effectiveQuery = query with
        {
            States = query.States is { Count: > 0 }
                ? query.States
                : (options.EligibleStates is { Count: > 0 }
                    ? options.EligibleStates
                    : null),

            Tags = query.Tags is { Count: > 0 }
                ? query.Tags
                : (options.EligibleTags is { Count: > 0 }
                    ? options.EligibleTags
                    : null),

            ExcludedTags = query.ExcludedTags is { Count: > 0 }
                ? query.ExcludedTags
                : (options.ExcludedTags is { Count: > 0 }
                    ? options.ExcludedTags
                    : null),
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        // Delegate to the persistence store which already handles lease-expiry,
        // status/tag/priority filtering, and ordering.
        return await store.FindEligibleAsync(effectiveQuery, cancellationToken);
    }

    public async Task<ClaimResult> TryClaimAsync(
        WorkCandidate candidate,
        ClaimRequest claim,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        // Delegate to the persistence store's atomic claim logic.
        var result = await store.TryClaimAsync(candidate.Id, claim, cancellationToken);

        // If the claim succeeded and we have an active-state configured,
        // update the status to reflect the claim.
        if (result.Success)
        {
            var activeState = _options.CurrentValue.ActiveState;
            if (!string.IsNullOrWhiteSpace(activeState))
            {
                await store.UpdateStatusAsync(candidate.Id, activeState, cancellationToken);
            }
        }

        return result;
    }

    public Task UpdateStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken)
    {
        // Local fake has no external system to push status updates to.
        // In Phase 1, the primary path for status updates is the controller
        // lifecycle service which uses IWorkItemStore.UpdateStatusAsync directly.
        return Task.CompletedTask;
    }

    public Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken)
    {
        // Local fake has no external work item system to comment on.
        // comments are recorded through lifecycle events instead.
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        ExternalWorkRef workRef,
        int maxComments,
        CancellationToken cancellationToken)
    {
        // Local fake has no external work item system to fetch comments from.
        return Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());
    }

    public Task ReleaseClaimAsync(
        ReleaseClaimRequest request,
        CancellationToken cancellationToken)
    {
        // Local fake has no external system to release claims on.
        // The local persistence store handles lease expiry automatically.
        return Task.CompletedTask;
    }

    public async Task<ReworkReactivateResult> ReactivateForReworkAsync(
        ReworkReactivateRequest request,
        CancellationToken cancellationToken)
    {
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

        // Determine target state: first eligible state.
        var options = _options.CurrentValue;
        var eligibleStates = options.EligibleStates;
        if (eligibleStates is { Count: > 0 })
        {
            await store.UpdateStatusAsync(request.WorkItemId, eligibleStates[0], cancellationToken);
        }

        // Ensure agent-ready; remove agent lifecycle exclusion tags.
        var tags = candidate.Tags
            .Where(t => t != WorkSourceOptions.DefaultExcludedTagAgentActive &&
                        t != WorkSourceOptions.DefaultExcludedTagAgentFailed &&
                        t != WorkSourceOptions.DefaultExcludedTagAgentNeedsHuman)
            .ToList();

        if (!tags.Contains(WorkSourceOptions.DefaultTagAgentReady))
        {
            tags.Add(WorkSourceOptions.DefaultTagAgentReady);
        }

        // Upsert with updated tags (idempotent against local state).
        await store.UpsertAsync(candidate with
        {
            Tags = tags,
        }, cancellationToken);

        // Comment is a no-op for local fake (no external system).
        return new ReworkReactivateResult { Success = true };
    }
}
