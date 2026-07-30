namespace AgentController.Application.Queries;

/// <summary>Concise pickup result shown for a board item.</summary>
public enum BoardItemMatchResult
{
    Eligible,
    MissingTags,
    Excluded,
}

/// <summary>One operator-facing pickup policy check.</summary>
public sealed record BoardItemDiagnosticCheck
{
    public string Code { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Board item fields and its concise pickup result.</summary>
public sealed record BoardItemDiagnosticSummary
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string Project { get; init; } = string.Empty;
    public string WorkSourceEnvironmentKey { get; init; } = string.Empty;
    public string? RepositoryKey { get; init; }
    public string? State { get; init; }
    public BoardItemMatchResult Match { get; init; }
}

/// <summary>A page of evaluated board items with provider discovery metadata.</summary>
public sealed record BoardItemDiagnosticsPage
{
    public IReadOnlyList<BoardItemDiagnosticSummary> Items { get; init; } = [];
    public IReadOnlyList<ManagedBoardItemDiscoveryFailure> Failures { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
}

/// <summary>Full, lazy pickup diagnostics for one board item.</summary>
public sealed record BoardItemDiagnosticDetail
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string Project { get; init; } = string.Empty;
    public string WorkSourceEnvironmentKey { get; init; } = string.Empty;
    public string? RepositoryKey { get; init; }
    public string? State { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public BoardItemMatchResult Match { get; init; }
    public bool Eligible { get; init; }
    public IReadOnlyList<BoardItemDiagnosticCheck> Checks { get; init; } = [];
    public IReadOnlyList<string> RecognizedRepositoryTags { get; init; } = [];
    public string RecognizedReadyTag { get; init; } = string.Empty;
    public string RecognizedReadyReworkTag { get; init; } = string.Empty;
}
