using AgentController.Application.Abstractions;

namespace AgentController.Application;

/// <summary>
/// Provider-keyed registry for unified connections.
/// Delegates to the generic <see cref="ProviderKeyedRegistry{TService}"/> to avoid
/// duplicating the registry pattern.
/// </summary>
internal static class ConnectionRegistry
{
    /// <summary>Register a connection type for one or more provider keys.</summary>
    internal static void Register(Type connectionType, params string[] providerKeys) =>
        ProviderKeyedRegistry<IConnection>.Register(connectionType, providerKeys);

    /// <summary>Resolve the accumulated mappings (called once at container build time).</summary>
    internal static IReadOnlyDictionary<string, Type> Build() =>
        ProviderKeyedRegistry<IConnection>.Build();
}
