using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Api;

/// <summary>
/// Maps the web UI API for managed repository, work source, and runtime environment profiles.
/// Endpoints delegate all validation and relationship behavior to application handlers.
/// </summary>
public static class WebUiControllers
{
    private const string RepositoriesPath = "/api/webui/repositories";
    private const string WorkSourceEnvironmentsPath = "/api/webui/work-source-environments";
    private const string RuntimeEnvironmentsPath = "/api/webui/runtime-environments";
    private const string RepositoryHostConnectionsPath = "/api/webui/repository-host-connections";

    public static IEndpointRouteBuilder MapWebUiControllers(this IEndpointRouteBuilder app)
    {
        MapRepositoryControllers(app.MapGroup(RepositoriesPath));
        MapWorkSourceEnvironmentControllers(app.MapGroup(WorkSourceEnvironmentsPath));
        MapRuntimeEnvironmentControllers(app.MapGroup(RuntimeEnvironmentsPath));
        MapRepositoryHostConnectionControllers(app.MapGroup(RepositoryHostConnectionsPath));
        return app;
    }

    private static void MapRepositoryControllers(RouteGroupBuilder group)
    {
        group.MapPost(
            "",
            async (
                RepositoryProfile profile,
                ICommandHandler<CreateRepositoryCommand, RepositoryOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new CreateRepositoryCommand(profile),
                    cancellationToken
                );
                return MapResult(
                    result,
                    repository =>
                        Results.Created(
                            $"{RepositoriesPath}/{Uri.EscapeDataString(repository!.Key)}",
                            repository
                        )
                );
            }
        );

        group.MapGet(
            "",
            async (
                IQueryHandler<ListRepositoriesQuery, IReadOnlyList<RepositoryProfile>> handler,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    await handler.ExecuteAsync(new ListRepositoriesQuery(), cancellationToken)
                )
        );

