using AgentController.Application;
using AgentController.Application.Services;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Data;
using AgentController.Infrastructure.Data.Repositories;
using AgentController.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions for registering AgentController infrastructure services.
/// </summary>
public static class AgentControllerServiceCollectionExtensions
{
    /// <summary>
    /// Registers all AgentController options from configuration with validation-on-start.
    /// </summary>
    public static IServiceCollection AddAgentControllerOptions(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services
            .AddOptions<AgentControllerOptions>()
            .Bind(configuration.GetSection(AgentControllerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<WorkSourceOptions>()
            .Bind(configuration.GetSection(WorkSourceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<SourceControlOptions>()
            .Bind(configuration.GetSection(SourceControlOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<EnvironmentProviderOptions>()
            .Bind(configuration.GetSection(EnvironmentProviderOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<RuntimeOptions>()
            .Bind(configuration.GetSection(RuntimeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddOptions<Dictionary<string, RepositoryProfileOptions>>()
            .Bind(configuration.GetSection(RepositoriesOptions.SectionName));

        services
            .AddOptions<AzureDevOpsBoardsOptions>()
            .Bind(configuration.GetSection(AzureDevOpsBoardsOptions.SectionName));

        services
            .AddOptions<LocalWorkOptions>()
            .Bind(configuration.GetSection(LocalWorkOptions.SectionName));

        return services;
    }

    /// <summary>
    /// Registers the EF Core <see cref="AgentControllerDbContext"/> with a SQLite
    /// connection string from <see cref="PersistenceOptions"/>. The context is registered
    /// as scoped so each request or worker poll cycle gets a fresh unit-of-work.
    ///
    /// This method never calls <c>MigrateAsync()</c> or <c>EnsureCreated()</c>.
    /// Schema migrations are owned by the dedicated <c>AgentController.Migrations</c>
    /// console application.
    /// </summary>
    public static IServiceCollection AddAgentControllerDbContext(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var persistenceOptions = configuration
            .GetSection(PersistenceOptions.SectionName)
            .Get<PersistenceOptions>()
            ?? new PersistenceOptions();

        var connectionString =
            PersistenceConnectionResolver.Resolve(persistenceOptions);

        services.AddDbContext<AgentControllerDbContext>(options =>
        {
            options.UseSqlite(
                connectionString,
                sqliteOptions => sqliteOptions.MigrationsAssembly("AgentController.Migrations")
            );
        });

        return services;
    }

    /// <summary>
    /// Registers EF Core-backed repository implementations for all application-layer
    /// persistence contracts (<see cref="IWorkItemStore"/>, <see cref="IAgentRunStore"/>,
    /// <see cref="ILifecycleEventStore"/>, <see cref="IEnvironmentStore"/>,
    /// <see cref="IRepositoryStore"/>).
    ///
    /// Repositories are registered as scoped so each request or worker poll cycle
    /// gets a fresh unit-of-work backed by the same <see cref="AgentControllerDbContext"/>.
    ///
    /// Requires <see cref="AddAgentControllerDbContext"/> to be called first.
    /// </summary>
    public static IServiceCollection AddAgentControllerRepositories(
        this IServiceCollection services
    )
    {
        services.AddScoped<IWorkItemStore, EfWorkItemStore>();
        services.AddScoped<IAgentRunStore, EfAgentRunStore>();
        services.AddScoped<ILifecycleEventStore, EfLifecycleEventStore>();
        services.AddScoped<IEnvironmentStore, EfEnvironmentStore>();
        services.AddScoped<IRepositoryStore, EfRepositoryStore>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="RunLifecycleService"/> as a scoped <see cref="IRunLifecycleService"/>.
    /// Coordinates <see cref="IAgentRunStore"/>, <see cref="ILifecycleEventStore"/>, and
    /// <see cref="IWorkItemStore"/> for consistent run lifecycle transitions.
    ///
    /// Requires <see cref="AddAgentControllerRepositories"/> to be called first.
    /// </summary>
    public static IServiceCollection AddAgentControllerLifecycleService(
        this IServiceCollection services)
    {
        services.AddScoped<IRunLifecycleService, RunLifecycleService>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="LocalFakeWorkSource"/> as a singleton <see cref="IWorkSource"/>
    /// implementation backed by the persisted <see cref="IWorkItemStore"/>. Applies
    /// configured <see cref="WorkSourceOptions"/> for tag/state eligibility filtering.
    ///
    /// <see cref="LocalFakeWorkSource"/> uses <see cref="IServiceScopeFactory"/> internally
    /// to resolve the scoped <see cref="IWorkItemStore"/> per operation, so it is safe for
    /// consumption by singleton consumers such as <see cref="BackgroundService"/>.
    ///
    /// Requires <see cref="AddAgentControllerRepositories"/> to be called first.
    /// </summary>
    public static IServiceCollection AddAgentControllerLocalFakeWorkSource(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IWorkSource, LocalFakeWorkSource>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="LocalFileWorkSource"/> as a singleton <see cref="IWorkSource"/>
    /// implementation that reads work item definitions from the <c>localWork</c> configuration
    /// section and upserts them into the persisted <see cref="IWorkItemStore"/> on first use.
    ///
    /// When <c>workSource:provider</c> is <c>"LocalFile"</c>, use this method instead of
    /// <see cref="AddAgentControllerLocalFakeWorkSource"/> to seed work items declaratively
    /// from configuration without API calls.
    ///
    /// <see cref="LocalFileWorkSource"/> uses <see cref="IServiceScopeFactory"/> internally
    /// to resolve the scoped <see cref="IWorkItemStore"/> per operation, so it is safe for
    /// consumption by singleton consumers such as <see cref="BackgroundService"/>.
    ///
    /// Requires <see cref="AddAgentControllerRepositories"/> to be called first.
    /// </summary>
    public static IServiceCollection AddAgentControllerLocalFileWorkSource(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IWorkSource, LocalFileWorkSource>();

        return services;
    }

    /// <summary>
    /// Registers deterministic no-op infrastructure providers suitable for DI seeding
    /// before real providers are wired.
    /// </summary>
    public static IServiceCollection AddAgentControllerNoOpProviders(
        this IServiceCollection services
    )
    {
        services.AddSingleton<IWorkSource, NoOpWorkSource>();
        services.AddSingleton<ISourceControlProvider, NoOpSourceControlProvider>();
        services.AddSingleton<IEnvironmentProvider, NoOpEnvironmentProvider>();
        services.AddSingleton<IAgentRuntime, NoOpAgentRuntime>();

        return services;
    }

    /// <summary>
    /// Registers the Azure DevOps Boards implementation as a singleton
    /// <see cref="IWorkSource"/> backed by <see cref="IAzureDevOpsBoardsClient"/>.
    ///
    /// This method:
    /// <list type="number">
    ///   <item>
    ///     Registers <see cref="AzureDevOpsBoardsClient"/> as a scoped
    ///     <see cref="IAzureDevOpsBoardsClient"/> with a managed <see cref="HttpClient"/>.
    ///   </item>
    ///   <item>
    ///     Registers <see cref="AzureDevOpsBoardsWorkSource"/> as a singleton
    ///     <see cref="IWorkSource"/>. It uses <see cref="IServiceScopeFactory"/>
    ///     internally to resolve scoped services per operation, making it safe
    ///     for consumption by singleton consumers such as <see cref="BackgroundService"/>.
    ///   </item>
    /// </list>
    ///
    /// Requires <see cref="AddAgentControllerOptions"/> to be called first
    /// (for <see cref="WorkSourceOptions"/> and <see cref="AzureDevOpsBoardsOptions"/>).
    ///
    /// Callers should register this <em>after</em>
    /// <see cref="AddAgentControllerNoOpProviders"/> or
    /// <see cref="AddAgentControllerLocalFakeWorkSource"/> so the last-registered
    /// <see cref="IWorkSource"/> wins.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="validateConnection">
    /// When <c>true</c> (default), validates at registration time that required
    /// Azure DevOps settings (organization URL, project, PAT) are configured.
    /// Set to <c>false</c> to skip eager validation (useful in tests or when
    /// the work source provider is not AzureDevOpsBoards).
    /// </param>
    public static IServiceCollection AddAgentControllerAzureDevOpsBoardsWorkSource(
        this IServiceCollection services,
        bool validateConnection = true)
    {
        // Register the Azure DevOps Boards HTTP client as scoped.
        // Each operation (poll cycle, request) gets a fresh client.
        services.AddScoped<IAzureDevOpsBoardsClient>(sp =>
        {
            var boardsOptions = sp.GetRequiredService<IOptions<AzureDevOpsBoardsOptions>>().Value;
            var workSourceOptions = sp.GetRequiredService<IOptions<WorkSourceOptions>>().Value;

            // Derive BaseUrl and Project from WorkSourceOptions into BoardsOptions
            boardsOptions.BaseUrl = workSourceOptions.OrganizationUrl;
            boardsOptions.Project = workSourceOptions.Project;

            if (validateConnection)
            {
                AzureDevOpsBoardsValidator.Validate(workSourceOptions, boardsOptions);
            }

            var http = new HttpClient();
            return new AzureDevOpsBoardsClient(http, boardsOptions);
        });

        // Register the work source implementation as singleton.
        // It uses IServiceScopeFactory to resolve scoped IAzureDevOpsBoardsClient
        // per operation.
        services.AddSingleton<IWorkSource, AzureDevOpsBoardsWorkSource>();

        return services;
    }
}
