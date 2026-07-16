using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application;

/// <summary>
/// Provider-keyed resolver that maps a <see cref="RepositoryHostConnectionProfile.Provider"/>
/// string to the registered <see cref="IRepositoryHostConnection"/> for that provider.
/// Returns a non-success result with a clear error for unsupported providers.
/// </summary>
internal sealed class RepositoryHostResolver(
    IServiceScopeFactory scopeFactory,
    IReadOnlyDictionary<string, Type> hostTypes
) : IRepositoryHostResolver
{
    /// <inheritdoc />
    public async Task<RepositoryHostConnectivityResult> VerifyConnectivityAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken
    )
    {
        if (!hostTypes.TryGetValue(profile.Provider, out var hostType))
        {
            return RepositoryHostConnectivityResult.FailureResult(
                [
                    $"Repository host operations are not supported for provider '{profile.Provider}'."
                ]
            );
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var host = (IRepositoryHostConnection)scope.ServiceProvider
            .GetRequiredService(hostType);

        return await host.VerifyConnectivityAsync(profile, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HostRepository>> ListRepositoriesAsync(
        RepositoryHostConnectionProfile profile,
        CancellationToken cancellationToken
    )
    {
        if (!hostTypes.TryGetValue(profile.Provider, out var hostType))
        {
            // Return empty list for unsupported providers rather than throwing.
            // The caller can distinguish this from a genuine empty result by checking
            // whether the provider is known via the resolver's VerifyConnectivityAsync.
            return Array.Empty<HostRepository>();
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var host = (IRepositoryHostConnection)scope.ServiceProvider
            .GetRequiredService(hostType);

        return await host.ListRepositoriesAsync(profile, cancellationToken);
    }
}
