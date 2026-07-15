using System.Linq;
using AgentController.Application.Abstractions;
using AgentController.Application.Results;

namespace AgentController.Application.Queries;

/// <summary>
/// Resolves valid board states for a managed work source environment.
/// Uses the boards client factory to create a client from the environment profile
/// and calls GetValidStatesAsync to discover System.State values.
/// </summary>
public sealed class GetBoardStatesQueryHandler(
    IWorkSourceEnvironmentStore environmentStore,
    IAzureDevOpsBoardsClientFactory boardsClientFactory
) : IQueryHandler<GetBoardStatesQuery, BoardStatesResult>
{
    private readonly IWorkSourceEnvironmentStore _environmentStore = environmentStore;
    private readonly IAzureDevOpsBoardsClientFactory _boardsClientFactory = boardsClientFactory;

    public async Task<BoardStatesResult> ExecuteAsync(
        GetBoardStatesQuery query,
        CancellationToken cancellationToken
    )
    {
        // Validate and normalize the environment key.
        var key = WorkSourceEnvironmentProfileValidation.ValidateAndNormalizeKey(
            query.EnvironmentKey
        );
        if (!key.IsValid)
        {
            return BoardStatesResult.NotFound(
                $"Invalid environment key '{query.EnvironmentKey}'."
            );
        }

        // Look up the environment profile.
        var profile = await _environmentStore.GetByKeyAsync(key.Key, cancellationToken);
        if (profile is null)
        {
            return BoardStatesResult.NotFound(
                $"Work source environment '{key.Key}' was not found."
            );
        }

        // Only AzureDevOpsBoards provider supports board introspection.
        if (
            !string.Equals(
                profile.Provider,
                "AzureDevOpsBoards",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return BoardStatesResult.UnsupportedProvider(
                $"Provider '{profile.Provider}' does not support board introspection."
            );
        }

        // Create a boards client from the profile and discover valid states.
        // GetValidStatesAsync now returns states grouped by work item type.
        var client = _boardsClientFactory.Create(profile);
        var groupedStates = await client.GetValidStatesAsync(profile.Project, cancellationToken);

        // If no states were returned, the board may be misconfigured or unreachable.
        if (groupedStates.Count == 0)
        {
            return BoardStatesResult.ConnectivityError(
                "Unable to retrieve board states. Verify organization URL, project, and PAT."
            );
        }

        // Flatten grouped states into a sorted union for the current flat result shape.
        // Work item 3 will update BoardStatesResult to carry the grouped shape directly.
        var flatStates = groupedStates
            .SelectMany(kvp => kvp.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return BoardStatesResult.Succeeded(flatStates);
    }
}
