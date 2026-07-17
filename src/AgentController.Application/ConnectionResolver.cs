using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application;

/// <summary>
/// Provider-keyed resolver that maps a <see cref="ConnectionProfile.Provider"/>
/// string to the registered <see cref="IConnection"/> for that provider.
/// Returns a non-success result with a clear error for unsupported providers.
///
/// Uses the generic <see cref="ProviderKeyedResolver{TService,TResult}"/> to avoid
/// duplicating the scoped resolution pattern.
/// </summary>
internal sealed class ConnectionResolver(
    IServiceScopeFactory scopeFactory,
    IReadOnlyDictionary<string, Type> connectionTypes
) : IConnectionResolver
{
    private readonly ProviderKeyedResolver<IConnection, ConnectionConnectivityResult>
        _connectivityResolver = new(scopeFactory, connectionTypes);

    private readonly ProviderKeyedResolver<IConnection, IReadOnlyList<ConnectionProject>>
        _projectsResolver = new(scopeFactory, connectionTypes);

    private readonly ProviderKeyedResolver<IConnection, IReadOnlyList<HostRepository>>
        _repositoriesResolver = new(scopeFactory, connectionTypes);

    public Task<ConnectionConnectivityResult> VerifyConnectivityAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken
    )
    {
        return _connectivityResolver.ResolveAndExecuteAsync(
            profile.Provider,
            ConnectionConnectivityResult.FailureResult(
                [
                    $"Connection operations are not supported for provider '{profile.Provider}'."
                ]
            ),
            (connection, ct) => connection.VerifyConnectivityAsync(profile, ct),
            cancellationToken
        );
    }

    public Task<IReadOnlyList<ConnectionProject>> ListProjectsAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken
    )
    {
        return _projectsResolver.ResolveAndExecuteAsync(
            profile.Provider,
            Array.Empty<ConnectionProject>(),
            (connection, ct) => connection.ListProjectsAsync(profile, ct),
            cancellationToken
        );
    }

    public Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
        ConnectionProfile profile,
        string project,
        CancellationToken cancellationToken
    )
    {
        return _repositoriesResolver.ResolveAndExecuteAsync(
            profile.Provider,
            Array.Empty<HostRepository>(),
            (connection, ct) => connection.ListRepositoriesAsync(profile, project, ct),
            cancellationToken
        );
    }
}
