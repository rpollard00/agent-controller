using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates and updates mutable work source environment profile fields.</summary>
public sealed class UpdateWorkSourceEnvironmentCommandHandler(
    IWorkSourceEnvironmentStore environmentStore
) : ICommandHandler<UpdateWorkSourceEnvironmentCommand, WorkSourceEnvironmentOperationResult>
{
    private readonly IWorkSourceEnvironmentStore _environmentStore = environmentStore;

    public async Task<WorkSourceEnvironmentOperationResult> HandleAsync(
        UpdateWorkSourceEnvironmentCommand command,
        CancellationToken cancellationToken
    )
    {
        var routeKey = AzureDevOpsEnvironmentProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!routeKey.IsValid)
        {
            return WorkSourceEnvironmentOperationResult.ValidationFailed(routeKey.Errors);
        }

        if (command.Profile is null)
        {
            return WorkSourceEnvironmentOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["profile"] = ["A work source environment profile is required."],
                }
            );
        }

        var validation = AzureDevOpsEnvironmentProfileValidation.ValidateAndNormalize(
            command.Profile
        );
        if (!validation.IsValid)
        {
            return WorkSourceEnvironmentOperationResult.ValidationFailed(validation.Errors);
        }

        if (!string.Equals(routeKey.Key, validation.Profile.Key, StringComparison.Ordinal))
        {
            return WorkSourceEnvironmentOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["key"] =
                    [
                        "Work source environment keys are immutable; the request key must match the profile key.",
                    ],
                }
            );
        }

        var existing = await _environmentStore.GetByKeyAsync(routeKey.Key, cancellationToken);
        if (existing is null)
        {
            return WorkSourceEnvironmentOperationResult.NotFound(
                $"Work source environment '{routeKey.Key}' was not found."
            );
        }

        var profile = validation.Profile with
        {
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var updated = await _environmentStore.UpdateAsync(profile, cancellationToken);

        return updated
            ? WorkSourceEnvironmentOperationResult.Succeeded(profile)
            : WorkSourceEnvironmentOperationResult.NotFound(
                $"Work source environment '{routeKey.Key}' was not found."
            );
    }
}
