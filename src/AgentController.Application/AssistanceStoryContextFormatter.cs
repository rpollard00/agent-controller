using System.Globalization;
using System.Net;
using System.Text;
using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Inputs used to render a work item for assistance on an existing pull request.</summary>
public sealed record AssistanceStoryContextRequest
{
    /// <summary>Title of the pull request being continued.</summary>
    public string PullRequestTitle { get; init; } = string.Empty;

    /// <summary>Canonical identity and source snapshot of the pull request.</summary>
    public PullRequestReference PullRequest { get; init; } = new();

    /// <summary>One-based assistance cycle number for this pull request.</summary>
    public int CycleNumber { get; init; }

    /// <summary>
    /// Qualifying review threads. Callers supply the threads after reviewer and content
    /// filtering; the formatter renders only threads that remain unresolved.
    /// </summary>
    public IReadOnlyList<ReviewThread> QualifyingThreads { get; init; } = [];
}

/// <summary>Deterministic title and Azure DevOps rich-text description for an assistance story.</summary>
public sealed record RenderedAssistanceStoryContext(string Title, string Description);

/// <summary>
/// Pure formatter for assistance stories that direct an agent to continue an existing
/// pull request and preserve enough review-thread context to act without another lookup.
/// </summary>
public static class AssistanceStoryContextFormatter
{
    private const int MaximumTitleLength = 255;

    /// <summary>Renders a concise title and an HTML-safe Azure DevOps description.</summary>
    public static RenderedAssistanceStoryContext Render(AssistanceStoryContextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pullRequest = request.PullRequest
            ?? throw new ArgumentException(
                "A pull-request reference is required.",
                nameof(request)
            );
        var pullRequestId = RequireValue(
            pullRequest.PullRequestId,
            nameof(request.PullRequest.PullRequestId)
        );
        var repositoryKey = RequireValue(
            pullRequest.RepositoryKey,
            nameof(request.PullRequest.RepositoryKey)
        );
        var sourceBranch = RequireValue(
            pullRequest.SourceBranch,
            nameof(request.PullRequest.SourceBranch)
        );
        var targetBranch = RequireValue(
            pullRequest.TargetBranch,
            nameof(request.PullRequest.TargetBranch)
        );
        var sourceCommit = RequireValue(
            pullRequest.SourceCommitSha,
            nameof(request.PullRequest.SourceCommitSha)
        );
        var pullRequestUrl = RequireHttpUrl(
            pullRequest.PullRequestUrl,
            nameof(request.PullRequest.PullRequestUrl)
        );
        if (request.CycleNumber < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.CycleNumber,
                "The assistance cycle number must be at least one."
            );
        }

        var pullRequestTitle = NormalizeSingleLine(request.PullRequestTitle);
        var storyTitle = TruncateTitle(
            pullRequestTitle.Length == 0
                ? $"Assist PR {pullRequestId}"
                : $"Assist PR {pullRequestId}: {pullRequestTitle}"
        );
        var unresolvedThreads = (request.QualifyingThreads ?? [])
            .Where(thread => thread.Status == ReviewThreadStatus.Active)
            .OrderBy(ThreadFileSortKey, StringComparer.Ordinal)
            .ThenBy(thread => thread.StartLine ?? int.MinValue)
            .ThenBy(thread => thread.EndLine ?? int.MinValue)
            .ThenBy(thread => thread.ThreadId, StringComparer.Ordinal)
            .ToList();

        var description = new StringBuilder();
        description.Append(
            "<p><strong>Existing pull request assistance.</strong> Continue the existing "
            + "pull request and update its source branch. Do not create or open another "
            + "pull request.</p>\n"
        );
        description.Append("<h2>Pull request context</h2>\n<ul>\n");
        description.Append("<li><strong>Pull request:</strong> <a href=\"")
            .Append(Html(pullRequestUrl))
            .Append("\">PR ")
            .Append(Html(pullRequestId));
        if (pullRequestTitle.Length > 0)
        {
            description.Append(" — ").Append(Html(pullRequestTitle));
        }

        description.Append("</a></li>\n");
        AppendCodeReference(description, "Repository", repositoryKey);
        AppendCodeReference(description, "Source branch", sourceBranch);
        AppendCodeReference(description, "Target branch", targetBranch);
        AppendCodeReference(description, "Observed source commit", sourceCommit);
        AppendCodeReference(
            description,
            "Assistance cycle",
            request.CycleNumber.ToString(CultureInfo.InvariantCulture)
        );
        description.Append("</ul>\n<h2>Requested work</h2>\n");

