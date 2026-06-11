using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Deterministic no-op implementation of <see cref="IAgentRuntime"/>.
/// Returns queued handles and idle status. Suitable for DI seeding
/// before real providers are wired.
/// </summary>
public sealed class NoOpAgentRuntime : IAgentRuntime
{
    public Task<AgentRunHandle> StartAsync(AgentRunSpec spec, CancellationToken cancellationToken)
    {
        var handle = new AgentRunHandle
        {
            RunId = spec.RunId,
            RuntimeRunId = null,
            Status = RunLifecycleState.Queued,
            StartedAt = DateTimeOffset.UnixEpoch,
        };

        return Task.FromResult(handle);
    }

    public Task<AgentRuntimeStatus> GetStatusAsync(
        AgentRunHandle handle,
        CancellationToken cancellationToken
    )
    {
        var status = new AgentRuntimeStatus
        {
            Status = handle.Status,
            RuntimeRunId = handle.RuntimeRunId,
            StartedAt = handle.StartedAt,
            LastHeartbeatAt = null,
            Events = null,
            Error = null,
        };

        return Task.FromResult(status);
    }

    public Task CancelAsync(AgentRunHandle handle, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
