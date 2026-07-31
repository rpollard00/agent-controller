using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentController.Infrastructure;

/// <summary>
/// <see cref="IPrLabelSource"/> implementation against the Azure DevOps Git REST API.
///
/// Repository profiles and credentials are resolved inside a short-lived scope for
/// every lookup. No connection or credential state is retained by this singleton.
/// </summary>
internal sealed partial class AzureDevOpsReposPrLabelSource(
    IServiceScopeFactory scopeFactory,
    AzureDevOpsPullRequestLabelClientFactory clientFactory,
    ILogger<AzureDevOpsReposPrLabelSource> logger
) : IPrLabelSource
{
    public async Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
        PrUnderTest pr,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(pr);
        cancellationToken.ThrowIfCancellationRequested();

        var pullRequest = ToPullRequestReference(pr);
        if (string.IsNullOrWhiteSpace(pullRequest.RepositoryKey)
            || string.IsNullOrWhiteSpace(pullRequest.PullRequestId))
        {
            return [];
        }

        try
        {
            var target = await AzureDevOpsPullRequestTargetResolver.ResolveAsync(
                scopeFactory,
                pullRequest,
                cancellationToken
            );
            using var client = clientFactory.Create(
                target.OrganizationUrl,
                target.PersonalAccessToken
            );

            return await client.GetLabelsAsync(
                target.Repository,
                pullRequest.PullRequestId.Trim(),
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Label reads are a marker-gate input and must fail closed. Do not log the
            // exception because provider and credential implementations may include
            // sensitive request or secret details in their messages.
            Log.LabelFetchFailed(
                logger,
                pullRequest.RepositoryKey.Trim(),
                pullRequest.PullRequestId.Trim()
            );
            return [];
        }
    }

    private static PullRequestReference ToPullRequestReference(PrUnderTest pr)
    {
        var reference = pr.PullRequest;
        if (reference.HasCanonicalIdentity)
        {
            return reference;
        }

        // PrUnderTest retains flattened fields for older callers. They still identify
        // the managed repository and pull request; all provider coordinates and the
        // credential come from the repository/connection profiles below.
        return reference with
        {
            RepositoryKey = FirstNonEmpty(reference.RepositoryKey, pr.RepoKey),
            PullRequestId = FirstNonEmpty(reference.PullRequestId, pr.PullRequestId),
            PullRequestUrl = FirstNonEmpty(reference.PullRequestUrl, pr.PullRequestUrl),
        };
    }

    private static string FirstNonEmpty(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;

    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Could not fetch Azure DevOps PR labels for managed repository '{RepositoryKey}' and pull request '{PullRequestId}'."
        )]
        public static partial void LabelFetchFailed(
            ILogger logger,
            string repositoryKey,
            string pullRequestId
        );
    }
}
