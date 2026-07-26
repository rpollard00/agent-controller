namespace AgentController.Application;

/// <summary>Rendered assistance comment content and its provider-neutral idempotency marker.</summary>
internal sealed record RenderedAssistanceLifecycleComment(
    string Content,
    string IdempotencyMarker
);

/// <summary>Creates deterministic, retry-detectable assistance lifecycle comments.</summary>
internal static class AssistanceLifecycleCommentRenderer
{
    public static RenderedAssistanceLifecycleComment Render(
        AssistanceLifecycleCommentRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var correlationId = RequireValue(request.CorrelationId, nameof(request.CorrelationId));
        var storyId = RequireValue(request.AssistanceStoryId, nameof(request.AssistanceStoryId));
        var storyUrl = RequireStoryUrl(
            request.AssistanceStoryUrl,
            nameof(request.AssistanceStoryUrl)
        );
        if (!Enum.IsDefined(request.Kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Kind,
                "The assistance lifecycle comment kind is not supported."
            );
        }

        var kind = request.Kind switch
        {
            AssistanceLifecycleCommentKind.Queued => "queued",
            AssistanceLifecycleCommentKind.Completed => "completed",
            AssistanceLifecycleCommentKind.Failed => "failed",
            AssistanceLifecycleCommentKind.NeedsHuman => "needs-human",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        var marker = $"<!-- agent-controller:assistance:{kind}:"
            + $"{Uri.EscapeDataString(correlationId)} -->";
        var storyLink = $"[User Story {EscapeMarkdownLabel(storyId)}](<{storyUrl}>)";
        var message = request.Kind switch
        {
            AssistanceLifecycleCommentKind.Queued =>
                $"Agent assistance is queued in {storyLink}. The agent will continue work on this pull request.",
            AssistanceLifecycleCommentKind.Completed =>
                $"Agent assistance from {storyLink} completed. This pull request has been updated.",
            AssistanceLifecycleCommentKind.Failed =>
                $"Agent assistance from {storyLink} failed. The assistance request remains open for retry.",
            AssistanceLifecycleCommentKind.NeedsHuman =>
                $"Agent assistance from {storyLink} needs human attention. The assistance request remains open.",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        return new RenderedAssistanceLifecycleComment(
            $"{message}{Environment.NewLine}{Environment.NewLine}{marker}",
            marker
        );
    }

    private static string RequireValue(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A nonempty value is required.", parameterName);
        }

        return value.Trim();
    }

    private static string RequireStoryUrl(string? value, string parameterName)
    {
        var cleaned = RequireValue(value, parameterName);
        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "An absolute HTTP or HTTPS assistance story URL is required.",
                parameterName
            );
        }

        return uri.AbsoluteUri;
    }

    private static string EscapeMarkdownLabel(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal);
}