        group.MapGet(
            "/{key}",
            async (
                string key,
                IQueryHandler<GetRepositoryByKeyQuery, RepositoryOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new GetRepositoryByKeyQuery(key),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapPut(
            "/{key}",
            async (
                string key,
                RepositoryProfile profile,
                ICommandHandler<UpdateRepositoryCommand, RepositoryOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new UpdateRepositoryCommand(key, profile),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapDelete(
            "/{key}",
            async (
                string key,
                ICommandHandler<DeleteRepositoryCommand, RepositoryOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new DeleteRepositoryCommand(key),
                    cancellationToken
                );
                return MapResult(result, _ => Results.NoContent());
            }
        );
    }

    private static void MapWorkSourceEnvironmentControllers(RouteGroupBuilder group)
    {
        group.MapPost(
            "",
            async (
                WorkSourceEnvironmentProfile profile,
                ICommandHandler<
                    CreateWorkSourceEnvironmentCommand,
                    WorkSourceEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new CreateWorkSourceEnvironmentCommand(profile),
                    cancellationToken
                );
                return MapResult(
                    result,
                    environment =>
                        Results.Created(
                            $"{WorkSourceEnvironmentsPath}/{Uri.EscapeDataString(environment!.Key)}",
                            environment
                        )
                );
            }
        );

        group.MapGet(
            "",
            async (
                IQueryHandler<
                    ListWorkSourceEnvironmentsQuery,
                    IReadOnlyList<WorkSourceEnvironmentProfile>
                > handler,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    await handler.ExecuteAsync(
                        new ListWorkSourceEnvironmentsQuery(),
                        cancellationToken
                    )
                )
        );

        group.MapGet(
            "/{key}",
            async (
                string key,
                IQueryHandler<
                    GetWorkSourceEnvironmentByKeyQuery,
                    WorkSourceEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new GetWorkSourceEnvironmentByKeyQuery(key),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapPut(
            "/{key}",
            async (
                string key,
                WorkSourceEnvironmentProfile profile,
                ICommandHandler<
                    UpdateWorkSourceEnvironmentCommand,
                    WorkSourceEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new UpdateWorkSourceEnvironmentCommand(key, profile),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapDelete(
            "/{key}",
            async (
                string key,
                ICommandHandler<
                    DeleteWorkSourceEnvironmentCommand,
                    WorkSourceEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new DeleteWorkSourceEnvironmentCommand(key),
                    cancellationToken
                );
                return MapResult(result, _ => Results.NoContent());
            }
        );

    }

    private static void MapRuntimeEnvironmentControllers(RouteGroupBuilder group)
    {
        group.MapPost(
            "",
            async (
                RuntimeEnvironmentProfile profile,
                ICommandHandler<
                    CreateRuntimeEnvironmentCommand,
                    RuntimeEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new CreateRuntimeEnvironmentCommand(profile),
                    cancellationToken
                );
                return MapResult(
                    result,
                    environment =>
                        Results.Created(
                            $"{RuntimeEnvironmentsPath}/{Uri.EscapeDataString(environment!.Key)}",
                            environment
                        )
                );
            }
        );

        group.MapGet(
            "",
            async (
                IQueryHandler<
                    ListRuntimeEnvironmentsQuery,
                    IReadOnlyList<RuntimeEnvironmentProfile>
                > handler,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    await handler.ExecuteAsync(
                        new ListRuntimeEnvironmentsQuery(),
                        cancellationToken
                    )
                )
        );

        group.MapGet(
            "/{key}",
            async (
                string key,
                IQueryHandler<
                    GetRuntimeEnvironmentByKeyQuery,
                    RuntimeEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new GetRuntimeEnvironmentByKeyQuery(key),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapPut(
            "/{key}",
            async (
                string key,
                RuntimeEnvironmentProfile profile,
                ICommandHandler<
                    UpdateRuntimeEnvironmentCommand,
                    RuntimeEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new UpdateRuntimeEnvironmentCommand(key, profile),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapDelete(
            "/{key}",
            async (
                string key,
                ICommandHandler<
                    DeleteRuntimeEnvironmentCommand,
                    RuntimeEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new DeleteRuntimeEnvironmentCommand(key),
                    cancellationToken
                );
                return MapResult(result, _ => Results.NoContent());
            }
        );
    }

    private static void MapRepositoryHostConnectionControllers(RouteGroupBuilder group)
    {
        group.MapPost(
            "",
            async (
                RepositoryHostConnectionProfile profile,
                ICommandHandler<
                    CreateRepositoryHostConnectionCommand,
                    RepositoryHostConnectionOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new CreateRepositoryHostConnectionCommand(profile),
                    cancellationToken
                );
                return MapResult(
                    result,
                    connection =>
                        Results.Created(
                            $"{RepositoryHostConnectionsPath}/{Uri.EscapeDataString(connection!.Key)}",
                            connection
                        )
                );
            }
        );

        group.MapGet(
            "",
            async (
                IQueryHandler<
                    ListRepositoryHostConnectionsQuery,
                    IReadOnlyList<RepositoryHostConnectionProfile>
                > handler,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    await handler.ExecuteAsync(
                        new ListRepositoryHostConnectionsQuery(),
                        cancellationToken
                    )
                )
        );

        group.MapGet(
            "/{key}",
            async (
                string key,
                IQueryHandler<
                    GetRepositoryHostConnectionByKeyQuery,
                    RepositoryHostConnectionOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new GetRepositoryHostConnectionByKeyQuery(key),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapPut(
            "/{key}",
            async (
                string key,
                RepositoryHostConnectionProfile profile,
                ICommandHandler<
                    UpdateRepositoryHostConnectionCommand,
                    RepositoryHostConnectionOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new UpdateRepositoryHostConnectionCommand(key, profile),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapDelete(
            "/{key}",
            async (
                string key,
                ICommandHandler<
                    DeleteRepositoryHostConnectionCommand,
                    RepositoryHostConnectionOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new DeleteRepositoryHostConnectionCommand(key),
                    cancellationToken
                );
                return MapResult(result, _ => Results.NoContent());
            }
        );
    }

    private static IResult MapResult(
        RepositoryOperationResult result,
        Func<RepositoryProfile?, IResult> onSuccess
    ) =>
        result.Status switch
        {
            RepositoryOperationStatus.Succeeded => onSuccess(result.Repository),
            RepositoryOperationStatus.ValidationFailed => ValidationProblem(
                result.ValidationErrors
            ),
            RepositoryOperationStatus.NotFound => NotFoundProblem(result.Detail),
            RepositoryOperationStatus.Conflict => ConflictProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported repository operation status '{result.Status}'."
            ),
        };

    private static IResult MapResult(
        WorkSourceEnvironmentOperationResult result,
        Func<WorkSourceEnvironmentProfile?, IResult> onSuccess
    ) =>
        result.Status switch
        {
            WorkSourceEnvironmentOperationStatus.Succeeded => onSuccess(result.Environment),
            WorkSourceEnvironmentOperationStatus.ValidationFailed => ValidationProblem(
                result.ValidationErrors
            ),
            WorkSourceEnvironmentOperationStatus.NotFound => NotFoundProblem(result.Detail),
            WorkSourceEnvironmentOperationStatus.Conflict => ConflictProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported work source environment operation status '{result.Status}'."
            ),
        };

    private static IResult MapResult(
        RuntimeEnvironmentOperationResult result,
        Func<RuntimeEnvironmentProfile?, IResult> onSuccess
    ) =>
        result.Status switch
        {
            RuntimeEnvironmentOperationStatus.Succeeded => onSuccess(result.Environment),
            RuntimeEnvironmentOperationStatus.ValidationFailed => ValidationProblem(
                result.ValidationErrors
            ),
            RuntimeEnvironmentOperationStatus.NotFound => NotFoundProblem(result.Detail),
            RuntimeEnvironmentOperationStatus.Conflict => ConflictProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported runtime environment operation status '{result.Status}'."
            ),
        };

    private static IResult MapResult(
        RepositoryHostConnectionOperationResult result,
        Func<RepositoryHostConnectionProfile?, IResult> onSuccess
    ) =>
        result.Status switch
        {
            RepositoryHostConnectionOperationStatus.Succeeded => onSuccess(result.Connection),
            RepositoryHostConnectionOperationStatus.ValidationFailed => ValidationProblem(
                result.ValidationErrors
            ),
            RepositoryHostConnectionOperationStatus.NotFound => NotFoundProblem(result.Detail),
            RepositoryHostConnectionOperationStatus.Conflict => ConflictProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported repository host connection operation status '{result.Status}'."
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
}
