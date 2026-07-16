using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Application;

/// <summary>
/// Generic provider-keyed resolver that maps a provider discriminator string
/// to a registered service implementation via scoped DI resolution.
///
/// Callers supply:
/// <list type="bullet">
///   <item>The provider key to look up</item>
///   <item>A default result for unsupported providers</item>
///   <item>An async action to execute against the resolved service</item>
/// </list>
///
/// Used by both work-source connectivity verifiers and repository host connections
/// to avoid duplicating the scoped resolution pattern.
/// </summary>
/// <typeparam name="TService">The service interface type being resolved.</typeparam>
/// <typeparam name="TResult">The result type returned to callers.</typeparam>
internal sealed class ProviderKeyedResolver<TService, TResult>(
    IServiceScopeFactory scopeFactory,
    IReadOnlyDictionary<string, Type> serviceTypes
)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IReadOnlyDictionary<string, Type> _serviceTypes = serviceTypes;

    /// <summary>
    /// Resolve the service by provider key and execute the operation.
    /// Returns <paramref name="defaultResult"/> when the provider is unsupported.
    /// </summary>
    /// <param name="providerKey">The provider discriminator string.</param>
    /// <param name="defaultResult">Result returned when no service is registered for the provider.</param>
    /// <param name="execute">Action to invoke against the resolved service instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<TResult> ResolveAndExecuteAsync(
        string providerKey,
        TResult defaultResult,
        Func<TService, CancellationToken, Task<TResult>> execute,
        CancellationToken cancellationToken
    )
    {
        if (!_serviceTypes.TryGetValue(providerKey, out var serviceType))
        {
            return defaultResult;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = (TService)scope.ServiceProvider.GetRequiredService(serviceType);

        return await execute(service, cancellationToken);
    }
}
