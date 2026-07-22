namespace AgentController.Application.Queries;

/// <summary>
/// Lists branches available from a unified connection identified by <paramref name="ConnectionKey"/>
/// for the specified <paramref name="Project"/> and <paramref name="RepositoryId"/>.
/// Returns bare branch names with provider-specific prefixes stripped.
/// </summary>
public sealed record ListHostRepositoryBranchesQuery(
    /// <summary>Key of the unified connection.</summary>
    string ConnectionKey,

    /// <summary>Provider-specific project name to scope the enumeration.</summary>
    string Project,

    /// <summary>Provider-specific repository identifier.</summary>
    string RepositoryId
);
