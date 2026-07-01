using AgentController.Domain;
using Microsoft.Extensions.Logging;

namespace AgentController.Application;

/// <summary>
/// Applies the load-bearing feedback filter pipeline in exact order:
/// <list type="number">
///   <item><term>Marker gate</term>
///   <description>Fetch PR labels, require <c>agent-rework-requested</c> label.
///   Fail-closed per-PR.</description></item>
///   <item><term>Allowlist fail-closed</term>
///   <description>If AllowedReviewers is empty, log warning once at startup and
///   return nothing.</description></item>
///   <item><term>Thread-status filter</term>
///   <description>Keep only Active status threads.</description></item>
///   <item><term>Thread-author filter</term>
///   <description>Keep threads where at least one comment in the reply chain is by
///   an allowed reviewer.</description></item>
///   <item><term>Comment-content filter</term>
///   <description>Drop threads whose entire reply chain is empty/whitespace.</description></item>
/// </list>
///
/// AllowedReviewers is resolved once per poll; canonical key is uniqueName (email).
/// </summary>
public sealed partial class ReviewFeedbackFilterPipeline : IDisposable
{
    private readonly IPrLabelSource _labelSource;
    private readonly ILogger<ReviewFeedbackFilterPipeline> _logger;
    private readonly SemaphoreSlim _allowlistWarningLock = new(1, 1);
    private bool _allowlistWarningLogged;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReviewFeedbackFilterPipeline"/> class.
    /// </summary>
    public ReviewFeedbackFilterPipeline(
        IPrLabelSource labelSource,
        ILogger<ReviewFeedbackFilterPipeline> logger)
    {
        _labelSource = labelSource;
        _logger = logger;
    }

    /// <summary>
    /// Apply the full 5-step filter pipeline to raw rework signals.
    /// Returns signals with only surviving threads; signals with zero surviving
    /// threads are dropped from the result.
    /// </summary>
    public async Task<IReadOnlyList<ReworkSignal>> FilterAsync(
        FeedbackQuery query,
        IReadOnlyList<ReworkSignal> rawSignals,
        CancellationToken cancellationToken)
    {
        // ── (2) Allowlist fail-closed ─────────────────────────────
        // Check once at startup; if empty, log warning and return nothing.
        if (query.AllowedReviewers.Count == 0)
        {
            await LogAllowlistEmptyWarningAsync();
            return [];
        }

        if (rawSignals.Count == 0)
        {
            return [];
        }

        var filteredSignals = new List<ReworkSignal>();

        foreach (var signal in rawSignals)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Find the matching PrUnderTest for this signal (needed for label fetch).
            var pr = query.OpenPrs
                .FirstOrDefault(p => p.PullRequestId == signal.PullRequestId);

            if (pr is null)
            {
                // Signal without a matching PR — skip (shouldn't happen).
                Log.SignalWithoutMatchingPr(_logger, signal.PullRequestId);
                continue;
            }

            // ── (1) Marker gate ───────────────────────────────────
            if (!await PassMarkerGateAsync(pr, query, cancellationToken))
            {
                Log.PrFailedMarkerGate(_logger, signal.PullRequestId, pr.PullRequestUrl);
                continue;
            }

            // ── (3) Thread-status filter ──────────────────────────
            var activeThreads = signal.Threads
                .Where(t => t.Status == ReviewThreadStatus.Active)
                .ToList();

            if (activeThreads.Count != signal.Threads.Count)
            {
                Log.ThreadsFilteredByStatus(
                    _logger, signal.PullRequestId, signal.Threads.Count, activeThreads.Count);
            }

            if (activeThreads.Count == 0)
            {
                Log.PrNoActiveThreads(_logger, signal.PullRequestId);
                continue;
            }

            // ── (4) Thread-author filter ──────────────────────────
            var authorFilteredThreads = activeThreads
                .Where(t => HasCommentByAllowedReviewer(t, query.AllowedReviewers))
                .ToList();

            if (authorFilteredThreads.Count != activeThreads.Count)
            {
                Log.ThreadsFilteredByAuthor(
                    _logger, signal.PullRequestId, activeThreads.Count, authorFilteredThreads.Count);
            }

            if (authorFilteredThreads.Count == 0)
            {
                Log.PrNoThreadsByAllowedReviewer(_logger, signal.PullRequestId);
                continue;
            }

            // ── (5) Comment-content filter ────────────────────────
            var contentFilteredThreads = authorFilteredThreads
                .Where(t => HasNonEmptyComment(t))
                .ToList();

            if (contentFilteredThreads.Count != authorFilteredThreads.Count)
            {
                Log.ThreadsFilteredByContent(
                    _logger, signal.PullRequestId, authorFilteredThreads.Count, contentFilteredThreads.Count);
            }

            if (contentFilteredThreads.Count == 0)
            {
                Log.PrNoThreadsWithContent(_logger, signal.PullRequestId);
                continue;
            }

            // Rebuild timestamps from surviving threads.
            var survivingComments = contentFilteredThreads
                .SelectMany(t => t.Comments)
                .ToList();

            var firstQualifyingCommentAt = survivingComments.Count > 0
                ? survivingComments.Min(c => c.CreatedAt)
                : signal.FirstQualifyingCommentAt;

            var lastQualifyingCommentAt = survivingComments.Count > 0
                ? survivingComments.Max(c => c.CreatedAt)
                : signal.LastQualifyingCommentAt;

            filteredSignals.Add(new ReworkSignal
            {
                OriginatingRunId = signal.OriginatingRunId,
                PullRequestId = signal.PullRequestId,
                Threads = contentFilteredThreads,
                FirstQualifyingCommentAt = firstQualifyingCommentAt,
                LastQualifyingCommentAt = lastQualifyingCommentAt,
            });

            Log.PrPassedAllFilters(
                _logger, signal.PullRequestId, contentFilteredThreads.Count);
        }

