using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates and updates mutable repository host connection profile fields.</summary>
public sealed class UpdateRepositoryHostConnectionCommandHandler(
    IRepositoryHostConnectionStore connectionStore
) : ICommandHandler<UpdateRepositoryHostConnectionCommand, RepositoryHostConnectionOperationResult>
{
    private readonly IRepositoryHostConnectionStore _connectionStore = connectionStore;

    public async Task<RepositoryHostConnectionOperationResult> HandleAsync(
        UpdateRepositoryHostConnectionCommand command,
        CancellationToken cancellationToken
    )
    {
        var routeKey = RepositoryHostConnectionProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!routeKey.IsValid)
        {
            return RepositoryHostConnectionOperationResult.ValidationFailed(routeKey.Errors);
        }

        if (command.Profile is null)
        {
            return RepositoryHostConnectionOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["profile"] = ["A repository host connection profile is required."],
                }
            );
        }

        var validation = RepositoryHostConnectionProfileValidation.ValidateAndNormalize(
            command.Profile
        );
        if (!validation.IsValid)
        {
            return RepositoryHostConnectionOperationResult.ValidationFailed(validation.Errors);
        }

        if (!string.Equals(routeKey.Key, validation.Profile.Key, StringComparison.Ordinal))
        {
            return RepositoryHostConnectionOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["key"] =
                    [
                        "Repository host connection keys are immutable; the request key must match the profile key.",
                    ],
                }
            );
        }

        var existing = await _connectionStore.GetByKeyAsync(routeKey.Key, cancellationToken);
        if (existing is null)
        {
            return RepositoryHostConnectionOperationResult.NotFound(
                $"Repository host connection '{routeKey.Key}' was not found."
            );
        }

        var profile = validation.Profile with
        {
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var updated = await _connectionStore.UpdateAsync(profile, cancellationToken);

        return updated
            ? RepositoryHostConnectionOperationResult.Succeeded(profile)
            : RepositoryHostConnectionOperationResult.NotFound(
                $"Repository host connection '{routeKey.Key}' was not found."
            );
    }
}
