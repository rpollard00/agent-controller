using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>Maps profile provider identifiers to registered infrastructure implementations.</summary>
internal sealed class ExecutionProviderResolver(
    NoOpEnvironmentProvider noOpEnvironmentProvider,
    LocalWorkspaceEnvironmentProvider localWorkspaceEnvironmentProvider,
    NoOpAgentRuntime noOpAgentRuntime,
    PiMateriaRuntime piMateriaRuntime,
    MockPiMateriaRuntime mockPiMateriaRuntime
) : IExecutionProviderResolver
{
    public IEnvironmentProvider ResolveEnvironmentProvider(RuntimeEnvironmentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.EnvironmentProvider.Trim() switch
        {
            var provider
                when provider.Equals("LocalWorkspace", StringComparison.OrdinalIgnoreCase) =>
                localWorkspaceEnvironmentProvider,
            "" => noOpEnvironmentProvider,
            var provider when provider.Equals("NoOp", StringComparison.OrdinalIgnoreCase) =>
                noOpEnvironmentProvider,
            var provider => throw new InvalidOperationException(
                $"Environment provider '{provider}' is not registered."
            ),
        };
    }

    public IAgentRuntime ResolveAgentRuntime(RuntimeEnvironmentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.RuntimeProvider.Trim() switch
        {
            var provider when provider.Equals("PiMateria", StringComparison.OrdinalIgnoreCase) =>
                piMateriaRuntime,
            var provider
                when provider.Equals("MockPiMateria", StringComparison.OrdinalIgnoreCase) =>
                mockPiMateriaRuntime,
            "" => noOpAgentRuntime,
            var provider when provider.Equals("NoOp", StringComparison.OrdinalIgnoreCase) =>
                noOpAgentRuntime,
            var provider => throw new InvalidOperationException(
                $"Agent runtime provider '{provider}' is not registered."
            ),
        };
    }
}
