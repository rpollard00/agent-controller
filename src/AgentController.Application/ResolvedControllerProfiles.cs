using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Effective profiles selected for one controller execution.</summary>
public sealed record ResolvedControllerProfiles
{
    public RepositoryProfile Repository { get; init; } = new();

    public RuntimeEnvironmentProfile RuntimeEnvironment { get; init; } = new();

    public WorkSourceEnvironmentProfile? AzureDevOpsEnvironment { get; init; }

    public bool RepositoryIsManaged { get; init; }

    public bool RuntimeEnvironmentIsManaged { get; init; }

    public bool AzureDevOpsEnvironmentIsManaged { get; init; }
}

/// <summary>An Azure DevOps profile together with its managed/configured origin.</summary>
public sealed record ResolvedAzureDevOpsEnvironment(
    WorkSourceEnvironmentProfile Profile,
    bool IsManaged
);
