using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Commands;

/// <summary>Validates and updates mutable repository profile fields.</summary>
public sealed class UpdateRepositoryCommandHandler(
    IRepositoryStore repositoryStore,
    IWorkSourceEnvironmentStore workSourceEnvironmentStore,
    IRuntimeEnvironmentStore runtimeEnvironmentStore,
    IRepositoryHostConnectionStore? repositoryHostConnectionStore
) : ICommandHandler<UpdateRepositoryCommand, RepositoryOperationResult>
{
    private readonly IRepositoryStore _repositoryStore = repositoryStore;
    private readonly IWorkSourceEnvironmentStore _workSourceEnvironmentStore =
        workSourceEnvironmentStore;
    private readonly IRuntimeEnvironmentStore _runtimeEnvironmentStore = runtimeEnvironmentStore;
    private readonly IRepositoryHostConnectionStore? _repositoryHostConnectionStore =
        repositoryHostConnectionStore;

    public async Task<RepositoryOperationResult> HandleAsync(
        UpdateRepositoryCommand command,
        CancellationToken cancellationToken
    )
    {
        var routeKey = RepositoryProfileValidation.ValidateAndNormalizeKey(command.Key);
        if (!routeKey.IsValid)
        {
            return RepositoryOperationResult.ValidationFailed(routeKey.Errors);
        }

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

        if (!string.Equals(routeKey.Key, validation.Profile.Key, StringComparison.Ordinal))
        {
            return RepositoryOperationResult.ValidationFailed(
                new Dictionary<string, string[]>
                {
                    ["key"] =
                    [
                        "Repository keys are immutable; the request key must match the profile key.",
                    ],
                }
            );
        }

        var updated = await _repositoryStore.UpdateAsync(validation.Profile, cancellationToken);

        return updated
            ? RepositoryOperationResult.Succeeded(validation.Profile)
            : RepositoryOperationResult.NotFound(
                $"Repository '{validation.Profile.Key}' was not found."
            );
    }
}
