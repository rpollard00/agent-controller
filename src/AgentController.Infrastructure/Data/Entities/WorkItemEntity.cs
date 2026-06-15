namespace AgentController.Infrastructure.Data.Entities;

/// <summary>
/// EF Core entity for the WorkItems table.
/// Maps to the prototype data model defined in the architecture (§7.5).
/// JSON-like columns (AcceptanceCriteriaJson, TagsJson) are stored as TEXT.
/// </summary>
internal sealed class WorkItemEntity
{
    /// <summary>Controller-assigned stable identifier (PK).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Identifier of the work source that produced this item.</summary>
    public string ExternalSource { get; set; } = string.Empty;

    /// <summary>External identifier from the work source.</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>URL to the work item in the source system.</summary>
    public string? ExternalUrl { get; set; }

    /// <summary>Repository key this work item maps to.</summary>
    public string RepoKey { get; set; } = string.Empty;

    /// <summary>Work item title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Work item description or body.</summary>
    public string? Body { get; set; }

    /// <summary>Acceptance criteria serialized as JSON.</summary>
    public string? AcceptanceCriteriaJson { get; set; }

    /// <summary>Work item priority.</summary>
    public int? Priority { get; set; }

    /// <summary>Current status in the work source.</summary>
    public string? Status { get; set; }

    /// <summary>Tags serialized as a JSON array.</summary>
    public string? TagsJson { get; set; }

    /// <summary>Who the work item is assigned to in the source.</summary>
    public string? AssignedTo { get; set; }

    /// <summary>Identifier of the work source that produced this item.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Worker ID that currently holds the lease on this item.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>When the current lease expires. NULL if not leased.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>When the record was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the record was last mutated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
