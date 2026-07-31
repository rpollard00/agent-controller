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
        return TryParsePullRequestUrl(url, out var identity) ? identity.PullRequestId : null;
    }

    public static string? ExtractRepositoryKey(string? url)
    {
        return TryParsePullRequestUrl(url, out var identity)
            ? $"{identity.Project}/{identity.Repository}"
            : null;
    }

    /// <summary>
    /// Parses Azure DevOps pull-request coordinates from either the modern
    /// <c>dev.azure.com</c> URL or the legacy <c>visualstudio.com</c> URL form.
    /// The result is deliberately limited to non-secret browser URL coordinates.
    /// </summary>
    public static bool TryParsePullRequestUrl(
        string? url,
        out PullRequestUrlIdentity identity)
    {
        identity = new PullRequestUrlIdentity();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!TryParseGitCoordinates(uri, out var organization, out var project, out var repository,
                out var gitSegmentIndex))
        {
            return false;
        }

        var segments = uri.Segments;
        for (var index = gitSegmentIndex + 2; index < segments.Length; index++)
        {
            if (!DecodeSegment(segments[index]).Equals(
                    "pullrequest",
                    StringComparison.OrdinalIgnoreCase)
                || index + 1 >= segments.Length)
            {
                continue;
            }

            var pullRequestId = DecodeSegment(segments[index + 1]);
            if (pullRequestId.Length == 0)
            {
                return false;
            }

            identity = new PullRequestUrlIdentity(
                organization,
                project,
                repository,
                pullRequestId);
            return true;
        }

        return false;
    }

    /// <summary>Compares browser URLs by provider coordinates rather than raw formatting.</summary>
    public static bool UrlsMatch(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        if (TryParsePullRequestUrl(first, out var firstIdentity)
            && TryParsePullRequestUrl(second, out var secondIdentity))
        {
            return string.Equals(
                       firstIdentity.Organization,
                       secondIdentity.Organization,
                       StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    firstIdentity.Project,
                    secondIdentity.Project,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    firstIdentity.Repository,
                    secondIdentity.Repository,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    firstIdentity.PullRequestId,
                    secondIdentity.PullRequestId,
                    StringComparison.OrdinalIgnoreCase);
        }

        return TryNormalizeUri(first, out var normalizedFirst)
            && TryNormalizeUri(second, out var normalizedSecond)
            && normalizedFirst.Equals(normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a managed repository profile describes the repository in a
    /// parsed pull-request URL. The profile key is intentionally never considered: it
    /// is an operator-defined alias and need not match provider URL coordinates.
    /// </summary>
    public static bool MatchesRepository(
        PullRequestUrlIdentity pullRequest,
        RepositoryProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var profileProject = Clean(profile.Project);
        var profileRepositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profileOrganizations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var remoteIdentity = Clean(profile.RemoteIdentity);
        if (remoteIdentity.Length > 0)
        {
            profileRepositories.Add(NormalizeRepositoryName(remoteIdentity));
        }

        foreach (var url in new[] { profile.WebUrl, profile.CloneUrl })
        {
            if (!TryParseGitCoordinates(
                    url,
                    out var organization,
                    out var project,
                    out var repository,
                    out _))
            {
                continue;
            }

            profileOrganizations.Add(organization);
            if (profileProject.Length == 0)
            {
                profileProject = project;
            }

            profileRepositories.Add(NormalizeRepositoryName(repository));
        }

        return profileProject.Length > 0
            && profileProject.Equals(pullRequest.Project, StringComparison.OrdinalIgnoreCase)
            && profileRepositories.Contains(pullRequest.Repository)
            && (profileOrganizations.Count == 0
                || profileOrganizations.Contains(pullRequest.Organization));
    }

    private static bool TryParseGitCoordinates(
        Uri uri,
        out string organization,
        out string project,
        out string repository,
        out int gitSegmentIndex)
    {
        organization = string.Empty;
        project = string.Empty;
        repository = string.Empty;
        gitSegmentIndex = -1;

        var segments = uri.Segments;
        for (var index = 0; index < segments.Length; index++)
        {
            if (!DecodeSegment(segments[index]).Equals("_git", StringComparison.OrdinalIgnoreCase)
                || index == 0
                || index + 1 >= segments.Length)
            {
                continue;
            }

            project = DecodeSegment(segments[index - 1]);
            repository = DecodeSegment(segments[index + 1]);
            if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                repository = repository[..^4];
            }

            if (project.Length == 0 || repository.Length == 0)
            {
                return false;
            }

            organization = ResolveOrganization(uri, segments);
            if (organization.Length == 0)
            {
                return false;
            }

            gitSegmentIndex = index;
            return true;
        }

        return false;
    }

    private static bool TryParseGitCoordinates(
        string? url,
        out string organization,
        out string project,
        out string repository,
        out int gitSegmentIndex)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            organization = string.Empty;
            project = string.Empty;
            repository = string.Empty;
            gitSegmentIndex = -1;
            return false;
        }

        return TryParseGitCoordinates(
            uri,
            out organization,
            out project,
            out repository,
            out gitSegmentIndex);
    }

    private static string ResolveOrganization(Uri uri, IReadOnlyList<string> segments)
    {
        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return segments
                .Select(DecodeSegment)
                .FirstOrDefault(segment => segment.Length > 0)
                ?? string.Empty;
        }

        const string visualStudioSuffix = ".visualstudio.com";
        if (uri.Host.EndsWith(visualStudioSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return uri.Host[..^visualStudioSuffix.Length];
        }

        return uri.Host;
    }

    private static string DecodeSegment(string segment)
    {
        var value = segment.Trim('/');
        return value.Length == 0 ? string.Empty : Uri.UnescapeDataString(value);
    }

    private static string NormalizeRepositoryName(string value)
    {
        var normalized = Uri.UnescapeDataString(Clean(value));
        return normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static bool TryNormalizeUri(string value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = string.Join(
            '/',
            uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(DecodeSegment)
        );
        path = path.TrimEnd('/');
        normalized = $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}"
            + (uri.IsDefaultPort ? string.Empty : $":{uri.Port}")
            + (path.Length == 0 ? "/" : $"/{path}");
        return true;
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}

/// <summary>Non-secret provider coordinates parsed from a pull-request browser URL.</summary>
public sealed record PullRequestUrlIdentity(
    string Organization,
    string Project,
    string Repository,
    string PullRequestId)
{
    public PullRequestUrlIdentity() : this(string.Empty, string.Empty, string.Empty, string.Empty) { }
}
