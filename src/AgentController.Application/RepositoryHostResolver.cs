using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application;

/// <summary>
/// Provider-keyed resolver that maps a <see cref="RepositoryHostConnectionProfile.Provider"/>
/// string to the registered <see cref="IRepositoryHostConnection"/> for that provider.
/// Returns a non-success result with a clear error for unsupported providers.
///
/// Uses the generic <see cref="ProviderKeyedResolver{TService,TResult}"/> to avoid
/// duplicating the scoped resolution pattern.
/// </summary>
internal sealed class RepositoryHostResolver(
    IServiceScopeFactory scopeFactory,
    IReadOnlyDictionary<string, Type> hostTypes
) : IRepositoryHostResolver
{
    private readonly ProviderKeyedResolver<IRepositoryHostConnection, RepositoryHostConnectivityResult>
        _connectivityResolver = new(scopeFactory, hostTypes);

    private readonly ProviderKeyedResolver<IRepositoryHostConnection, IReadOnlyList<HostRepository>>
        _listResolver = new(scopeFactory, hostTypes);

    public Task<RepositoryHostConnectivityResult> VerifyConnectivityAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken
    )
    {
        return _connectivityResolver.ResolveAndExecuteAsync(
            profile.Provider,
            RepositoryHostConnectivityResult.FailureResult(
                [
                    $"Repository host operations are not supported for provider '{profile.Provider}'."
                ]
            ),
            (host, ct) => host.VerifyConnectivityAsync(profile, ct),
            cancellationToken
        );
    }

    public Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken
    )
    {
        return _listResolver.ResolveAndExecuteAsync(
            profile.Provider,
            Array.Empty<HostRepository>(),
            (host, ct) => host.ListRepositoriesAsync(profile, ct),
            cancellationToken
        );
    }
}
