using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates, normalizes, and creates a managed Azure DevOps environment.</summary>
public sealed class CreateAzureDevOpsEnvironmentCommandHandler(
    IWorkSourceEnvironmentStore environmentStore
) : ICommandHandler<CreateAzureDevOpsEnvironmentCommand, AzureDevOpsEnvironmentOperationResult>
{
    private readonly IWorkSourceEnvironmentStore _environmentStore = environmentStore;

    public async Task<AzureDevOpsEnvironmentOperationResult> HandleAsync(
        CreateAzureDevOpsEnvironmentCommand command,
        CancellationToken cancellationToken
    )
    {
        if (command.Profile is null)
        {
            return AzureDevOpsEnvironmentOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["profile"] = ["An Azure DevOps environment profile is required."],
                }
            );
        }

        var validation = AzureDevOpsEnvironmentProfileValidation.ValidateAndNormalize(
            command.Profile
        );
        if (!validation.IsValid)
        {
            return AzureDevOpsEnvironmentOperationResult.ValidationFailed(validation.Errors);
        }

        var now = DateTimeOffset.UtcNow;
        var profile = validation.Profile with { CreatedAt = now, UpdatedAt = now };
        var created = await _environmentStore.CreateAsync(profile, cancellationToken);

        return created
            ? AzureDevOpsEnvironmentOperationResult.Succeeded(profile)
            : AzureDevOpsEnvironmentOperationResult.Conflict(
                $"Azure DevOps environment '{profile.Key}' already exists."
            );
    }
}
