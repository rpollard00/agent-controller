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
    /// including the work-source connectivity verifier resolver infrastructure.
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
        services.AddScoped<
            ICommandHandler<
                CreateRepositoryHostConnectionCommand,
                RepositoryHostConnectionOperationResult
            >,
            CreateRepositoryHostConnectionCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<
                UpdateRepositoryHostConnectionCommand,
                RepositoryHostConnectionOperationResult
            >,
            UpdateRepositoryHostConnectionCommandHandler
        >();
        services.AddScoped<
            ICommandHandler<
                DeleteRepositoryHostConnectionCommand,
                RepositoryHostConnectionOperationResult
            >,
            DeleteRepositoryHostConnectionCommandHandler
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
            IQueryHandler<VerifyWorkSourceConnectivityQuery, WorkSourceConnectivityResult>,
            VerifyWorkSourceConnectivityQueryHandler
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
                ListRepositoryHostConnectionsQuery,
                IReadOnlyList<RepositoryHostConnectionProfile>
            >,
            ListRepositoryHostConnectionsQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<
                GetRepositoryHostConnectionByKeyQuery,
                RepositoryHostConnectionOperationResult
            >,
            GetRepositoryHostConnectionByKeyQueryHandler
        >();
        services.AddScoped<
            IQueryHandler<
                VerifyRepositoryHostConnectivityQuery,
                RepositoryHostConnectivityResult
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
        // Work-source connectivity verifier resolver
        services.AddWorkSourceConnectivityVerifierResolver();

        // Repository host resolver
        services.AddRepositoryHostResolver();

        return services;
    }

    /// <summary>
    /// Registers the work-source connectivity verifier resolver and its backing registry.
    /// Individual providers register their verifiers via
    /// <see cref="AddWorkSourceConnectivityVerifier{TVerifier}(IServiceCollection, string[])"/>.
    /// </summary>
    public static IServiceCollection AddWorkSourceConnectivityVerifierResolver(
        this IServiceCollection services
    )
    {
        // Build the type-keyed dictionary from the static registry at container build time.
        // Provider-to-type mappings are accumulated via AddWorkSourceConnectivityVerifier<T>()
        // calls before the container is built.
        services.AddSingleton<IReadOnlyDictionary<string, Type>>(_ =>
            WorkSourceConnectivityVerifierRegistry.Build());
        services.AddSingleton<IWorkSourceConnectivityVerifierResolver, WorkSourceConnectivityVerifierResolver>();
        return services;
    }

    /// <summary>
    /// Registers a provider-specific connectivity verifier for one or more provider strings.
    /// The verifier is registered as scoped and keyed into the resolver's type dictionary.
    /// </summary>
    /// <typeparam name="TVerifier">The verifier implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="providerKeys">
    /// Provider discriminator strings (e.g. "AzureDevOpsBoards", "AzureDevOpsRepos").
    /// </param>
    public static IServiceCollection AddWorkSourceConnectivityVerifier<TVerifier>(
        this IServiceCollection services,
        params string[] providerKeys
    ) where TVerifier : class, IWorkSourceConnectivityVerifier
    {
        // Register the verifier implementation as a scoped service so it can be resolved
        // by the resolver at runtime through the scoped service provider.
        services.AddScoped<TVerifier>();

        // Accumulate the provider key → implementation type mapping in the static registry.
        // The registry is consumed once at container build time by the resolver registration.
        WorkSourceConnectivityVerifierRegistry.Register(typeof(TVerifier), providerKeys);

        return services;
    }

    // ─── Repository host resolver ───

    /// <summary>
    /// Registers the repository host resolver and its backing registry.
    /// Individual providers register their hosts via
    /// <see cref="AddRepositoryHost{THost}(IServiceCollection, string[])"/>.
    /// </summary>
    public static IServiceCollection AddRepositoryHostResolver(
        this IServiceCollection services
    )
    {
        // Build the type-keyed dictionary from the static registry at container build time.
        // Provider-to-type mappings are accumulated via AddRepositoryHost<T>()
        // calls before the container is built.
        // We capture the dictionary inline to avoid conflicting with the
        // WorkSourceConnectivityVerifierResolver which also registers IReadOnlyDictionary<string, Type>.
        var hostTypes = RepositoryHostConnectionRegistry.Build();
        services.AddSingleton<IRepositoryHostResolver>(sp =>
            new RepositoryHostResolver(sp.GetRequiredService<IServiceScopeFactory>(), hostTypes));
        return services;
    }

    /// <summary>
    /// Registers a provider-specific repository host for one or more provider strings.
    /// The host is registered as scoped and keyed into the resolver's type dictionary.
    /// </summary>
    /// <typeparam name="THost">The host implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="providerKeys">
    /// Provider discriminator strings (e.g. "AzureDevOpsRepos", "GitHub", "GitLab").
    /// </param>
    public static IServiceCollection AddRepositoryHost<THost>(
        this IServiceCollection services,
        params string[] providerKeys
    ) where THost : class, IRepositoryHostConnection
    {
        // Register the host implementation as a scoped service so it can be resolved
        // by the resolver at runtime through the scoped service provider.
        services.AddScoped<THost>();

        // Accumulate the provider key → implementation type mapping in the static registry.
        // The registry is consumed once at container build time by the resolver registration.
        RepositoryHostConnectionRegistry.Register(typeof(THost), providerKeys);

        return services;
    }
}
