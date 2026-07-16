using AgentController.Application.Abstractions;

namespace AgentController.Application;

/// <summary>
/// Provider-keyed registry for repository host connections.
/// Delegates to the generic <see cref="ProviderKeyedRegistry{TService}"/> to avoid
/// duplicating the registry pattern.
/// </summary>
internal static class RepositoryHostConnectionRegistry
{
    /// <summary>Register a host type for one or more provider keys.</summary>
    internal static void Register(Type hostType, params string[] providerKeys) =>
        ProviderKeyedRegistry<IRepositoryHostConnection>.Register(hostType, providerKeys);

    /// <summary>Resolve the accumulated mappings (called once at container build time).</summary>
    internal static IReadOnlyDictionary<string, Type> Build() =>
        ProviderKeyedRegistry<IRepositoryHostConnection>.Build();
}
