using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Outcome of resolving a run-backed pull request to managed repository metadata.</summary>
public enum ManagedPullRequestResolutionStatus
{
    Matched,
    Missing,
    Ambiguous,
}

/// <summary>
/// Result of managed pull-request resolution. A missing or ambiguous result must never
/// be converted back into a repository key parsed from the provider URL.
/// </summary>
public sealed record ManagedPullRequestResolution
{
    public ManagedPullRequestResolutionStatus Status { get; init; }

    public ManagedPullRequestSnapshot? Snapshot { get; init; }

    public int CandidateCount { get; init; }

    public bool IsResolved => Status == ManagedPullRequestResolutionStatus.Matched
        && Snapshot?.PullRequest.HasCanonicalIdentity == true;
}

/// <summary>
/// Resolves run URLs against the managed pull-request inventory, with a repository
/// profile fallback for provider discovery outages. Repository profile keys remain
/// authoritative aliases and are never inferred from URL project/repository segments.
/// </summary>
public static class ManagedPullRequestResolver
{
    public static ManagedPullRequestResolution Resolve(
        string? pullRequestUrl,
        IReadOnlyList<ManagedPullRequestSnapshot> discoveredPullRequests,
        IReadOnlyList<RepositoryProfile> repositoryProfiles)
    {
        if (!PullRequestIdentityMatcher.TryParsePullRequestUrl(
                pullRequestUrl,
                out var pullRequestIdentity))
        {
            return new ManagedPullRequestResolution
            {
                Status = ManagedPullRequestResolutionStatus.Missing,
            };
        }

        var discoveredMatches = discoveredPullRequests
            .Where(snapshot => PullRequestIdentityMatcher.Matches(
                snapshot.PullRequest,
                pullRequestUrl))
            .ToArray();
        if (discoveredMatches.Length == 1)
        {
            return new ManagedPullRequestResolution
            {
                Status = ManagedPullRequestResolutionStatus.Matched,
                Snapshot = discoveredMatches[0],
                CandidateCount = 1,
            };
        }

        if (discoveredMatches.Length > 1)
        {
            return new ManagedPullRequestResolution
            {
                Status = ManagedPullRequestResolutionStatus.Ambiguous,
                CandidateCount = discoveredMatches.Length,
            };
        }

        var profileMatches = repositoryProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Key))
            .Where(profile => !string.IsNullOrWhiteSpace(profile.RepositoryHostConnectionKey))
            .Where(profile => PullRequestIdentityMatcher.MatchesRepository(
                pullRequestIdentity,
                profile))
            .ToArray();

        if (profileMatches.Length != 1)
        {
            return new ManagedPullRequestResolution
            {
                Status = profileMatches.Length > 1
                    ? ManagedPullRequestResolutionStatus.Ambiguous
                    : ManagedPullRequestResolutionStatus.Missing,
                CandidateCount = profileMatches.Length,
            };
        }

        var profileMatch = profileMatches[0];
        return new ManagedPullRequestResolution
        {
            Status = ManagedPullRequestResolutionStatus.Matched,
            CandidateCount = 1,
            Snapshot = new ManagedPullRequestSnapshot
            {
                PullRequest = new PullRequestReference
                {
                    EnvironmentKey = profileMatch.RepositoryHostConnectionKey!.Trim(),
                    RepositoryKey = profileMatch.Key.Trim(),
                    PullRequestId = pullRequestIdentity.PullRequestId,
                    PullRequestUrl = pullRequestUrl!.Trim(),
                },
            },
        };
    }
}
