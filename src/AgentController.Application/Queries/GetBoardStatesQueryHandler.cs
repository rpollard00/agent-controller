using System.Net;
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

        // (a) Disabled source — return a clear disabled message.
        if (!profile.Enabled)
        {
            return BoardStatesResult.ConnectivityError(
                $"Work source environment '{key.Key}' is disabled."
            );
        }

        // (b) Missing PAT env var in the host process.
        if (!string.IsNullOrWhiteSpace(profile.PatEnvironmentVariable))
        {
            var envValue = Environment.GetEnvironmentVariable(profile.PatEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(envValue))
            {
                return BoardStatesResult.ConnectivityError(
                    $"PAT environment variable '{profile.PatEnvironmentVariable}' " +
                    "is not set in the API host process."
                );
            }
        }

        try
        {
            // Create a boards client from the profile and discover valid states.
            // GetValidStatesAsync now returns states grouped by work item type.
            var client = _boardsClientFactory.Create(profile);
            var groupedStates = await client.GetValidStatesAsync(
                profile.Project,
                cancellationToken
            );

            // If no states were returned, the process may have no WITs with states.
            if (groupedStates.Count == 0)
            {
                return BoardStatesResult.ConnectivityError(
                    "Unable to retrieve board states. Verify organization URL, project, and PAT."
                );
            }

            return BoardStatesResult.Succeeded(groupedStates);
        }
        catch (HttpRequestException ex)
        {
            // (c) HTTP error from ADO — surface the underlying detail.
            return BoardStatesResult.ConnectivityError(
                $"Azure DevOps request failed: {ex.Message}"
            );
        }
        catch (OperationCanceledException)
        {
            return BoardStatesResult.ConnectivityError(
                "Request to Azure DevOps timed out or was cancelled."
            );
        }
        catch (InvalidOperationException ex)
        {
            // Unexpected configuration error (e.g. factory threw for an unhandled reason).
            return BoardStatesResult.ConnectivityError(
                $"Failed to create Azure DevOps client: {ex.Message}"
            );
        }
        catch (Exception ex)
        {
            // Fallback: surface the detail without leaking secrets.
            return BoardStatesResult.ConnectivityError(
                $"Unexpected error querying Azure DevOps: {ex.Message}"
            );
        }
    }
}