        if (unresolvedThreads.Count == 0)
        {
            description.Append(
                "<p>No qualifying unresolved review threads were included. Review and clean "
                + "up the existing pull request, address evident issues, and leave the same "
                + "pull request in a merge-ready state.</p>"
            );
        }
        else
        {
            description.Append(
                "<p>Address the qualifying unresolved review threads below. Preserve the "
                + "existing pull request and its source branch while making the updates.</p>\n"
            );
            description.Append("<h2>Unresolved review threads (")
                .Append(unresolvedThreads.Count.ToString(CultureInfo.InvariantCulture))
                .Append(")</h2>\n");

            foreach (var thread in unresolvedThreads)
            {
                AppendThread(description, thread);
            }
        }

        return new RenderedAssistanceStoryContext(storyTitle, description.ToString());
    }

    private static void AppendCodeReference(StringBuilder description, string label, string value)
    {
        description.Append("<li><strong>")
            .Append(label)
            .Append(":</strong> <code>")
            .Append(Html(value))
            .Append("</code></li>\n");
    }

    private static void AppendThread(StringBuilder description, ReviewThread thread)
    {
        var threadId = string.IsNullOrWhiteSpace(thread.ThreadId)
            ? "(unavailable)"
            : thread.ThreadId.Trim();

        description.Append("<h3>")
            .Append(RenderLocation(thread))
            .Append(" — thread <code>")
            .Append(Html(threadId))
            .Append("</code></h3>\n");

        if (thread.Comments.Count == 0)
        {
            description.Append("<p><em>No comment text was supplied for this thread.</em></p>\n");
            return;
        }

        description.Append("<ol>\n");
        foreach (var comment in thread.Comments)
        {
            var author = string.IsNullOrWhiteSpace(comment.Author)
                ? "(unknown reviewer)"
                : comment.Author.Trim();
            var timestamp = comment.CreatedAt.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture
            );

            description.Append("<li><p><strong>")
                .Append(comment.IsReply ? "Reply from " : "Comment from ")
                .Append(Html(author))
                .Append("</strong> at <code>")
                .Append(timestamp)
                .Append("</code></p>");
            if (string.IsNullOrEmpty(comment.Body))
            {
                description.Append("<p><em>(empty comment)</em></p>");
            }
            else
            {
                description.Append("<pre>").Append(Html(comment.Body)).Append("</pre>");
            }

            description.Append("</li>\n");
        }

        description.Append("</ol>\n");
    }

    private static string RenderLocation(ReviewThread thread)
    {
        if (string.IsNullOrWhiteSpace(thread.FilePath))
        {
            return "Pull request";
        }

        var location = new StringBuilder("<code>")
            .Append(Html(thread.FilePath.Trim()))
            .Append("</code>");
        if (thread.StartLine is not int startLine)
        {
            location.Append(" (file)");
            return location.ToString();
        }

        if (thread.EndLine is int endLine && endLine != startLine)
        {
            location.Append(", lines ")
                .Append(startLine.ToString(CultureInfo.InvariantCulture))
                .Append('–')
                .Append(endLine.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            location.Append(", line ")
                .Append(startLine.ToString(CultureInfo.InvariantCulture));
        }

        return location.ToString();
    }

    private static string ThreadFileSortKey(ReviewThread thread) =>
        string.IsNullOrWhiteSpace(thread.FilePath) ? string.Empty : thread.FilePath.Trim();

    private static string NormalizeSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var result = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    result.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            if (!char.IsControl(character))
            {
                result.Append(character);
                previousWasWhitespace = false;
            }
        }

        return result.ToString().Trim();
    }

    private static string TruncateTitle(string title)
    {
        if (title.Length <= MaximumTitleLength)
        {
            return title;
        }

        var contentLength = MaximumTitleLength - 1;
        if (char.IsHighSurrogate(title[contentLength - 1]))
        {
            contentLength--;
        }

        return title[..contentLength].TrimEnd() + "…";
    }

    private static string RequireValue(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A nonempty value is required.", parameterName);
        }

        return value.Trim();
    }

    private static string RequireHttpUrl(string? value, string parameterName)
    {
        var cleaned = RequireValue(value, parameterName);
        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "An absolute HTTP or HTTPS pull-request URL is required.",
                parameterName
            );
        }

        return uri.AbsoluteUri;
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}
