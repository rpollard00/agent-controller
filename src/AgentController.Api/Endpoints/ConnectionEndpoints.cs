using System.Text.Json;
using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentController.Api.Endpoints;

/// <summary>
/// Connection management and project-enumeration endpoint group:
/// GET /connections — list all connections.
/// GET /connections/{key} — get a connection by key.
/// POST /connections — create a connection.
/// PUT /connections/{key} — update a connection.
/// DELETE /connections/{key} — delete a connection.
/// POST /connections/{key}/verify — verify connectivity for a connection.
/// GET /connections/{key}/projects — list projects for a connection.
/// GET /connections/{key}/repositories?project= — list repositories within a project.
/// POST /connections/{key}/repositories/onboard — onboard a repository from the connection.
/// </summary>
public static class ConnectionEndpoints
{
    private const string ConnectionsPath = "/api/webui/connections";

    public static IEndpointRouteBuilder MapConnectionEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/api/webui/connections");

        // GET /api/webui/connections
        group.MapGet(
            "",
            async (
                IQueryHandler<ListConnectionsQuery, IReadOnlyList<ConnectionProfile>> handler,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    await handler.ExecuteAsync(new ListConnectionsQuery(), cancellationToken)
                )
        );

        // GET /api/webui/connections/{key}
        group.MapGet(
            "/{key}",
            async (
                string key,
                IQueryHandler<GetConnectionByKeyQuery, ConnectionOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new GetConnectionByKeyQuery(key),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        // POST /api/webui/connections
        group.MapPost(
            "",
            async (
                HttpRequest request,
                ICommandHandler<CreateConnectionCommand, ConnectionOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                ConnectionProfile? profile;
                try
                {
                    profile = await request.ReadFromJsonAsync<ConnectionProfile>(
                        cancellationToken: cancellationToken
                    );
                }
                catch (NotSupportedException)
                {
                    return ValidationProblem(
                        new Dictionary<string, string[]>(StringComparer.Ordinal)
                        {
                            ["providerSettings"] =
                            [
                                "Missing or invalid 'provider' type discriminator."
                            ]
                        }
                    );
                }
                catch (JsonException)
                {
                    return ValidationProblem(
                        new Dictionary<string, string[]>(StringComparer.Ordinal)
                        {
                            ["providerSettings"] =
                            [
                                "Malformed JSON in request body."
                            ]
                        }
                    );
                }

                if (profile is null)
                {
                    return ValidationProblem(
                        new Dictionary<string, string[]>(StringComparer.Ordinal)
                        {
                            [""] = ["Request body is empty."]
                        }
                    );
                }

                var result = await handler.HandleAsync(
                    new CreateConnectionCommand(profile),
                    cancellationToken
                );
                return MapResult(
                    result,
                    connection =>
                        Results.Created(
                            $"{ConnectionsPath}/{Uri.EscapeDataString(connection!.Key)}",
                            connection
                        )
                );
            }
        );

        // PUT /api/webui/connections/{key}
        group.MapPut(
            "/{key}",
            async (
                string key,
                HttpRequest request,
                ICommandHandler<UpdateConnectionCommand, ConnectionOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                ConnectionProfile? profile;
                try
                {
                    profile = await request.ReadFromJsonAsync<ConnectionProfile>(
                        cancellationToken: cancellationToken
                    );
                }
                catch (NotSupportedException)
                {
                    return ValidationProblem(
                        new Dictionary<string, string[]>(StringComparer.Ordinal)
                        {
                            ["providerSettings"] =
                            [
                                "Missing or invalid 'provider' type discriminator."
                            ]
                        }
                    );
                }
                catch (JsonException)
                {
                    return ValidationProblem(
                        new Dictionary<string, string[]>(StringComparer.Ordinal)
                        {
                            ["providerSettings"] =
                            [
                                "Malformed JSON in request body."
                            ]
                        }
                    );
                }

                if (profile is null)
                {
                    return ValidationProblem(
                        new Dictionary<string, string[]>(StringComparer.Ordinal)
                        {
                            [""] = ["Request body is empty."]
                        }
                    );
                }

                var result = await handler.HandleAsync(
                    new UpdateConnectionCommand(key, profile),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        // DELETE /api/webui/connections/{key}
        group.MapDelete(
            "/{key}",
            async (
                string key,
                ICommandHandler<DeleteConnectionCommand, ConnectionOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new DeleteConnectionCommand(key),
                    cancellationToken
                );
                return MapResult(result, _ => Results.NoContent());
            }
        );

        // POST /api/webui/connections/{key}/verify
        group.MapPost(
            "/{key}/verify",
            async (
                string key,
                IQueryHandler<VerifyConnectionQuery, ConnectionConnectivityResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new VerifyConnectionQuery(key),
                    cancellationToken
                );
                return Results.Ok(result);
            }
        );

        // GET /api/webui/connections/{key}/projects
        group.MapGet(
            "/{key}/projects",
            async (
                string key,
                IQueryHandler<ListConnectionProjectsQuery, IReadOnlyList<ConnectionProject>> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var projects = await handler.ExecuteAsync(
                    new ListConnectionProjectsQuery(key),
                    cancellationToken
                );
                return Results.Ok(projects);
            }
        );

        // GET /api/webui/connections/{key}/repositories?project=
        group.MapGet(
            "/{key}/repositories",
            async (
                string key,
                string? project,
                IQueryHandler<ListHostRepositoriesQuery, IReadOnlyList<HostRepository>> handler,
                CancellationToken cancellationToken
            ) =>
            {
                if (string.IsNullOrWhiteSpace(project))
                {
                    return Results.BadRequest("Project parameter is required.");
                }

                var repositories = await handler.ExecuteAsync(
                    new ListHostRepositoriesQuery(key, project!),
                    cancellationToken
                );
                return Results.Ok(repositories);
            }
        );

        // POST /api/webui/connections/{key}/repositories/onboard
        group.MapPost(
            "/{key}/repositories/onboard",
            async (
                string key,
                OnboardRepositoryRequest request,
                ICommandHandler<OnboardRepositoryFromHostCommand, RepositoryOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var command = new OnboardRepositoryFromHostCommand(
                    key,
                    request.Project,
                    request.RepositoryId,
                    request.RepositoryKey
                );
                var result = await handler.HandleAsync(command, cancellationToken);
                return MapOnboardResult(result);
            }
        );

        // GET /api/webui/connections/{key}/repositories/{repositoryId}/branches?project=
        group.MapGet(
            "/{key}/repositories/{repositoryId}/branches",
            async (
                string key,
                string repositoryId,
                string? project,
                IConnectionStore connectionStore,
                IQueryHandler<ListHostRepositoryBranchesQuery, IReadOnlyList<string>> handler,
                CancellationToken cancellationToken
            ) =>
            {
                if (string.IsNullOrWhiteSpace(project))
                {
                    return Results.BadRequest("Project parameter is required.");
                }

                var profile = await connectionStore.GetByKeyAsync(key, cancellationToken);
                if (profile is null)
                {
                    return NotFoundProblem($"Connection '{key}' not found.");
                }

                var branches = await handler.ExecuteAsync(
                    new ListHostRepositoryBranchesQuery(key, project!, repositoryId),
                    cancellationToken
                );
                return Results.Ok(branches);
            }
        );

        return app;
    }

    private static IResult MapResult(
        ConnectionOperationResult result,
        Func<ConnectionProfile?, IResult> onSuccess
    ) =>
        result.Status switch
        {
            ConnectionOperationStatus.Succeeded => onSuccess(result.Connection),
            ConnectionOperationStatus.ValidationFailed => ValidationProblem(
                result.ValidationErrors
            ),
            ConnectionOperationStatus.NotFound => NotFoundProblem(result.Detail),
            ConnectionOperationStatus.Conflict => ConflictProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported connection operation status '{result.Status}'."
            ),
        };

    private static IResult MapOnboardResult(RepositoryOperationResult result) =>
        result.Status switch
        {
            RepositoryOperationStatus.Succeeded => Results.Created(
                $"/api/webui/repositories/{Uri.EscapeDataString(result.Repository!.Key)}",
                result.Repository
            ),
            RepositoryOperationStatus.ValidationFailed => ValidationProblem(
                result.ValidationErrors
            ),
            RepositoryOperationStatus.NotFound => NotFoundProblem(result.Detail),
            RepositoryOperationStatus.Conflict => ConflictProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported repository operation status '{result.Status}'."
            ),
        };

    private static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            statusCode: StatusCodes.Status400BadRequest,
            title: "Validation failed."
        );

    private static IResult NotFoundProblem(string? detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Resource not found.",
            detail: detail
        );

    private static IResult ConflictProblem(string? detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Resource conflict.",
            detail: detail
        );

    /// <summary>Request body for the onboard endpoint.</summary>
    public sealed record OnboardRepositoryRequest(
        /// <summary>Provider-specific project name to scope the repository.</summary>
        string Project,

        /// <summary>Provider-specific repository identifier (e.g. ADO repo GUID).</summary>
        string RepositoryId,

        /// <summary>Optional stable key for the new repository profile.</summary>
        string? RepositoryKey
    );
}
