using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Selects the execution providers named by an effective runtime-environment profile.</summary>
public interface IExecutionProviderResolver
{
    /// <summary>Resolves the environment provider selected for an execution.</summary>
    IEnvironmentProvider ResolveEnvironmentProvider(RuntimeEnvironmentProfile profile);

    /// <summary>Resolves the agent runtime selected for an execution.</summary>
    IAgentRuntime ResolveAgentRuntime(RuntimeEnvironmentProfile profile);
}
