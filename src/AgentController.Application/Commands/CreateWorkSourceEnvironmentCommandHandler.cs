using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates, normalizes, and creates a managed work source environment.</summary>
public sealed class CreateWorkSourceEnvironmentCommandHandler(
    IWorkSourceEnvironmentStore environmentStore
) : ICommandHandler<CreateWorkSourceEnvironmentCommand, WorkSourceEnvironmentOperationResult>
{
    private readonly IWorkSourceEnvironmentStore _environmentStore = environmentStore;

    public async Task<WorkSourceEnvironmentOperationResult> HandleAsync(
        CreateWorkSourceEnvironmentCommand command,
        CancellationToken cancellationToken
    )
    {
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

        var now = DateTimeOffset.UtcNow;
        var profile = validation.Profile with { CreatedAt = now, UpdatedAt = now };
        var created = await _environmentStore.CreateAsync(profile, cancellationToken);

        return created
            ? WorkSourceEnvironmentOperationResult.Succeeded(profile)
            : WorkSourceEnvironmentOperationResult.Conflict(
                $"Work source environment '{profile.Key}' already exists."
            );
    }
}
