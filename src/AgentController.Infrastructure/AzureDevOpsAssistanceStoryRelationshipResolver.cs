using AgentController.Application;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure;

/// <summary>
/// Resolves assistance-story lineage through the Azure DevOps Repos and Work Item
/// Tracking APIs using the connection that owns the pull request.
/// </summary>
internal sealed class AzureDevOpsAssistanceStoryRelationshipResolver(
    IServiceScopeFactory scopeFactory,
    AzureDevOpsAssistanceStoryRelationshipClientFactory clientFactory
) : IAssistanceStoryRelationshipResolver
{
    public async Task<AssistanceStoryRelationships> ResolveAsync(
        AssistanceStoryRelationshipRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.PullRequest.HasCanonicalIdentity)
        {
            throw new ArgumentException(
                "A canonical pull-request identity is required to resolve assistance-story relationships.",
                nameof(request)
            );
        }

        var target = await AzureDevOpsPullRequestTargetResolver.ResolveAsync(
            scopeFactory,
            request.PullRequest,
            cancellationToken
        );
        using var client = clientFactory.Create(
            target.OrganizationUrl,
            target.PersonalAccessToken
        );
        return await client.ResolveAsync(
            target.Repository,
            request.PullRequest.PullRequestId.Trim(),
            request.OriginatingWorkItem?.ExternalId,
            cancellationToken
        );
    }
}
