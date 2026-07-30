using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Read-only query for board items visible through managed work sources.</summary>
public sealed record ManagedBoardItemDiscoveryQuery
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;

    /// <summary>Optional managed work-source environment key.</summary>
    public string? WorkSourceEnvironmentKey { get; init; }

    /// <summary>Whether terminal board states should be included.</summary>
    public bool IncludeTerminal { get; init; }

    /// <summary>One-based result page.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Number of rows per page.</summary>
    public int PageSize { get; init; } = DefaultPageSize;
}

/// <summary>A board item together with the managed source that produced it.</summary>
public sealed record ManagedBoardItemSnapshot
{
    public WorkCandidate Item { get; init; } = new();
    public string WorkSourceEnvironmentKey { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
}

/// <summary>Operator-safe failure for one managed work-source environment.</summary>
public sealed record ManagedBoardItemDiscoveryFailure
{
    public string WorkSourceEnvironmentKey { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>A deterministic page of managed board-item discovery results.</summary>
public sealed record ManagedBoardItemDiscoveryPage
{
    public IReadOnlyList<ManagedBoardItemSnapshot> Items { get; init; } = [];
    public IReadOnlyList<ManagedBoardItemDiscoveryFailure> Failures { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
}

/// <summary>
/// Provider-neutral, read-only port for diagnostic discovery of managed board items.
/// This path intentionally does not apply normal pickup eligibility filters.
/// </summary>
public interface IManagedBoardItemDiscovery
{
    Task<ManagedBoardItemDiscoveryPage> ListAsync(
        ManagedBoardItemDiscoveryQuery query,
        CancellationToken cancellationToken
    );
}
