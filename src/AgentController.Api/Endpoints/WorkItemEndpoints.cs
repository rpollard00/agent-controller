using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentController.Api.Endpoints;

/// <summary>
/// Work item endpoint group: POST /work-items, GET /work-items, GET /work-items/{id}.
/// Endpoints are thin — they inject CQRS handlers and map results to HTTP.
/// </summary>
public static class WorkItemEndpoints
{
    public static IEndpointRouteBuilder MapWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/work-items",
            async (
                CreateWorkItemRequest request,
                ICommandHandler<CreateWorkItemCommand, WorkCandidate> handler,
                CancellationToken ct
            ) =>
            {
                var created = await handler.HandleAsync(new CreateWorkItemCommand(request), ct);
                return Results.Created($"/work-items/{created.Id}", created);
            }
        );

        app.MapGet(
            "/work-items",
            async (
                string? status,
                string? repoKey,
                string? tags,
                int? maxResults,
                int? offset,
                IQueryHandler<ListWorkItemsQuery, IReadOnlyList<WorkCandidate>> handler,
                CancellationToken ct
            ) =>
            {
                var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var query = new ListWorkItemsQuery
                {
                    Status = status,
                    RepoKey = repoKey,
                    Tags = tagList is { Length: > 0 } ? tagList : null,
                    MaxResults = maxResults ?? 100,
                    Offset = offset ?? 0,
                };

                var items = await handler.ExecuteAsync(query, ct);
                return Results.Ok(items);
            }
        );

        app.MapGet(
            "/work-items/{id}",
            async (
                string id,
                IQueryHandler<GetWorkItemByIdQuery, WorkCandidate?> handler,
                CancellationToken ct
            ) =>
            {
                var item = await handler.ExecuteAsync(new GetWorkItemByIdQuery(id), ct);
                return item is null
                    ? Results.NotFound(new { error = $"Work item '{id}' not found." })
                    : Results.Ok(item);
            }
        );

        return app;
    }
}
