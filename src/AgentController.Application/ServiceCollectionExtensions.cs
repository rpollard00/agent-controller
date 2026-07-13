using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application;

/// <summary>
/// Extension methods for registering application-layer CQRS handlers with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all application-layer command and query handlers as scoped services.
    /// </summary>
    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services)
    {
        services.AddScoped<IManagedProfileResolver, ManagedProfileResolver>();

        // Command handlers
        services.AddScoped<
            ICommandHandler<CreateWorkItemCommand, WorkCandidate>,
            CreateWorkItemCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<IngestRuntimeEventCommand, IngestRuntimeEventResult>,
            IngestRuntimeEventCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<CreateRepositoryCommand, RepositoryOperationResult>,
            CreateRepositoryCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<UpdateRepositoryCommand, RepositoryOperationResult>,
            UpdateRepositoryCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<DeleteRepositoryCommand, RepositoryOperationResult>,
            DeleteRepositoryCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<
                CreateAzureDevOpsEnvironmentCommand,
                AzureDevOpsEnvironmentOperationResult
            >,
            CreateAzureDevOpsEnvironmentCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<
                UpdateAzureDevOpsEnvironmentCommand,
                AzureDevOpsEnvironmentOperationResult
            >,
            UpdateAzureDevOpsEnvironmentCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<
                DeleteAzureDevOpsEnvironmentCommand,
                AzureDevOpsEnvironmentOperationResult
            >,
            DeleteAzureDevOpsEnvironmentCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<CreateRuntimeEnvironmentCommand, RuntimeEnvironmentOperationResult>,
            CreateRuntimeEnvironmentCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<UpdateRuntimeEnvironmentCommand, RuntimeEnvironmentOperationResult>,
            UpdateRuntimeEnvironmentCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<DeleteRuntimeEnvironmentCommand, RuntimeEnvironmentOperationResult>,
            DeleteRuntimeEnvironmentCommandHandler
        >();

        // Query handlers
        services.AddScoped<
            IQueryHandler<ListWorkItemsQuery, IReadOnlyList<WorkCandidate>>,
            ListWorkItemsQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<GetWorkItemByIdQuery, WorkCandidate?>,
            GetWorkItemByIdQueryHandler
        >();
        services.AddScoped<IQueryHandler<ListRunsQuery, RunListResult>, ListRunsQueryHandler>();
        services.AddScoped<
            IQueryHandler<GetRunByIdQuery, RunDetailResult?>,
            GetRunByIdQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<RunAzureDevOpsDiagnosticQuery, AzureDevOpsDiagnosticResult>,
            RunAzureDevOpsDiagnosticQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<ListRepositoriesQuery, IReadOnlyList<RepositoryProfile>>,
            ListRepositoriesQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<GetRepositoryByKeyQuery, RepositoryOperationResult>,
            GetRepositoryByKeyQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<
                ListAzureDevOpsEnvironmentsQuery,
                IReadOnlyList<AzureDevOpsEnvironmentProfile>
            >,
            ListAzureDevOpsEnvironmentsQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<
                GetAzureDevOpsEnvironmentByKeyQuery,
                AzureDevOpsEnvironmentOperationResult
            >,
            GetAzureDevOpsEnvironmentByKeyQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<ListRuntimeEnvironmentsQuery, IReadOnlyList<RuntimeEnvironmentProfile>>,
            ListRuntimeEnvironmentsQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<GetRuntimeEnvironmentByKeyQuery, RuntimeEnvironmentOperationResult>,
            GetRuntimeEnvironmentByKeyQueryHandler
        >();

        return services;
    }
}
