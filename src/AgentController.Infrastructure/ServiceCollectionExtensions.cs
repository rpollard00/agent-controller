using AgentController.Application;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
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
