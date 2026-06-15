using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for the controller's authoritative lifecycle event log.
/// Events are append-only. Used by the run lifecycle service, runtime event
/// ingestion, and API endpoints for run detail inspection.
/// Implementations are storage-agnostic; API and worker code must not
/// reference EF Core or any specific persistence technology directly.
/// </summary>
public interface ILifecycleEventStore
{
    /// <summary>
    /// Append a lifecycle event to the authoritative event log.
    /// The store assigns the event's <see cref="LifecycleEvent.Id"/>.
    /// </summary>
    Task AppendAsync(
        LifecycleEvent evt,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// List all lifecycle events for a given run, ordered by creation time ascending.
    /// </summary>
    Task<IReadOnlyList<LifecycleEvent>> ListByRunIdAsync(
        string runId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Check whether a lifecycle event with the given external <paramref name="eventId"/>
    /// already exists for the specified run. Used for runtime event idempotency:
    /// duplicate events with the same <c>eventId</c> must be rejected.
    /// </summary>
    Task<bool> ExistsByEventIdAsync(
        string runId,
        string eventId,
        CancellationToken cancellationToken
    );
}
