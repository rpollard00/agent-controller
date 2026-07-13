using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates, normalizes, and creates a managed runtime environment.</summary>
public sealed class CreateRuntimeEnvironmentCommandHandler(
    IRuntimeEnvironmentStore environmentStore
) : ICommandHandler<CreateRuntimeEnvironmentCommand, RuntimeEnvironmentOperationResult>
{
    private readonly IRuntimeEnvironmentStore _environmentStore = environmentStore;

    public async Task<RuntimeEnvironmentOperationResult> HandleAsync(
        CreateRuntimeEnvironmentCommand command,
        CancellationToken cancellationToken
    )
    {
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

        var now = DateTimeOffset.UtcNow;
        var profile = validation.Profile with { CreatedAt = now, UpdatedAt = now };
        var created = await _environmentStore.CreateAsync(profile, cancellationToken);

        return created
            ? RuntimeEnvironmentOperationResult.Succeeded(profile)
            : RuntimeEnvironmentOperationResult.Conflict(
                $"Runtime environment '{profile.Key}' already exists."
            );
    }
}
