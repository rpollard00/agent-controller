using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Api;

/// <summary>
/// Maps the web UI API for managed repository, Azure DevOps, and runtime environment profiles.
/// Endpoints delegate all validation and relationship behavior to application handlers.
/// </summary>
public static class WebUiControllers
{
    private const string RepositoriesPath = "/api/webui/repositories";
    private const string AzureDevOpsEnvironmentsPath = "/api/webui/ado-environments";
    private const string RuntimeEnvironmentsPath = "/api/webui/runtime-environments";

    public static IEndpointRouteBuilder MapWebUiControllers(this IEndpointRouteBuilder app)
    {
        MapRepositoryControllers(app.MapGroup(RepositoriesPath));
        MapAzureDevOpsEnvironmentControllers(app.MapGroup(AzureDevOpsEnvironmentsPath));
        MapRuntimeEnvironmentControllers(app.MapGroup(RuntimeEnvironmentsPath));
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

    private static void MapAzureDevOpsEnvironmentControllers(RouteGroupBuilder group)
    {
        group.MapPost(
            "",
            async (
                AzureDevOpsEnvironmentProfile profile,
                ICommandHandler<
                    CreateAzureDevOpsEnvironmentCommand,
                    AzureDevOpsEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new CreateAzureDevOpsEnvironmentCommand(profile),
                    cancellationToken
                );
                return MapResult(
                    result,
                    environment =>
                        Results.Created(
                            $"{AzureDevOpsEnvironmentsPath}/{Uri.EscapeDataString(environment!.Key)}",
                            environment
                        )
                );
            }
        );

        group.MapGet(
            "",
            async (
                IQueryHandler<
                    ListAzureDevOpsEnvironmentsQuery,
                    IReadOnlyList<AzureDevOpsEnvironmentProfile>
                > handler,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    await handler.ExecuteAsync(
                        new ListAzureDevOpsEnvironmentsQuery(),
                        cancellationToken
                    )
                )
        );

        group.MapGet(
            "/{key}",
            async (
                string key,
                IQueryHandler<
                    GetAzureDevOpsEnvironmentByKeyQuery,
                    AzureDevOpsEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new GetAzureDevOpsEnvironmentByKeyQuery(key),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapPut(
            "/{key}",
            async (
                string key,
                AzureDevOpsEnvironmentProfile profile,
                ICommandHandler<
                    UpdateAzureDevOpsEnvironmentCommand,
                    AzureDevOpsEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new UpdateAzureDevOpsEnvironmentCommand(key, profile),
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
                    DeleteAzureDevOpsEnvironmentCommand,
                    AzureDevOpsEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new DeleteAzureDevOpsEnvironmentCommand(key),
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
        AzureDevOpsEnvironmentOperationResult result,
        Func<AzureDevOpsEnvironmentProfile?, IResult> onSuccess
    ) =>
        result.Status switch
        {
            AzureDevOpsEnvironmentOperationStatus.Succeeded => onSuccess(result.Environment),
            AzureDevOpsEnvironmentOperationStatus.ValidationFailed => ValidationProblem(
                result.ValidationErrors
            ),
            AzureDevOpsEnvironmentOperationStatus.NotFound => NotFoundProblem(result.Detail),
            AzureDevOpsEnvironmentOperationStatus.Conflict => ConflictProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported Azure DevOps environment operation status '{result.Status}'."
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
