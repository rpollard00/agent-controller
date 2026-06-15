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
            .Get<PersistenceOptions>();

        var connectionString = persistenceOptions?.ConnectionString
            ?? "Data Source=agent-controller.db";

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
}
