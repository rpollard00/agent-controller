using AgentController.Application;

namespace AgentController.Infrastructure;

/// <summary>Empty relationship resolver for providers without external work-item links.</summary>
internal sealed class NoOpAssistanceStoryRelationshipResolver
    : IAssistanceStoryRelationshipResolver
{
    public Task<AssistanceStoryRelationships> ResolveAsync(
        AssistanceStoryRelationshipRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AssistanceStoryRelationships());
    }
}
