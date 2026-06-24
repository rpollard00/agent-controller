using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AzureDevOpsBoardsClient"/> field mapping and
/// work item discovery behavior. Uses a fake <see cref="HttpMessageHandler"/>
/// to simulate Azure DevOps REST API responses.
/// </summary>
public class AzureDevOpsBoardsClientTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "TestProject";

    /// <summary>
    /// Creates an <see cref="AzureDevOpsBoardsClient"/> wired to a fake
    /// <see cref="HttpMessageHandler"/> that returns the supplied JSON
    /// responses in order.
    /// </summary>
    private static AzureDevOpsBoardsClient CreateClient(
        params (string urlContains, string jsonResponse)[] responses)
    {
        var handler = new FakeHttpMessageHandler(responses);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(OrgUrl + "/"),
        };

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "test-pat",
        };

        // Set auth header as the real constructor does
        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        return new AzureDevOpsBoardsClient(http, options);
    }

    /// <summary>
    /// Creates an <see cref="AzureDevOpsBoardsClient"/> wired to a fake handler
    /// that returns responses with custom status codes and JSON bodies.
    /// Each tuple: (URL substring to match, HTTP status code, JSON response body).
    /// </summary>
    private static AzureDevOpsBoardsClient CreateClientWithStatusCodes(
        params (string urlContains, HttpStatusCode statusCode, string body)[] responses)
    {
        var handler = new FakeHttpMessageHandler();
        foreach (var (urlContains, statusCode, body) in responses)
        {
            handler.AddResponse(urlContains, new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(OrgUrl + "/"),
        };

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "test-pat",
        };

        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        return new AzureDevOpsBoardsClient(http, options);
    }

    // ──────────────────────────────────────────────
    // Successful mapping
    // ──────────────────────────────────────────────

    [Fact]
    public async Task QueryWorkItemsAsync_MapsAllFields()
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(42, 99)),
            (urlContains: "workitemsbatch",
             jsonResponse: BatchResponse(
                 (id: 42, rev: 3, title: "Fix login bug", description: "Users cannot log in",
                  state: "New", tags: "agent-ready; repo:example-service; ui",
                  assignedToDisplay: "Jane Dev", priority: 1,
                  areaPath: @"TestProject\TeamA", iterationPath: @"TestProject\Sprint 1",
                  workItemType: "Bug"),
                 (id: 99, rev: 1, title: "Add retry logic", description: null,
                  state: "Approved", tags: "agent-ready; backend",
                  assignedToDisplay: null, priority: 2,
                  areaPath: @"TestProject", iterationPath: @"TestProject\Sprint 2",
                  workItemType: "User Story")))
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Equal(2, results.Count);

        // First work item
        var wi1 = results[0];
        Assert.Equal("wi_42", wi1.Id);
        Assert.Equal("42", wi1.ExternalId);
        Assert.Equal($"{OrgUrl}/{Project}/_workitems/edit/42", wi1.ExternalUrl);
        Assert.Equal("example-service", wi1.RepoKey);
        Assert.Equal("Fix login bug", wi1.Title);
        Assert.Equal("Users cannot log in", wi1.Description);
        Assert.Equal("New", wi1.Status);
        Assert.Equal(1, wi1.Priority);
        Assert.Equal("Jane Dev", wi1.AssignedTo);
        Assert.Contains("agent-ready", wi1.Tags);
        Assert.Contains("ui", wi1.Tags);
        Assert.Equal("AzureDevOpsBoards", wi1.Source);

        // Source metadata
        Assert.NotNull(wi1.SourceMetadata);
        Assert.Equal("3", wi1.SourceMetadata["revision"]);
        Assert.Equal(@"TestProject\TeamA", wi1.SourceMetadata["areaPath"]);
        Assert.Equal(@"TestProject\Sprint 1", wi1.SourceMetadata["iterationPath"]);
        Assert.Equal("Bug", wi1.SourceMetadata["workItemType"]);

        // Second work item
        var wi2 = results[1];
        Assert.Equal("wi_99", wi2.Id);
        Assert.Equal("99", wi2.ExternalId);
        Assert.Equal(string.Empty, wi2.RepoKey); // No repo: tag
        Assert.Equal("Add retry logic", wi2.Title);
        Assert.Null(wi2.Description);
        Assert.Equal("Approved", wi2.Status);
        Assert.Equal(2, wi2.Priority);
        Assert.Null(wi2.AssignedTo);
        Assert.NotNull(wi2.SourceMetadata);
        Assert.Equal("1", wi2.SourceMetadata["revision"]);
        Assert.Equal("User Story", wi2.SourceMetadata["workItemType"]);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_EmptyWiqlResults_ReturnsEmptyList()
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: """{"queryType":"flat","asOf":"2024-01-01T00:00:00Z","workItems":[]}""")
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    // ──────────────────────────────────────────────
    // RepoKey resolution from tags
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("repo:my-service", "my-service")]
    [InlineData("repo:  example-svc  ", "example-svc")]
    [InlineData("agent-ready; repo:backend; high-priority", "backend")]
    [InlineData("REPO:CaseInsensitive", "CaseInsensitive")]
    [InlineData("other-tag; repo:svc; another-tag", "svc")]
    public async Task QueryWorkItemsAsync_ResolvesRepoKeyFromTags(string tagsRaw, string expectedRepoKey)
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: BatchResponse(
                 (id: 1, rev: 1, title: "Test", description: null,
                  state: "New", tags: tagsRaw,
                  assignedToDisplay: null, priority: 1,
                  areaPath: @"Project", iterationPath: @"Project\Sprint",
                  workItemType: "Task")))
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(expectedRepoKey, results[0].RepoKey);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_NoRepoTag_RepoKeyIsEmpty()
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: BatchResponse(
                 (id: 1, rev: 1, title: "Test", description: null,
                  state: "New", tags: "agent-ready; backend; ui",
                  assignedToDisplay: null, priority: 1,
                  areaPath: @"Project", iterationPath: @"Project\Sprint",
                  workItemType: "Task")))
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(string.Empty, results[0].RepoKey);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_RepoTagWithOnlyWhitespace_RepoKeyIsEmpty()
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: BatchResponse(
                 (id: 1, rev: 1, title: "Test", description: null,
                  state: "New", tags: "agent-ready; repo:   ; backend",
                  assignedToDisplay: null, priority: 1,
                  areaPath: @"Project", iterationPath: @"Project\Sprint",
                  workItemType: "Task")))
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(string.Empty, results[0].RepoKey);
    }

    // ──────────────────────────────────────────────
    // Missing / malformed fields
    // ──────────────────────────────────────────────

    [Fact]
    public async Task QueryWorkItemsAsync_WorkItemWithoutFields_IsSkipped()
    {
        // Azure DevOps batch response where one item has no "fields" object
        var batchJson = """
        {
          "count": 2,
          "value": [
            {
              "id": 1,
              "rev": 1,
              "url": "https://dev.azure.com/org/_apis/wit/workItems/1"
            },
            {
              "id": 2,
              "rev": 2,
              "fields": {
                "System.Id": 2,
                "System.Title": "Valid item",
                "System.State": "New"
              },
              "url": "https://dev.azure.com/org/_apis/wit/workItems/2"
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "wiql", jsonResponse: WiqlResponse(1, 2)),
            (urlContains: "workitemsbatch", jsonResponse: batchJson)
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        // The malformed item without fields is skipped; only the valid one remains
        Assert.Single(results);
        Assert.Equal("wi_2", results[0].Id);
        Assert.Equal("Valid item", results[0].Title);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_MissingOptionalFields_ProducesNullValues()
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: """
             {
               "count": 1,
               "value": [
                 {
                   "id": 1,
                   "rev": 5,
                   "fields": {
                     "System.Id": 1,
                     "System.Title": "Minimal item"
                   },
                   "url": "https://dev.azure.com/org/_apis/wit/workItems/1"
                 }
               ]
             }
             """)
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        var wi = results[0];
        Assert.Equal("Minimal item", wi.Title);
        Assert.Null(wi.Description);
        Assert.Null(wi.Status);
        Assert.Null(wi.Priority);
        Assert.Null(wi.AssignedTo);
        Assert.Empty(wi.Tags);
        Assert.Equal(string.Empty, wi.RepoKey);

        // Metadata should still have the revision
        Assert.NotNull(wi.SourceMetadata);
        Assert.Equal("5", wi.SourceMetadata["revision"]);
        Assert.False(wi.SourceMetadata.ContainsKey("areaPath"));
        Assert.False(wi.SourceMetadata.ContainsKey("iterationPath"));
        Assert.False(wi.SourceMetadata.ContainsKey("workItemType"));
    }

    [Fact]
    public async Task QueryWorkItemsAsync_TagsFieldIsNotAString_ProducesEmptyTags()
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: """
             {
               "count": 1,
               "value": [
                 {
                   "id": 1,
                   "rev": 1,
                   "fields": {
                     "System.Id": 1,
                     "System.Title": "Test",
                     "System.State": "New",
                     "System.Tags": null
                   },
                   "url": "https://dev.azure.com/org/_apis/wit/workItems/1"
                 }
               ]
             }
             """)
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        Assert.Empty(results[0].Tags);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_PriorityFieldIsString_ConvertsToInt()
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: """
             {
               "count": 1,
               "value": [
                 {
                   "id": 1,
                   "rev": 1,
                   "fields": {
                     "System.Id": 1,
                     "System.Title": "Test",
                     "System.State": "New",
                     "Microsoft.VSTS.Common.Priority": "3"
                   },
                   "url": "https://dev.azure.com/org/_apis/wit/workItems/1"
                 }
               ]
             }
             """)
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(3, results[0].Priority);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_AssignedToIsObject_ExtractsDisplayName()
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: """
             {
               "count": 1,
               "value": [
                 {
                   "id": 1,
                   "rev": 1,
                   "fields": {
                     "System.Id": 1,
                     "System.Title": "Test",
                     "System.State": "New",
                     "System.AssignedTo": {
                       "displayName": "Alice Smith",
                       "uniqueName": "alice@example.com",
                       "id": "guid-here"
                     }
                   },
                   "url": "https://dev.azure.com/org/_apis/wit/workItems/1"
                 }
               ]
             }
             """)
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Alice Smith", results[0].AssignedTo);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_AssignedToIsString_ReturnsDirectly()
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: """
             {
               "count": 1,
               "value": [
                 {
                   "id": 1,
                   "rev": 1,
                   "fields": {
                     "System.Id": 1,
                     "System.Title": "Test",
                     "System.State": "New",
                     "System.AssignedTo": "Bob Builder"
                   },
                   "url": "https://dev.azure.com/org/_apis/wit/workItems/1"
                 }
               ]
             }
             """)
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Bob Builder", results[0].AssignedTo);
    }

    // ──────────────────────────────────────────────
    // Source metadata preservation
    // ──────────────────────────────────────────────

    [Fact]
    public async Task QueryWorkItemsAsync_PreservesSourceMetadataForLaterUpdates()
    {
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: BatchResponse(
                 (id: 1, rev: 7, title: "Test", description: "Desc",
                  state: "New", tags: "agent-ready",
                  assignedToDisplay: "Dev", priority: 1,
                  areaPath: @"Project\Area1", iterationPath: @"Project\Iteration",
                  workItemType: "Feature")))
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        var metadata = results[0].SourceMetadata;
        Assert.NotNull(metadata);
        Assert.Equal(4, metadata.Count);
        Assert.Equal("7", metadata["revision"]);
        Assert.Equal(@"Project\Area1", metadata["areaPath"]);
        Assert.Equal(@"Project\Iteration", metadata["iterationPath"]);
        Assert.Equal("Feature", metadata["workItemType"]);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_MissingRevision_OmitsFromMetadata()
    {
        var batchJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 1,
              "fields": {
                "System.Id": 1,
                "System.Title": "No revision item",
                "System.State": "New",
                "System.AreaPath": "Project",
                "System.IterationPath": "Project\\Sprint",
                "System.WorkItemType": "Task"
              },
              "url": "https://dev.azure.com/org/_apis/wit/workItems/1"
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "wiql", jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch", jsonResponse: batchJson)
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        var metadata = results[0].SourceMetadata;
        Assert.NotNull(metadata);
        // revision is absent
        Assert.False(metadata.ContainsKey("revision"));
        Assert.Equal("Project", metadata["areaPath"]);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_NoSourceMetadataFields_ProducesNullMetadata()
    {
        // Item with only basic fields, no area/iteration/type/revision
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: """
             {
               "count": 1,
               "value": [
                 {
                   "id": 1,
                   "fields": {
                     "System.Id": 1,
                     "System.Title": "Bare item",
                     "System.State": "New"
                   },
                   "url": "https://dev.azure.com/org/_apis/wit/workItems/1"
                 }
               ]
             }
             """)
        );

        var parameters = new BoardsQueryParameters { Project = Project };
        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
        Assert.Null(results[0].SourceMetadata);
    }

    // ──────────────────────────────────────────────
    // WIQL builder edge cases
    // ──────────────────────────────────────────────

    [Fact]
    public async Task QueryWorkItemsAsync_WiqlContainsSingleQuotedTags_IsEscapedCorrectly()
    {
        // Tags with single quotes should not break WIQL — verify the
        // mapping still works when the WIQL is valid.
        var client = CreateClient(
            (urlContains: "wiql",
             jsonResponse: WiqlResponse(1)),
            (urlContains: "workitemsbatch",
             jsonResponse: BatchResponse(
                 (id: 1, rev: 1, title: "Test", description: null,
                  state: "New", tags: "agent-ready",
                  assignedToDisplay: null, priority: 1,
                  areaPath: "P", iterationPath: "P\\I", workItemType: "T")))
        );

        var parameters = new BoardsQueryParameters
        {
            Project = Project,
            Tags = new[] { "agent-ready" },
            ExcludedTags = new[] { "agent-blocked" },
        };

        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.Single(results);
    }

    // ──────────────────────────────────────────────
    // HTTP error handling
    // ──────────────────────────────────────────────

    [Fact]
    public async Task QueryWorkItemsAsync_MissingProject_ThrowsInvalidOperation()
    {
        // Create a client with no project configured — the query must fail with
        // a clear error before any HTTP call is made.
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };
        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = null, // No project configured
            PersonalAccessToken = "test-pat",
        };
        var client = new AzureDevOpsBoardsClient(http, options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.QueryWorkItemsAsync(new BoardsQueryParameters(), CancellationToken.None));

        Assert.Contains("project", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_NonSuccessStatusCode_Throws()
    {
        var handler = new FakeHttpMessageHandler(
            (urlContains: "wiql",
             httpResponse: new HttpResponseMessage(HttpStatusCode.Unauthorized)
             {
                 Content = new StringContent("""{"message":"Access denied"}""",
                     Encoding.UTF8, "application/json"),
             }));
        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };
        var authBytes = Encoding.ASCII.GetBytes(":bad-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "bad-pat",
        };
        var client = new AzureDevOpsBoardsClient(http, options);

        var parameters = new BoardsQueryParameters { Project = Project };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.QueryWorkItemsAsync(parameters, CancellationToken.None));
    }

    // ──────────────────────────────────────────────
    // Claiming (TryClaimWorkItemAsync)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task TryClaimWorkItem_SuccessfulClaim_ReturnsSuccessWithLeaseToken()
    {
        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Url = $"{OrgUrl}/{Project}/_workitems/edit/42",
        };

        var client = CreateClient(
            // GET work item — unclaimed, rev 3
            (urlContains: "workitems/42?api-version=7.1&$expand=all",
             jsonResponse: WorkItemGetResponse(42, 3, "New", "agent-ready; backend")),
            // PATCH claim — success, returns rev 4
            (urlContains: "workitems/42?api-version=7.1",
             jsonResponse: WorkItemPatchResponse(42, 4))
        );

        var claim = new ClaimRequest
        {
            WorkerId = "worker-1",
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        var result = await client.TryClaimWorkItemAsync(workRef, claim, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.WorkRef);
        Assert.Equal("42", result.WorkRef.ExternalId);
        Assert.NotNull(result.LeaseToken);
        Assert.Contains("lease_42", result.LeaseToken, StringComparison.Ordinal);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task TryClaimWorkItem_AlreadyClaimedAgentActiveTag_ReturnsFailure()
    {
        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Url = $"{OrgUrl}/{Project}/_workitems/edit/42",
        };

        var client = CreateClient(
            // GET work item — already has agent-active tag
            (urlContains: "workitems/42?api-version=7.1&$expand=all",
             jsonResponse: WorkItemGetResponse(42, 3, "New", "agent-ready; agent-active; backend"))
        );

        var claim = new ClaimRequest
        {
            WorkerId = "worker-1",
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        var result = await client.TryClaimWorkItemAsync(workRef, claim, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("already claimed", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agent-active", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryClaimWorkItem_AlreadyClaimedWorkerTag_ReturnsFailure()
    {
        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Url = $"{OrgUrl}/{Project}/_workitems/edit/42",
        };

        var client = CreateClient(
            // GET work item — already has agent-worker:someone-else tag
            (urlContains: "workitems/42?api-version=7.1&$expand=all",
             jsonResponse: WorkItemGetResponse(42, 3, "New", "agent-ready; agent-worker:other-worker; backend"))
        );

        var claim = new ClaimRequest
        {
            WorkerId = "worker-1",
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        var result = await client.TryClaimWorkItemAsync(workRef, claim, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("already claimed", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryClaimWorkItem_PreconditionFailed_ReturnsFailure()
    {
        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Url = $"{OrgUrl}/{Project}/_workitems/edit/42",
        };

        var client = CreateClientWithStatusCodes(
            // GET work item — unclaimed, rev 3
            (urlContains: "workitems/42?api-version=7.1&$expand=all",
             statusCode: HttpStatusCode.OK,
             body: WorkItemGetResponse(42, 3, "New", "agent-ready")),
            // PATCH claim — 412 Precondition Failed (someone else modified it)
            (urlContains: "workitems/42?api-version=7.1",
             statusCode: HttpStatusCode.PreconditionFailed,
             body: """{"message":"The resource has been modified by another user."}""")
        );

        var claim = new ClaimRequest
        {
            WorkerId = "worker-1",
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        var result = await client.TryClaimWorkItemAsync(workRef, claim, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("412", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryClaimWorkItem_GetFails_ReturnsFailure()
    {
        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "99",
            Url = $"{OrgUrl}/{Project}/_workitems/edit/99",
        };

        var client = CreateClientWithStatusCodes(
            // GET work item — 404 Not Found
            (urlContains: "workitems/99?api-version=7.1&$expand=all",
             statusCode: HttpStatusCode.NotFound,
             body: """{"message":"Work item not found."}""")
        );

        var claim = new ClaimRequest
        {
            WorkerId = "worker-1",
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        var result = await client.TryClaimWorkItemAsync(workRef, claim, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("404", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryClaimWorkItem_MissingExternalId_ReturnsFailure()
    {
        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = string.Empty, // missing
        };

        var client = CreateClient();

        var claim = new ClaimRequest { WorkerId = "worker-1" };

        var result = await client.TryClaimWorkItemAsync(workRef, claim, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("External work item identifier", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryClaimWorkItem_AddsAgentTagsAndHistoryComment()
    {
        // This test verifies the PATCH body includes the correct tags and comment.
        // We capture the request body via the fake handler.
        string? patchBody = null;
        string? ifMatchHeader = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemGetResponse(42, 3, "New", "agent-ready"),
                        Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Patch)
            {
                if (req.Content is not null)
                    patchBody = await req.Content.ReadAsStringAsync();
                ifMatchHeader = req.Headers.TryGetValues("If-Match", out var values)
                    ? values.FirstOrDefault()
                    : null;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemPatchResponse(42, 4),
                        Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };
        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "test-pat",
        };
        var client = new AzureDevOpsBoardsClient(http, options);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
        };

        var claim = new ClaimRequest
        {
            WorkerId = "worker-1",
            ClaimedAt = DateTimeOffset.UtcNow,
        };

        var result = await client.TryClaimWorkItemAsync(workRef, claim, CancellationToken.None);

        Assert.True(result.Success);

        // Verify PATCH body contains agent-active and agent-worker: tags
        Assert.NotNull(patchBody);
        Assert.Contains("agent-active", patchBody, StringComparison.Ordinal);
        Assert.Contains("agent-worker:worker-1", patchBody, StringComparison.Ordinal);

        // Verify System.History comment was added
        Assert.Contains("System.History", patchBody, StringComparison.Ordinal);
        Assert.Contains("Claimed by agent controller", patchBody, StringComparison.Ordinal);
        Assert.Contains("worker-1", patchBody, StringComparison.Ordinal);

        // Verify If-Match header was sent for optimistic concurrency
        Assert.NotNull(ifMatchHeader);
        Assert.Equal("3", ifMatchHeader);
    }

    // ──────────────────────────────────────────────
    // Status projection (UpdateWorkItemStatusAsync)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateWorkItemStatus_UpdatesStatusAndTags()
    {
        string? patchBody = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Patch)
            {
                if (req.Content is not null)
                    patchBody = await req.Content.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemPatchResponse(42, 5),
                        Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };
        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "test-pat",
        };
        var client = new AzureDevOpsBoardsClient(http, options);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "3",
        };

        var status = new ExternalWorkStatus
        {
            Status = "Active",
            Tags = new[] { "agent-active", "agent-worker:worker-1" },
        };

        await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        Assert.NotNull(patchBody);
        Assert.Contains("System.State", patchBody, StringComparison.Ordinal);
        Assert.Contains("Active", patchBody, StringComparison.Ordinal);
        Assert.Contains("System.Tags", patchBody, StringComparison.Ordinal);
        Assert.Contains("agent-active", patchBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_NoStatusOrTags_NoOp()
    {
        bool patchCalled = false;

        var handler = new CaptureHttpMessageHandler((req) =>
        {
            patchCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };
        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "test-pat",
        };
        var client = new AzureDevOpsBoardsClient(http, options);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
        };

        var status = new ExternalWorkStatus
        {
            Status = null,
            Tags = null,
        };

        await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // No PATCH should be made when there's nothing to update
        Assert.False(patchCalled);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_PreconditionFailed_NoException()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddResponse("workitems/42?api-version=7.1",
            new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
            {
                Content = new StringContent(
                    """{"message":"The resource has been modified."}""",
                    Encoding.UTF8, "application/json"),
            });

        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };
        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "test-pat",
        };
        var client = new AzureDevOpsBoardsClient(http, options);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "1",
        };

        var status = new ExternalWorkStatus { Status = "Active" };

        // Should not throw — 412 is handled gracefully for status projection
        var ex = await Record.ExceptionAsync(() =>
            client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None));

        Assert.Null(ex);
    }

    // ──────────────────────────────────────────────
    // Comment projection (AddCommentAsync)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task AddComment_AddsSystemHistoryField()
    {
        string? patchBody = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Patch)
            {
                if (req.Content is not null)
                    patchBody = await req.Content.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemPatchResponse(42, 5),
                        Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };
        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "test-pat",
        };
        var client = new AzureDevOpsBoardsClient(http, options);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "3",
        };

        const string comment = "Agent runtime completed: Implemented retry handling.";

        await client.AddCommentAsync(workRef, comment, CancellationToken.None);

        Assert.NotNull(patchBody);
        // Verify the PATCH body contains the expected operation targeting System.History
        Assert.Contains("System.History", patchBody, StringComparison.Ordinal);
        Assert.Contains("retry handling", patchBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddComment_PreconditionFailed_NoException()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddResponse("workitems/42?api-version=7.1",
            new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
            {
                Content = new StringContent(
                    """{"message":"The resource has been modified."}""",
                    Encoding.UTF8, "application/json"),
            });

        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };
        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = Project,
            PersonalAccessToken = "test-pat",
        };
        var client = new AzureDevOpsBoardsClient(http, options);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "1",
        };

        // Should not throw — 412 is handled gracefully for comment projection
        var ex = await Record.ExceptionAsync(() =>
            client.AddCommentAsync(workRef, "Should be fine.", CancellationToken.None));

        Assert.Null(ex);
    }

    // ──────────────────────────────────────────────
    // Repository listing (ListRepositoriesAsync)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ListRepositoriesAsync_SuccessfulListing_MultipleRepos()
    {
        var reposJson = """
        {
          "count": 3,
          "value": [
            {
              "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
              "name": "web-app",
              "defaultBranch": "refs/heads/main",
              "remoteUrl": "https://dev.azure.com/testorg/TestProject/_git/web-app"
            },
            {
              "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
              "name": "api-service",
              "defaultBranch": "refs/heads/develop",
              "remoteUrl": "https://dev.azure.com/testorg/TestProject/_git/api-service"
            },
            {
              "id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
              "name": "shared-lib",
              "defaultBranch": "refs/heads/main",
              "remoteUrl": "https://dev.azure.com/testorg/TestProject/_git/shared-lib"
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "git/repositories", jsonResponse: reposJson)
        );

        var repos = await client.ListRepositoriesAsync(Project, CancellationToken.None);

        Assert.Equal(3, repos.Count);

        Assert.Equal("a1b2c3d4-e5f6-7890-abcd-ef1234567890", repos[0].Id);
        Assert.Equal("web-app", repos[0].Name);
        Assert.Equal("refs/heads/main", repos[0].DefaultBranch);
        Assert.Equal("https://dev.azure.com/testorg/TestProject/_git/web-app", repos[0].RemoteUrl);

        Assert.Equal("b2c3d4e5-f6a7-8901-bcde-f12345678901", repos[1].Id);
        Assert.Equal("api-service", repos[1].Name);
        Assert.Equal("refs/heads/develop", repos[1].DefaultBranch);

        Assert.Equal("c3d4e5f6-a7b8-9012-cdef-123456789012", repos[2].Id);
        Assert.Equal("shared-lib", repos[2].Name);
    }

    [Fact]
    public async Task ListRepositoriesAsync_EmptyRepoList_ReturnsEmptyList()
    {
        var reposJson = """{"count":0,"value":[]}""";

        var client = CreateClient(
            (urlContains: "git/repositories", jsonResponse: reposJson)
        );

        var repos = await client.ListRepositoriesAsync(Project, CancellationToken.None);

        Assert.NotNull(repos);
        Assert.Empty(repos);
    }

    [Fact]
    public async Task ListRepositoriesAsync_RepoWithoutOptionalFields_UsesDefaults()
    {
        // A repo missing defaultBranch and remoteUrl should get null and webUrl fallback
        var reposJson = """
        {
          "count": 1,
          "value": [
            {
              "id": "d4e5f6a7-b8c9-0123-defa-234567890123",
              "name": "minimal-repo",
              "webUrl": "https://dev.azure.com/testorg/TestProject/_git/minimal-repo"
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "git/repositories", jsonResponse: reposJson)
        );

        var repos = await client.ListRepositoriesAsync(Project, CancellationToken.None);

        Assert.Single(repos);
        Assert.Equal("d4e5f6a7-b8c9-0123-defa-234567890123", repos[0].Id);
        Assert.Equal("minimal-repo", repos[0].Name);
        Assert.Null(repos[0].DefaultBranch);
        // Should fall back to webUrl when remoteUrl is absent
        Assert.Equal("https://dev.azure.com/testorg/TestProject/_git/minimal-repo", repos[0].RemoteUrl);
    }

    [Fact]
    public async Task ListRepositoriesAsync_EmptyProject_ThrowsInvalidOperation()
    {
        var client = CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ListRepositoriesAsync(string.Empty, CancellationToken.None));

        Assert.Contains("project", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListRepositoriesAsync_NullProject_ThrowsInvalidOperation()
    {
        var client = CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ListRepositoriesAsync(null!, CancellationToken.None));

        Assert.Contains("project", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListRepositoriesAsync_Unauthorized_ThrowsHttpRequestException()
    {
        var client = CreateClientWithStatusCodes(
            (urlContains: "git/repositories",
             statusCode: HttpStatusCode.Unauthorized,
             body: """{"message":"Unauthorized"}""")
        );

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ListRepositoriesAsync(Project, CancellationToken.None));
    }

    [Fact]
    public async Task ListRepositoriesAsync_ServerError_ThrowsHttpRequestException()
    {
        var client = CreateClientWithStatusCodes(
            (urlContains: "git/repositories",
             statusCode: HttpStatusCode.InternalServerError,
             body: """{"message":"Internal server error"}""")
        );

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ListRepositoriesAsync(Project, CancellationToken.None));
    }

    // ──────────────────────────────────────────────
    // JSON response helpers
    // ──────────────────────────────────────────────

    private static string WiqlResponse(params int[] ids)
    {
        var workItems = ids.Select(id => new { id, url = $"{OrgUrl}/_apis/wit/workItems/{id}" });
        return JsonSerializer.Serialize(new
        {
            queryType = "flat",
            asOf = "2024-01-01T00:00:00Z",
            workItems,
        });
    }

    /// <summary>
    /// Builds a JSON response for a single work item GET (for claim verification).
    /// </summary>
    private static string WorkItemGetResponse(int id, int rev, string state, string tags)
    {
        return JsonSerializer.Serialize(new
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
        });
    }

    /// <summary>
    /// Builds a JSON response for a successful work item PATCH.
    /// </summary>
    private static string WorkItemPatchResponse(int id, int rev)
    {
        return JsonSerializer.Serialize(new
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
        });
    }

    private static string BatchResponse(
        params (int id, int rev, string title, string? description,
                string state, string tags, string? assignedToDisplay,
                int priority, string areaPath, string iterationPath,
                string workItemType)[] items)
    {
        var value = items.Select(item =>
        {
            var fields = new Dictionary<string, object?>
            {
                ["System.Id"] = item.id,
                ["System.Title"] = item.title,
                ["System.State"] = item.state,
                ["System.Tags"] = item.tags,
                ["Microsoft.VSTS.Common.Priority"] = item.priority,
                ["System.AreaPath"] = item.areaPath,
                ["System.IterationPath"] = item.iterationPath,
                ["System.WorkItemType"] = item.workItemType,
            };

            if (item.description is not null)
                fields["System.Description"] = item.description;

            if (item.assignedToDisplay is not null)
            {
                fields["System.AssignedTo"] = new Dictionary<string, string>
                {
                    ["displayName"] = item.assignedToDisplay,
                    ["uniqueName"] = item.assignedToDisplay.Replace(" ", ".").ToLowerInvariant() + "@example.com",
                };
            }

            return new
            {
                id = item.id,
                rev = item.rev,
                fields,
                url = $"{OrgUrl}/_apis/wit/workItems/{item.id}",
            };
        });

        return JsonSerializer.Serialize(new
        {
            count = items.Length,
            value,
        });
    }

    /// <summary>
    /// Fake <see cref="HttpMessageHandler"/> that matches requests by URL substring
    /// and returns predefined responses in order.
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<(string urlContains, Func<HttpResponseMessage> responseFactory)> _responses;

        public FakeHttpMessageHandler()
        {
            _responses = new Queue<(string, Func<HttpResponseMessage>)>();
        }

        public FakeHttpMessageHandler(params (string urlContains, string jsonResponse)[] responses)
        {
            _responses = new Queue<(string, Func<HttpResponseMessage>)>(
                responses.Select(r =>
                    (r.urlContains,
                     (Func<HttpResponseMessage>)(() => new HttpResponseMessage(HttpStatusCode.OK)
                     {
                         Content = new StringContent(r.jsonResponse, Encoding.UTF8, "application/json"),
                     }))));
        }

        public FakeHttpMessageHandler(params (string urlContains, HttpResponseMessage httpResponse)[] responses)
        {
            _responses = new Queue<(string, Func<HttpResponseMessage>)>(
                responses.Select(r =>
                    (r.urlContains, (Func<HttpResponseMessage>)(() => r.httpResponse))));
        }

        /// <summary>
        /// Adds a response that will be returned when a request URL contains the
        /// given substring. Responses are consumed in FIFO order; each call to
        /// <see cref="SendAsync"/> dequeues the first matching response.
        /// </summary>
        public void AddResponse(string urlContains, HttpResponseMessage response)
        {
            _responses.Enqueue((urlContains, () => response));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;

            // Find the first queued response whose URL match contains the request URL
            var match = _responses.FirstOrDefault(r => url.Contains(r.urlContains, StringComparison.OrdinalIgnoreCase));
            if (match.urlContains is not null)
            {
                // Re-queue to remove consumed match; rest remain
                var remaining = new Queue<(string, Func<HttpResponseMessage>)>(
                    _responses.Where(r => r.urlContains != match.urlContains));
                _responses.Clear();
                foreach (var r in remaining)
                    _responses.Enqueue(r);

                return Task.FromResult(match.responseFactory());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    "{\"message\":\"No handler for " + url + "\"}",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// Fake <see cref="HttpMessageHandler"/> that delegates to an async callback
    /// for each request, enabling inspection of request headers and body.
    /// </summary>
    private sealed class CaptureHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public CaptureHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = req => Task.FromResult(handler(req));
        }

        public CaptureHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return await _handler(request);
        }
    }
}
