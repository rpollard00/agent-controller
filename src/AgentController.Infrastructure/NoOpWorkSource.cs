using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Deterministic no-op implementation of <see cref="IWorkSource"/>.
/// Returns empty results and successful no-ops. Suitable for DI seeding
/// before real providers are wired.
/// </summary>
public sealed class NoOpWorkSource : IWorkSource
{
    public Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
        WorkQuery query,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<IReadOnlyList<WorkCandidate>>([]);
    }

    public Task<ClaimResult> TryClaimAsync(
        WorkCandidate candidate,
        ClaimRequest claim,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(
            new ClaimResult
            {
                Success = false,
                FailureReason = "No-op work source: claiming is not supported.",
            }
        );
    }

    public Task UpdateStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken
    )
    {
        return Task.CompletedTask;
    }

    public Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken
    )
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        ExternalWorkRef workRef,
        int maxComments,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());
    }

    public Task ReleaseClaimAsync(
        ReleaseClaimRequest request,
        CancellationToken cancellationToken
    )
    {
        return Task.CompletedTask;
    }
}
