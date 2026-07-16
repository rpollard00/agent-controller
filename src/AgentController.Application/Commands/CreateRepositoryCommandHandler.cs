using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates, normalizes, and creates a managed repository profile.</summary>
public sealed class CreateRepositoryCommandHandler(
    IRepositoryStore repositoryStore,
    IWorkSourceEnvironmentStore workSourceEnvironmentStore,
    IRuntimeEnvironmentStore runtimeEnvironmentStore,
    IRepositoryHostConnectionStore? repositoryHostConnectionStore
) : ICommandHandler<CreateRepositoryCommand, RepositoryOperationResult>
{
    private readonly IRepositoryStore _repositoryStore = repositoryStore;
    private readonly IWorkSourceEnvironmentStore _workSourceEnvironmentStore =
        workSourceEnvironmentStore;
    private readonly IRuntimeEnvironmentStore _runtimeEnvironmentStore = runtimeEnvironmentStore;
    private readonly IRepositoryHostConnectionStore? _repositoryHostConnectionStore =
        repositoryHostConnectionStore;

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
            _workSourceEnvironmentStore,
            _runtimeEnvironmentStore,
            _repositoryHostConnectionStore,
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
