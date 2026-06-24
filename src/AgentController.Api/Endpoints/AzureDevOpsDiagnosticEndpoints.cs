using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentController.Api.Endpoints;

/// <summary>
/// Azure DevOps diagnostic endpoint group: GET /azure-devops/diagnostic.
/// Thin endpoint — all config validation, PAT resolution, and HTTP calls
/// are handled by the CQRS handler and infrastructure client.
/// </summary>
public static class AzureDevOpsDiagnosticEndpoints
{
    public static IEndpointRouteBuilder MapAzureDevOpsDiagnosticEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/azure-devops/diagnostic",
            async (
                IQueryHandler<RunAzureDevOpsDiagnosticQuery, AzureDevOpsDiagnosticResult> handler,
                CancellationToken ct
            ) =>
            {
                var result = await handler.ExecuteAsync(new RunAzureDevOpsDiagnosticQuery(), ct);
                return Results.Ok(result);
            }
        );

        return app;
    }
}
