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
    /// Registers all application-layer command and query handlers with the DI container,
    /// including the unified connection resolver infrastructure.
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
                CreateWorkSourceEnvironmentCommand,
                WorkSourceEnvironmentOperationResult
            >,
            CreateWorkSourceEnvironmentCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<
                UpdateWorkSourceEnvironmentCommand,
                WorkSourceEnvironmentOperationResult
            >,
            UpdateWorkSourceEnvironmentCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<
                DeleteWorkSourceEnvironmentCommand,
                WorkSourceEnvironmentOperationResult
            >,
            DeleteWorkSourceEnvironmentCommandHandler
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
            IQueryHandler<ListRepositoriesQuery, IReadOnlyList<RepositoryProfile>>,
            ListRepositoriesQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<GetRepositoryByKeyQuery, RepositoryOperationResult>,
            GetRepositoryByKeyQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<GetRepositoryCloneTransportQuery, RepositoryCloneTransportQueryResult>,
            GetRepositoryCloneTransportQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<
                ListWorkSourceEnvironmentsQuery,
                IReadOnlyList<WorkSourceEnvironmentProfile>
            >,
            ListWorkSourceEnvironmentsQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<
                GetWorkSourceEnvironmentByKeyQuery,
                WorkSourceEnvironmentOperationResult
            >,
            GetWorkSourceEnvironmentByKeyQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<ListRuntimeEnvironmentsQuery, IReadOnlyList<RuntimeEnvironmentProfile>>,
            ListRuntimeEnvironmentsQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<GetRuntimeEnvironmentByKeyQuery, RuntimeEnvironmentOperationResult>,
            GetRuntimeEnvironmentByKeyQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<
                VerifyRepositoryHostConnectivityQuery,
                ConnectionConnectivityResult
            >,
            VerifyRepositoryHostConnectivityQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<ListHostRepositoriesQuery, IReadOnlyList<HostRepository>>,
            ListHostRepositoriesQueryHandler
        >();
        services.AddScoped<
            ICommandHandler<OnboardRepositoryFromHostCommand, RepositoryOperationResult>,
            OnboardRepositoryFromHostCommandHandler
        >();

        // Connection management command handlers
        services.AddScoped<
            ICommandHandler<CreateConnectionCommand, ConnectionOperationResult>,
            CreateConnectionCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<UpdateConnectionCommand, ConnectionOperationResult>,
            UpdateConnectionCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<DeleteConnectionCommand, ConnectionOperationResult>,
            DeleteConnectionCommandHandler
        >();

        // Connection management query handlers
        services.AddScoped<
            IQueryHandler<ListConnectionsQuery, IReadOnlyList<Domain.ConnectionProfile>>,
            ListConnectionsQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<GetConnectionByKeyQuery, ConnectionOperationResult>,
            GetConnectionByKeyQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<VerifyConnectionQuery, ConnectionConnectivityResult>,
            VerifyConnectionQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<ListConnectionProjectsQuery, IReadOnlyList<Abstractions.ConnectionProject>>,
            ListConnectionProjectsQueryHandler
        >();

        // Secrets management command handlers
        services.AddScoped<
            ICommandHandler<CreateSecretCommand, CreateSecretResult>,
            CreateSecretCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<CreateSecretVersionCommand, CreateSecretVersionResult>,
            CreateSecretVersionCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<DeleteSecretCommand, DeleteSecretResult>,
            DeleteSecretCommandHandler
        >();

        // Secrets management query handlers
        services.AddScoped<
            IQueryHandler<ListSecretsQuery, IReadOnlyList<Domain.Secrets.SecretInfo>>,
            ListSecretsQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<ListSecretVersionsQuery, IReadOnlyList<Domain.Secrets.SecretVersionInfo>?>,
            ListSecretVersionsQueryHandler
        >();

        // Unified connection resolver
        services.AddConnectionResolver();

        return services;
    }

    // ─── Unified connection resolver ───

    /// <summary>
    /// Registers the unified connection resolver and its backing registry.
    /// Individual providers register their connections via
    /// <see cref="AddConnection{TConnection}(IServiceCollection, string[])"/>.
    /// </summary>
    public static IServiceCollection AddConnectionResolver(
        this IServiceCollection services
    )
    {
        // Defer Build() into the singleton factory lambda so the registry snapshot is
        // captured at first resolution time — after all providers (including AzureDevOps)
        // have been registered. AddConnectionResolver() can be called before or after
        // AddConnection<T>() calls and the lookup order is independent of registration order.
        services.AddSingleton<IConnectionResolver>(sp =>
            new ConnectionResolver(
                sp.GetRequiredService<IServiceScopeFactory>(),
                ConnectionRegistry.Build()
            ));
        return services;
    }

    /// <summary>
    /// Registers a provider-specific unified connection for one or more provider strings.
    /// The connection is registered as scoped and keyed into the resolver's type dictionary.
    /// </summary>
    /// <typeparam name="TConnection">The connection implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="providerKeys">
    /// Provider discriminator strings (e.g. "AzureDevOps", "GitHub", "GitLab").
    /// </param>
    public static IServiceCollection AddConnection<TConnection>(
        this IServiceCollection services,
        params string[] providerKeys
    ) where TConnection : class, IConnection
    {
        // Register the connection implementation as a scoped service so it can be resolved
        // by the resolver at runtime through the scoped service provider.
        services.AddScoped<TConnection>();

        // Accumulate the provider key → implementation type mapping in the static registry.
        // The registry is consumed once at container build time by the resolver registration.
        ConnectionRegistry.Register(typeof(TConnection), providerKeys);

        return services;
    }
}
