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
        // Note: EligibleStates/EligibleTags/ExcludedTags removed in favor of
        // TagPrefix model; caller overrides take precedence.
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

    public async Task<CreatedWorkItemResult> CreateAssistanceStoryAsync(
        CreateAssistanceStoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.CurrentValue;
        var tags = AssistanceStoryCreation.BuildManagedTags(request, options.TagPrefix);
        var externalId = $"local-assistance-{Guid.NewGuid():N}";
        var url = $"localfake://work-items/{externalId}";
        var metadata = new Dictionary<string, string>
        {
            ["revision"] = "1",
            ["workItemType"] = request.WorkItemType.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(request.EnvironmentKey))
        {
            metadata["workSourceEnvironmentKey"] = request.EnvironmentKey.Trim();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        var candidate = await store.UpsertAsync(
            new WorkCandidate
            {
                ExternalId = externalId,
                ExternalUrl = url,
                RepoKey = request.RepoKey.Trim(),
                Title = request.Title.Trim(),
                Description = request.Description,
                Status = "New",
                Tags = tags,
                Source = "LocalFake",
                SourceMetadata = metadata,
            },
            cancellationToken
        );

        return new CreatedWorkItemResult
        {
            ExternalId = externalId,
            Url = url,
            Revision = "1",
            Candidate = candidate,
        };
    }

    public async Task<WorkCandidate> MakeAssistanceStoryReadyAsync(
        WorkCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var prefix = string.IsNullOrWhiteSpace(_options.CurrentValue.TagPrefix)
            ? WorkSourceOptions.DefaultTagPrefix
            : _options.CurrentValue.TagPrefix.Trim();
        var readyTag = WorkSourceOptions.TagReadyRework(prefix);
        var published = candidate with
        {
            Tags = candidate.Tags
                .Append(readyTag)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        return await store.UpsertAsync(published, cancellationToken);
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

        // Determine target state: ActiveState from options.
        var options = _options.CurrentValue;
        if (!string.IsNullOrWhiteSpace(options.ActiveState))
        {
            await store.UpdateStatusAsync(request.WorkItemId, options.ActiveState, cancellationToken);
        }

        // Restore eligibility while removing this source's managed lifecycle tags.
        // Keep an existing ready-rework marker instead of converting it to new work.
        var tagPrefix = string.IsNullOrWhiteSpace(options.TagPrefix)
            ? WorkSourceOptions.DefaultTagPrefix
            : options.TagPrefix.Trim();
        var readyTag = WorkSourceOptions.TagReady(tagPrefix);
        var readyReworkTag = WorkSourceOptions.TagReadyRework(tagPrefix);
        var workerTagPrefix = $"{tagPrefix}-worker:";
        var tags = candidate.Tags
            .Where(t =>
                !t.Equals(WorkSourceOptions.TagActive(tagPrefix), StringComparison.OrdinalIgnoreCase)
                && !t.Equals(
                    WorkSourceOptions.TagFailed(tagPrefix),
                    StringComparison.OrdinalIgnoreCase
                )
                && !t.Equals(
                    WorkSourceOptions.TagNeedsHuman(tagPrefix),
                    StringComparison.OrdinalIgnoreCase
                )
                && !t.StartsWith(workerTagPrefix, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        if (
            !tags.Contains(readyTag, StringComparer.OrdinalIgnoreCase)
            && !tags.Contains(readyReworkTag, StringComparer.OrdinalIgnoreCase)
        )
        {
            tags.Add(readyTag);
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
