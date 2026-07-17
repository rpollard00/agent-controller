using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentController.Api.Endpoints;

/// <summary>
/// Repository-host connectivity verification endpoint group:
/// POST /api/webui/repository-host-connections/{key}:verify.
/// Thin endpoint — all resolution, verification, and error mapping
/// are handled by the CQRS handler and provider hosts.
/// </summary>
public static class RepositoryHostConnectivityEndpoints
{
    public static IEndpointRouteBuilder MapRepositoryHostConnectivityEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        app.MapPost(
            "/api/webui/repository-host-connections/{key}:verify",
            async (
                string key,
                IQueryHandler<VerifyRepositoryHostConnectivityQuery, ConnectionConnectivityResult> handler,
                CancellationToken ct
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new VerifyRepositoryHostConnectivityQuery(key),
                    ct
                );
                return Results.Ok(result);
            }
        );

        return app;
    }
}
