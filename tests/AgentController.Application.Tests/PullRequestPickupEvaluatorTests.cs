using System.Reflection;
using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Application.Tests;

public sealed class PullRequestPickupEvaluatorTests
{
    [Fact]
    public async Task EvaluatePageAsync_ReturnsMixedResultsWithSharedStoreAndProviderWork()
    {
        var eligible = Snapshot("42", ["agent-rework-requested"]);
        var ineligible = Snapshot("43", []);
        var repositoryLookups = 0;
        var runLookups = 0;
        var feedbackPolls = 0;

        var repositoryStore = Stub<IRepositoryStore>.Create((method, args) =>
        {
            if (method.Name != nameof(IRepositoryStore.GetByKeyAsync))
            {
                throw new NotSupportedException(method.Name);
            }

            repositoryLookups++;
            return Task.FromResult<RepositoryProfile?>(new RepositoryProfile
            {
                Key = (string)args[0]!,
                RepositoryHostConnectionKey = "ado",
                ReviewerIdentities =
                [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.test" }],
            });
        });
        var runStore = Stub<IAgentRunStore>.Create((method, _) =>
        {
            if (method.Name != nameof(IAgentRunStore.FindRunsForFeedbackAsync))
            {
                throw new NotSupportedException(method.Name);
            }

            runLookups++;
            return Task.FromResult<IReadOnlyList<AgentRunHandle>>([
                new AgentRunHandle
                {
                    RunId = "run-42",
                    WorkItemId = "work-42",
                    PullRequestUrl = eligible.PullRequestUrl,
                    Status = RunLifecycleState.Completed,
                },
            ]);
        });
        var cycleStore = Stub<IReworkCycleStore>.Create((method, _) => method.Name switch
        {
            nameof(IReworkCycleStore.ListPendingAsync) =>
                Task.FromResult<IReadOnlyList<ReworkCycle>>([]),
            nameof(IReworkCycleStore.ListConsumedAsync) =>
                Task.FromResult<IReadOnlyList<ReworkCycle>>([]),
            _ => throw new NotSupportedException(method.Name),
        });
        var feedbackStore = Stub<IReworkFeedbackStore>.Create((method, _) => method.Name switch
        {
            nameof(IReworkFeedbackStore.GetTrackedAsync) =>
                Task.FromResult<IReadOnlyList<ReworkFeedback>>([]),
            _ => throw new NotSupportedException(method.Name),
        });
        var feedbackSource = Stub<IFeedbackSource>.Create((method, _) =>
        {
            if (method.Name != nameof(IFeedbackSource.PollAsync))
            {
                throw new NotSupportedException(method.Name);
            }

            feedbackPolls++;
            return Task.FromResult<IReadOnlyList<ReworkSignal>>([
                new ReworkSignal
                {
                    PullRequest = eligible.PullRequest,
                    PullRequestId = eligible.PullRequestId,
                    Threads = [QualifyingThread()],
                },
            ]);
        });
        var labelSource = Stub<IPrLabelSource>.Create((method, args) =>
            method.Name == nameof(IPrLabelSource.GetLabelsAsync)
                ? Task.FromResult<IReadOnlyList<PrLabel>>(
                    ((PrUnderTest)args[0]!).PullRequestId == eligible.PullRequestId
                        ? [new PrLabel { Name = "agent-rework-requested" }]
                        : [])
                : throw new NotSupportedException(method.Name));
        var pipeline = new ReviewFeedbackFilterPipeline(
            labelSource,
            NullLogger<ReviewFeedbackFilterPipeline>.Instance);
        var evaluator = new PullRequestPickupEvaluator(
            repositoryStore,
            runStore,
            cycleStore,
            feedbackStore,
            feedbackSource,
            pipeline,
            new PullRequestDiagnosticOptions());

        var details = await evaluator.EvaluatePageAsync([eligible, ineligible], CancellationToken.None);

        Assert.Equal(2, details.Count);
        Assert.True(details[0].Eligible);
        Assert.False(details[1].Eligible);
        Assert.Equal(PullRequestRequestMatch.None, details[1].Request);
        Assert.Equal(1, repositoryLookups);
        Assert.Equal(1, runLookups);
        Assert.Equal(1, feedbackPolls);
    }

    private static ManagedPullRequestSnapshot Snapshot(
        string id,
        IReadOnlyList<string> labels) => new()
        {
            PullRequest = new PullRequestReference
            {
                EnvironmentKey = "ado",
                RepositoryKey = "orders",
                PullRequestId = id,
                PullRequestUrl = $"https://example.test/orders/pullrequest/{id}",
                SourceBranch = "refs/heads/feature",
                TargetBranch = "refs/heads/main",
            },
            Title = $"Pull request {id}",
            Status = "active",
            Labels = labels,
        };

    private static ReviewThread QualifyingThread() => new()
    {
        ThreadId = "thread-1",
        Status = ReviewThreadStatus.Active,
        Comments =
        [
            new ReviewThreadComment
            {
                Author = "reviewer@example.test",
                AuthorIdentities =
                [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.test" }],
                Body = "Please fix this.",
            },
        ],
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1852:Seal internal types", Justification = "DispatchProxy subclasses this type at runtime.")]
    private class Stub<T> : DispatchProxy where T : class
    {
        private Func<MethodInfo, object?[], object?> _handler = null!;

        public static T Create(Func<MethodInfo, object?[], object?> handler)
        {
            var proxy = DispatchProxy.Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handler(targetMethod!, args ?? []);
    }
}
