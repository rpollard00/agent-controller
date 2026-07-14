using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Effective profiles selected for one controller execution.</summary>
public sealed record ResolvedControllerProfiles
{
    public RepositoryProfile Repository { get; init; } = new();

    public RuntimeEnvironmentProfile RuntimeEnvironment { get; init; } = new();

    public WorkSourceEnvironmentProfile? WorkSourceEnvironment { get; init; }

    public bool RepositoryIsManaged { get; init; }

    public bool RuntimeEnvironmentIsManaged { get; init; }

    public bool WorkSourceEnvironmentIsManaged { get; init; }
}

/// <summary>A work source environment profile together with its managed/configured origin.</summary>
public sealed record ResolvedWorkSourceEnvironment(
    WorkSourceEnvironmentProfile Profile,
    bool IsManaged
);
