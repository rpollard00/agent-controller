namespace AgentController.Application.Results;

/// <summary>
/// Status outcomes for the board-states introspection query.
/// </summary>
public enum BoardStatesStatus
{
    /// <summary>The query succeeded and states are available.</summary>
    Succeeded,

    /// <summary>The work source environment was not found.</summary>
    NotFound,

    /// <summary>The environment provider does not support board introspection.</summary>
    UnsupportedProvider,

    /// <summary>Connectivity to the work source failed.</summary>
    ConnectivityError,
}

/// <summary>
/// Result returned by the board-states introspection query.
/// Contains the valid System.State values for the configured board.
/// </summary>
public sealed record BoardStatesResult
{
    /// <summary>The outcome status of the query.</summary>
    public BoardStatesStatus Status { get; init; }

    /// <summary>
    /// The valid System.State values discovered from the board.
    /// Populated only when Status is Succeeded.
    /// </summary>
    public IReadOnlyList<string> States { get; init; } = Array.Empty<string>();

    /// <summary>Human-readable error detail when the query fails.</summary>
    public string? Error { get; init; }

    public static BoardStatesResult Succeeded(IReadOnlyList<string> states) =>
        new() { Status = BoardStatesStatus.Succeeded, States = states };

    public static BoardStatesResult NotFound(string? error = null) =>
        new() { Status = BoardStatesStatus.NotFound, Error = error };

    public static BoardStatesResult UnsupportedProvider(string? error = null) =>
        new() { Status = BoardStatesStatus.UnsupportedProvider, Error = error };

    public static BoardStatesResult ConnectivityError(string? error = null) =>
        new() { Status = BoardStatesStatus.ConnectivityError, Error = error };
}
