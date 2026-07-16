using AgentController.Application;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure.Secrets;

/// <summary>
/// Resolves <see cref="IManagedSecretStore"/> calls to the correct underlying store
/// based on <see cref="SecretReference.Kind"/>.
///
/// Dispatches to registered store implementations:
/// <list type="bullet">
///   <item><description><c>"EnvVar"</c> → <see cref="EnvVarSecretStore"/></description></item>
///   <item><description><c>"Db"</c> → <see cref="DbSecretStore"/></description></item>
/// </list>
///
/// Stores are resolved lazily from the service provider to support scoped services
/// (e.g., <see cref="DbSecretStore"/> which depends on EF Core DbContext).
/// For scoped stores, a new scope is created per operation to ensure the DbContext
/// is available.
///
/// Unknown kinds return <c>null</c> for resolve and failure for write.
/// </summary>
internal sealed class SecretStoreResolver : IManagedSecretStore
{
    private readonly IServiceProvider _serviceProvider;

    public SecretStoreResolver(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public Task<string?> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        return reference.Kind switch
        {
            "EnvVar" => _serviceProvider
                .GetRequiredService<EnvVarSecretStore>()
                .ResolveAsync(reference, cancellationToken),
            "Db" => ResolveScopedAsync(store =>
                store.ResolveAsync(reference, cancellationToken)),
            _ => Task.FromResult<string?>(null),
        };
    }

    /// <inheritdoc />
    public Task<SecretWriteResult> WriteAsync(
        SecretReference reference,
        string value,
        CancellationToken cancellationToken
    )
    {
        return reference.Kind switch
        {
            "EnvVar" => _serviceProvider
                .GetRequiredService<EnvVarSecretStore>()
                .WriteAsync(reference, value, cancellationToken),
            "Db" => ResolveScopedAsync(store =>
                store.WriteAsync(reference, value, cancellationToken)),
            _ => Task.FromResult(
                SecretWriteResult.FailureResult(
                    $"No secret store registered for Kind '{reference.Kind}'."
                )),
        };
    }

    /// <summary>
    /// Executes an operation within a scoped service context.
    /// Creates a scope, resolves the scoped service, executes the operation,
    /// and disposes the scope.
    /// </summary>
    private Task<T> ResolveScopedAsync<T>(
        Func<DbSecretStore, Task<T>> operation
    )
    {
        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DbSecretStore>();
        return operation(store);
    }
}
