using AgentController.Application;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Legacy factory superseded by the unified IConnection path (item 7).
/// Retained for backward compatibility; throws when invoked.
/// </summary>
internal sealed class AzureDevOpsBoardsClientFactory : IAzureDevOpsBoardsClientFactory
{
    /// <inheritdoc />
    public IAzureDevOpsBoardsClient Create(WorkSourceEnvironmentProfile profile)
    {
        throw new NotSupportedException(
            "AzureDevOpsBoardsClientFactory is superseded by the unified IConnection path. " +
            "Use IAzureDevOpsBoardsClientFactory.Create with a resolved ConnectionProfile instead. " +
            "See item 7 (refactor(boards): rewire AzureDevOpsBoardsWorkSource and client factory to the connection)."
        );
    }
}
