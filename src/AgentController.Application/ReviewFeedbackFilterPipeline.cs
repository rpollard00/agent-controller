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
    /// Apply the full 5-step filter pipeline to raw revival signals.
    /// Returns signals with only surviving threads; signals with zero surviving
    /// threads are dropped from the result.
    /// </summary>
    public async Task<IReadOnlyList<ReworkSignal>> FilterAsync(
        FeedbackQuery query,
        IReadOnlyList<ReworkSignal> rawSignals,
        CancellationToken cancellationToken)
    {
        var evaluations = await EvaluateCoreAsync(
            query,
            rawSignals,
            applyMarkerGate: true,
            preserveEmptySignals: false,
            cancellationToken
        );

        return evaluations
            .Where(evaluation => evaluation.FilteredSignal is not null)
            .Select(evaluation => evaluation.FilteredSignal!)
            .ToList();
    }

    /// <summary>
    /// Produce body-free check traces using the exact Revival policy used by
    /// <see cref="FilterAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<ReviewFeedbackCheckTrace>> TraceAsync(
        FeedbackQuery query,
        IReadOnlyList<ReworkSignal> rawSignals,
        CancellationToken cancellationToken)
    {
        var evaluations = await EvaluateCoreAsync(
            query,
            rawSignals,
            applyMarkerGate: true,
            preserveEmptySignals: false,
            cancellationToken
        );
        return evaluations.Select(evaluation => evaluation.Trace).ToList();
    }

    /// <summary>
    /// Filter review threads included in assistance requests discovered from managed
    /// pull requests. Discovery has already applied the assistance-marker gate, so this
    /// path reuses reviewer, status, and content filtering while retaining the request
    /// when no thread qualifies. An empty reviewer allowlist therefore produces an empty
    /// bundle rather than suppressing the label-driven assistance request.
    /// </summary>
    public async Task<IReadOnlyList<ReworkSignal>> FilterAssistanceAsync(
        FeedbackQuery query,
        IReadOnlyList<ReworkSignal> rawSignals,
        CancellationToken cancellationToken)
    {
        var evaluations = await EvaluateCoreAsync(
            query,
            rawSignals,
            applyMarkerGate: false,
            preserveEmptySignals: true,
            cancellationToken
        );

        return evaluations.Select(evaluation => evaluation.FilteredSignal!).ToList();
    }

    /// <summary>
    /// Produce body-free check traces using the exact Assistance policy used by
    /// <see cref="FilterAssistanceAsync"/>. The request marker is already validated by
    /// managed pull-request discovery, and zero qualifying comments remain valid.
    /// </summary>
    public async Task<IReadOnlyList<ReviewFeedbackCheckTrace>> TraceAssistanceAsync(
        FeedbackQuery query,
        IReadOnlyList<ReworkSignal> rawSignals,
        CancellationToken cancellationToken)
    {
        var evaluations = await EvaluateCoreAsync(
            query,
            rawSignals,
            applyMarkerGate: false,
            preserveEmptySignals: true,
            cancellationToken
        );
        return evaluations.Select(evaluation => evaluation.Trace).ToList();
    }

    private async Task<IReadOnlyList<FeedbackEvaluation>> EvaluateCoreAsync(
        FeedbackQuery query,
        IReadOnlyList<ReworkSignal> rawSignals,
        bool applyMarkerGate,
        bool preserveEmptySignals,
        CancellationToken cancellationToken)
    {
        if (rawSignals.Count == 0)
        {
            return [];
        }

        var hasAllowedReviewers = query.AllowedReviewers.Count > 0;
        if (!hasAllowedReviewers)
        {
            await LogAllowlistEmptyWarningAsync();
            if (!preserveEmptySignals)
            {
                return rawSignals.Select(signal => new FeedbackEvaluation(
                    null,
                    CreateTrace(
                        signal,
                        FeedbackMarkerCheckStatus.NotAttempted,
                        hasAllowedReviewers,
                        activeThreadCount: signal.Threads.Count(thread =>
                            thread.Status == ReviewThreadStatus.Active),
                        allowlistedReviewerThreadCount: 0,
                        nonEmptyContentThreadCount: 0,
                        qualifyingThreadCount: 0,
                        isAccepted: false)))
                    .ToList();
            }
        }

        var evaluations = new List<FeedbackEvaluation>();

        foreach (var signal in rawSignals)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (applyMarkerGate)
            {
                // Find the matching PrUnderTest for this signal (needed for label fetch).
                var pr = query.OpenPrs.FirstOrDefault(candidate =>
                    candidate.PullRequestId == signal.PullRequestId
                );

                if (pr is null)
                {
                    // Signal without a matching PR — skip (shouldn't happen).
                    Log.SignalWithoutMatchingPr(_logger, signal.PullRequestId);
                    evaluations.Add(new FeedbackEvaluation(
                        null,
                        CreateRejectedTrace(
                            signal,
                            FeedbackMarkerCheckStatus.PullRequestNotFound,
                            query.AllowedReviewers)));
                    continue;
                }

                // ── (1) Marker gate ───────────────────────────────
                var markerStatus = await CheckMarkerGateAsync(pr, query, cancellationToken);
                if (markerStatus != FeedbackMarkerCheckStatus.Present)
                {
                    Log.PrFailedMarkerGate(_logger, signal.PullRequestId, pr.PullRequestUrl);
                    evaluations.Add(new FeedbackEvaluation(
                        null,
                        CreateRejectedTrace(
                            signal,
                            markerStatus,
                            query.AllowedReviewers)));
                    continue;
                }
            }

            // ── (3) Thread-status filter ──────────────────────────
            var activeThreads = signal.Threads
                .Where(t => t.Status == ReviewThreadStatus.Active)
                .ToList();

            if (activeThreads.Count != signal.Threads.Count)
            {
                Log.ThreadsFilteredByStatus(
                    _logger,
                    signal.PullRequestId,
                    signal.Threads.Count,
                    activeThreads.Count
                );
            }

            if (activeThreads.Count == 0)
            {
                Log.PrNoActiveThreads(_logger, signal.PullRequestId);
            }

            // ── (4) Thread-author filter ──────────────────────────
            var authorFilteredThreads = hasAllowedReviewers
                ? activeThreads
                    .Where(t => HasCommentByAllowedReviewer(t, query.AllowedReviewers))
                    .ToList()
                : [];

            if (authorFilteredThreads.Count != activeThreads.Count)
            {
                Log.ThreadsFilteredByAuthor(
                    _logger,
                    signal.PullRequestId,
                    activeThreads.Count,
                    authorFilteredThreads.Count
                );
            }

            if (activeThreads.Count > 0 && authorFilteredThreads.Count == 0)
            {
                Log.PrNoThreadsByAllowedReviewer(_logger, signal.PullRequestId);
            }

            // ── (5) Comment-content filter ────────────────────────
            var contentFilteredThreads = authorFilteredThreads
                .Where(HasNonEmptyComment)
                .ToList();

            if (contentFilteredThreads.Count != authorFilteredThreads.Count)
            {
                Log.ThreadsFilteredByContent(
                    _logger,
                    signal.PullRequestId,
                    authorFilteredThreads.Count,
                    contentFilteredThreads.Count
                );
            }

            if (authorFilteredThreads.Count > 0 && contentFilteredThreads.Count == 0)
            {
                Log.PrNoThreadsWithContent(_logger, signal.PullRequestId);
            }

            if (contentFilteredThreads.Count == 0 && !preserveEmptySignals)
            {
                evaluations.Add(new FeedbackEvaluation(
                    null,
                    CreateTrace(
                        signal,
                        FeedbackMarkerCheckStatus.Present,
                        hasAllowedReviewers,
                        activeThreads.Count,
                        authorFilteredThreads.Count,
                        contentFilteredThreads.Count,
                        qualifyingThreadCount: 0,
                        isAccepted: false)));
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

            var filteredSignal = signal with
            {
                Threads = contentFilteredThreads,
                FirstQualifyingCommentAt = firstQualifyingCommentAt,
                LastQualifyingCommentAt = lastQualifyingCommentAt,
            };
            evaluations.Add(new FeedbackEvaluation(
                filteredSignal,
                CreateTrace(
                    signal,
                    applyMarkerGate
                        ? FeedbackMarkerCheckStatus.Present
                        : FeedbackMarkerCheckStatus.AlreadyValidated,
                    hasAllowedReviewers,
                    activeThreads.Count,
                    authorFilteredThreads.Count,
                    contentFilteredThreads.Count,
                    contentFilteredThreads.Count,
                    isAccepted: true)));

            if (contentFilteredThreads.Count > 0)
            {
                Log.PrPassedAllFilters(
                    _logger,
                    signal.PullRequestId,
                    contentFilteredThreads.Count
                );
            }
        }

        return evaluations;
    }

    private static ReviewFeedbackCheckTrace CreateRejectedTrace(
        ReworkSignal signal,
        FeedbackMarkerCheckStatus markerStatus,
        IReadOnlySet<string> allowedReviewers)
    {
        var activeThreads = signal.Threads
            .Where(thread => thread.Status == ReviewThreadStatus.Active)
            .ToList();
        var reviewerThreads = activeThreads
            .Where(thread => HasCommentByAllowedReviewer(thread, allowedReviewers))
            .ToList();
        var contentThreads = reviewerThreads.Count == 0
            ? []
            : reviewerThreads.Where(HasNonEmptyComment).ToList();

        return CreateTrace(
            signal,
            markerStatus,
            allowedReviewers.Count > 0,
            activeThreads.Count,
            reviewerThreads.Count,
            contentThreads.Count,
            qualifyingThreadCount: 0,
            isAccepted: false);
    }

    private static ReviewFeedbackCheckTrace CreateTrace(
        ReworkSignal signal,
        FeedbackMarkerCheckStatus markerStatus,
        bool reviewerAllowlistConfigured,
        int activeThreadCount,
        int allowlistedReviewerThreadCount,
        int nonEmptyContentThreadCount,
        int qualifyingThreadCount,
        bool isAccepted)
    {
        return new ReviewFeedbackCheckTrace
        {
            PullRequestId = signal.PullRequestId,
            MarkerStatus = markerStatus,
            ReviewerAllowlistConfigured = reviewerAllowlistConfigured,
            TotalThreadCount = signal.Threads.Count,
            ActiveThreadCount = activeThreadCount,
            AllowlistedReviewerThreadCount = allowlistedReviewerThreadCount,
            NonEmptyContentThreadCount = nonEmptyContentThreadCount,
            QualifyingThreadCount = qualifyingThreadCount,
            IsAccepted = isAccepted,
        };
    }

    private sealed record FeedbackEvaluation(
        ReworkSignal? FilteredSignal,
        ReviewFeedbackCheckTrace Trace);

    /// <summary>
    /// Marker gate: fetch PR labels, require the rework marker label.
    /// Fail-closed per-PR.
    /// </summary>
    private async Task<FeedbackMarkerCheckStatus> CheckMarkerGateAsync(
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
                return FeedbackMarkerCheckStatus.Missing;
            }

            return FeedbackMarkerCheckStatus.Present;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Label fetch failure — fail-closed per-PR.
            Log.MarkerGateFetchFailed(_logger, pr.PullRequestId, ex);
            return FeedbackMarkerCheckStatus.FetchFailed;
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
