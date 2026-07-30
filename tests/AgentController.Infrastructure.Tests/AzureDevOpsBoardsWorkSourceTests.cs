using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AzureDevOpsBoardsWorkSource.ReactivateForReworkAsync"/>
/// verifying the single-PATCH reactivation flow that strips agent lifecycle tags
/// and re-adds agent-ready atomically.
/// </summary>
public class AzureDevOpsBoardsWorkSourceTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "TestProject";
    private const string EligibleState = "New";
    private const string ActiveState = "Active";
    private static readonly string[] EmptyStringArray = [];

    [Fact]
    public async Task FindEligibleAsync_PollsAllEnabledManagedEnvironmentsWithProfileSettings()
    {
        var alphaProfile = ManagedEnvironment("alpha", "AlphaProject") with
        {
            TagPrefix = "custom",
        };
        var zetaProfile = ManagedEnvironment("zeta", "ZetaProject");
        var alphaClient = new MockAzureDevOpsBoardsClient
        {
            QueryResults =
            [
                new WorkCandidate
                {
                    Id = "wi-alpha",
                    ExternalId = "1",
                    Source = "AzureDevOpsBoards",
                },
            ],
        };
        var zetaClient = new MockAzureDevOpsBoardsClient
        {
            QueryResults =
            [
                new WorkCandidate
                {
                    Id = "wi-zeta",
                    ExternalId = "2",
                    Source = "AzureDevOpsBoards",
                },
            ],
        };
        var services = new ServiceCollection();
        services.AddSingleton<IManagedProfileResolver>(
            new StubManagedProfileResolver([alphaProfile, zetaProfile])
        );
        services.AddSingleton<IAzureDevOpsBoardsClientFactory>(
            new StubClientFactory(
                new Dictionary<string, IAzureDevOpsBoardsClient>
                {
                    ["alpha"] = alphaClient,
                    ["zeta"] = zetaClient,
                }
            )
        );
        var provider = services.BuildServiceProvider();
        var workSource = new AzureDevOpsBoardsWorkSource(
            new DelegatingScopeFactory(provider)
        );

        var candidates = await workSource.FindEligibleAsync(
            new WorkQuery { MaxResults = 5 },
            CancellationToken.None
        );

        Assert.Equal(2, candidates.Count);
        Assert.Equal("AlphaProject", Assert.Single(alphaClient.QueryCalls).Project);
        Assert.Equal(BoardTerminalStates.Values, alphaClient.QueryCalls[0].ExcludedStates);
        Assert.Null(alphaClient.QueryCalls[0].States);
        Assert.Null(alphaClient.QueryCalls[0].Tags);
        Assert.Equal(
            ["custom-ready", "custom-ready-rework"],
            alphaClient.QueryCalls[0].AnyTags
        );
        Assert.Equal("ZetaProject", Assert.Single(zetaClient.QueryCalls).Project);
        Assert.Equal(BoardTerminalStates.Values, zetaClient.QueryCalls[0].ExcludedStates);
        Assert.Null(zetaClient.QueryCalls[0].States);
        Assert.Null(zetaClient.QueryCalls[0].Tags);
        Assert.Equal(
            ["agent-ready", "agent-ready-rework"],
            zetaClient.QueryCalls[0].AnyTags
        );
        Assert.Equal("alpha", candidates[0].SourceMetadata?["workSourceEnvironmentKey"]);
        Assert.Equal("zeta", candidates[1].SourceMetadata?["workSourceEnvironmentKey"]);
    }

    [Fact]
    public async Task FindEligibleAsync_NoManagedEnvironments_ReturnsEmpty()
    {
        var workSource = CreateWorkSourceWithoutManagedEnvironments();

        var candidates = await workSource.FindEligibleAsync(
            new WorkQuery { MaxResults = 5 },
            CancellationToken.None
        );

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task TryClaimAsync_NoManagedEnvironment_ReturnsFailure()
    {
        var workSource = CreateWorkSourceWithoutManagedEnvironments();

        var result = await workSource.TryClaimAsync(
            new WorkCandidate
            {
                Id = "wi-1",
                ExternalId = "1",
                Source = "AzureDevOpsBoards",
            },
            new ClaimRequest { WorkerId = "worker-1" },
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("managed", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseClaimAsync_NoManagedEnvironment_Throws()
    {
        var workSource = CreateWorkSourceWithoutManagedEnvironments();
        var request = new ReleaseClaimRequest
        {
            WorkRef = new ExternalWorkRef
            {
                Source = "AzureDevOpsBoards",
                ExternalId = "1",
            },
            WorkerId = "worker-1",
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workSource.ReleaseClaimAsync(request, CancellationToken.None)
        );

        Assert.Contains("managed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClaimAndRelease_ForwardManagedEnvironmentTagPrefix()
    {
        var client = new MockAzureDevOpsBoardsClient
        {
            ClaimResult = new ClaimResult { Success = true },
        };
        var environment = ManagedEnvironment("managed", Project) with
        {
            TagPrefix = "ac",
        };
        var workSource = CreateWorkSource(client, environment);
        var candidate = new WorkCandidate
        {
            Id = "wi-1",
            ExternalId = "1",
            Source = "AzureDevOpsBoards",
            SourceMetadata = new Dictionary<string, string>
            {
                ["workSourceEnvironmentKey"] = environment.Key,
            },
        };

        var result = await workSource.TryClaimAsync(
            candidate,
            new ClaimRequest { WorkerId = "worker-1" },
            CancellationToken.None
        );
        await workSource.ReleaseClaimAsync(
            new ReleaseClaimRequest
            {
                WorkRef = new ExternalWorkRef
                {
                    Source = candidate.Source,
                    ExternalId = candidate.ExternalId,
                    EnvironmentKey = environment.Key,
                },
                WorkerId = "worker-1",
            },
            CancellationToken.None
        );

        Assert.True(result.Success);
        Assert.Equal("ac", Assert.Single(client.ClaimCalls).TagPrefix);
        Assert.Equal("ac", Assert.Single(client.ReleaseClaimCalls).TagPrefix);
    }

    [Fact]
    public async Task CreateAssistanceStoryAsync_AppliesManagedTagsAndEnvironmentMetadata()
    {
        var client = new MockAzureDevOpsBoardsClient
        {
            CreateResult = new CreatedWorkItemResult
            {
                ExternalId = "501",
                Url = "https://dev.azure.com/testorg/TestProject/_workitems/edit/501",
                Revision = "3",
                Candidate = new WorkCandidate
                {
                    Id = "wi_501",
                    ExternalId = "501",
                    Source = "AzureDevOpsBoards",
                    SourceMetadata = new Dictionary<string, string> { ["revision"] = "3" },
                },
            },
        };
        var environment = ManagedEnvironment("managed", Project) with
        {
            TagPrefix = "custom",
        };
        var workSource = CreateWorkSource(client, environment);
        var relation = new WorkItemRelation
        {
            RelationType = "System.LinkTypes.Related",
            Url = "https://dev.azure.com/testorg/_apis/wit/workItems/41",
        };

        var created = await workSource.CreateAssistanceStoryAsync(
            new CreateAssistanceStoryRequest
            {
                EnvironmentKey = environment.Key,
                WorkItemType = "Product Backlog Item",
                RepoKey = "widgets",
                Title = "Continue PR 17",
                Description = "<p>Review &amp; update the existing PR.</p>",
                CorrelationTags = ["assistance:correlation-17", "repo:widgets"],
                Relations = [relation],
            },
            CancellationToken.None
        );

        var call = Assert.Single(client.CreateCalls);
        Assert.Equal(Project, call.Project);
        Assert.Equal("Product Backlog Item", call.WorkItemType);
        Assert.Equal("widgets", call.RepoKey);
        Assert.Equal("Continue PR 17", call.Title);
        Assert.Equal("<p>Review &amp; update the existing PR.</p>", call.Description);
        Assert.Equal(
            ["repo:widgets", "custom-ready-rework", "assistance:correlation-17"],
            call.Tags
        );
        Assert.Equal(relation, Assert.Single(call.Relations));
        Assert.Equal("501", created.ExternalId);
        Assert.Equal("3", created.Revision);
        Assert.Equal("managed", created.Candidate.SourceMetadata?["workSourceEnvironmentKey"]);
        Assert.Equal("3", created.Candidate.SourceMetadata?["revision"]);
    }

    [Fact]
    public async Task CreateThenPublishAssistanceStory_DefersReadyTagUntilPublication()
    {
        var client = new MockAzureDevOpsBoardsClient
        {
            CreateResult = new CreatedWorkItemResult
            {
                ExternalId = "502",
                Url = "https://dev.azure.com/testorg/TestProject/_workitems/edit/502",
                Revision = "1",
                Candidate = new WorkCandidate
                {
                    Id = "wi_502",
                    ExternalId = "502",
                    ExternalUrl =
                        "https://dev.azure.com/testorg/TestProject/_workitems/edit/502",
                    RepoKey = "widgets",
                    Tags = ["repo:widgets", "assistance:correlation-18"],
                    Source = "AzureDevOpsBoards",
                    SourceMetadata = new Dictionary<string, string>
                    {
                        ["revision"] = "1",
                    },
                },
            },
        };
        var environment = ManagedEnvironment("managed", Project) with
        {
            TagPrefix = "custom",
        };
        var workSource = CreateWorkSource(client, environment);

        var created = await workSource.CreateAssistanceStoryAsync(
            new CreateAssistanceStoryRequest
            {
                EnvironmentKey = environment.Key,
                RepoKey = "widgets",
                Title = "Continue PR 18",
                Description = "<p>Continue.</p>",
                CorrelationTags = ["assistance:correlation-18"],
                ReadyForClaim = false,
            },
            CancellationToken.None
        );

        Assert.Equal(
            ["repo:widgets", "assistance:correlation-18"],
            Assert.Single(client.CreateCalls).Tags
        );
        Assert.DoesNotContain("custom-ready-rework", client.CreateCalls[0].Tags);

        var published = await workSource.MakeAssistanceStoryReadyAsync(
            created.Candidate,
            CancellationToken.None
        );

        var update = Assert.Single(client.UpdateWorkItemStatusCalls);
        Assert.Equal("502", update.WorkRef.ExternalId);
        Assert.Equal("managed", update.WorkRef.EnvironmentKey);
        Assert.Equal(["custom-ready-rework"], update.Status.Tags);
        Assert.Contains("custom-ready-rework", published.Tags);
    }

    [Fact]
    public async Task ReactivateForReworkAsync_NoManagedEnvironment_ReturnsFailure()
    {
        var workSource = CreateWorkSourceWithoutManagedEnvironments();
        var request = new ReworkReactivateRequest
        {
            WorkItemId = "wi-1",
            WorkRef = new ExternalWorkRef
            {
                Source = "AzureDevOpsBoards",
                ExternalId = "1",
            },
            CycleNumber = 1,
            ThreadCount = 1,
            PullRequestUrl = "https://example.com/pr/1",
        };

        var result = await workSource.ReactivateForReworkAsync(
            request,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("managed", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // ReactivateForReworkAsync — single-PATCH success
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ReactivateForReworkAsync_Success_SinglePatchStripsAgentTagsAndAddsAgentReady()
    {
        // Arrange: mock client returns success for the merged PATCH
        var mockClient = new MockAzureDevOpsBoardsClient
        {
            UpdateWorkItemStatusAsyncReturns = true,
        };

        var workSource = CreateWorkSource(mockClient);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "5",
        };

        var request = new ReworkReactivateRequest
        {
            WorkItemId = "wi_42",
            WorkRef = workRef,
            CycleNumber = 2,
            ThreadCount = 3,
            PullRequestUrl = "https://dev.azure.com/testorg/TestProject/_git/repo/pullrequest/10",
        };

        // Act
        var result = await workSource.ReactivateForReworkAsync(request, CancellationToken.None);

        // Assert: reactivation succeeded
        Assert.True(result.Success);
        Assert.Null(result.FailureReason);

        // Assert: exactly one UpdateWorkItemStatusAsync call (single PATCH)
        Assert.Single(mockClient.UpdateWorkItemStatusCalls);
        var call = mockClient.UpdateWorkItemStatusCalls[0];

        // Assert: status is set to the managed profile's active state
        Assert.Equal(ActiveState, call.Status.Status);

        // Assert: RemovedTags contains active, failed, needs-human lifecycle tags and agent-worker:*
        Assert.NotNull(call.Status.RemovedTags);
        Assert.Contains(WorkSourceOptions.TagActive(), call.Status.RemovedTags);
        Assert.Contains(WorkSourceOptions.TagFailed(), call.Status.RemovedTags);
        Assert.Contains(
            WorkSourceOptions.TagNeedsHuman(),
            call.Status.RemovedTags
        );
        Assert.Contains("agent-worker:*", call.Status.RemovedTags);

        // Assert: Tags to add contains agent-ready
        Assert.NotNull(call.Status.Tags);
        Assert.Single(call.Status.Tags);
        Assert.Equal(WorkSourceOptions.TagReady(), call.Status.Tags[0]);

        // Assert: a rework-start comment was posted
        Assert.Single(mockClient.AddCommentCalls);
        Assert.Contains("Rework cycle 2 started", mockClient.AddCommentCalls[0]);
        Assert.Contains("3 review threads", mockClient.AddCommentCalls[0]);
    }

    [Fact]
    public async Task ReactivateForReworkAsync_PatchFailure_ReturnsFailureWithReworkTagStripFailed()
    {
        // Arrange: mock client returns false for the merged PATCH (simulates 412)
        var mockClient = new MockAzureDevOpsBoardsClient
        {
            UpdateWorkItemStatusAsyncReturns = false,
        };

        var workSource = CreateWorkSource(mockClient);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "5",
        };

        var request = new ReworkReactivateRequest
        {
            WorkItemId = "wi_42",
            WorkRef = workRef,
            CycleNumber = 1,
            ThreadCount = 1,
            PullRequestUrl = "https://dev.azure.com/testorg/TestProject/_git/repo/pullrequest/5",
        };

        // Act
        var result = await workSource.ReactivateForReworkAsync(request, CancellationToken.None);

        // Assert: reactivation failed with the expected diagnostic code
        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("[rework_tag_strip_failed]", result.FailureReason);
        Assert.Contains(ActiveState, result.FailureReason);

        // Assert: no comment was posted (comment is only posted on success)
        Assert.Empty(mockClient.AddCommentCalls);
    }

    [Fact]
    public async Task ReactivateForReworkAsync_UsesActiveStateAsTarget()
    {
        // Arrange: mock client with custom active state
        var mockClient = new MockAzureDevOpsBoardsClient
        {
            UpdateWorkItemStatusAsyncReturns = true,
        };

        var environment = ManagedEnvironment("managed", Project, activeState: "Ready");
        var workSource = CreateWorkSource(mockClient, environment);

        var request = new ReworkReactivateRequest
        {
            WorkItemId = "wi_10",
            WorkRef = new ExternalWorkRef { Source = "AzureDevOpsBoards", ExternalId = "10" },
            CycleNumber = 1,
            ThreadCount = 1,
            PullRequestUrl = "https://example.com/pr/1",
        };

        // Act
        var result = await workSource.ReactivateForReworkAsync(request, CancellationToken.None);

        // Assert: uses the managed profile's active state
        Assert.True(result.Success);
        Assert.Single(mockClient.UpdateWorkItemStatusCalls);
        Assert.Equal("Ready", mockClient.UpdateWorkItemStatusCalls[0].Status.Status);
    }

    [Fact]
    public async Task ReactivateForReworkAsync_NoActiveState_ReturnsFailure()
    {
        // Arrange: the managed profile has no active state configured.
        var mockClient = new MockAzureDevOpsBoardsClient();
        var environment = ManagedEnvironment("managed", Project, activeState: null);
        var workSource = CreateWorkSource(mockClient, environment);

        var request = new ReworkReactivateRequest
        {
            WorkItemId = "wi_1",
            WorkRef = new ExternalWorkRef { Source = "AzureDevOpsBoards", ExternalId = "1" },
            CycleNumber = 1,
            ThreadCount = 1,
            PullRequestUrl = "https://example.com/pr/1",
        };

        // Act
        var result = await workSource.ReactivateForReworkAsync(request, CancellationToken.None);

        // Assert: fails with clear reason about missing active state
        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains(
            "active state",
            result.FailureReason,
            StringComparison.OrdinalIgnoreCase
        );
    }

    // ──────────────────────────────────────────────
    // UpdateWorkItemStatusAsync — RemovedTags + 412 hard-fail (HTTP level)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTagsWithWildcard_FetchesCurrentTags_StripsAndPatches()
    {
        // Arrange: fake HTTP handler that returns current tags on GET and 200 on PATCH
        int requestCount = 0;
        string? patchBody = null;
        string? ifMatchValue = null;

        var handler = new CaptureHttpMessageHandler(
            async (req) =>
            {
                requestCount++;

                if (req.Method == HttpMethod.Get)
                {
                    // Return current work item with agent lifecycle tags
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemGetResponse(
                                42,
                                7,
                                "Resolved",
                                "agent-active; agent-worker:live-ado_worker; agent-ready; backend; high-priority"
                            ),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                if (req.Method == HttpMethod.Patch)
                {
                    if (req.Content is not null)
                        patchBody = await req.Content.ReadAsStringAsync();

                    ifMatchValue = req.Headers.TryGetValues("If-Match", out var values)
                        ? values.FirstOrDefault()
                        : null;

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemPatchResponse(42, 8),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        );

        var client = CreateClientWithHandler(handler);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "7",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[]
            {
                "agent-active",
                "agent-failed",
                "agent-needs-human",
                "agent-worker:*",
            },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: PATCH succeeded
        Assert.True(ok);

        // Assert: two requests — GET (read current) + PATCH (write back)
        Assert.Equal(2, requestCount);

        // Assert: If-Match header sent with revision
        Assert.Equal("7", ifMatchValue);

        // Assert: PATCH body contains state change and merged tags
        Assert.NotNull(patchBody);
        Assert.Contains("System.State", patchBody);
        Assert.Contains("New", patchBody);
        Assert.Contains("System.Tags", patchBody);
        Assert.Contains("agent-ready", patchBody);

        // Assert: agent lifecycle tags were stripped
        Assert.DoesNotContain("agent-active", patchBody!);
        Assert.DoesNotContain("agent-worker:", patchBody!);
        Assert.DoesNotContain("agent-failed", patchBody!);
        Assert.DoesNotContain("agent-needs-human", patchBody!);

        // Assert: unrelated tags preserved
        Assert.Contains("backend", patchBody!);
        Assert.Contains("high-priority", patchBody!);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTags_412ReturnsFalse()
    {
        // Arrange: fake handler returns 412 on the PATCH
        var handler = new CaptureHttpMessageHandler(
            async (req) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemGetResponse(
                                42,
                                7,
                                "Resolved",
                                "agent-active; agent-worker:live-ado_worker"
                            ),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                if (req.Method == HttpMethod.Patch)
                {
                    // Simulate 412 Precondition Failed (concurrent modification)
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                    {
                        Content = new StringContent(
                            """{"message":"The resource has been modified by another user."}""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        );

        var client = CreateClientWithHandler(handler);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "7",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[] { "agent-active", "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: Returns false for RemovedTags-bearing PATCH on 412 (hard-fail)
        Assert.False(ok);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_NoRemovedTags_412ReturnsTrue_BestEffort()
    {
        // Arrange: fake handler returns 412 on the PATCH, but no RemovedTags
        var handler = new CaptureHttpMessageHandler(
            async (req) =>
            {
                if (req.Method == HttpMethod.Patch)
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                    {
                        Content = new StringContent(
                            """{"message":"The resource has been modified."}""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        );

        var client = CreateClientWithHandler(handler);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "3",
        };

        var status = new ExternalWorkStatus
        {
            Status = "Active",
            // No RemovedTags — status-only projection
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: Returns true for status-only PATCH on 412 (best-effort)
        Assert.True(ok);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTags_NonSuccessNon412_ReturnsFalse()
    {
        // Arrange: fake handler returns 500 on the PATCH
        var handler = new CaptureHttpMessageHandler(
            async (req) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemGetResponse(42, 7, "Resolved", "agent-active"),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                if (req.Method == HttpMethod.Patch)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent(
                            """{"message":"Server error"}""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        );

        var client = CreateClientWithHandler(handler);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "7",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            RemovedTags = new[] { "agent-active" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: Non-success (non-412) always returns false
        Assert.False(ok);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTagsWithWildcard_StripsConcreteAgentWorkerTag()
    {
        // Arrange: work item has a concrete agent-worker:live-ado_worker tag
        string? patchBody = null;

        var handler = new CaptureHttpMessageHandler(
            async (req) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemGetResponse(
                                10,
                                3,
                                "Resolved",
                                "agent-active; agent-worker:live-ado_worker; backend"
                            ),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                if (req.Method == HttpMethod.Patch)
                {
                    if (req.Content is not null)
                        patchBody = await req.Content.ReadAsStringAsync();

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            WorkItemPatchResponse(10, 4),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        );

        var client = CreateClientWithHandler(handler);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "10",
            Revision = "3",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[] { "agent-active", "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert
        Assert.True(ok);
        Assert.NotNull(patchBody);
        // agent-worker:live-ado_worker should be stripped by the wildcard
        Assert.DoesNotContain("agent-worker:", patchBody);
        Assert.DoesNotContain("agent-active", patchBody);
        // Unrelated tags preserved
        Assert.Contains("backend", patchBody);
        Assert.Contains("agent-ready", patchBody);
    }

    [Fact]
    public async Task DiagnosticDiscovery_MergesFiltersAndPagesWithoutFetchingUnselectedDetails()
    {
        var first = new MockAzureDevOpsBoardsClient
        {
            ReferenceResults = [new(3), new(1)],
            DetailResults = [Candidate(1), Candidate(3)],
        };
        var second = new MockAzureDevOpsBoardsClient
        {
            ReferenceResults = [new(2)],
            DetailResults = [Candidate(2)],
        };
        var source = CreateWorkSource(
            new Dictionary<string, IAzureDevOpsBoardsClient>
            {
                ["z-source"] = second,
                ["a-source"] = first,
            },
            [ManagedEnvironment("z-source", "Project Z"), ManagedEnvironment("a-source", "Project A")]
        );

        var result = await source.ListAsync(
            new ManagedBoardItemDiscoveryQuery { Page = 2, PageSize = 2 },
            CancellationToken.None
        );

        Assert.Equal(3, result.Total);
        var item = Assert.Single(result.Items);
        Assert.Equal("z-source", item.WorkSourceEnvironmentKey);
        Assert.Equal("Project Z", item.Project);
        Assert.Equal("2", item.Item.ExternalId);
        Assert.Empty(first.DetailCalls);
        Assert.Equal([2], Assert.Single(second.DetailCalls).Ids);
        Assert.Equal(BoardTerminalStates.Values, Assert.Single(first.ReferenceCalls).ExcludedStates);
    }

    [Fact]
    public async Task DiagnosticDiscovery_IncludeTerminalAndEnvironmentFilter_RemoveOnlyTerminalConstraint()
    {
        var selected = new MockAzureDevOpsBoardsClient { ReferenceResults = [new(7)], DetailResults = [Candidate(7)] };
        var ignored = new MockAzureDevOpsBoardsClient { ReferenceResults = [new(8)] };
        var source = CreateWorkSource(
            new Dictionary<string, IAzureDevOpsBoardsClient> { ["selected"] = selected, ["ignored"] = ignored },
            [ManagedEnvironment("ignored", "Other"), ManagedEnvironment("selected", "Selected")]
        );

        var result = await source.ListAsync(
            new ManagedBoardItemDiscoveryQuery
            {
                WorkSourceEnvironmentKey = "SELECTED",
                IncludeTerminal = true,
            },
            CancellationToken.None
        );

        Assert.Single(result.Items);
        Assert.Null(Assert.Single(selected.ReferenceCalls).ExcludedStates);
        Assert.Empty(ignored.ReferenceCalls);
        Assert.Null(Assert.Single(selected.ReferenceCalls).Tags);
        Assert.Null(Assert.Single(selected.ReferenceCalls).ExcludedTags);
    }

    [Fact]
    public async Task DiagnosticDiscovery_IsolatesEnvironmentFailuresAndHonorsCancellation()
    {
        var successful = new MockAzureDevOpsBoardsClient { ReferenceResults = [new(1)], DetailResults = [Candidate(1)] };
        var failed = new MockAzureDevOpsBoardsClient { DiscoveryException = new HttpRequestException("secret detail") };
        var source = CreateWorkSource(
            new Dictionary<string, IAzureDevOpsBoardsClient> { ["good"] = successful, ["bad"] = failed },
            [ManagedEnvironment("good", "Good"), ManagedEnvironment("bad", "Bad")]
        );

        var result = await source.ListAsync(new(), CancellationToken.None);

        Assert.Single(result.Items);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("bad", failure.WorkSourceEnvironmentKey);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.OrdinalIgnoreCase);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            source.ListAsync(new(), cancellation.Token));
    }

    [Fact]
    public async Task DiagnosticDiscovery_IncludesConfiguredEnvironmentDisabledForPolling()
    {
        var client = new MockAzureDevOpsBoardsClient
        {
            ReferenceResults = [new(4)],
            DetailResults = [Candidate(4)],
        };
        var disabled = ManagedEnvironment("disabled", "Disabled Project") with { Enabled = false };
        var source = CreateWorkSource(
            new Dictionary<string, IAzureDevOpsBoardsClient> { ["disabled"] = client },
            [disabled]
        );

        var result = await source.ListAsync(new(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("disabled", item.WorkSourceEnvironmentKey);
        Assert.Equal("Disabled Project", item.Project);
    }

    [Theory]
    [InlineData("{\"value\":[]}", 0)]
    [InlineData("{\"workItems\":[{\"id\":1}]}", 1)]
    public async Task DiagnosticDiscovery_IsolatesMalformedProviderResponses(
        string wiqlResponse,
        int expectedTotal
    )
    {
        var handler = new CaptureHttpMessageHandler(req => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    req.RequestUri!.AbsolutePath.EndsWith("/wiql", StringComparison.OrdinalIgnoreCase)
                        ? wiqlResponse
                        : "{\"notValue\":[]}",
                    Encoding.UTF8,
                    "application/json"
                ),
            }
        ));
        var source = CreateWorkSource(CreateClientWithHandler(handler));

        var result = await source.ListAsync(new(), CancellationToken.None);

        Assert.Equal(expectedTotal, result.Total);
        Assert.Empty(result.Items);
        Assert.Single(result.Failures);
    }

    [Fact]
    public async Task DiagnosticDiscovery_DetailFailureDoesNotBlankOtherEnvironments()
    {
        var failed = new MockAzureDevOpsBoardsClient
        {
            ReferenceResults = [new(1)],
            DetailException = new HttpRequestException("sensitive detail"),
        };
        var successful = new MockAzureDevOpsBoardsClient
        {
            ReferenceResults = [new(2)],
            DetailResults = [Candidate(2)],
        };
        var source = CreateWorkSource(
            new Dictionary<string, IAzureDevOpsBoardsClient>
            {
                ["bad"] = failed,
                ["good"] = successful,
            },
            [ManagedEnvironment("bad", "Bad"), ManagedEnvironment("good", "Good")]
        );

        var result = await source.ListAsync(new(), CancellationToken.None);

        Assert.Equal(2, result.Total);
        Assert.Equal("2", Assert.Single(result.Items).Item.ExternalId);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("bad", failure.WorkSourceEnvironmentKey);
        Assert.DoesNotContain("sensitive", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnosticDetailClient_ChunksRequestsAtAzureDevOpsBatchLimit()
    {
        var batchSizes = new List<int>();
        var handler = new CaptureHttpMessageHandler(async req =>
        {
            using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
            var ids = body.RootElement.GetProperty("ids").EnumerateArray()
                .Select(value => value.GetInt32())
                .ToArray();
            batchSizes.Add(ids.Length);
            var values = ids.Select(id => JsonSerializer.Deserialize<object>(
                WorkItemGetResponse(id, 1, "New", string.Empty))!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { value = values }),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var client = CreateClientWithHandler(handler);

        var result = await client.GetWorkItemsAsync(
            Project,
            Enumerable.Range(1, 401).ToArray(),
            CancellationToken.None
        );

        Assert.Equal(401, result.Count);
        Assert.Equal([200, 200, 1], batchSizes);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task DiagnosticDiscovery_RejectsInvalidPagination(int page, int pageSize)
    {
        var source = CreateWorkSourceWithoutManagedEnvironments();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.ListAsync(
            new ManagedBoardItemDiscoveryQuery { Page = page, PageSize = pageSize },
            CancellationToken.None));
    }

    private static WorkCandidate Candidate(int id) => new()
    {
        Id = $"wi_{id}",
        ExternalId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Title = $"Item {id}",
        Source = "AzureDevOpsBoards",
    };

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="AzureDevOpsBoardsWorkSource"/> wired to a mock client.
    /// </summary>
    private static AzureDevOpsBoardsWorkSource CreateWorkSource(
        IAzureDevOpsBoardsClient mockClient,
        WorkSourceEnvironmentProfile? environment = null
    )
    {
        environment ??= ManagedEnvironment("managed", Project);
        var services = new ServiceCollection();
        services.AddSingleton<IManagedProfileResolver>(
            new StubManagedProfileResolver([environment])
        );
        services.AddSingleton<IAzureDevOpsBoardsClientFactory>(
            new StubClientFactory(
                new Dictionary<string, IAzureDevOpsBoardsClient>
                {
                    [environment.Key] = mockClient,
                }
            )
        );
        var provider = services.BuildServiceProvider();

        return new AzureDevOpsBoardsWorkSource(new DelegatingScopeFactory(provider));
    }

    private static AzureDevOpsBoardsWorkSource CreateWorkSource(
        IReadOnlyDictionary<string, IAzureDevOpsBoardsClient> clients,
        IReadOnlyList<WorkSourceEnvironmentProfile> environments
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IManagedProfileResolver>(new StubManagedProfileResolver(environments));
        services.AddSingleton<IAzureDevOpsBoardsClientFactory>(new StubClientFactory(clients));
        var provider = services.BuildServiceProvider();
        return new AzureDevOpsBoardsWorkSource(new DelegatingScopeFactory(provider));
    }

    private static AzureDevOpsBoardsWorkSource CreateWorkSourceWithoutManagedEnvironments()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IManagedProfileResolver>(new StubManagedProfileResolver([]));
        var provider = services.BuildServiceProvider();

        return new AzureDevOpsBoardsWorkSource(new DelegatingScopeFactory(provider));
    }

    private static WorkSourceEnvironmentProfile ManagedEnvironment(
        string key,
        string project,
        string? activeState = ActiveState
    )
    {
        return new WorkSourceEnvironmentProfile
        {
            Key = key,
            DisplayName = key,
            Enabled = true,
            Provider = "AzureDevOpsBoards",
            TagPrefix = "agent",
            ConnectionKey = $"azuredevops-{key}",
            Project = project,
            ActiveState = activeState,
        };
    }

    /// <summary>
    /// Creates an <see cref="AzureDevOpsBoardsClient"/> wired to a custom HTTP handler.
    /// </summary>
    private static AzureDevOpsBoardsClient CreateClientWithHandler(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "test-pat",
        };

        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(authBytes)
        );

        return new AzureDevOpsBoardsClient(
            http,
            options,
            NullLogger<AzureDevOpsBoardsClient>.Instance
        );
    }

    /// <summary>
    /// Simple <see cref="IServiceScopeFactory"/> that resolves from a static provider.
    /// </summary>
    private sealed class DelegatingScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _provider;

        public DelegatingScopeFactory(IServiceProvider provider)
        {
            _provider = provider;
        }

        public IServiceScope CreateScope()
        {
            return new DelegatingScope(_provider);
        }
    }

    private sealed class DelegatingScope : IServiceScope
    {
        private readonly IServiceProvider _provider;

        public DelegatingScope(IServiceProvider provider)
        {
            _provider = provider;
        }

        public IServiceProvider ServiceProvider => _provider;

        public void Dispose() { }
    }

    private sealed class StubManagedProfileResolver(
        IReadOnlyList<WorkSourceEnvironmentProfile> profiles
    ) : IManagedProfileResolver
    {
        public Task<ResolvedControllerProfiles?> ResolveForRepositoryAsync(
            string repositoryKey,
            CancellationToken cancellationToken
        ) => Task.FromResult<ResolvedControllerProfiles?>(null);

        public Task<ResolvedWorkSourceEnvironment?> ResolveWorkSourceEnvironmentAsync(
            string? key,
            CancellationToken cancellationToken
        )
        {
            WorkSourceEnvironmentProfile? profile;
            if (string.IsNullOrWhiteSpace(key))
            {
                profile = profiles.Count > 0 ? profiles[0] : null;
            }
            else
            {
                profile = profiles.SingleOrDefault(candidate => candidate.Key == key);
            }
            return Task.FromResult(
                profile is null
                    ? null
                    : new ResolvedWorkSourceEnvironment(profile, Connection: null)
            );
        }

        public Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ListWorkSourceEnvironmentsAsync(
            CancellationToken cancellationToken
        ) => ResolveProfiles(profiles.Where(profile => profile.Enabled));

        public Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ListConfiguredWorkSourceEnvironmentsAsync(
            CancellationToken cancellationToken
        ) => ResolveProfiles(profiles);

        private static Task<IReadOnlyList<ResolvedWorkSourceEnvironment>> ResolveProfiles(
            IEnumerable<WorkSourceEnvironmentProfile> selectedProfiles
        ) => Task.FromResult<IReadOnlyList<ResolvedWorkSourceEnvironment>>(
            selectedProfiles
                .Select(profile => new ResolvedWorkSourceEnvironment(profile, Connection: null))
                .ToList()
        );
    }

    private sealed class StubClientFactory(
        IReadOnlyDictionary<string, IAzureDevOpsBoardsClient> clients
    ) : IAzureDevOpsBoardsClientFactory
    {
        public Task<IAzureDevOpsBoardsClient> CreateAsync(
            ResolvedWorkSourceEnvironment resolved,
            CancellationToken ct
        ) => Task.FromResult(clients[resolved.Profile.Key]);
    }

    /// <summary>
    /// Mock <see cref="IAzureDevOpsBoardsClient"/> that records calls and returns
    /// pre-configured results for <see cref="IAzureDevOpsBoardsClient.UpdateWorkItemStatusAsync"/>
    /// and <see cref="IAzureDevOpsBoardsClient.AddCommentAsync"/>.
    /// </summary>
    private sealed class MockAzureDevOpsBoardsClient : IAzureDevOpsBoardsClient
    {
        public bool UpdateWorkItemStatusAsyncReturns { get; set; } = true;
        public ClaimResult ClaimResult { get; init; } = new() { Success = false };
        public CreatedWorkItemResult CreateResult { get; init; } = new();
        public IReadOnlyList<WorkCandidate> QueryResults { get; init; } = [];
        public IReadOnlyList<AzureDevOpsWorkItemReference> ReferenceResults { get; init; } = [];
        public IReadOnlyList<WorkCandidate> DetailResults { get; init; } = [];
        public Exception? DiscoveryException { get; init; }
        public Exception? DetailException { get; init; }
        public List<BoardsQueryParameters> QueryCalls { get; } = [];
        public List<BoardsQueryParameters> ReferenceCalls { get; } = [];
        public List<(string Project, IReadOnlyList<int> Ids)> DetailCalls { get; } = [];
        public List<BoardsCreateWorkItemParameters> CreateCalls { get; } = [];
        public List<ClaimRequest> ClaimCalls { get; } = [];
        public List<ReleaseClaimRequest> ReleaseClaimCalls { get; } = [];
        public List<(
            ExternalWorkRef WorkRef,
            ExternalWorkStatus Status
        )>
        UpdateWorkItemStatusCalls
        { get; } = new();

        public List<string> AddCommentCalls { get; } = new();

        public Task<IReadOnlyList<WorkCandidate>> QueryWorkItemsAsync(
            BoardsQueryParameters parameters,
            CancellationToken ct
        )
        {
            QueryCalls.Add(parameters);
            return Task.FromResult(QueryResults);
        }

        public Task<IReadOnlyList<AzureDevOpsWorkItemReference>> QueryWorkItemReferencesAsync(
            BoardsQueryParameters parameters,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            if (DiscoveryException is not null) throw DiscoveryException;
            ReferenceCalls.Add(parameters);
            return Task.FromResult(ReferenceResults);
        }

        public Task<IReadOnlyList<WorkCandidate>> GetWorkItemsAsync(
            string project,
            IReadOnlyList<int> ids,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            if (DetailException is not null) throw DetailException;
            if (DiscoveryException is not null) throw DiscoveryException;
            DetailCalls.Add((project, ids));
            var selected = DetailResults
                .Where(item => ids.Contains(int.Parse(item.ExternalId, System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkCandidate>>(selected);
        }

        public Task<CreatedWorkItemResult> CreateWorkItemAsync(
            BoardsCreateWorkItemParameters parameters,
            CancellationToken ct
        )
        {
            CreateCalls.Add(parameters);
            return Task.FromResult(CreateResult);
        }

        public Task<ClaimResult> TryClaimWorkItemAsync(
            ExternalWorkRef workRef,
            ClaimRequest claim,
            CancellationToken ct
        )
        {
            ClaimCalls.Add(claim);
            return Task.FromResult(ClaimResult);
        }

        public Task<bool> UpdateWorkItemStatusAsync(
            ExternalWorkRef workRef,
            ExternalWorkStatus status,
            CancellationToken ct
        )
        {
            UpdateWorkItemStatusCalls.Add((workRef, status));
            return Task.FromResult(UpdateWorkItemStatusAsyncReturns);
        }

        public Task AddCommentAsync(ExternalWorkRef workRef, string comment, CancellationToken ct)
        {
            AddCommentCalls.Add(comment);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
            ExternalWorkRef workRef,
            int maxComments,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<WorkItemComment>>(Array.Empty<WorkItemComment>());

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
            string project,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<RepositoryInfo>>(Array.Empty<RepositoryInfo>());

        public Task ReleaseClaimWorkItemAsync(ReleaseClaimRequest request, CancellationToken ct)
        {
            ReleaseClaimCalls.Add(request);
            return Task.CompletedTask;
        }

        public Task<AzureDevOpsConnectivityResult> VerifyConnectivityAsync(
            string organizationUrl,
            string project,
            string personalAccessToken,
            CancellationToken ct
        ) => Task.FromResult(new AzureDevOpsConnectivityResult { Success = false });

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetValidStatesAsync(
            string project,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(
            new Dictionary<string, IReadOnlyList<string>>());

        public Task<IReadOnlyList<string>> ListBranchesAsync(
            string project,
            string repositoryId,
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    /// <summary>
    /// Fake <see cref="HttpMessageHandler"/> that delegates to an async callback
    /// for each request, enabling inspection of request headers and body.
    /// </summary>
    private sealed class CaptureHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public CaptureHttpMessageHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> handler
        )
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return await _handler(request);
        }
    }

    /// <summary>
    /// Builds a JSON response for a single work item GET (for claim/tag verification).
    /// </summary>
    private static string WorkItemGetResponse(int id, int rev, string state, string tags)
    {
        return JsonSerializer.Serialize(
            new
            {
                id,
                rev,
                fields = new Dictionary<string, object>
                {
                    ["System.Id"] = id,
                    ["System.Title"] = "Test work item",
                    ["System.State"] = state,
                    ["System.Tags"] = tags,
                },
                _links = new { self = new { href = $"{OrgUrl}/_apis/wit/workItems/{id}" } },
                url = $"{OrgUrl}/_apis/wit/workItems/{id}",
            }
        );
    }

    /// <summary>
    /// Builds a JSON response for a successful work item PATCH.
    /// </summary>
    private static string WorkItemPatchResponse(int id, int rev)
    {
        return JsonSerializer.Serialize(
            new
            {
                id,
                rev,
                fields = new Dictionary<string, object>
                {
                    ["System.Id"] = id,
                    ["System.Rev"] = rev,
                },
                _links = new { self = new { href = $"{OrgUrl}/_apis/wit/workItems/{id}" } },
                url = $"{OrgUrl}/_apis/wit/workItems/{id}",
            }
        );
    }
}
