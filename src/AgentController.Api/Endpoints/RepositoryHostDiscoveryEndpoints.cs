using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentController.Api.Endpoints;

/// <summary>
/// Repository discovery and onboarding endpoint group:
/// GET /repository-host-connections/{key}/repositories — list enumerable repos from a connected host.
/// POST /repository-host-connections/{key}/repositories/onboard — create a RepositoryProfile from a selected repo.
/// </summary>
public static class RepositoryHostDiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapRepositoryHostDiscoveryEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/api/webui/repository-host-connections/{connectionKey}");

        // GET /api/webui/repository-host-connections/{connectionKey}/repositories
        group.MapGet(
            "/repositories",
            async (
                string connectionKey,
                IQueryHandler<ListHostRepositoriesQuery, IReadOnlyList<HostRepository>> handler,
                CancellationToken ct
            ) =>
            {
                var repositories = await handler.ExecuteAsync(
                    new ListHostRepositoriesQuery(connectionKey),
                    ct
                );
                return Results.Ok(repositories);
            }
        );

        // POST /api/webui/repository-host-connections/{connectionKey}/repositories/onboard
        group.MapPost(
            "/repositories/onboard",
            async (
                string connectionKey,
                OnboardRepositoryRequest request,
                ICommandHandler<OnboardRepositoryFromHostCommand, RepositoryOperationResult> handler,
                CancellationToken ct
            ) =>
            {
                var command = new OnboardRepositoryFromHostCommand(
                    connectionKey,
                    request.RepositoryId,
                    request.RepositoryKey
                );
                var result = await handler.HandleAsync(command, ct);
                return MapOnboardResult(result);
            }
        );

        return app;
    }

    private static IResult MapOnboardResult(RepositoryOperationResult result) =>
        result.Status switch
        {
            RepositoryOperationStatus.Succeeded => Results.Created(
                $"/api/webui/repositories/{Uri.EscapeDataString(result.Repository!.Key)}",
                result.Repository
            ),
            RepositoryOperationStatus.ValidationFailed => Results.ValidationProblem(
                result.ValidationErrors.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal
                ),
                statusCode: StatusCodes.Status400BadRequest,
                title: "Validation failed."
            ),
            RepositoryOperationStatus.NotFound => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Resource not found.",
                detail: result.Detail
            ),
            RepositoryOperationStatus.Conflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Resource conflict.",
                detail: result.Detail
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported repository operation status '{result.Status}'."
            ),
        };

    /// <summary>
    /// Request body for the onboard endpoint.
    /// </summary>
    public sealed record OnboardRepositoryRequest(
        /// <summary>
        /// Provider-specific repository identifier (e.g. ADO repo GUID).
        /// </summary>
        string RepositoryId,

        /// <summary>
        /// Optional stable key for the new repository profile.
        /// </summary>
        string? RepositoryKey
    );
}
