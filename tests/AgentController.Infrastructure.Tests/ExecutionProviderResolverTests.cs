using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure.Tests;

public sealed class ExecutionProviderResolverTests
{
    [Fact]
    public void Resolve_UsesProvidersNamedByManagedProfileInsteadOfConfiguredDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["agentController:workerId"] = "provider-test",
                    ["agentController:runRoot"] = "/tmp/provider-test",
                    ["environmentProvider:provider"] = "NoOp",
                    ["runtime:provider"] = "NoOp",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddSilentLogging();
        services.AddAgentControllerOptions(configuration);
        services.AddAgentControllerNoOpProviders();

        using var serviceProvider = services.BuildServiceProvider();
        var resolver = serviceProvider.GetRequiredService<IExecutionProviderResolver>();
        var profile = new RuntimeEnvironmentProfile
        {
            EnvironmentProvider = "LocalWorkspace",
            RuntimeProvider = "MockPiMateria",
        };

        Assert.IsType<LocalWorkspaceEnvironmentProvider>(
            resolver.ResolveEnvironmentProvider(profile)
        );
        Assert.IsType<MockPiMateriaRuntime>(resolver.ResolveAgentRuntime(profile));
        Assert.IsType<NoOpEnvironmentProvider>(
            serviceProvider.GetRequiredService<IEnvironmentProvider>()
        );
        Assert.IsType<NoOpAgentRuntime>(serviceProvider.GetRequiredService<IAgentRuntime>());
    }

    [Fact]
    public void Resolve_UsesNoOpProvidersForLegacyEmptySelection()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSilentLogging();
        services.AddAgentControllerOptions(configuration);
        services.AddAgentControllerNoOpProviders();

        using var serviceProvider = services.BuildServiceProvider();
        var resolver = serviceProvider.GetRequiredService<IExecutionProviderResolver>();
        var profile = new RuntimeEnvironmentProfile();

        Assert.IsType<NoOpEnvironmentProvider>(resolver.ResolveEnvironmentProvider(profile));
        Assert.IsType<NoOpAgentRuntime>(resolver.ResolveAgentRuntime(profile));
    }
}
