using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates, normalizes, and creates a managed repository host connection.</summary>
public sealed class CreateRepositoryHostConnectionCommandHandler(
    IRepositoryHostConnectionStore connectionStore
) : ICommandHandler<CreateRepositoryHostConnectionCommand, RepositoryHostConnectionOperationResult>
{
    private readonly IRepositoryHostConnectionStore _connectionStore = connectionStore;

    public async Task<RepositoryHostConnectionOperationResult> HandleAsync(
        CreateRepositoryHostConnectionCommand command,
        CancellationToken cancellationToken
    )
    {
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

        var now = DateTimeOffset.UtcNow;
        var profile = validation.Profile with { CreatedAt = now, UpdatedAt = now };
        var created = await _connectionStore.CreateAsync(profile, cancellationToken);

        return created
            ? RepositoryHostConnectionOperationResult.Succeeded(profile)
            : RepositoryHostConnectionOperationResult.Conflict(
                $"Repository host connection '{profile.Key}' already exists."
            );
    }
}
