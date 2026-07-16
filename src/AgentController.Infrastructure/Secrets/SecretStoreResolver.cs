using AgentController.Application;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Infrastructure.Secrets;

/// <summary>
/// Resolves <see cref="ISecretStore"/> calls to the correct underlying store
/// based on <see cref="SecretReference.Kind"/>.
///
/// Dispatches to registered store implementations:
/// <list type="bullet">
///   <item><description><c>"EnvVar"</c> → <see cref="EnvVarSecretStore"/></description></item>
///   <item><description><c>"Db"</c> → <see cref="DbSecretStore"/></description></item>
/// </list>
///
/// Unknown kinds return <c>null</c> for resolve and failure for write.
/// </summary>
internal sealed class SecretStoreResolver : ISecretStore
{
    private readonly IDictionary<string, ISecretStore> _stores;

    public SecretStoreResolver(IDictionary<string, ISecretStore> stores)
    {
        _stores = stores;
    }

    /// <inheritdoc />
    public Task<string?> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        if (_stores.TryGetValue(reference.Kind, out var store))
        {
            return store.ResolveAsync(reference, cancellationToken);
        }

        // Unknown kind — cannot resolve.
        return Task.FromResult<string?>(null);
    }

    /// <inheritdoc />
    public Task<SecretWriteResult> WriteAsync(
        SecretReference reference,
        string value,
        CancellationToken cancellationToken
    )
    {
        if (_stores.TryGetValue(reference.Kind, out var store))
        {
            return store.WriteAsync(reference, value, cancellationToken);
        }

        // Unknown kind — cannot write.
        return Task.FromResult(
            SecretWriteResult.FailureResult(
                $"No secret store registered for Kind '{reference.Kind}'."
            )
        );
    }
}
