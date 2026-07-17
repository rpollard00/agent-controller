namespace AgentController.Application.Queries;

/// <summary>
/// Lists repositories available from a unified connection identified by <paramref name="ConnectionKey"/>
/// within the specified <paramref name="Project"/>.
/// </summary>
public sealed record ListHostRepositoriesQuery(
    /// <summary>Key of the unified connection.</summary>
    string ConnectionKey,

    /// <summary>Provider-specific project name to scope the enumeration.</summary>
    string Project
);
