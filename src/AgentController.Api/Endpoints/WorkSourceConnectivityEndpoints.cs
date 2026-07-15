using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentController.Api.Endpoints;

/// <summary>
/// Work-source connectivity verification endpoint group:
/// POST /api/webui/work-source-environments/{key}:verify.
/// Thin endpoint — all resolution, verification, and error mapping
/// are handled by the CQRS handler and provider verifiers.
/// </summary>
public static class WorkSourceConnectivityEndpoints
{
    public static IEndpointRouteBuilder MapWorkSourceConnectivityEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        app.MapPost(
            "/api/webui/work-source-environments/{key}:verify",
            async (
                string key,
                IQueryHandler<VerifyWorkSourceConnectivityQuery, WorkSourceConnectivityResult> handler,
                CancellationToken ct
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new VerifyWorkSourceConnectivityQuery(key),
                    ct
                );
                return Results.Ok(result);
            }
        );

        return app;
    }
}
