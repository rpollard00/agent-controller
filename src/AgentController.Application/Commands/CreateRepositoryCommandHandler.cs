using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates, normalizes, and creates a managed repository profile.</summary>
public sealed class CreateRepositoryCommandHandler(
    IRepositoryStore repositoryStore,
    IRuntimeEnvironmentStore runtimeEnvironmentStore,
    IConnectionStore? connectionStore
) : ICommandHandler<CreateRepositoryCommand, RepositoryOperationResult>
{
    private readonly IRepositoryStore _repositoryStore = repositoryStore;
    private readonly IRuntimeEnvironmentStore _runtimeEnvironmentStore = runtimeEnvironmentStore;
    private readonly IConnectionStore? _connectionStore = connectionStore;

    public async Task<RepositoryOperationResult> HandleAsync(
        CreateRepositoryCommand command,
        CancellationToken cancellationToken
    )
    {
        if (command.Profile is null)
        {
            return RepositoryOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["profile"] = ["A repository profile is required."],
                }
            );
        }

        var validation = await RepositoryProfileValidation.ValidateAndNormalizeAsync(
            command.Profile,
            _runtimeEnvironmentStore,
            _connectionStore,
            cancellationToken
        );

        if (!validation.IsValid)
        {
            return RepositoryOperationResult.ValidationFailed(validation.Errors);
        }

        var created = await _repositoryStore.CreateAsync(validation.Profile, cancellationToken);

        return created
            ? RepositoryOperationResult.Succeeded(validation.Profile)
            : RepositoryOperationResult.Conflict(
                $"Repository '{validation.Profile.Key}' already exists."
            );
    }
}
