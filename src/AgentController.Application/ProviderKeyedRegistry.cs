namespace AgentController.Application;

/// <summary>
/// Generic static registry that accumulates provider-keyed type mappings during
/// service registration, before the DI container is built.
///
/// Used by both work-source connectivity verifiers and repository host connections
/// to avoid duplicating the registry pattern.
/// </summary>
/// <typeparam name="TService">
/// The service interface type being registered (e.g. <see cref="Abstractions.IWorkSourceConnectivityVerifier"/>
/// or <see cref="Abstractions.IRepositoryHostConnection"/>).
/// </typeparam>
internal static class ProviderKeyedRegistry<TService>
{
    private static readonly Dictionary<string, Type> _mappings =
        new(StringComparer.Ordinal);

    /// <summary>Register a service type for one or more provider keys.</summary>
    internal static void Register(Type implementationType, params string[] providerKeys)
    {
        foreach (var key in providerKeys)
        {
            _mappings[key] = implementationType;
        }
    }

    /// <summary>Resolve the accumulated mappings (called once at container build time).</summary>
    internal static IReadOnlyDictionary<string, Type> Build() =>
        new Dictionary<string, Type>(_mappings, StringComparer.Ordinal);
}
