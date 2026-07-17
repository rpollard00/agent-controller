using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Infrastructure;

/// <summary>
/// Legacy verifier superseded by the unified IConnection path (item 7).
/// Retained for backward compatibility; throws when invoked.
/// </summary>
internal sealed class AzureDevOpsConnectivityVerifier : IWorkSourceConnectivityVerifier
{
    /// <inheritdoc />
    public Task<WorkSourceConnectivityResult> VerifyAsync(
        WorkSourceEnvironmentProfile profile,
        CancellationToken cancellationToken
    )
    {
        throw new NotSupportedException(
            "AzureDevOpsConnectivityVerifier is superseded by the unified IConnection.VerifyConnectivityAsync path. " +
            "See item 7 (refactor(boards): rewire AzureDevOpsBoardsWorkSource and client factory to the connection)."
        );
    }
}
