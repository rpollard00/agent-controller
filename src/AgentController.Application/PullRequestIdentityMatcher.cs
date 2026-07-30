using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Applies the pull-request identity semantics shared by production pickup and diagnostics.
/// </summary>
public static class PullRequestIdentityMatcher
{
    public static bool Matches(PullRequestReference pullRequest, string? candidateUrl)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl)) return false;
        if (UrlsMatch(pullRequest.PullRequestUrl, candidateUrl)) return true;

        var candidateId = ExtractPullRequestId(candidateUrl);
        var candidateRepository = ExtractRepositoryKey(candidateUrl);
        return candidateId is not null
            && candidateRepository is not null
            && candidateId.Equals(pullRequest.PullRequestId, StringComparison.OrdinalIgnoreCase)
            && candidateRepository.Equals(
                pullRequest.RepositoryKey,
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool Matches(PullRequestReference first, PullRequestReference second)
    {
        if (first.HasCanonicalIdentity && second.HasCanonicalIdentity)
        {
            return first.CanonicalKey.Equals(second.CanonicalKey, StringComparison.OrdinalIgnoreCase);
        }

        if (UrlsMatch(first.PullRequestUrl, second.PullRequestUrl)) return true;

        return first.PullRequestId.Equals(second.PullRequestId, StringComparison.OrdinalIgnoreCase)
            && first.RepositoryKey.Equals(second.RepositoryKey, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractPullRequestId(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var segments = uri.Segments;
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Equals("pullrequest/", StringComparison.Ordinal)
                && index + 1 < segments.Length)
            {
                return segments[index + 1].TrimEnd('/');
            }
        }

        return null;
    }

    public static string? ExtractRepositoryKey(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var segments = uri.Segments;
        for (var index = 0; index < segments.Length; index++)
        {
            if (!segments[index].Equals("_git/", StringComparison.Ordinal)
                || index == 0
                || index + 1 >= segments.Length) continue;

            var project = segments[index - 1].TrimEnd('/');
            var repository = segments[index + 1].TrimEnd('/');
            return string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(repository)
                ? null
                : $"{project}/{repository}";
        }

        return null;
    }

    private static bool UrlsMatch(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first)
        && !string.IsNullOrWhiteSpace(second)
        && first.TrimEnd('/').Equals(second.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
