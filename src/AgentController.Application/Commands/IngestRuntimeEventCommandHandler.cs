using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>
/// Handles <see cref="IngestRuntimeEventCommand"/> by validating the input,
/// delegating to <see cref="IRunLifecycleService.IngestRuntimeEventAsync"/>,
/// and returning a discriminated <see cref="IngestRuntimeEventResult"/>.
/// </summary>
public sealed class IngestRuntimeEventCommandHandler(
    IRunLifecycleService lifecycleService,
    IAgentRunStore runStore
) : ICommandHandler<IngestRuntimeEventCommand, IngestRuntimeEventResult>
{
    private readonly IRunLifecycleService _lifecycleService = lifecycleService;
    private readonly IAgentRunStore _runStore = runStore;

    public async Task<IngestRuntimeEventResult> HandleAsync(
        IngestRuntimeEventCommand command,
        CancellationToken cancellationToken
    )
    {
        // ── Field validation ──────────────────────────────────────

        // Required: eventType
        if (string.IsNullOrWhiteSpace(command.EventType))
        {
            return new IngestRuntimeEventResult(
                IngestRuntimeEventStatus.Unprocessable,
                "Request body is missing required field 'eventType'.",
                null
            );
        }

        // Required: eventId
        if (string.IsNullOrWhiteSpace(command.EventId))
        {
            return new IngestRuntimeEventResult(
                IngestRuntimeEventStatus.Unprocessable,
                "Request body is missing required field 'eventId'.",
                null
            );
        }

        // Route-vs-body runId consistency
        if (!string.IsNullOrWhiteSpace(command.BodyRunId)
            && !command.BodyRunId.Equals(command.RouteRunId, StringComparison.Ordinal))
        {
            return new IngestRuntimeEventResult(
                IngestRuntimeEventStatus.Unprocessable,
                $"RunId mismatch: route parameter '{command.RouteRunId}' does not match " +
                $"request body 'runId' value '{command.BodyRunId}'.",
                null
            );
        }

        // Severity must be a defined enum value (checked when it has a value)
        if (command.Severity.HasValue && !Enum.IsDefined(command.Severity.Value))
        {
            return new IngestRuntimeEventResult(
                IngestRuntimeEventStatus.Unprocessable,
                $"Unsupported severity value {(int)command.Severity.Value}. " +
                $"Valid values: {string.Join(", ", Enum.GetNames<EventSeverity>())}.",
                null
            );
        }

        // occurredAt must not be more than 5 minutes in the future
        if (command.OccurredAt.HasValue
            && command.OccurredAt.Value > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return new IngestRuntimeEventResult(
                IngestRuntimeEventStatus.Unprocessable,
                $"Field 'occurredAt' is too far in the future. " +
                $"Maximum allowed skew is 5 minutes.",
                null
            );
        }

        // ── Build the domain event and ingest ─────────────────────

        var evt = new RuntimeEvent
        {
            EventId = command.EventId!,
            RunId = command.RouteRunId,
            EventType = command.EventType!,
            RuntimeRunId = command.RuntimeRunId,
            Sequence = command.Sequence,
            OccurredAt = command.OccurredAt ?? DateTimeOffset.UtcNow,
            Severity = command.Severity ?? EventSeverity.Info,
            Message = command.Message,
            Payload = command.Payload,
        };

        try
        {
            await _lifecycleService.IngestRuntimeEventAsync(evt, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Distinguish idempotency conflicts from other validation errors.
            var isDuplicate = ex.Message.Contains(
                "already been processed",
                StringComparison.OrdinalIgnoreCase
            );

            return isDuplicate
                ? new IngestRuntimeEventResult(
                    IngestRuntimeEventStatus.Conflict,
                    ex.Message,
                    null
                  )
                : new IngestRuntimeEventResult(
                    IngestRuntimeEventStatus.Unprocessable,
                    ex.Message,
                    null
                  );
        }

        // ── Fetch the updated run ─────────────────────────────────

        var updatedRun = await _runStore.GetByIdAsync(command.RouteRunId, cancellationToken);

        return updatedRun is null
            ? new IngestRuntimeEventResult(
                IngestRuntimeEventStatus.NotFound,
                $"Run '{command.RouteRunId}' not found after event ingestion.",
                null
              )
            : new IngestRuntimeEventResult(
                IngestRuntimeEventStatus.Ingested,
                null,
                updatedRun
              );
    }
}
