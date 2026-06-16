namespace AgentController.Domain;

/// <summary>
/// A work item candidate discovered from a work source.
/// Eligibility filtering (tags, states) is configuration-driven;
/// this record carries raw data from the source.
/// </summary>
public sealed record WorkCandidate
{
    /// <summary>Controller-assigned stable identifier for this candidate.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>External identifier from the work source (e.g. Azure DevOps work item ID).</summary>
    public string ExternalId { get; init; } = string.Empty;

    /// <summary>URL to the work item in the source system.</summary>
    public string? ExternalUrl { get; init; }

    /// <summary>Repository key this work item maps to.</summary>
    public string RepoKey { get; init; } = string.Empty;

    /// <summary>Work item title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Work item description or body.</summary>
    public string? Description { get; init; }

    /// <summary>Acceptance criteria stored as key-value pairs.</summary>
    public IReadOnlyDictionary<string, string>? AcceptanceCriteria { get; init; }

    /// <summary>Work item priority, if the source provides it.</summary>
    public int? Priority { get; init; }

    /// <summary>
    /// Current status in the work source. Not hard-coded to an enum;
    /// eligibility is configuration-driven.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Tags on the work item in the work source. Not hard-coded to an enum;
    /// eligibility is configuration-driven.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Who the work item is assigned to in the source.</summary>
    public string? AssignedTo { get; init; }

    /// <summary>Identifier of the work source that produced this candidate.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Opaque source metadata needed for later updates (claiming, status projection).
    /// For Azure DevOps Boards this carries the revision number, area path,
    /// iteration path, and work item type so subsequent PATCH operations can
    /// use optimistic concurrency and preserve field-level fidelity.
    /// </summary>
    public IReadOnlyDictionary<string, string>? SourceMetadata { get; init; }
}

/// <summary>
/// Stable reference to a work item in an external work source.
/// </summary>
public sealed record ExternalWorkRef
{
    /// <summary>Work source identifier (e.g. "AzureDevOpsBoards").</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>External identifier in the source system.</summary>
    public string ExternalId { get; init; } = string.Empty;

    /// <summary>URL to the work item, if available.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// Source-controlled revision number for optimistic concurrency.
    /// For Azure DevOps Boards this is the work item <c>rev</c> field.
    /// When set, update operations should include an <c>If-Match</c>
    /// header to detect conflicting modifications.
    /// </summary>
    public string? Revision { get; init; }
}

/// <summary>
/// Status projection to report back to a work source.
/// </summary>
public sealed record ExternalWorkStatus
{
    /// <summary>The new external status string (e.g. "Active", "Resolved").</summary>
    public string? Status { get; init; }

    /// <summary>Tags to add or set on the work item.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Comment to append to the work item.</summary>
    public string? Comment { get; init; }
}

/// <summary>
/// Query parameters for discovering eligible work items.
/// All fields are optional; a provider interprets them according to its capabilities.
/// </summary>
public sealed record WorkQuery
{
    /// <summary>Project or team project to scope the query.</summary>
    public string? Project { get; init; }

    /// <summary>Area path filter.</summary>
    public string? AreaPath { get; init; }

    /// <summary>Iteration path filter.</summary>
    public string? IterationPath { get; init; }

    /// <summary>Work item type filter (e.g. "Bug", "Task", "User Story").</summary>
    public string? WorkItemType { get; init; }

    /// <summary>States to include. Eligibility is configuration-driven.</summary>
    public IReadOnlyList<string>? States { get; init; }

    /// <summary>Tags to include. Eligibility is configuration-driven.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Tags to exclude.</summary>
    public IReadOnlyList<string>? ExcludedTags { get; init; }

    /// <summary>Assigned-to filter.</summary>
    public string? AssignedTo { get; init; }

    /// <summary>Minimum priority (inclusive).</summary>
    public int? PriorityMin { get; init; }

    /// <summary>Maximum priority (inclusive).</summary>
    public int? PriorityMax { get; init; }

    /// <summary>Maximum number of candidates to return.</summary>
    public int MaxResults { get; init; } = 50;
}

/// <summary>
/// Request to claim a work candidate for exclusive execution.
/// </summary>
public sealed record ClaimRequest
{
    /// <summary>Identifier of the worker/controller instance claiming the item.</summary>
    public string WorkerId { get; init; } = string.Empty;

    /// <summary>How long the lease should last before expiring.</summary>
    public TimeSpan LeaseTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>When the claim was initiated.</summary>
    public DateTimeOffset ClaimedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Result of attempting to claim a work candidate.
/// </summary>
public sealed record ClaimResult
{
    /// <summary>Whether the claim was successful.</summary>
    public bool Success { get; init; }

    /// <summary>Reference to the claimed work item (populated on success).</summary>
    public ExternalWorkRef? WorkRef { get; init; }

    /// <summary>Opaque lease token for lease renewal or release (populated on success).</summary>
    public string? LeaseToken { get; init; }

    /// <summary>Human-readable reason for claim failure (populated on failure).</summary>
    public string? FailureReason { get; init; }
}

/// <summary>
/// Request to create a local fake work item in the persistence store.
/// </summary>
public sealed record CreateWorkItemRequest
{
    /// <summary>Repository key this work item maps to.</summary>
    public string RepoKey { get; init; } = string.Empty;

    /// <summary>Work item title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Work item body. If set, takes precedence over <see cref="Description"/>.</summary>
    public string? Body { get; init; }

    /// <summary>Work item description (alias for body). Used when <see cref="Body"/> is not set.</summary>
    public string? Description { get; init; }

    /// <summary>Acceptance criteria stored as key-value pairs.</summary>
    public IReadOnlyDictionary<string, string>? AcceptanceCriteria { get; init; }

    /// <summary>Work item priority. Defaults to 0 (unprioritized).</summary>
    public int Priority { get; init; }

    /// <summary>Initial status. Defaults to "New".</summary>
    public string Status { get; init; } = "New";

    /// <summary>Tags on the work item.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Identifier of the work source. Defaults to "LocalFake".</summary>
    public string Source { get; init; } = "LocalFake";
}

/// <summary>
/// Query parameters for listing work items from the persistence store.
/// All fields are optional; implementations apply the provided filters.
/// </summary>
public sealed record WorkItemListQuery
{
    /// <summary>Filter by status string.</summary>
    public string? Status { get; init; }

    /// <summary>Filter by repository key.</summary>
    public string? RepoKey { get; init; }

    /// <summary>Filter by tags (items must have at least one matching tag).</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Maximum number of items to return.</summary>
    public int MaxResults { get; init; } = 100;

    /// <summary>Number of items to skip for pagination.</summary>
    public int Offset { get; init; }
}
