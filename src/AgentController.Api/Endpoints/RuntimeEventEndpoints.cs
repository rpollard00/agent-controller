using System.Net;
using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Results;
using AgentController.Api.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;

namespace AgentController.Api.Endpoints;

/// <summary>
/// Runtime event ingestion endpoint: POST /runs/{runId}/events.
/// The endpoint is thin — it binds the request body, builds the command,
/// invokes the CQRS handler, and maps the discriminated result to HTTP.
/// All validation (required fields, runId consistency, severity enum,
/// occurredAt skew) lives in <see cref="IngestRuntimeEventCommandHandler"/>.
///
/// <para>Endpoint-level observability: every incoming POST is logged at
/// Information with method, path, runId, eventType (if present), and
/// outcome status + reason so empty bodies and 422s are visible at
/// the default log level without requiring Debug.</para>
/// </summary>
public static partial class RuntimeEventEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/runs/{runId}/events",
            async (
                string runId,
                RuntimeEventRequest request,
                ICommandHandler<IngestRuntimeEventCommand, IngestRuntimeEventResult> handler,
                ILoggerFactory loggerFactory,
                CancellationToken ct
            ) =>
            {
                var logger = loggerFactory.CreateLogger("AgentController.Api.Endpoints.RuntimeEventEndpoints");

                // ── Endpoint-level request observability ──────────────────
#pragma warning disable CA1873 // LoggerMessage source-gen has its own IsEnabled guard
                Log.IncomingEventRequest(
                    logger,
                    "POST",
                    $"/runs/{runId}/events",
                    runId,
                    request.EventType);
#pragma warning restore CA1873

                var command = new IngestRuntimeEventCommand(
                    RouteRunId: runId,
                    BodyRunId: request.RunId,
                    EventId: request.EventId,
                    EventType: request.EventType,
                    RuntimeRunId: request.RuntimeRunId,
                    Sequence: request.Sequence,
                    OccurredAt: request.OccurredAt,
                    Severity: request.Severity,
                    Message: request.Message,
                    Payload: request.Payload
                );

                var result = await handler.HandleAsync(command, ct);

                // ── Endpoint-level outcome observability ──────────────────
#pragma warning disable CA1873 // LoggerMessage source-gen has its own IsEnabled guard
                Log.EventRequestOutcome(
                    logger,
                    runId,
                    request.EventType,
                    (int)MapToStatusCode(result.Status),
                    result.ConflictReason);
#pragma warning restore CA1873

                return result.Status switch
                {
                    IngestRuntimeEventStatus.Ingested => Results.Ok(new
                    {
                        runId = result.UpdatedRun!.RunId,
                        status = result.UpdatedRun.Status.ToString(),
                        runtimeRunId = result.UpdatedRun.RuntimeRunId,
                        lastHeartbeatAt = result.UpdatedRun.LastHeartbeatAt,
                        finishedAt = result.UpdatedRun.FinishedAt,
                        resultSummary = result.UpdatedRun.ResultSummary,
                        error = result.UpdatedRun.Error,
                        eventId = request.EventId,
                        message = $"Runtime event '{request.EventType}' ingested successfully.",
                    }),
                    IngestRuntimeEventStatus.NotFound => Results.NotFound(new
                    {
                        error = result.ConflictReason,
                    }),
                    IngestRuntimeEventStatus.Conflict => Results.Conflict(new
                    {
                        error = result.ConflictReason,
                        runId,
                        eventId = request.EventId,
                    }),
                    IngestRuntimeEventStatus.Unprocessable => Results.UnprocessableEntity(new
                    {
                        error = result.ConflictReason,
                        runId,
                        eventId = request.EventId,
                    }),
                    _ => Results.BadRequest(new { error = "Unexpected handler result." }),
                };
            }
        );

        return app;
    }

    private static HttpStatusCode MapToStatusCode(IngestRuntimeEventStatus status) =>
        status switch
        {
            IngestRuntimeEventStatus.Ingested => HttpStatusCode.OK,
            IngestRuntimeEventStatus.NotFound => HttpStatusCode.NotFound,
            IngestRuntimeEventStatus.Conflict => HttpStatusCode.Conflict,
            IngestRuntimeEventStatus.Unprocessable => HttpStatusCode.UnprocessableEntity,
            _ => HttpStatusCode.BadRequest,
        };

    // ── Source-generated LoggerMessage partials ─────────────────────

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Runtime event request — method={Method}, path={Path}, runId={RunId}, eventType={EventType}" )]
        public static partial void IncomingEventRequest(
            ILogger logger,
            string method,
            string path,
            string runId,
            string? eventType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Runtime event outcome — runId={RunId}, eventType={EventType}, statusCode={StatusCode}, reason={Reason}" )]
        public static partial void EventRequestOutcome(
            ILogger logger,
            string runId,
            string? eventType,
            int statusCode,
            string? reason);
    }
}

