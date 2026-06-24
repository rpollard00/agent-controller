using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AgentController.Application.Commands;

/// <summary>
/// Handles <see cref="IngestRuntimeEventCommand"/> by validating the input,
/// delegating to <see cref="IRunLifecycleService.IngestRuntimeEventAsync"/>,
/// and returning a discriminated <see cref="IngestRuntimeEventResult"/>.
/// </summary>
public sealed partial class IngestRuntimeEventCommandHandler(
    IRunLifecycleService lifecycleService,
    IAgentRunStore runStore,
    ILogger<IngestRuntimeEventCommandHandler> logger
) : ICommandHandler<IngestRuntimeEventCommand, IngestRuntimeEventResult>
{
    private readonly IRunLifecycleService _lifecycleService = lifecycleService;
    private readonly IAgentRunStore _runStore = runStore;
    private readonly ILogger _logger = logger;

    public async Task<IngestRuntimeEventResult> HandleAsync(
        IngestRuntimeEventCommand command,
        CancellationToken cancellationToken
    )
    {
        // ── Field validation ──────────────────────────────────────

        // Required: eventType
        if (string.IsNullOrWhiteSpace(command.EventType))
        {
            Log.UnprocessableValidation(
                _logger, command.RouteRunId, command.EventId, "eventType missing");
            return new IngestRuntimeEventResult(
                IngestRuntimeEventStatus.Unprocessable,
                "Request body is missing required field 'eventType'.",
                null
            );
        }

        // Required: eventId
        if (string.IsNullOrWhiteSpace(command.EventId))
        {
            Log.UnprocessableValidation(
                _logger, command.RouteRunId, command.EventId, "eventId missing");
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
            Log.UnprocessableValidation(
                _logger, command.RouteRunId, command.EventId,
                "runId mismatch");
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
            Log.UnprocessableValidation(
                _logger, command.RouteRunId, command.EventId,
                "unsupported severity");
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
            Log.UnprocessableValidation(
                _logger, command.RouteRunId, command.EventId, "occurredAt too far in future");
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

        // Log the incoming event at Debug with key fields + raw JSON truncated to ~2KB.
#pragma warning disable CA1873 // LoggerMessage source-gen has its own IsEnabled guard
        Log.IncomingRuntimeEvent(
            _logger,
            evt.EventType,
            evt.RunId,
            evt.EventId,
            evt.Severity,
            evt.Sequence,
            evt.Message,
            Truncate(JsonSerializer.Serialize(evt), 2048));
#pragma warning restore CA1873

        IngestRuntimeEventResult outcome;

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

            outcome = isDuplicate
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

            LogOutcome(outcome, evt.RunId, evt.EventId, evt.EventType);
            return outcome;
        }

        // ── Fetch the updated run ─────────────────────────────────

        var updatedRun = await _runStore.GetByIdAsync(command.RouteRunId, cancellationToken);

        if (updatedRun is null)
        {
            outcome = new IngestRuntimeEventResult(
                IngestRuntimeEventStatus.NotFound,
                $"Run '{command.RouteRunId}' not found after event ingestion.",
                null
            );
        }
        else
        {
            outcome = new IngestRuntimeEventResult(
                IngestRuntimeEventStatus.Ingested,
                null,
                updatedRun
            );
        }

        LogOutcome(outcome, evt.RunId, evt.EventId, evt.EventType);
        return outcome;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "... (truncated)";
    }

    private void LogOutcome(
        IngestRuntimeEventResult result,
        string runId,
        string eventId,
        string eventType)
    {
        switch (result.Status)
        {
            case IngestRuntimeEventStatus.Ingested:
                Log.IngestionSuccess(_logger, eventType, runId, eventId);
                break;
            case IngestRuntimeEventStatus.Conflict:
                Log.IngestionConflict(_logger, eventType, runId, eventId, result.ConflictReason);
                break;
            case IngestRuntimeEventStatus.NotFound:
                Log.IngestionNotFound(_logger, eventType, runId, eventId, result.ConflictReason);
                break;
            case IngestRuntimeEventStatus.Unprocessable:
                Log.IngestionUnprocessable(_logger, eventType, runId, eventId, result.ConflictReason);
                break;
            default:
                Log.IngestionUnknown(_logger, eventType, runId, eventId, result.Status.ToString());
                break;
        }
    }

    // ── Source-generated LoggerMessage partials ───────────────────

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Incoming runtime event — eventType={EventType}, runId={RunId}, " +
                      "eventId={EventId}, severity={Severity}, sequence={Sequence}, " +
                      "message={Message}, rawJson={RawJson}")]
        public static partial void IncomingRuntimeEvent(
            ILogger logger,
            string eventType,
            string runId,
            string eventId,
            EventSeverity severity,
            int? sequence,
            string? message,
            string rawJson);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Runtime event ingested successfully — eventType={EventType}, " +
                      "runId={RunId}, eventId={EventId}")]
        public static partial void IngestionSuccess(
            ILogger logger, string eventType, string runId, string eventId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Runtime event ingestion conflict — eventType={EventType}, " +
                      "runId={RunId}, eventId={EventId}, reason={Reason}")]
        public static partial void IngestionConflict(
            ILogger logger, string eventType, string runId, string eventId, string? reason);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Runtime event ingestion — run not found — eventType={EventType}, " +
                      "runId={RunId}, eventId={EventId}, reason={Reason}")]
        public static partial void IngestionNotFound(
            ILogger logger, string eventType, string runId, string eventId, string? reason);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Runtime event ingestion — unprocessable — eventType={EventType}, " +
                      "runId={RunId}, eventId={EventId}, reason={Reason}")]
        public static partial void IngestionUnprocessable(
            ILogger logger, string eventType, string runId, string eventId, string? reason);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Runtime event ingestion — unexpected result — eventType={EventType}, " +
                      "runId={RunId}, eventId={EventId}, status={Status}")]
        public static partial void IngestionUnknown(
            ILogger logger, string eventType, string runId, string eventId, string status);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Runtime event validation failed — runId={RunId}, eventId={EventId}, " +
                      "reason={Reason}")]
        public static partial void UnprocessableValidation(
            ILogger logger, string runId, string? eventId, string reason);
    }
}
