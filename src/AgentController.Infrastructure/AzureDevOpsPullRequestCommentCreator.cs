using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure;

/// <summary>
/// Resolves managed repository credentials and posts assistance lifecycle comments through
/// the Azure DevOps Repos REST API.
/// </summary>
internal sealed class AzureDevOpsPullRequestCommentCreator(
    IServiceScopeFactory scopeFactory,
    AzureDevOpsPullRequestCommentClientFactory clientFactory
) : IPullRequestCommentCreator
{
    public async Task CreateAsync(
        PullRequestReference pullRequest,
        AssistanceLifecycleCommentRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!pullRequest.HasCanonicalIdentity)
        {
            throw new ArgumentException(
                "A canonical pull-request identity is required for comment creation.",
                nameof(pullRequest)
            );
        }

        var target = await AzureDevOpsPullRequestTargetResolver.ResolveAsync(
            scopeFactory,
            pullRequest,
            cancellationToken
        );
        using var client = clientFactory.Create(
            target.OrganizationUrl,
            target.PersonalAccessToken
        );
        await client.CreateAsync(
            target.Repository,
            pullRequest.PullRequestId.Trim(),
            request,
            cancellationToken
        );
    }
}
