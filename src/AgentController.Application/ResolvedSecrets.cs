using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// A single resolved secret: the reference used to look it up and its resolved value.
/// The value is resolved at materialization time and must not cross process boundaries
/// as plain text beyond the scope of the current execution context.
/// </summary>
public sealed record ResolvedSecret(
    /// <summary>
    /// The original secret reference that was resolved.
    /// </summary>
    SecretReference Reference,

    /// <summary>
    /// The resolved secret value. May be <c>null</c> if the reference
    /// could not be resolved (e.g. missing environment variable).
    /// </summary>
    string? Value
);

/// <summary>
/// A manifest of resolved secrets for a single connection or repository operation.
/// Groups all credential material needed for one clone/connect/materialize action
/// so that the raw values are carried together and consumed atomically.
/// </summary>
public sealed record ResolvedSecretsManifest(
    /// <summary>
    /// The scope this manifest was resolved for (e.g. a repository host connection key
    /// or a repository profile key).
    /// </summary>
    string Scope,

    /// <summary>
    /// The resolved secrets for this scope.
    /// </summary>
    IReadOnlyList<ResolvedSecret> Secrets
);
