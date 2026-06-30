using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// <see cref="IFeedbackSource"/> implementation that returns deterministic
/// <see cref="ReworkSignal"/> instances from the <c>localFeedback</c> configuration
/// section.
///
/// Mirrors <see cref="LocalFileWorkSource"/>: definitions are validated and cached
/// on first use, then returned for any <see cref="PrUnderTest"/> whose
/// <see cref="PrUnderTest.PullRequestId"/> matches a configured signal.
///
/// This enables the entire feedback pipeline (polling worker, filter pipeline,
/// soak window, materialization, rework consumption) to run offline end to end
/// without requiring an Azure DevOps connection.
///
/// Registered as a singleton via
/// <see cref="AgentControllerServiceCollectionExtensions.AddAgentControllerLocalFeedbackSource"/>.
/// </summary>
internal sealed partial class LocalFeedbackSource : IFeedbackSource, IDisposable
{
    private readonly IOptionsMonitor<LocalFeedbackOptions> _options;
    private readonly ILogger<LocalFeedbackSource> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <summary>
    /// Validated and cached signal definitions, keyed by PullRequestId.
    /// Populated on first poll call.
    /// </summary>
    private Dictionary<string, ReworkSignal> _signals = new(StringComparer.Ordinal);

    public LocalFeedbackSource(
        IOptionsMonitor<LocalFeedbackOptions> options,
        ILogger<LocalFeedbackSource> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ReworkSignal>> PollAsync(
        FeedbackQuery query,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (query.OpenPrs.Count == 0)
        {
            return [];
        }

        var signals = new List<ReworkSignal>();

        foreach (var pr in query.OpenPrs)
        {
            if (_signals.TryGetValue(pr.PullRequestId, out var signal))
            {
                signals.Add(signal);
                Log.SignalMatched(_logger, pr.PullRequestId, signal.Threads.Count);
            }
        }

        return signals;
    }

    /// <summary>
    /// Lazily validate and cache signal definitions from configuration.
    /// Thread-safe; initialization happens exactly once across all callers.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var definitions = _options.CurrentValue.Signals;
            if (definitions.Count == 0)
            {
                Log.NoLocalFeedbackSignals(_logger);
                _initialized = true;
                return;
            }

            var signals = new Dictionary<string, ReworkSignal>(StringComparer.Ordinal);
            var valid = 0;
            var skipped = 0;

            foreach (var def in definitions)
            {
                ct.ThrowIfCancellationRequested();

                // Validate required fields
                if (string.IsNullOrWhiteSpace(def.PullRequestId))
                {
                    Log.SkippingSignalMissingPullRequestId(_logger, def.OriginatingRunId ?? "(no runId)");
                    skipped++;
                    continue;
                }

                if (def.Threads.Count == 0)
                {
                    Log.SkippingSignalNoThreads(_logger, def.PullRequestId);
                    skipped++;
                    continue;
                }

                // Derive originatingRunId when not explicitly provided
                var originatingRunId = !string.IsNullOrWhiteSpace(def.OriginatingRunId)
                    ? def.OriginatingRunId
                    : $"local-run-{def.PullRequestId}";

                // Map thread definitions to domain ReviewThread records
                var threads = new List<ReviewThread>();
                foreach (var threadDef in def.Threads)
                {
                    if (string.IsNullOrWhiteSpace(threadDef.ThreadId))
                    {
                        Log.SkippingThreadMissingThreadId(_logger, def.PullRequestId);
                        continue;
                    }

                    var comments = new List<ReviewThreadComment>();
                    foreach (var commentDef in threadDef.Comments)
                    {
                        comments.Add(new ReviewThreadComment
                        {
                            Author = commentDef.Author,
                            Body = commentDef.Body,
                            CreatedAt = ParseCreatedAt(commentDef.CreatedAt),
                            IsReply = commentDef.IsReply,
                        });
                    }

                    threads.Add(new ReviewThread
                    {
                        ThreadId = threadDef.ThreadId,
                        Author = threadDef.Author,
                        CreatedAt = ParseCreatedAt(threadDef.CreatedAt),
                        Status = ParseThreadStatus(threadDef.Status),
                        FilePath = string.IsNullOrWhiteSpace(threadDef.FilePath) ? null : threadDef.FilePath,
                        StartLine = threadDef.StartLine,
                        EndLine = threadDef.EndLine,
                        IsFileLevel = threadDef.IsFileLevel,
                        Comments = comments,
                    });
                }

                if (threads.Count == 0)
                {
                    Log.SkippingSignalNoValidThreads(_logger, def.PullRequestId);
                    skipped++;
                    continue;
                }

                // Compute qualifying comment timestamps across all threads
                var allComments = threads.SelectMany(t => t.Comments).ToList();
                var firstQualifyingCommentAt = allComments.Count > 0
                    ? allComments.Min(c => c.CreatedAt)
                    : DateTimeOffset.UtcNow;

                var lastQualifyingCommentAt = allComments.Count > 0
                    ? allComments.Max(c => c.CreatedAt)
                    : DateTimeOffset.UtcNow;

                signals[def.PullRequestId] = new ReworkSignal
                {
                    OriginatingRunId = originatingRunId,
                    PullRequestId = def.PullRequestId,
                    Threads = threads,
                    FirstQualifyingCommentAt = firstQualifyingCommentAt,
                    LastQualifyingCommentAt = lastQualifyingCommentAt,
                };

                valid++;
                Log.SignalCached(_logger, def.PullRequestId, threads.Count);
            }

            _signals = signals;
            Log.InitializationComplete(_logger, valid, skipped, definitions.Count);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Parse an ISO 8601 date-time string. Returns <c>DateTimeOffset.UtcNow</c>
    /// when the string is null, empty, or unparseable.
    /// </summary>
    private static DateTimeOffset ParseCreatedAt(string? createdAt)
    {
        if (!string.IsNullOrWhiteSpace(createdAt)
            && DateTimeOffset.TryParse(createdAt, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Parse a thread status string into <see cref="ReviewThreadStatus"/>.
    /// Defaults to <see cref="ReviewThreadStatus.Active"/> for unknown values.
    /// </summary>
    private static ReviewThreadStatus ParseThreadStatus(string status)
    {
        return status switch
        {
            "Active" => ReviewThreadStatus.Active,
            "Resolved" => ReviewThreadStatus.Resolved,
            "Fixed" => ReviewThreadStatus.Fixed,
            "WontFix" => ReviewThreadStatus.WontFix,
            "Closed" => ReviewThreadStatus.Closed,
            "ByDesign" => ReviewThreadStatus.ByDesign,
            _ => ReviewThreadStatus.Active,
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _initLock.Dispose();
    }

    /// <summary>
    /// Source-generated high-performance logger methods.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "No local feedback signal definitions configured in 'localFeedback:signals'. " +
                      "The feedback source will not return any rework signals.")]
        public static partial void NoLocalFeedbackSignals(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping local feedback signal: missing pullRequestId. OriginatingRunId: {OriginatingRunId}")]
        public static partial void SkippingSignalMissingPullRequestId(
            ILogger logger, string originatingRunId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping local feedback signal: no threads defined. PullRequestId: {PullRequestId}")]
        public static partial void SkippingSignalNoThreads(
            ILogger logger, string pullRequestId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping local feedback thread: missing threadId. PullRequestId: {PullRequestId}")]
        public static partial void SkippingThreadMissingThreadId(
            ILogger logger, string pullRequestId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Skipping local feedback signal: no valid threads after filtering. PullRequestId: {PullRequestId}")]
        public static partial void SkippingSignalNoValidThreads(
            ILogger logger, string pullRequestId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Cached local feedback signal for PR {PullRequestId} with {ThreadCount} thread(s)")]
        public static partial void SignalCached(
            ILogger logger, string pullRequestId, int threadCount);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "LocalFeedbackSource initialization complete: {ValidCount} valid, " +
                      "{SkippedCount} skipped (of {TotalCount} total definitions).")]
        public static partial void InitializationComplete(
            ILogger logger, int validCount, int skippedCount, int totalCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Local feedback source matched PR {PullRequestId} with {ThreadCount} thread(s)")]
        public static partial void SignalMatched(
            ILogger logger, string pullRequestId, int threadCount);
    }
}