        return filteredSignals;
    }

    /// <summary>
    /// Marker gate: fetch PR labels, require the rework marker label.
    /// Fail-closed per-PR.
    /// </summary>
    private async Task<bool> PassMarkerGateAsync(
        PrUnderTest pr,
        FeedbackQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var labels = await _labelSource.GetLabelsAsync(pr, cancellationToken);

            // Find the marker label.
            var markerLabel = labels
                .FirstOrDefault(l => l.Name.Equals(query.ReworkMarkerTag, StringComparison.Ordinal));

            if (markerLabel is null)
            {
                // No marker label — fail-closed.
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Label fetch failure — fail-closed per-PR.
            Log.MarkerGateFetchFailed(_logger, pr.PullRequestId, ex);
            return false;
        }
    }

    /// <summary>
    /// Check whether at least one comment in the thread's reply chain is by an allowed reviewer.
    /// </summary>
    private static bool HasCommentByAllowedReviewer(
        ReviewThread thread,
        IReadOnlySet<string> allowedReviewers)
    {
        foreach (var comment in thread.Comments)
        {
            if (allowedReviewers.Contains(comment.Author))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Check whether the thread has at least one non-empty, non-whitespace comment.
    /// </summary>
    private static bool HasNonEmptyComment(ReviewThread thread)
    {
        foreach (var comment in thread.Comments)
        {
            if (!string.IsNullOrWhiteSpace(comment.Body))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Log the "empty allowlist" warning exactly once across all poll cycles.
    /// </summary>
    private async Task LogAllowlistEmptyWarningAsync()
    {
        await _allowlistWarningLock.WaitAsync(CancellationToken.None);
        try
        {
            if (!_allowlistWarningLogged)
            {
                Log.AllowlistEmpty(_logger);
                _allowlistWarningLogged = true;
            }
        }
        finally
        {
            _allowlistWarningLock.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _allowlistWarningLock.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Source-generated high-performance logger methods.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Feedback allowedReviewers is empty. All feedback will be rejected " +
                      "(fail-closed). Configure 'feedback:allowedReviewers' with reviewer emails.")]
        public static partial void AllowlistEmpty(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "PR {PullRequestId} ({PullRequestUrl}) failed marker gate: " +
                      "missing rework marker label or label not created by allowed reviewer.")]
        public static partial void PrFailedMarkerGate(
            ILogger logger, string pullRequestId, string pullRequestUrl);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: {OriginalCount} -> {SurvivingCount} threads after status filter.")]
        public static partial void ThreadsFilteredByStatus(
            ILogger logger, string pullRequestId, int originalCount, int survivingCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: {OriginalCount} -> {SurvivingCount} threads after author filter.")]
        public static partial void ThreadsFilteredByAuthor(
            ILogger logger, string pullRequestId, int originalCount, int survivingCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: {OriginalCount} -> {SurvivingCount} threads after content filter.")]
        public static partial void ThreadsFilteredByContent(
            ILogger logger, string pullRequestId, int originalCount, int survivingCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: no active threads after status filter.")]
        public static partial void PrNoActiveThreads(ILogger logger, string pullRequestId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: no threads by allowed reviewer after author filter.")]
        public static partial void PrNoThreadsByAllowedReviewer(
            ILogger logger, string pullRequestId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "PR {PullRequestId}: no threads with content after content filter.")]
        public static partial void PrNoThreadsWithContent(ILogger logger, string pullRequestId);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "PR {PullRequestId}: {ThreadCount} thread(s) passed all filters.")]
        public static partial void PrPassedAllFilters(
            ILogger logger, string pullRequestId, int threadCount);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to fetch labels for PR {PullRequestId} — failing closed.")]
        public static partial void MarkerGateFetchFailed(
            ILogger logger, string pullRequestId, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "ReworkSignal for PR {PullRequestId} has no matching PrUnderTest — skipping.")]
        public static partial void SignalWithoutMatchingPr(
            ILogger logger, string pullRequestId);
    }
}
