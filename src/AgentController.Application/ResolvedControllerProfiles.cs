using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Effective profiles selected for one controller execution.</summary>
public sealed record ResolvedControllerProfiles
{
    public RepositoryProfile Repository { get; init; } = new();

    public RuntimeEnvironmentProfile RuntimeEnvironment { get; init; } = new();

    public WorkSourceEnvironmentProfile? WorkSourceEnvironment { get; init; }

    /// <summary>
    /// The resolved <see cref="ConnectionProfile"/> for the work source environment,
    /// when the work source references a managed connection.
    /// </summary>
    public ConnectionProfile? WorkSourceConnection { get; init; }

    /// <summary>
    /// The resolved <see cref="ConnectionProfile"/> for the repository,
    /// when the repository references a managed connection.
    /// </summary>
    public ConnectionProfile? RepositoryConnection { get; init; }

    public bool RepositoryIsManaged { get; init; }

    public bool RuntimeEnvironmentIsManaged { get; init; }

    public bool WorkSourceEnvironmentIsManaged { get; init; }
}

/// <summary>A work source environment profile together with its managed/configured origin.</summary>
public sealed record ResolvedWorkSourceEnvironment(
    WorkSourceEnvironmentProfile Profile,
    ConnectionProfile? Connection,
    bool IsManaged
);


