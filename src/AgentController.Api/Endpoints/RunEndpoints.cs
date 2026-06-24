using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentController.Api.Endpoints;

/// <summary>
/// Run endpoint group: GET /runs, GET /runs/{runId}.
/// Endpoints are thin — they inject CQRS handlers and map results to HTTP.
/// All N+1 joins and multi-store assembly live in the handlers, not here.
/// </summary>
public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /runs — list runs with optional filters
        app.MapGet(
            "/runs",
            async (
                string? status,
                string? workItemId,
                int? maxResults,
                int? offset,
                IQueryHandler<ListRunsQuery, RunListResult> handler,
                CancellationToken ct
            ) =>
            {
                RunLifecycleState? statusFilter = null;
                if (!string.IsNullOrWhiteSpace(status)
                    && Enum.TryParse<RunLifecycleState>(status, ignoreCase: true, out var parsed))
                {
                    statusFilter = parsed;
                }

                var query = new ListRunsQuery
                {
                    Status = statusFilter,
                    WorkItemId = workItemId,
                    MaxResults = maxResults ?? 100,
                    Offset = offset ?? 0,
                };

                var result = await handler.ExecuteAsync(query, ct);

                // Keep the JSON contract (Runs + TotalCount) consistent for clients.
                return Results.Ok(new
                {
                    Runs = result.Runs,
                    TotalCount = result.Runs.Count,
                });
            }
        );

        // GET /runs/{runId} — full run detail
        app.MapGet(
            "/runs/{runId}",
            async (
                string runId,
                IQueryHandler<GetRunByIdQuery, RunDetailResult?> handler,
                CancellationToken ct
            ) =>
            {
                var detail = await handler.ExecuteAsync(new GetRunByIdQuery(runId), ct);
                return detail is null
                    ? Results.NotFound(new { error = $"Run '{runId}' not found." })
                    : Results.Ok(detail);
            }
        );

        return app;
    }
}
