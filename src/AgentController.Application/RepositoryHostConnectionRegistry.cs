using AgentController.Application.Abstractions;

namespace AgentController.Application;

/// <summary>
/// Static registry that accumulates provider-keyed host type mappings during
/// service registration, before the DI container is built.
/// </summary>
internal static class RepositoryHostConnectionRegistry
{
    private static readonly Dictionary<string, Type> _mappings =
        new(StringComparer.Ordinal);

    /// <summary>Register a host type for one or more provider keys.</summary>
    internal static void Register(Type hostType, params string[] providerKeys)
    {
        foreach (var key in providerKeys)
        {
            _mappings[key] = hostType;
        }
    }

    /// <summary>Resolve the accumulated mappings (called once at container build time).</summary>
    internal static IReadOnlyDictionary<string, Type> Build() =>
        new Dictionary<string, Type>(_mappings, StringComparer.Ordinal);
}
