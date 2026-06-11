using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Deterministic no-op implementation of <see cref="IEnvironmentProvider"/>.
/// Returns empty handles and successful (empty) command results.
/// Suitable for DI seeding before real providers are wired.
/// </summary>
public sealed class NoOpEnvironmentProvider : IEnvironmentProvider
{
    public Task<EnvironmentHandle> CreateAsync(
        EnvironmentSpec spec,
        CancellationToken cancellationToken
    )
    {
        var handle = new EnvironmentHandle
        {
            Id = $"noop-{spec.RunId}",
            ProviderType = "NoOp",
            RootPath = string.Empty,
            Status = "created",
        };

        return Task.FromResult(handle);
    }

    public Task<CommandResult> ExecuteAsync(
        EnvironmentHandle handle,
        CommandSpec command,
        CancellationToken cancellationToken
    )
    {
        var result = new CommandResult
        {
            ExitCode = 0,
            StdOut = string.Empty,
            StdErr = string.Empty,
            Duration = TimeSpan.Zero,
            TimedOut = false,
        };

        return Task.FromResult(result);
    }

    public Task DestroyAsync(EnvironmentHandle handle, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
