using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Results;
using AgentController.Api.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentController.Api.Endpoints;

/// <summary>
/// Runtime event ingestion endpoint: POST /runs/{runId}/events.
/// The endpoint is thin — it binds the request body, builds the command,
/// invokes the CQRS handler, and maps the discriminated result to HTTP.
/// All validation (required fields, runId consistency, severity enum,
/// occurredAt skew) lives in <see cref="IngestRuntimeEventCommandHandler"/>.
/// </summary>
public static class RuntimeEventEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/runs/{runId}/events",
            async (
                string runId,
                RuntimeEventRequest request,
                ICommandHandler<IngestRuntimeEventCommand, IngestRuntimeEventResult> handler,
                CancellationToken ct
            ) =>
            {
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
}
