using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates and updates mutable Azure DevOps environment profile fields.</summary>
public sealed class UpdateAzureDevOpsEnvironmentCommandHandler(
    IWorkSourceEnvironmentStore environmentStore
) : ICommandHandler<UpdateAzureDevOpsEnvironmentCommand, AzureDevOpsEnvironmentOperationResult>
{
    private readonly IWorkSourceEnvironmentStore _environmentStore = environmentStore;

    public async Task<AzureDevOpsEnvironmentOperationResult> HandleAsync(
        UpdateAzureDevOpsEnvironmentCommand command,
        CancellationToken cancellationToken
    )
    {
        var routeKey = AzureDevOpsEnvironmentProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!routeKey.IsValid)
        {
            return AzureDevOpsEnvironmentOperationResult.ValidationFailed(routeKey.Errors);
        }

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

        if (!string.Equals(routeKey.Key, validation.Profile.Key, StringComparison.Ordinal))
        {
            return AzureDevOpsEnvironmentOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["key"] =
                    [
                        "Azure DevOps environment keys are immutable; the request key must match the profile key.",
                    ],
                }
            );
        }

        var existing = await _environmentStore.GetByKeyAsync(routeKey.Key, cancellationToken);
        if (existing is null)
        {
            return AzureDevOpsEnvironmentOperationResult.NotFound(
                $"Azure DevOps environment '{routeKey.Key}' was not found."
            );
        }

        var profile = validation.Profile with
        {
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var updated = await _environmentStore.UpdateAsync(profile, cancellationToken);

        return updated
            ? AzureDevOpsEnvironmentOperationResult.Succeeded(profile)
            : AzureDevOpsEnvironmentOperationResult.NotFound(
                $"Azure DevOps environment '{routeKey.Key}' was not found."
            );
    }
}
