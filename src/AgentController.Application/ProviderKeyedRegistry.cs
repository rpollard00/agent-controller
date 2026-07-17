namespace AgentController.Application;

/// <summary>
/// Generic static registry that accumulates provider-keyed type mappings during
/// service registration, before the DI container is built.
///
/// Used by the unified connection resolver to map provider discriminator strings
/// to implementation types without duplicating the registry pattern.
/// </summary>
/// <typeparam name="TService">
/// The service interface type being registered (e.g. <see cref="Abstractions.IConnection"/>).
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
