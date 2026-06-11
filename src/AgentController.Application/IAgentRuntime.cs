using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Port for starting, monitoring, and cancelling agent runs.
/// First implementation: PiMateriaRuntime.
/// The runtime owns branch creation, commits, pushes, and PR creation.
/// The controller records whatever the runtime reports.
/// </summary>
public interface IAgentRuntime
{
    /// <summary>
    /// Start an agent run with the provided specification.
    /// Returns a handle for tracking and controlling the run.
    /// </summary>
    Task<AgentRunHandle> StartAsync(AgentRunSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// Get the current status of a running agent.
    /// </summary>
    Task<AgentRuntimeStatus> GetStatusAsync(
        AgentRunHandle handle,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Request cancellation of a running agent.
    /// </summary>
    Task CancelAsync(AgentRunHandle handle, CancellationToken cancellationToken);
}
