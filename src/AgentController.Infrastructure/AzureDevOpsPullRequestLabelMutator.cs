using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure;

/// <summary>
/// Resolves managed repository credentials and mutates pull-request labels through
/// the Azure DevOps Repos REST API.
/// </summary>
internal sealed class AzureDevOpsPullRequestLabelMutator(
    IServiceScopeFactory scopeFactory,
    AzureDevOpsPullRequestLabelClientFactory clientFactory
) : IPullRequestLabelMutator
{
    public async Task MutateAsync(
        PullRequestReference pullRequest,
        PullRequestLabelMutation mutation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();

        if (!pullRequest.HasCanonicalIdentity)
        {
            throw new ArgumentException(
                "A canonical pull-request identity is required for label mutation.",
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
        await client.MutateAsync(
            target.Repository,
            pullRequest.PullRequestId.Trim(),
            mutation,
            cancellationToken
        );
    }
}
