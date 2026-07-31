using AgentController.Application;
using AgentController.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Application.Tests;

public class ReviewFeedbackFilterPipelineTests
{
    private static ReviewFeedbackFilterPipeline CreatePipeline(IPrLabelSource labelSource)
    {
        return new ReviewFeedbackFilterPipeline(
            labelSource,
            NullLogger<ReviewFeedbackFilterPipeline>.Instance);
    }

    // ── Allowlist fail-closed ──────────────────────────────────────

    [Fact]
    public async Task FilterAsync_EmptyAllowlist_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource([]));
        var query = new FeedbackQuery
        {
            OpenPrs = [],
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    new()
                    {
                        ThreadId = "t1",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "a@example.com", Body = "fix this" },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FilterAssistanceAsync_NoQualifyingThreads_PreservesRequest()
    {
        var pipeline = CreatePipeline(new FailingPrLabelSource());
        var observedAt = DateTimeOffset.UtcNow;
        var query = new FeedbackQuery
        {
            OpenPrs =
            [
                new PrUnderTest
                {
                    RequestMode = ReworkRequestMode.Assistance,
                    PullRequestId = "1",
                },
            ],
            ReworkMarkerTag = "agent-assistance-requested",
        };
        var signals = new ReworkSignal[]
        {
            new()
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequestId = "1",
                Threads =
                [
                    new ReviewThread
                    {
                        ThreadId = "resolved",
                        Status = ReviewThreadStatus.Resolved,
                        Comments =
                        [
                            new ReviewThreadComment
                            {
                                Author = "reviewer@example.com",
                                AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }],
                                Body = "already handled",
                                CreatedAt = observedAt,
                            },
                        ],
                    },
                    new ReviewThread
                    {
                        ThreadId = "other-reviewer",
                        Status = ReviewThreadStatus.Active,
                        Comments =
                        [
                            new ReviewThreadComment
                            {
                                Author = "other@example.com",
                                Body = "not qualifying",
                                CreatedAt = observedAt,
                            },
                        ],
                    },
                ],
                FirstQualifyingCommentAt = observedAt,
                LastQualifyingCommentAt = observedAt,
            },
        };

        var result = await pipeline.FilterAssistanceAsync(
            query,
            signals,
            CancellationToken.None
        );

        var assistance = Assert.Single(result);
        Assert.Empty(assistance.Threads);
        Assert.Equal(ReworkRequestMode.Assistance, assistance.RequestMode);
    }

    [Fact]
    public async Task FilterAssistanceAsync_EmptyAllowlist_PreservesZeroCommentRequest()
    {
        var pipeline = CreatePipeline(new FailingPrLabelSource());
        var query = new FeedbackQuery
        {
            ReworkMarkerTag = "agent-assistance-requested",
        };
        var signals = new ReworkSignal[]
        {
            new()
            {
                RequestMode = ReworkRequestMode.Assistance,
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAssistanceAsync(
            query,
            signals,
            CancellationToken.None
        );

        Assert.Empty(Assert.Single(result).Threads);
    }

    // ── Marker gate ────────────────────────────────────────────────

    [Fact]
    public async Task FilterAsync_NoMarkerLabel_FailsClosed()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = [], // No labels
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FilterAsync_MarkerLabelByAllowedReviewer_Passes()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
    }

    [Fact]
    public async Task FilterAsync_LabelFetchFailure_FailsClosedPerPr()
    {
        var pipeline = CreatePipeline(new FailingPrLabelSource());

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Thread-status filter ───────────────────────────────────────

    [Fact]
    public async Task FilterAsync_KeepsOnlyActiveThreads()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    new()
                    {
                        ThreadId = "active",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix" },
                        },
                    },
                    new()
                    {
                        ThreadId = "resolved",
                        Status = ReviewThreadStatus.Resolved,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix" },
                        },
                    },
                    new()
                    {
                        ThreadId = "fixed",
                        Status = ReviewThreadStatus.Fixed,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix" },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("active", result[0].Threads[0].ThreadId);
    }

    [Fact]
    public async Task FilterAsync_AllThreadsNonActive_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    new()
                    {
                        ThreadId = "resolved",
                        Status = ReviewThreadStatus.Resolved,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix" },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Thread-author filter ───────────────────────────────────────

    [Fact]
    public async Task FilterAsync_KeepsThreadsWithAllowedReviewerComment()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    // Thread by allowed reviewer — kept
                    new()
                    {
                        ThreadId = "by-reviewer",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix this" },
                        },
                    },
                    // Thread by non-allowed reviewer — dropped
                    new()
                    {
                        ThreadId = "by-other",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "other@example.com", Body = "fix this" },
                        },
                    },
                    // Thread with reply from allowed reviewer — kept
                    new()
                    {
                        ThreadId = "reply-by-reviewer",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "other@example.com", Body = "initial" },
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "yes fix", IsReply = true },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(2, result[0].Threads.Count);
        Assert.Contains(result[0].Threads, t => t.ThreadId == "by-reviewer");
        Assert.Contains(result[0].Threads, t => t.ThreadId == "reply-by-reviewer");
    }

    [Fact]
    public async Task FilterAsync_NoThreadsByAllowedReviewer_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    new()
                    {
                        ThreadId = "t1",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "other@example.com", Body = "fix this" },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Comment-content filter ─────────────────────────────────────

    [Fact]
    public async Task FilterAsync_DropsEmptyCommentThreads()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    // Has content — kept
                    new()
                    {
                        ThreadId = "with-content",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix this" },
                        },
                    },
                    // Empty body — dropped
                    new()
                    {
                        ThreadId = "empty",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = string.Empty },
                        },
                    },
                    // Whitespace only — dropped
                    new()
                    {
                        ThreadId = "whitespace",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "   \n\t  " },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("with-content", result[0].Threads[0].ThreadId);
    }

    [Fact]
    public async Task FilterAsync_AllCommentsEmpty_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    new()
                    {
                        ThreadId = "t1",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = string.Empty },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Load-bearing order: all filters combined ───────────────────

    [Fact]
    public async Task FilterAsync_FullPipeline_CorrectOrder()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    // Active, by reviewer, has content — survives all filters
                    new()
                    {
                        ThreadId = "survives",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix this" },
                        },
                    },
                    // Resolved — dropped by status filter
                    new()
                    {
                        ThreadId = "resolved",
                        Status = ReviewThreadStatus.Resolved,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix" },
                        },
                    },
                    // Active but by non-reviewer — dropped by author filter
                    new()
                    {
                        ThreadId = "wrong-author",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "other@example.com", Body = "fix" },
                        },
                    },
                    // Active, by reviewer, but empty — dropped by content filter
                    new()
                    {
                        ThreadId = "empty",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = string.Empty },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("survives", result[0].Threads[0].ThreadId);
    }

    // ── Per-PR fail-closed ─────────────────────────────────────────

    [Fact]
    public async Task FilterAsync_OnePrFailsMarker_OtherPrPasses()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
                ["2"] = [], // No marker — fails closed
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
                ConfiguredPr("2", "https://example.com/pr/2"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
            new()
            {
                PullRequestId = "2",
                Threads = ActiveThread("t2", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("1", result[0].PullRequestId);
    }

    // ── Timestamps rebuilt from surviving threads ──────────────────

    [Fact]
    public async Task FilterAsync_RebuildsTimestampsFromSurvivingThreads()
    {
        var t1 = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    // Survives — comment at t2
                    new()
                    {
                        ThreadId = "survives",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix", CreatedAt = t2 },
                        },
                    },
                    // Dropped by status — had earlier comment at t1
                    new()
                    {
                        ThreadId = "resolved",
                        Status = ReviewThreadStatus.Resolved,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix", CreatedAt = t1 },
                        },
                    },
                },
                FirstQualifyingCommentAt = t1, // Original earliest (from dropped thread)
                LastQualifyingCommentAt = t2,
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        // Timestamps should reflect only surviving threads
        Assert.Equal(t2, result[0].FirstQualifyingCommentAt);
        Assert.Equal(t2, result[0].LastQualifyingCommentAt);
    }

    // ── Attributed-marker cases ────────────────────────────────────

    [Fact]
    public async Task FilterAsync_MarkerTagNameCaseMismatch_FailsClosed()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    // Label name differs in case — Ordinal comparison should not match
                    new() { Name = "Agent-Rework-Requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FilterAsync_MarkerAmongMultipleLabels_Passes()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "priority-high" },
                    new() { Name = "agent-rework-requested" },
                    new() { Name = "needs-review" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
    }

    // ── Load-bearing order ─────────────────────────────────────────

    [Fact]
    public async Task FilterAsync_MarkerGateRunsBeforeThreadFilters()
    {
        // Marker gate fails — thread-level filters must NOT be invoked.
        // We verify this by ensuring no label fetch occurs for PR 2 (marker gate
        // for PR 1 fails, so the pipeline short-circuits per-PR).
        var labelFetches = new List<string>();
        var trackingSource = new TrackingPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = [], // No marker — fails closed
                ["2"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            },
            labelFetches);

        var pipeline = CreatePipeline(trackingSource);
        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
                ConfiguredPr("2", "https://example.com/pr/2"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
            new()
            {
                PullRequestId = "2",
                Threads = ActiveThread("t2", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        // PR 1 fails marker gate (no label), PR 2 passes all filters.
        Assert.Single(result);
        Assert.Equal("2", result[0].PullRequestId);
        // Both PRs should have had labels fetched (marker gate is per-PR, not global).
        Assert.Contains("1", labelFetches);
        Assert.Contains("2", labelFetches);
    }

    [Fact]
    public async Task FilterAsync_AllowlistGateRunsBeforeMarkerGate()
    {
        // When allowlist is empty, the pipeline should return immediately
        // without ever calling the label source.
        var labelFetches = new List<string>();
        var trackingSource = new TrackingPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            },
            labelFetches);

        var pipeline = CreatePipeline(trackingSource);
        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                new() { PullRequestId = "1" },
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
        // Label source should never have been called.
        Assert.Empty(labelFetches);
    }

    // ── Per-PR fail-closed (extended) ──────────────────────────────

    [Fact]
    public async Task FilterAsync_AllPrsFailMarker_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = [], // No marker
                ["2"] = [], // No marker
                ["3"] = [], // No marker
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
                ConfiguredPr("2", "https://example.com/pr/2"),
                ConfiguredPr("3", "https://example.com/pr/3"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new() { PullRequestId = "1", Threads = ActiveThread("t1", "reviewer@example.com") },
            new() { PullRequestId = "2", Threads = ActiveThread("t2", "reviewer@example.com") },
            new() { PullRequestId = "3", Threads = ActiveThread("t3", "reviewer@example.com") },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(result);
    }

    // ── Signal without matching PR ─────────────────────────────────

    [Fact]
    public async Task FilterAsync_SignalWithoutMatchingPr_SkippedGracefully()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
                // PR "99" has no entry in OpenPrs
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "reviewer@example.com"),
            },
            new()
            {
                PullRequestId = "99", // No matching PrUnderTest
                Threads = ActiveThread("t99", "reviewer@example.com"),
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        // Only PR 1 survives; PR 99 is skipped (no matching PrUnderTest).
        Assert.Single(result);
        Assert.Equal("1", result[0].PullRequestId);
    }

    // ── Allowlist fail-closed (multiple calls) ─────────────────────

    [Fact]
    public async Task FilterAsync_EmptyAllowlistMultipleCalls_AlwaysReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource([]));
        var query = new FeedbackQuery
        {
            OpenPrs = [],
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = ActiveThread("t1", "a@example.com"),
            },
        };

        // Three successive calls — all must return empty.
        var r1 = await pipeline.FilterAsync(query, signals, CancellationToken.None);
        var r2 = await pipeline.FilterAsync(query, signals, CancellationToken.None);
        var r3 = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Empty(r1);
        Assert.Empty(r2);
        Assert.Empty(r3);
    }

    // ── Comment-content filter (extended) ──────────────────────────

    [Fact]
    public async Task FilterAsync_ThreadWithMixedContentKeepsIfAnyCommentHasContent()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    // Thread with multiple comments — only one has content
                    new()
                    {
                        ThreadId = "mixed",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = string.Empty },
                            new() { Author = "other@example.com", Body = "   " },
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "please fix", IsReply = true },
                        },
                    },
                    // Thread where all comments are empty
                    new()
                    {
                        ThreadId = "all-empty",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = string.Empty },
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "\t\n" },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("mixed", result[0].Threads[0].ThreadId);
    }

    // ── Thread-author filter (reply chain) ─────────────────────────

    [Fact]
    public async Task FilterAsync_ThreadWithOnlyNonAllowedAuthorComments_Dropped()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    // Initial by non-allowed, reply by non-allowed — dropped
                    new()
                    {
                        ThreadId = "no-reviewer",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "author@example.com", Body = "initial comment" },
                            new() { Author = "other@example.com", Body = "reply", IsReply = true },
                        },
                    },
                    // Initial by non-allowed, reply by allowed — kept
                    new()
                    {
                        ThreadId = "reviewer-replied",
                        Status = ReviewThreadStatus.Active,
                        Comments = new List<ReviewThreadComment>
                        {
                            new() { Author = "author@example.com", Body = "initial comment" },
                            new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "yes fix this", IsReply = true },
                        },
                    },
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("reviewer-replied", result[0].Threads[0].ThreadId);
    }

    // ── Thread-status filter (all non-Active statuses) ─────────────

    [Fact]
    public async Task FilterAsync_DropsAllNonActiveStatuses()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = new()
                {
                    new() { Name = "agent-rework-requested" },
                },
            }));

        var query = new FeedbackQuery
        {
            OpenPrs = new List<PrUnderTest>
            {
                ConfiguredPr("1", "https://example.com/pr/1"),
            },
            ReworkMarkerTag = "agent-rework-requested",
        };

        var signals = new List<ReworkSignal>
        {
            new()
            {
                PullRequestId = "1",
                Threads = new List<ReviewThread>
                {
                    CreateThread("resolved", ReviewThreadStatus.Resolved),
                    CreateThread("fixed", ReviewThreadStatus.Fixed),
                    CreateThread("wontfix", ReviewThreadStatus.WontFix),
                    CreateThread("closed", ReviewThreadStatus.Closed),
                    CreateThread("bydesign", ReviewThreadStatus.ByDesign),
                    CreateThread("active", ReviewThreadStatus.Active),
                },
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
        Assert.Equal("active", result[0].Threads[0].ThreadId);
    }

    // ── Edge cases ─────────────────────────────────────────────────

    [Fact]
    public async Task FilterAsync_EmptySignals_ReturnsEmpty()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource([]));
        var query = new FeedbackQuery
        {
            OpenPrs = [],
        };

        var result = await pipeline.FilterAsync(query, Array.Empty<ReworkSignal>(), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public void Pipeline_ImplementsIDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(ReviewFeedbackFilterPipeline)));
    }

    [Fact]
    public async Task TraceAsync_UsesSameMarkerAndThreadPolicyAsFilterAsync()
    {
        var labels = new TestPrLabelSource(new Dictionary<string, List<PrLabel>>
        {
            ["1"] = [new PrLabel { Name = "agent-rework-requested" }],
            ["2"] = [new PrLabel { Name = "Agent-Rework-Requested" }],
        });
        var pipeline = CreatePipeline(labels);
        var query = new FeedbackQuery
        {
            OpenPrs =
            [
                ConfiguredPr("1"),
                ConfiguredPr("2"),
            ],
            ReworkMarkerTag = "agent-rework-requested",
        };
        var mixedThreads = new List<ReviewThread>
        {
            new()
            {
                ThreadId = "reply-chain",
                Status = ReviewThreadStatus.Active,
                Comments =
                [
                    new ReviewThreadComment { Author = "author@example.com", Body = "question" },
                    new ReviewThreadComment
                    {
                        Author = "reviewer@example.com",
                        AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }],
                        Body = "answer",
                        IsReply = true,
                    },
                ],
            },
            new()
            {
                ThreadId = "resolved",
                Status = ReviewThreadStatus.Resolved,
                Comments = [new ReviewThreadComment { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "done" }],
            },
            new()
            {
                ThreadId = "whitespace",
                Status = ReviewThreadStatus.Active,
                Comments = [new ReviewThreadComment { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = " \t" }],
            },
            new()
            {
                ThreadId = "other",
                Status = ReviewThreadStatus.Active,
                Comments = [new ReviewThreadComment { Author = "other@example.com", Body = "text" }],
            },
            new()
            {
                ThreadId = "reviewer-case-mismatch",
                Status = ReviewThreadStatus.Active,
                Comments = [new ReviewThreadComment { Author = "Reviewer@example.com", Body = "text" }],
            },
        };
        var signals = new ReworkSignal[]
        {
            new() { PullRequestId = "1", Threads = mixedThreads },
            new() { PullRequestId = "2", Threads = ActiveThread("case", "reviewer@example.com") },
        };

        var traces = await pipeline.TraceAsync(query, signals, CancellationToken.None);
        var filtered = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        var accepted = Assert.Single(traces, trace => trace.PullRequestId == "1");
        Assert.Equal(FeedbackMarkerCheckStatus.Present, accepted.MarkerStatus);
        Assert.Equal(5, accepted.TotalThreadCount);
        Assert.Equal(4, accepted.ActiveThreadCount);
        Assert.Equal(2, accepted.AllowlistedReviewerThreadCount);
        Assert.Equal(1, accepted.NonEmptyContentThreadCount);
        Assert.Equal(1, accepted.QualifyingThreadCount);
        Assert.True(accepted.IsAccepted);
        Assert.Single(Assert.Single(filtered).Threads);

        var rejected = Assert.Single(traces, trace => trace.PullRequestId == "2");
        Assert.Equal(FeedbackMarkerCheckStatus.Missing, rejected.MarkerStatus);
        Assert.False(rejected.IsAccepted);
        Assert.Equal(1, rejected.ActiveThreadCount);
        Assert.Equal(1, rejected.NonEmptyContentThreadCount);
    }

    [Fact]
    public async Task TraceAsync_FetchFailureAndEmptyAllowlist_MatchFailClosedFiltering()
    {
        var signal = new ReworkSignal
        {
            PullRequestId = "1",
            Threads = ActiveThread("thread", "reviewer@example.com"),
        };
        var configuredPr = ConfiguredPr("1");
        var emptyPr = new PrUnderTest { PullRequestId = "1" };

        var failingPipeline = CreatePipeline(new FailingPrLabelSource());
        var configuredQuery = new FeedbackQuery
        {
            OpenPrs = [configuredPr],
        };
        var failedTrace = Assert.Single(await failingPipeline.TraceAsync(
            configuredQuery, [signal], CancellationToken.None));
        Assert.Equal(FeedbackMarkerCheckStatus.FetchFailed, failedTrace.MarkerStatus);
        Assert.False(failedTrace.IsAccepted);
        Assert.Empty(await failingPipeline.FilterAsync(configuredQuery, [signal], CancellationToken.None));

        var fetches = new List<string>();
        var emptyPipeline = CreatePipeline(new TrackingPrLabelSource([], fetches));
        var emptyQuery = new FeedbackQuery { OpenPrs = [emptyPr] };
        var emptyTrace = Assert.Single(await emptyPipeline.TraceAsync(
            emptyQuery, [signal], CancellationToken.None));
        Assert.Equal(FeedbackMarkerCheckStatus.NotAttempted, emptyTrace.MarkerStatus);
        Assert.False(emptyTrace.ReviewerAllowlistConfigured);
        Assert.False(emptyTrace.IsAccepted);
        Assert.Empty(fetches);
        Assert.Empty(await emptyPipeline.FilterAsync(emptyQuery, [signal], CancellationToken.None));
    }

    [Fact]
    public async Task TraceAssistanceAsync_ZeroComments_RemainsAcceptedWithoutBodies()
    {
        var pipeline = CreatePipeline(new FailingPrLabelSource());
        var query = new FeedbackQuery();
        var signal = new ReworkSignal
        {
            RequestMode = ReworkRequestMode.Assistance,
            PullRequestId = "1",
            Threads =
            [
                new ReviewThread
                {
                    Status = ReviewThreadStatus.Active,
                    Comments = [new ReviewThreadComment { Author = "someone", Body = "secret body" }],
                },
            ],
        };

        var trace = Assert.Single(await pipeline.TraceAssistanceAsync(
            query, [signal], CancellationToken.None));
        var filtered = Assert.Single(await pipeline.FilterAssistanceAsync(
            query, [signal], CancellationToken.None));

        Assert.Equal(FeedbackMarkerCheckStatus.AlreadyValidated, trace.MarkerStatus);
        Assert.True(trace.IsAccepted);
        Assert.Equal(0, trace.QualifyingThreadCount);
        Assert.Empty(filtered.Threads);
        Assert.DoesNotContain("secret body", System.Text.Json.JsonSerializer.Serialize(trace));
    }

    [Fact]
    public async Task TraceAsync_LabelFetchCancellation_IsPropagated()
    {
        var pipeline = CreatePipeline(new CancelingPrLabelSource());
        var query = new FeedbackQuery
        {
            OpenPrs = [ConfiguredPr("1")],
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.TraceAsync(
            query,
            [new ReworkSignal { PullRequestId = "1" }],
            cts.Token));
    }

    [Fact]
    public async Task FilterAsync_UsesEachPullRequestsRepositoryReviewerIdentities()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = [new PrLabel { Name = "agent-rework-requested" }],
                ["2"] = [new PrLabel { Name = "agent-rework-requested" }],
            }));
        var query = new FeedbackQuery
        {
            OpenPrs =
            [
                new PrUnderTest
                {
                    PullRequest = new PullRequestReference
                    {
                        EnvironmentKey = "ado",
                        RepositoryKey = "orders",
                        PullRequestId = "1",
                    },
                    PullRequestId = "1",
                    ReviewerIdentityProvider = "AzureDevOps",
                    ReviewerIdentities =
                    [new ReviewerIdentity { Kind = "email", Value = "orders@example.com" }],
                },
                new PrUnderTest
                {
                    PullRequest = new PullRequestReference
                    {
                        EnvironmentKey = "ado",
                        RepositoryKey = "payments",
                        PullRequestId = "2",
                    },
                    PullRequestId = "2",
                    ReviewerIdentityProvider = "AzureDevOps",
                    ReviewerIdentities =
                    [new ReviewerIdentity { Kind = "email", Value = "payments@example.com" }],
                },
            ],
            // Each pull request carries its own repository reviewer policy.
        };
        var signals = new ReworkSignal[]
        {
            new()
            {
                PullRequest = query.OpenPrs[0].PullRequest,
                PullRequestId = "1",
                Threads =
                [new ReviewThread
                {
                    ThreadId = "orders-thread",
                    Status = ReviewThreadStatus.Active,
                    Comments =
                    [new ReviewThreadComment
                    {
                        Author = "Orders Reviewer",
                        AuthorIdentities =
                        [new ReviewerIdentity { Kind = "email", Value = "ORDERS@EXAMPLE.COM" }],
                        Body = "Fix orders.",
                    }],
                }],
            },
            new()
            {
                PullRequest = query.OpenPrs[1].PullRequest,
                PullRequestId = "2",
                Threads =
                [new ReviewThread
                {
                    ThreadId = "payments-thread",
                    Status = ReviewThreadStatus.Active,
                    Comments =
                    [new ReviewThreadComment
                    {
                        Author = "Payments Reviewer",
                        AuthorIdentities =
                        [new ReviewerIdentity { Kind = "email", Value = "PAYMENTS@EXAMPLE.COM" }],
                        Body = "Fix payments.",
                    }],
                }],
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, signal => signal.PullRequestId == "1");
        Assert.Contains(result, signal => signal.PullRequestId == "2");
    }

    [Fact]
    public async Task FilterAsync_RequiresSameTypedIdentityKindAndAnyAuthorAlias()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["1"] = [new PrLabel { Name = "agent-rework-requested" }],
            }));
        var configured = new PrUnderTest
        {
            PullRequestId = "1",
            ReviewerIdentities =
            [new ReviewerIdentity { Kind = "descriptor", Value = "aad.123" }],
        };
        var query = new FeedbackQuery { OpenPrs = [configured] };
        var signal = new ReworkSignal
        {
            PullRequestId = "1",
            Threads =
            [new ReviewThread
            {
                ThreadId = "aliases",
                Status = ReviewThreadStatus.Active,
                Comments =
                [new ReviewThreadComment
                {
                    Author = "Reviewer",
                    AuthorIdentities =
                    [
                        new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" },
                        new ReviewerIdentity { Kind = "descriptor", Value = "aad.123" },
                    ],
                    Body = "Please update this.",
                }],
            }],
        };

        var result = await pipeline.FilterAsync(query, [signal], CancellationToken.None);

        Assert.Single(result);
        Assert.Single(result[0].Threads);
    }

    [Fact]
    public async Task FilterAsync_EmptyRepositoryAllowlistFailsClosedPerRevivalPr()
    {
        var pipeline = CreatePipeline(new TestPrLabelSource(
            new Dictionary<string, List<PrLabel>>
            {
                ["empty"] = [new PrLabel { Name = "agent-rework-requested" }],
                ["configured"] = [new PrLabel { Name = "agent-rework-requested" }],
            }));
        var query = new FeedbackQuery
        {
            OpenPrs =
            [
                new PrUnderTest { PullRequestId = "empty" },
                new PrUnderTest
                {
                    PullRequestId = "configured",
                    ReviewerIdentities =
                    [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }],
                },
            ],
        };
        var signals = new ReworkSignal[]
        {
            new() { PullRequestId = "empty", Threads = ActiveThread("empty-thread", "reviewer@example.com") },
            new()
            {
                PullRequestId = "configured",
                Threads =
                [new ReviewThread
                {
                    ThreadId = "configured-thread",
                    Status = ReviewThreadStatus.Active,
                    Comments =
                    [new ReviewThreadComment
                    {
                        Author = "reviewer@example.com",
                        AuthorIdentities =
                        [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }],
                        Body = "fix this",
                    }],
                }],
            },
        };

        var result = await pipeline.FilterAsync(query, signals, CancellationToken.None);

        var accepted = Assert.Single(result);
        Assert.Equal("configured", accepted.PullRequestId);
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static PrUnderTest ConfiguredPr(string pullRequestId, string? pullRequestUrl = null)
    {
        return new PrUnderTest
        {
            PullRequestId = pullRequestId,
            PullRequestUrl = pullRequestUrl ?? $"https://example.com/pr/{pullRequestId}",
            ReviewerIdentityProvider = "AzureDevOps",
            ReviewerIdentities =
            [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }],
        };
    }

    private static List<ReviewThread> ActiveThread(string threadId, string author)
    {
        return new List<ReviewThread>
        {
            new()
            {
                ThreadId = threadId,
                Status = ReviewThreadStatus.Active,
                Comments = new List<ReviewThreadComment>
                {
                    new()
                    {
                        Author = author,
                        AuthorIdentities =
                        [new ReviewerIdentity { Kind = "email", Value = author }],
                        Body = "fix this",
                    },
                },
            },
        };
    }

    private static ReviewThread CreateThread(string threadId, ReviewThreadStatus status)
    {
        return new ReviewThread
        {
            ThreadId = threadId,
            Status = status,
            Comments = new List<ReviewThreadComment>
            {
                new() { Author = "reviewer@example.com", AuthorIdentities = [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.com" }], Body = "fix" },
            },
        };
    }

    // ── Test doubles ───────────────────────────────────────────────

    private sealed class TestPrLabelSource : IPrLabelSource
    {
        private readonly Dictionary<string, IReadOnlyList<PrLabel>> _labels;

        public TestPrLabelSource(Dictionary<string, List<PrLabel>> labels)
        {
            _labels = labels.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<PrLabel>)kvp.Value,
                StringComparer.Ordinal);
        }

        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<PrLabel>>(
                _labels.TryGetValue(pr.PullRequestId, out var labels) ? labels : Array.Empty<PrLabel>());
        }
    }

    private sealed class FailingPrLabelSource : IPrLabelSource
    {
        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Simulated label fetch failure");
        }
    }

    private sealed class CancelingPrLabelSource : IPrLabelSource
    {
        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken)
        {
            return Task.FromCanceled<IReadOnlyList<PrLabel>>(cancellationToken);
        }
    }

    /// <summary>
    /// Test double that records which PRs had their labels fetched,
    /// enabling verification of load-bearing filter order.
    /// </summary>
    private sealed class TrackingPrLabelSource : IPrLabelSource
    {
        private readonly Dictionary<string, IReadOnlyList<PrLabel>> _labels;
        private readonly List<string> _fetches;

        public TrackingPrLabelSource(
            Dictionary<string, List<PrLabel>> labels,
            List<string> fetches)
        {
            _labels = labels.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<PrLabel>)kvp.Value,
                StringComparer.Ordinal);
            _fetches = fetches;
        }

        public IReadOnlyList<string> Fetches => _fetches;

        public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
            PrUnderTest pr,
            CancellationToken cancellationToken)
        {
            _fetches.Add(pr.PullRequestId);
            return Task.FromResult<IReadOnlyList<PrLabel>>(
                _labels.TryGetValue(pr.PullRequestId, out var labels) ? labels : Array.Empty<PrLabel>());
        }
    }
}
