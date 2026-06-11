using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Port for provisioning, executing commands within, and destroying
/// execution environments.
/// Prototype implementation: LocalWorkspaceEnvironmentProvider.
/// First MVP isolation implementation: DockerEnvironmentProvider.
/// </summary>
public interface IEnvironmentProvider
{
    /// <summary>
    /// Create a new execution environment according to the specification.
    /// Returns a handle that can be used for subsequent operations.
    /// </summary>
    Task<EnvironmentHandle> CreateAsync(EnvironmentSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// Execute a command within the specified environment.
    /// </summary>
    Task<CommandResult> ExecuteAsync(
        EnvironmentHandle handle,
        CommandSpec command,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Destroy an execution environment and release its resources.
    /// </summary>
    Task DestroyAsync(EnvironmentHandle handle, CancellationToken cancellationToken);
}
