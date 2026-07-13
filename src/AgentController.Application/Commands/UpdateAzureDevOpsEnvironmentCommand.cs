using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>
/// Updates a managed Azure DevOps environment. <see cref="Key"/> identifies the existing profile
/// and must match the immutable key in <see cref="Profile"/>.
/// </summary>
public sealed record UpdateAzureDevOpsEnvironmentCommand(
    string Key,
    AzureDevOpsEnvironmentProfile Profile
);
