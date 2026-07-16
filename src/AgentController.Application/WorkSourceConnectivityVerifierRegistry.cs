using AgentController.Application.Abstractions;

namespace AgentController.Application;

/// <summary>
/// Provider-keyed registry for work-source connectivity verifiers.
/// Delegates to the generic <see cref="ProviderKeyedRegistry{TService}"/> to avoid
/// duplicating the registry pattern.
/// </summary>
internal static class WorkSourceConnectivityVerifierRegistry
{
    /// <summary>Register a verifier type for one or more provider keys.</summary>
    internal static void Register(Type verifierType, params string[] providerKeys) =>
        ProviderKeyedRegistry<IWorkSourceConnectivityVerifier>.Register(
            verifierType,
            providerKeys
        );

    /// <summary>Resolve the accumulated mappings (called once at container build time).</summary>
    internal static IReadOnlyDictionary<string, Type> Build() =>
        ProviderKeyedRegistry<IWorkSourceConnectivityVerifier>.Build();
}
