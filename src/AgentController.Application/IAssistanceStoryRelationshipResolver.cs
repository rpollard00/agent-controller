using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Inputs used to resolve the work-item and pull-request relations for a new
/// assistance story.
/// </summary>
public sealed record AssistanceStoryRelationshipRequest
{
    /// <summary>The existing pull request that the assistance story will continue.</summary>
    public PullRequestReference PullRequest { get; init; } = new();

    /// <summary>
    /// External reference for the controller work item that originated the pull request,
    /// when the pull request was produced by a controller run. When this item can be
    /// resolved it takes precedence over work items linked directly to the pull request.
    /// </summary>
    public ExternalWorkRef? OriginatingWorkItem { get; init; }
}

/// <summary>Resolved relations and source-story lineage for an assistance story.</summary>
public sealed record AssistanceStoryRelationships
{
    /// <summary>Relations to add atomically when the assistance story is created.</summary>
    public IReadOnlyList<WorkItemRelation> Relations { get; init; } = [];

    /// <summary>Provider-assigned identifiers of the source stories.</summary>
    public IReadOnlyList<string> SourceStoryIds { get; init; } = [];

    /// <summary>
    /// Provider URL of the parent shared by every source story, or <see langword="null"/>
    /// when the source stories do not have one unambiguous common parent.
    /// </summary>
    public string? CommonParentUrl { get; init; }
}

/// <summary>
/// Resolves provider relations for a newly materialized assistance story. Implementations
/// prefer the originating controller work item and otherwise discover applicable User
/// Stories linked to the pull request.
/// </summary>
public interface IAssistanceStoryRelationshipResolver
{
    /// <summary>Builds source-story, common-parent, and pull-request relations.</summary>
    Task<AssistanceStoryRelationships> ResolveAsync(
        AssistanceStoryRelationshipRequest request,
        CancellationToken cancellationToken
    );
}
