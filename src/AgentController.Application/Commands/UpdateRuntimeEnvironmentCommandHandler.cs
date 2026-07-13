using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates and updates mutable runtime environment profile fields.</summary>
public sealed class UpdateRuntimeEnvironmentCommandHandler(
    IRuntimeEnvironmentStore environmentStore
) : ICommandHandler<UpdateRuntimeEnvironmentCommand, RuntimeEnvironmentOperationResult>
{
    private readonly IRuntimeEnvironmentStore _environmentStore = environmentStore;

    public async Task<RuntimeEnvironmentOperationResult> HandleAsync(
        UpdateRuntimeEnvironmentCommand command,
        CancellationToken cancellationToken
    )
    {
        var routeKey = RuntimeEnvironmentProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!routeKey.IsValid)
        {
            return RuntimeEnvironmentOperationResult.ValidationFailed(routeKey.Errors);
        }

        if (command.Profile is null)
        {
            return RuntimeEnvironmentOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["profile"] = ["A runtime environment profile is required."],
                }
            );
        }

        var validation = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(command.Profile);
        if (!validation.IsValid)
        {
            return RuntimeEnvironmentOperationResult.ValidationFailed(validation.Errors);
        }

        if (!string.Equals(routeKey.Key, validation.Profile.Key, StringComparison.Ordinal))
        {
            return RuntimeEnvironmentOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["key"] =
                    [
                        "Runtime environment keys are immutable; the request key must match the profile key.",
                    ],
                }
            );
        }

        var existing = await _environmentStore.GetByKeyAsync(routeKey.Key, cancellationToken);
        if (existing is null)
        {
            return RuntimeEnvironmentOperationResult.NotFound(
                $"Runtime environment '{routeKey.Key}' was not found."
            );
        }

        var profile = validation.Profile with
        {
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var updated = await _environmentStore.UpdateAsync(profile, cancellationToken);

        return updated
            ? RuntimeEnvironmentOperationResult.Succeeded(profile)
            : RuntimeEnvironmentOperationResult.NotFound(
                $"Runtime environment '{routeKey.Key}' was not found."
            );
    }
}
