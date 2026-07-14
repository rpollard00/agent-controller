namespace AgentController.Application.Queries;

/// <summary>
/// Retrieves the valid System.State values for a managed work source environment
/// by querying the board's process configuration.
/// </summary>
/// <param name="EnvironmentKey">The immutable key of the work source environment.</param>
public sealed record GetBoardStatesQuery(string EnvironmentKey);
