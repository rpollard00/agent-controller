using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for agent run lifecycle records.
/// Used by the worker polling loop, the run lifecycle service, and
/// API endpoints. Implementations are storage-agnostic; API and worker
/// code must not reference EF Core or any specific persistence technology directly.
/// </summary>
public interface IAgentRunStore
{
    /// <summary>
    /// Create a new agent run record and return its handle with the
    /// controller-assigned run identifier.
    /// </summary>
    Task<AgentRunHandle> CreateAsync(
        CreateRunRequest request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Get a single agent run by its controller-assigned identifier.
    /// Returns null if no run matches.
    /// </summary>
    Task<AgentRunHandle?> GetByIdAsync(
        string runId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Transition the run to a new lifecycle state.
    /// Implementations should update <c>UpdatedAt</c> automatically.
    /// </summary>
    Task UpdateStatusAsync(
        string runId,
        RunLifecycleState status,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Apply a partial update of runtime-related fields on an agent run.
    /// Only non-null properties in <paramref name="update"/> are applied.
    /// Implementations should update <c>UpdatedAt</c> automatically.
    /// </summary>
    Task UpdateRuntimeFieldsAsync(
        string runId,
        RuntimeFieldUpdate update,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// List agent runs matching the optional filters in <paramref name="query"/>.
    /// Supports pagination via <see cref="RunListQuery.Offset"/> and
    /// <see cref="RunListQuery.MaxResults"/>.
    /// </summary>
    Task<IReadOnlyList<AgentRunHandle>> ListAsync(
        RunListQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Find runs that are in <see cref="RunLifecycleState.AwaitingResult"/>
    /// and whose <c>LastHeartbeatAt</c> (or <c>StartedAt</c> if no heartbeat
    /// was ever received) is older than <c>NOW - staleTimeout</c>.
    /// These are candidates for stale-run recovery.
    /// </summary>
    Task<IReadOnlyList<AgentRunHandle>> FindStaleAsync(
        TimeSpan staleTimeout,
        CancellationToken cancellationToken
    );
}
