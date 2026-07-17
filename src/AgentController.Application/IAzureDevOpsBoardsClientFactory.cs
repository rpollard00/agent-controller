namespace AgentController.Application;

/// <summary>
/// Creates an Azure DevOps client from a resolved work-source environment.
/// Organization URL and PAT are derived from the referenced <see cref="ConnectionProfile"/>;
/// project is taken from the consumer <see cref="Domain.WorkSourceEnvironmentProfile"/>.
/// </summary>
public interface IAzureDevOpsBoardsClientFactory
{
    /// <summary>
    /// Creates a client using the resolved environment's connection settings and consumer project.
    /// </summary>
    /// <param name="resolved">
    /// Resolved work-source environment carrying both the consumer profile and its resolved
    /// <see cref="Domain.ConnectionProfile"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for PAT resolution.</param>
    Task<IAzureDevOpsBoardsClient> CreateAsync(
        ResolvedWorkSourceEnvironment resolved,
        CancellationToken cancellationToken);
}
