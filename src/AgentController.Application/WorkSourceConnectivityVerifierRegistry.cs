namespace AgentController.Application;

/// <summary>
/// Static registry that accumulates provider-keyed verifier type mappings during
/// service registration, before the DI container is built.
/// </summary>
internal static class WorkSourceConnectivityVerifierRegistry
{
    private static readonly Dictionary<string, Type> _mappings =
        new(StringComparer.Ordinal);

    /// <summary>Register a verifier type for one or more provider keys.</summary>
    internal static void Register(Type verifierType, params string[] providerKeys)
    {
        foreach (var key in providerKeys)
        {
            _mappings[key] = verifierType;
        }
    }

    /// <summary>Resolve the accumulated mappings (called once at container build time).</summary>
    internal static IReadOnlyDictionary<string, Type> Build() =>
        new Dictionary<string, Type>(_mappings, StringComparer.Ordinal);
}
