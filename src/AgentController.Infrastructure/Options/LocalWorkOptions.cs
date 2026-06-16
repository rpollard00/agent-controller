namespace AgentController.Infrastructure.Options;

/// <summary>
/// Configuration for local work item definitions loaded from configuration
/// when the work source provider is <c>"LocalFile"</c>.
/// Section: "localWork"
/// </summary>
public sealed class LocalWorkOptions
{
    public const string SectionName = "localWork";

    /// <summary>
    /// Ordered set of work item definitions.
    /// Each definition requires at minimum a <c>repoKey</c> and <c>title</c>.
    /// Definitions missing required fields are logged and skipped at startup.
    /// </summary>
    public IReadOnlyList<LocalWorkItemDefinition> Definitions { get; init; } = [];
}

/// <summary>
/// A single work item definition for the <c>LocalFile</c> work source.
/// </summary>
public sealed class LocalWorkItemDefinition
{
    /// <summary>
    /// Optional stable external identifier. When not supplied, the provider
    /// derives a stable idempotency key from the definition content so
    /// upserts are idempotent across controller restarts.
    /// </summary>
    public string? ExternalId { get; init; }

    /// <summary>
    /// Repository key this work item maps to. Required.
    /// </summary>
    public string RepoKey { get; init; } = string.Empty;

    /// <summary>
    /// Work item title. Required.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Work item body/description.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>
    /// Alias for <see cref="Body"/>. Used when <see cref="Body"/> is not set.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Acceptance criteria stored as key-value pairs.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AcceptanceCriteria { get; init; }

    /// <summary>
    /// Work item priority. Defaults to 0 (unprioritized).
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Initial status. Defaults to "New".
    /// </summary>
    public string Status { get; init; } = "New";

    /// <summary>
    /// Tags on the work item.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}
