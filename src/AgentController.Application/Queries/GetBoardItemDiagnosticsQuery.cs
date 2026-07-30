namespace AgentController.Application.Queries;

/// <summary>Requests lazy pickup diagnostics for one externally identified board item.</summary>
public sealed record GetBoardItemDiagnosticsQuery
{
    public string WorkSourceEnvironmentKey { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
}
