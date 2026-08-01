using System.Reflection;
using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Application.Tests;

public sealed class ListPullRequestDiagnosticsQueryHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_EvaluatesEverySnapshotAndPreservesPageMetadata()
    {
        var eligible = Snapshot("42", ["agent-rework-requested"]);
        var ineligible = Snapshot("43", []);
        var discoveryQueries = new List<ManagedPullRequestDiscoveryQuery>();
        var feedbackPolls = 0;

        var discovery = Stub<IManagedPullRequestDiagnosticDiscovery>.Create((method, args) =>
        {
            if (method.Name != nameof(IManagedPullRequestDiagnosticDiscovery.ListAsync))
            {
                throw new NotSupportedException(method.Name);
            }

            discoveryQueries.Add((ManagedPullRequestDiscoveryQuery)args[0]!);
            return Task.FromResult(new ManagedPullRequestDiscoveryPage
            {
                Items = [eligible, ineligible],
                Failures =
                [
                    new ManagedPullRequestDiscoveryFailure
                    {
                        SourceControlEnvironmentKey = "ado",
                        RepositoryKey = "unavailable",
                        Message = "Unavailable",
                    },
                ],
                Page = 2,
                PageSize = 2,
                Total = 7,
            });
        });
        var repository = new RepositoryProfile
        {
            Key = "orders",
            RepositoryHostConnectionKey = "ado",
            ReviewerIdentities =
            [new ReviewerIdentity { Kind = "email", Value = "reviewer@example.test" }],
        };
        var repositoryStore = Stub<IRepositoryStore>.Create((method, args) => method.Name switch
        {
            nameof(IRepositoryStore.GetByKeyAsync) => Task.FromResult<RepositoryProfile?>(repository),
            nameof(IRepositoryStore.ListAsync) => Task.FromResult<IReadOnlyList<RepositoryProfile>>([repository]),
            _ => throw new NotSupportedException(method.Name),
        });
        var connectionStore = Stub<IConnectionStore>.Create((method, _) =>
            method.Name == nameof(IConnectionStore.ListAsync)
                ? Task.FromResult<IReadOnlyList<ConnectionProfile>>(
                [new ConnectionProfile
                {
                    Key = "ado",
                    DisplayName = "Azure Repos",
                    Capabilities = [ConnectionCapability.Repositories],
                }])
                : throw new NotSupportedException(method.Name));
        var runStore = Stub<IAgentRunStore>.Create((method, _) =>
            method.Name == nameof(IAgentRunStore.FindRunsForFeedbackAsync)
                ? Task.FromResult<IReadOnlyList<AgentRunHandle>>(
                [new AgentRunHandle
                {
                    RunId = "run-42",
                    WorkItemId = "work-42",
                    PullRequestUrl = eligible.PullRequestUrl,
                    Status = RunLifecycleState.Completed,
                }])
                : throw new NotSupportedException(method.Name));
        var cycleStore = Stub<IReworkCycleStore>.Create((method, _) => method.Name switch
        {
            nameof(IReworkCycleStore.ListPendingAsync) =>
                Task.FromResult<IReadOnlyList<ReworkCycle>>([]),
            nameof(IReworkCycleStore.ListConsumedAsync) =>
                Task.FromResult<IReadOnlyList<ReworkCycle>>([]),
            _ => throw new NotSupportedException(method.Name),
        });
        var feedbackStore = Stub<IReworkFeedbackStore>.Create((method, _) =>
            method.Name == nameof(IReworkFeedbackStore.GetTrackedAsync)
                ? Task.FromResult<IReadOnlyList<ReworkFeedback>>([])
                : throw new NotSupportedException(method.Name));
        var feedbackSource = Stub<IFeedbackSource>.Create((method, _) =>
        {
            if (method.Name != nameof(IFeedbackSource.PollAsync))
            {
                throw new NotSupportedException(method.Name);
            }

            feedbackPolls++;
            return Task.FromResult<IReadOnlyList<ReworkSignal>>
            ([new ReworkSignal
            {
                PullRequest = eligible.PullRequest,
                PullRequestId = eligible.PullRequestId,
                Threads = [QualifyingThread()],
            }]);
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
        var handler = new ListPullRequestDiagnosticsQueryHandler(
            discovery,
            new PullRequestSourceOptionsProvider(repositoryStore, connectionStore),
            evaluator);

        var result = await handler.ExecuteAsync(new ListPullRequestDiagnosticsQuery
        {
            SourceControlEnvironmentKey = "ado",
            IncludeInactive = true,
            Page = 2,
            PageSize = 2,
        }, CancellationToken.None);

        Assert.Collection(
            result.Items,
            summary =>
            {
                Assert.True(summary.Eligible);
                Assert.Equal(PullRequestRequestMatch.Revival, summary.Request);
            },
            summary =>
            {
                Assert.False(summary.Eligible);
                Assert.Equal(PullRequestRequestMatch.None, summary.Request);
            });
        Assert.Equal(2, result.Page);
        Assert.Equal(2, result.PageSize);
        Assert.Equal(7, result.Total);
        Assert.Equal("Unavailable", Assert.Single(result.Failures).Message);
        Assert.Equal("Azure Repos", Assert.Single(result.Sources).Name);
        var discoveryQuery = Assert.Single(discoveryQueries);
        Assert.Equal("ado", discoveryQuery.SourceControlEnvironmentKey);
        Assert.True(discoveryQuery.IncludeInactive);
        Assert.Equal(2, discoveryQuery.Page);
        Assert.Equal(2, discoveryQuery.PageSize);
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
