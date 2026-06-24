using AgentController.Domain;

namespace AgentController.Application.Results;

/// <summary>
/// Outcome of ingesting a runtime event, discriminating between success,
/// not-found, idempotency conflict, and unprocessable input.
/// </summary>
public enum IngestRuntimeEventStatus
{
    /// <summary>Event was ingested successfully and the run was updated.</summary>
    Ingested,

    /// <summary>The target run could not be found after ingestion.</summary>
    NotFound,

    /// <summary>Event was a duplicate (idempotency conflict) or another conflict occurred.</summary>
    Conflict,

    /// <summary>Input was invalid or the event could not be processed.</summary>
    Unprocessable,
}

/// <summary>
/// Result returned by <see cref="Commands.IngestRuntimeEventCommandHandler"/>.
/// The API endpoint maps <see cref="Status"/> to the appropriate HTTP status code.
/// </summary>
public sealed record IngestRuntimeEventResult(
    /// <summary>Discriminant indicating the outcome of the ingestion attempt.</summary>
    IngestRuntimeEventStatus Status,

    /// <summary>
    /// Reason for conflict or unprocessable status.
    /// Populated when <see cref="Status"/> is <see cref="IngestRuntimeEventStatus.Conflict"/>
    /// or <see cref="IngestRuntimeEventStatus.Unprocessable"/>.
    /// </summary>
    string? ConflictReason,

    /// <summary>
    /// The updated run after successful ingestion.
    /// Populated when <see cref="Status"/> is <see cref="IngestRuntimeEventStatus.Ingested"/>.
    /// </summary>
    AgentRunHandle? UpdatedRun
);
