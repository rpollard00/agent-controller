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
        // Command handlers
        services.AddScoped<ICommandHandler<CreateWorkItemCommand, WorkCandidate>, CreateWorkItemCommandHandler>();
        services.AddScoped<ICommandHandler<IngestRuntimeEventCommand, IngestRuntimeEventResult>, IngestRuntimeEventCommandHandler>();

        // Query handlers
        services.AddScoped<IQueryHandler<ListWorkItemsQuery, IReadOnlyList<WorkCandidate>>, ListWorkItemsQueryHandler>();
        services.AddScoped<IQueryHandler<GetWorkItemByIdQuery, WorkCandidate?>, GetWorkItemByIdQueryHandler>();
        services.AddScoped<IQueryHandler<ListRunsQuery, RunListResult>, ListRunsQueryHandler>();
        services.AddScoped<IQueryHandler<GetRunByIdQuery, RunDetailResult?>, GetRunByIdQueryHandler>();
        services.AddScoped<IQueryHandler<RunAzureDevOpsDiagnosticQuery, AzureDevOpsDiagnosticResult>, RunAzureDevOpsDiagnosticQueryHandler>();

        return services;
    }
}
