using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure;

/// <summary>
/// <see cref="IFeedbackSource"/> implementation against the Azure DevOps Repos REST API.
/// Provider coordinates and credentials are resolved from the managed repository target;
/// the pull-request display URL is retained only as display metadata.
/// </summary>
internal sealed class AzureDevOpsReposFeedbackSource(
    IServiceScopeFactory scopeFactory,
    AzureDevOpsPullRequestFeedbackClientFactory clientFactory
) : IFeedbackSource
{
    public async Task<IReadOnlyList<ReworkSignal>> PollAsync(
        FeedbackQuery query,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (query.OpenPrs.Count == 0)
        {
            return [];
        }

        var signals = new List<ReworkSignal>();
        foreach (var pr in query.OpenPrs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pullRequest = ToPullRequestReference(pr);
            if (string.IsNullOrWhiteSpace(pullRequest.RepositoryKey)
                || string.IsNullOrWhiteSpace(pullRequest.PullRequestId))
            {
                continue;
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
                var pullRequestId = pullRequest.PullRequestId.Trim();
                var threads = await client.GetThreadsAsync(
                    target.Repository,
                    pullRequestId,
                    cancellationToken
                );
                var comments = threads.SelectMany(thread => thread.Comments).ToArray();
                if (threads.Count == 0 || comments.Length == 0)
                {
                    continue;
                }

                // The target resolver normalizes the managed identity components. Return
                // that canonical identity so downstream correlation never depends on the
                // URL or on flattened legacy fields from PrUnderTest.
                var canonicalPullRequest = pullRequest with
                {
                    EnvironmentKey = target.Repository.EnvironmentKey,
                    RepositoryKey = target.Repository.RepositoryKey,
                    PullRequestId = pullRequestId,
                };

                signals.Add(new ReworkSignal
                {
                    RequestMode = pr.RequestMode,
                    PullRequest = canonicalPullRequest,
                    OriginatingRunId = pr.OriginatingRunId,
                    PullRequestId = pullRequestId,
                    Threads = threads,
                    FirstQualifyingCommentAt = comments.Min(comment => comment.CreatedAt),
                    LastQualifyingCommentAt = comments.Max(comment => comment.CreatedAt),
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A single unavailable managed target or provider response must not stop
                // polling other repositories. Do not expose resolver or HTTP details.
                continue;
            }
        }

        return signals;
    }

    private static PullRequestReference ToPullRequestReference(PrUnderTest pr)
    {
        var reference = pr.PullRequest;
        return reference with
        {
            RepositoryKey = FirstNonEmpty(reference.RepositoryKey, pr.RepoKey),
            PullRequestId = FirstNonEmpty(reference.PullRequestId, pr.PullRequestId),
            PullRequestUrl = FirstNonEmpty(reference.PullRequestUrl, pr.PullRequestUrl),
        };
    }

    private static string FirstNonEmpty(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;
}
