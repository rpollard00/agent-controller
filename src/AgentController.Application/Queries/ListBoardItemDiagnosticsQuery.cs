namespace AgentController.Application.Queries;

/// <summary>Requests an evaluated page of board items visible to managed work sources.</summary>
public sealed record ListBoardItemDiagnosticsQuery
{
    public string? WorkSourceEnvironmentKey { get; init; }
    public bool IncludeTerminal { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = ManagedBoardItemDiscoveryQuery.DefaultPageSize;
}
