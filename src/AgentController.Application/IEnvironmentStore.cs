using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Persistence contract for environment metadata records.
/// Used by the worker polling loop during environment provisioning.
/// Implementations are storage-agnostic; API and worker code must not
/// reference EF Core or any specific persistence technology directly.
/// </summary>
public interface IEnvironmentStore
{
    /// <summary>
    /// Create a new environment record and return its handle with the
    /// store-assigned identifier.
    /// </summary>
    Task<EnvironmentHandle> CreateAsync(
        CreateEnvironmentRequest request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Update the status string of an environment record.
    /// Implementations should update internal timestamps automatically.
    /// </summary>
    Task UpdateStatusAsync(
        string environmentId,
        string status,
        CancellationToken cancellationToken
    );
}
