using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;

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

        return new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);
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

        return new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);
    }

    /// <summary>
    /// Creates an <see cref="AzureDevOpsBoardsClient"/> wired to a custom HTTP handler.
    /// </summary>
    private static AzureDevOpsBoardsClient CreateClientWithHandler(HttpMessageHandler handler)
    {
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

        return new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);
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
                  state: "New", tags: "agent-ready; backend",
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
        Assert.Equal("New", wi2.Status);
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

    [Fact]
    public async Task QueryWorkItemsAsync_ExcludedTags_GenerateNotContainsClauses()
    {
        // Verify that excluded tags generate NOT CONTAINS clauses in the WIQL query.
        // This is the mechanism that prevents re-pickup of claimed/failed/needs-human items.
        string? wiqlBody = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.AbsoluteUri.Contains("wiql") == true)
            {
                wiqlBody = req.Content is not null
                    ? await req.Content.ReadAsStringAsync()
                    : string.Empty;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WiqlResponse(1),
                        Encoding.UTF8, "application/json"),
                };
            }

            // Batch response
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    BatchResponse(
                        (id: 1, rev: 1, title: "Test", description: null,
                         state: "New", tags: "agent-ready",
                         assignedToDisplay: null, priority: 1,
                         areaPath: "P", iterationPath: "P\\I", workItemType: "T")),
                    Encoding.UTF8, "application/json"),
            };
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        var parameters = new BoardsQueryParameters
        {
            Project = Project,
            ExcludedTags = new[] { "agent-active", "agent-failed", "agent-needs-human" },
        };

        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.NotNull(wiqlBody);
        // JSON serializer escapes single quotes as \u0027 in the wire format
        Assert.Contains("NOT CONTAINS \\u0027agent-active\\u0027", wiqlBody);
        Assert.Contains("NOT CONTAINS \\u0027agent-failed\\u0027", wiqlBody);
        Assert.Contains("NOT CONTAINS \\u0027agent-needs-human\\u0027", wiqlBody);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_ExcludedTagsWithDefaults_PreventsRePickup()
    {
        // Verify the default exclusion tags from WorkSourceOptions would
        // prevent re-pickup of agent-active, agent-failed, and agent-needs-human items.
        // This test verifies the WIQL excludes all three agent lifecycle tags.
        string? wiqlBody = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri?.AbsoluteUri.Contains("wiql") == true)
            {
                wiqlBody = req.Content is not null
                    ? await req.Content.ReadAsStringAsync()
                    : string.Empty;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WiqlResponse(0), // No results expected when all items are excluded
                        Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"value\":[]}",
                    Encoding.UTF8, "application/json"),
            };
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        // Use the same exclusion tags that WorkSourceOptions defaults to
        var parameters = new BoardsQueryParameters
        {
            Project = Project,
            ExcludedTags = WorkSourceOptions.LifecycleTags(),
        };

        var results = await client.QueryWorkItemsAsync(parameters, CancellationToken.None);

        Assert.NotNull(wiqlBody);
        // Verify all three default exclusion tags are in the WIQL
        // JSON serializer escapes single quotes as \u0027 in the wire format
        Assert.Contains("NOT CONTAINS \\u0027agent-active\\u0027", wiqlBody);
        Assert.Contains("NOT CONTAINS \\u0027agent-failed\\u0027", wiqlBody);
        Assert.Contains("NOT CONTAINS \\u0027agent-needs-human\\u0027", wiqlBody);
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

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

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemGetResponse(42, 3, "New", "agent-ready; backend"),
                        Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Patch)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemPatchResponse(42, 4),
                        Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = CreateClientWithHandler(handler);

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
            (urlContains: "workitems/42?api-version=7.1",
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
            (urlContains: "workitems/42?api-version=7.1",
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
                // Simulate 412 Precondition Failed (someone else modified it)
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                {
                    Content = new StringContent(
                        """{"message":"The resource has been modified by another user."}""",
                        Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = CreateClientWithHandler(handler);

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
            (urlContains: "workitems/99?api-version=7.1",
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

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
    // Wildcard tag stripping (agent-worker:*)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateWorkItemStatus_WildcardAgentWorkerStripsConcreteTag()
    {
        // Arrange: work item has a concrete agent-worker:{id} tag
        string? patchBody = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemGetResponse(42, 5, "Resolved",
                            "agent-active; agent-worker:live-ado_worker; backend; high-priority"),
                        Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Patch)
            {
                if (req.Content is not null)
                    patchBody = await req.Content.ReadAsStringAsync();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemPatchResponse(42, 6),
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "5",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[] { "agent-active", "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: PATCH succeeded
        Assert.True(ok);
        Assert.NotNull(patchBody);

        // Assert: agent-worker:live-ado_worker was stripped by the wildcard
        Assert.DoesNotContain("agent-worker:", patchBody!);
        Assert.DoesNotContain("agent-active", patchBody!);

        // Assert: unrelated tags preserved
        Assert.Contains("backend", patchBody!);
        Assert.Contains("high-priority", patchBody!);

        // Assert: agent-ready was added
        Assert.Contains("agent-ready", patchBody!);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_WildcardPreservesUnrelatedTags()
    {
        // Arrange: work item has agent-worker tag plus many unrelated tags
        string? patchBody = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemGetResponse(10, 3, "Resolved",
                            "agent-worker:worker-a; backend; ui; frontend; repo:my-service"),
                        Encoding.UTF8, "application/json"),
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "10",
            Revision = "3",
        };

        var status = new ExternalWorkStatus
        {
            RemovedTags = new[] { "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert
        Assert.True(ok);
        Assert.NotNull(patchBody);

        // Assert: agent-worker:worker-a was stripped
        Assert.DoesNotContain("agent-worker:", patchBody!);

        // Assert: all unrelated tags preserved
        Assert.Contains("backend", patchBody!);
        Assert.Contains("ui", patchBody!);
        Assert.Contains("frontend", patchBody!);
        Assert.Contains("repo:my-service", patchBody!);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_ExactMatchRemovalStillWorks()
    {
        // Arrange: work item has multiple tags, we remove one by exact match
        string? patchBody = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemGetResponse(20, 2, "Active",
                            "agent-active; agent-ready; backend; ui"),
                        Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Patch)
            {
                if (req.Content is not null)
                    patchBody = await req.Content.ReadAsStringAsync();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemPatchResponse(20, 3),
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "20",
            Revision = "2",
        };

        // Remove only agent-active by exact match — no wildcards
        var status = new ExternalWorkStatus
        {
            RemovedTags = new[] { "agent-active" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert
        Assert.True(ok);
        Assert.NotNull(patchBody);

        // Assert: agent-active was stripped
        Assert.DoesNotContain("agent-active", patchBody!);

        // Assert: all other tags preserved (including agent-ready which is similar but not exact match)
        Assert.Contains("agent-ready", patchBody!);
        Assert.Contains("backend", patchBody!);
        Assert.Contains("ui", patchBody!);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_WildcardAndExactMatchCombined()
    {
        // Arrange: work item has agent-worker tag, agent-active, and unrelated tags
        string? patchBody = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemGetResponse(30, 4, "Resolved",
                            "agent-active; agent-worker:live-ado_worker; agent-failed; backend; ui"),
                        Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Patch)
            {
                if (req.Content is not null)
                    patchBody = await req.Content.ReadAsStringAsync();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemPatchResponse(30, 5),
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "30",
            Revision = "4",
        };

        // Combine wildcard (agent-worker:*) with exact match (agent-active, agent-failed)
        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[] { "agent-active", "agent-failed", "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert
        Assert.True(ok);
        Assert.NotNull(patchBody);

        // Assert: all agent lifecycle tags stripped
        Assert.DoesNotContain("agent-active", patchBody!);
        Assert.DoesNotContain("agent-failed", patchBody!);
        Assert.DoesNotContain("agent-worker:", patchBody!);

        // Assert: unrelated tags preserved
        Assert.Contains("backend", patchBody!);
        Assert.Contains("ui", patchBody!);

        // Assert: agent-ready was re-added
        Assert.Contains("agent-ready", patchBody!);
    }

    // ──────────────────────────────────────────────
    // Load-bearing GET + fresh-revision (RemovedTags path)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTags_GetFails_ReturnsFalse_NoPatchEmitted()
    {
        // Arrange: fake handler returns non-success on GET — simulates missing work item
        // or insufficient scope on the PAT. The code should abort entirely, not emit
        // a state-only PATCH masquerading as success.
        int requestCount = 0;
        HttpMethod? lastMethod = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            requestCount++;
            lastMethod = req.Method;

            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"message":"Work item not found."}""",
                        Encoding.UTF8, "application/json"),
                };
            }

            // Should never reach PATCH
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "5",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[] { "agent-active", "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: returns false — no false success when GET fails
        Assert.False(ok);

        // Assert: only GET was made, no PATCH was emitted
        Assert.Equal(1, requestCount);
        Assert.Equal(HttpMethod.Get, lastMethod);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTags_GetSuccess_CorrectPatchBodyAndFreshRev()
    {
        // Arrange: GET succeeds with agent-active + agent-worker:{id} tags.
        // PATCH should carry correct RemovedTags + Tags agent-ready, using the
        // freshly-read rev for If-Match (not a stale workRef.Revision).
        string? patchBody = null;
        string? ifMatchValue = null;
        string? getRequestUri = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                getRequestUri = req.RequestUri?.AbsoluteUri;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemGetResponse(42, 7, "Resolved",
                            "agent-active; agent-worker:live-ado_worker; backend; high-priority"),
                        Encoding.UTF8, "application/json"),
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "3", // stale revision — should NOT be used for If-Match
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[] { "agent-active", "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: PATCH succeeded
        Assert.True(ok);

        // Assert: If-Match uses freshly-read rev (7) not stale workRef.Revision (3)
        Assert.Equal("7", ifMatchValue);

        // Assert: PATCH body contains state change and merged tags
        Assert.NotNull(patchBody);
        Assert.Contains("System.State", patchBody);
        Assert.Contains("New", patchBody);
        Assert.Contains("System.Tags", patchBody);
        Assert.Contains("agent-ready", patchBody);

        // Assert: agent lifecycle tags stripped
        Assert.DoesNotContain("agent-active", patchBody!);
        Assert.DoesNotContain("agent-worker:", patchBody!);

        // Assert: unrelated tags preserved
        Assert.Contains("backend", patchBody!);
        Assert.Contains("high-priority", patchBody!);

        // Assert: simplified GET URL — no $expand parameter
        Assert.NotNull(getRequestUri);
        Assert.DoesNotContain("$expand", getRequestUri);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTags_EmptyExistingTags_EmitsTagOps()
    {
        // Arrange: work item has NO tags (empty System.Tags). The tag op should
        // still be emitted so agent-ready is added. ADO tolerates RemovedTags
        // entries that are not present.
        string? patchBody = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemGetResponse(42, 5, "Resolved", ""),
                        Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Patch)
            {
                if (req.Content is not null)
                    patchBody = await req.Content.ReadAsStringAsync();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemPatchResponse(42, 6),
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "5",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[] { "agent-active", "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: PATCH succeeded even though existingTags was empty
        Assert.True(ok);

        // Assert: tag op is still emitted — agent-ready is added
        Assert.NotNull(patchBody);
        Assert.Contains("agent-ready", patchBody);

        // Assert: System.Tags path is present in the PATCH (op was not skipped)
        Assert.Contains("System.Tags", patchBody);
    }

    [Fact]
    public async Task UpdateWorkItemStatus_RemovedTags_Patch412_ReturnsFalse()
    {
        // Arrange: GET succeeds (reads tags), but PATCH returns 412 Precondition Failed.
        // Because RemovedTags is specified, the scoped hard-fail kicks in and returns false.
        bool getCalled = false;
        string? patchBody = null;

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                getCalled = true;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        WorkItemGetResponse(42, 5, "Resolved",
                            "agent-active; agent-worker:live-ado_worker; backend"),
                        Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Patch)
            {
                if (req.Content is not null)
                    patchBody = await req.Content.ReadAsStringAsync();

                // Simulate 412 Precondition Failed — concurrent modification
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
                {
                    Content = new StringContent(
                        """{"message":"The resource has been modified by another user."}""",
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
            Revision = "5",
        };

        var status = new ExternalWorkStatus
        {
            Status = "New",
            Tags = new[] { "agent-ready" },
            RemovedTags = new[] { "agent-active", "agent-worker:*" },
        };

        // Act
        var ok = await client.UpdateWorkItemStatusAsync(workRef, status, CancellationToken.None);

        // Assert: returns false — scoped hard-fail for RemovedTags-bearing PATCHes on 412
        Assert.False(ok);

        // Assert: GET was called (tag-read succeeded)
        Assert.True(getCalled);

        // Assert: PATCH body was constructed (tag ops were emitted before 412)
        Assert.NotNull(patchBody);
        Assert.Contains("System.Tags", patchBody);
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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

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
        var client = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

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
    // Acceptance criteria parsing
    // ──────────────────────────────────────────────

    [Fact]
    public async Task QueryWorkItemsAsync_AcceptanceCriteriaField_MapsIntoWorkCandidate()
    {
        // Work item with a dedicated AcceptanceCriteria field (Agile process template).
        var batchJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 10,
              "rev": 2,
              "fields": {
                "System.Id": 10,
                "System.Title": "Add login feature",
                "System.State": "New",
                "System.Tags": "agent-ready; repo:auth-service",
                "Microsoft.VSTS.Common.Priority": 1,
                "Microsoft.VSTS.Common.AcceptanceCriteria": "User can log in with email and password\nUser receives a token on successful login\nFailed login attempts are rate-limited",
                "System.AreaPath": "Project",
                "System.IterationPath": "Project\\Sprint",
                "System.WorkItemType": "User Story"
              },
              "url": "https://dev.azure.com/org/_apis/wit/workItems/10"
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "wiql", jsonResponse: WiqlResponse(10)),
            (urlContains: "workitemsbatch", jsonResponse: batchJson)
        );

        var results = await client.QueryWorkItemsAsync(
            new BoardsQueryParameters { Project = Project }, CancellationToken.None);

        Assert.Single(results);
        var wi = results[0];
        Assert.NotNull(wi.AcceptanceCriteria);
        Assert.Equal(3, wi.AcceptanceCriteria.Count);
        Assert.Equal("User can log in with email and password", wi.AcceptanceCriteria["1"]);
        Assert.Equal("User receives a token on successful login", wi.AcceptanceCriteria["2"]);
        Assert.Equal("Failed login attempts are rate-limited", wi.AcceptanceCriteria["3"]);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_DescriptionWithMarkdownChecklist_MapsIntoWorkCandidate()
    {
        // Work item with markdown checklist items in the description.
        var batchJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 20,
              "rev": 1,
              "fields": {
                "System.Id": 20,
                "System.Title": "Fix navigation bug",
                "System.Description": "The navigation bar is broken on mobile.\n\n## Acceptance Criteria\n\n- [ ] Nav collapses on screens < 768px\n- [ ] Hamburger menu opens/closes correctly\n- [x] Active page is highlighted\n- [ ] All links navigate to correct routes",
                "System.State": "New",
                "System.Tags": "agent-ready; repo:web-app",
                "Microsoft.VSTS.Common.Priority": 2,
                "System.AreaPath": "Project",
                "System.IterationPath": "Project\\Sprint",
                "System.WorkItemType": "Bug"
              },
              "url": "https://dev.azure.com/org/_apis/wit/workItems/20"
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "wiql", jsonResponse: WiqlResponse(20)),
            (urlContains: "workitemsbatch", jsonResponse: batchJson)
        );

        var results = await client.QueryWorkItemsAsync(
            new BoardsQueryParameters { Project = Project }, CancellationToken.None);

        Assert.Single(results);
        var wi = results[0];
        Assert.NotNull(wi.AcceptanceCriteria);
        Assert.Equal(4, wi.AcceptanceCriteria.Count);
        Assert.Equal("Nav collapses on screens < 768px", wi.AcceptanceCriteria["1"]);
        Assert.Equal("Hamburger menu opens/closes correctly", wi.AcceptanceCriteria["2"]);
        Assert.Equal("Active page is highlighted", wi.AcceptanceCriteria["3"]);
        Assert.Equal("All links navigate to correct routes", wi.AcceptanceCriteria["4"]);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_DescriptionWithHtmlCheckboxes_MapsIntoWorkCandidate()
    {
        // ADO rich-text editor renders checklists as HTML.
        var batchJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 30,
              "rev": 1,
              "fields": {
                "System.Id": 30,
                "System.Title": "Add search feature",
                "System.Description": "<div>Add a search bar to the header.</div><ul><li><input checked=\"false\" type=\"checkbox\" disabled=\"disabled\"> Search returns results from all categories</li><br><li><input checked=\"true\" type=\"checkbox\" disabled=\"disabled\"> Results are paginated</li><br><li><input checked=\"false\" type=\"checkbox\" disabled=\"disabled\"> Empty results show a message</li></ul>",
                "System.State": "New",
                "System.Tags": "agent-ready; repo:web-app",
                "Microsoft.VSTS.Common.Priority": 1,
                "System.AreaPath": "Project",
                "System.IterationPath": "Project\\Sprint",
                "System.WorkItemType": "User Story"
              },
              "url": "https://dev.azure.com/org/_apis/wit/workItems/30"
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "wiql", jsonResponse: WiqlResponse(30)),
            (urlContains: "workitemsbatch", jsonResponse: batchJson)
        );

        var results = await client.QueryWorkItemsAsync(
            new BoardsQueryParameters { Project = Project }, CancellationToken.None);

        Assert.Single(results);
        var wi = results[0];
        Assert.NotNull(wi.AcceptanceCriteria);
        Assert.Equal(3, wi.AcceptanceCriteria.Count);
        Assert.Equal("Search returns results from all categories", wi.AcceptanceCriteria["1"]);
        Assert.Equal("Results are paginated", wi.AcceptanceCriteria["2"]);
        Assert.Equal("Empty results show a message", wi.AcceptanceCriteria["3"]);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_NoAcceptanceCriteria_ReturnsNull()
    {
        // Work item with no acceptance criteria field and no checklist in description.
        var batchJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 40,
              "rev": 1,
              "fields": {
                "System.Id": 40,
                "System.Title": "Routine maintenance",
                "System.Description": "Update dependencies and run tests.",
                "System.State": "New",
                "System.Tags": "agent-ready",
                "Microsoft.VSTS.Common.Priority": 3,
                "System.AreaPath": "Project",
                "System.IterationPath": "Project\\Sprint",
                "System.WorkItemType": "Task"
              },
              "url": "https://dev.azure.com/org/_apis/wit/workItems/40"
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "wiql", jsonResponse: WiqlResponse(40)),
            (urlContains: "workitemsbatch", jsonResponse: batchJson)
        );

        var results = await client.QueryWorkItemsAsync(
            new BoardsQueryParameters { Project = Project }, CancellationToken.None);

        Assert.Single(results);
        Assert.Null(results[0].AcceptanceCriteria);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_AcceptanceCriteriaField_TakesPrecedenceOverDescription()
    {
        // When both the AC field and description checklist exist, the AC field wins.
        var batchJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 50,
              "rev": 1,
              "fields": {
                "System.Id": 50,
                "System.Title": "Complex work item",
                "System.Description": "Some description with - [ ] a checklist item in the description",
                "System.State": "New",
                "System.Tags": "agent-ready",
                "Microsoft.VSTS.Common.Priority": 1,
                "Microsoft.VSTS.Common.AcceptanceCriteria": "Field criterion one\nField criterion two",
                "System.AreaPath": "Project",
                "System.IterationPath": "Project\\Sprint",
                "System.WorkItemType": "User Story"
              },
              "url": "https://dev.azure.com/org/_apis/wit/workItems/50"
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "wiql", jsonResponse: WiqlResponse(50)),
            (urlContains: "workitemsbatch", jsonResponse: batchJson)
        );

        var results = await client.QueryWorkItemsAsync(
            new BoardsQueryParameters { Project = Project }, CancellationToken.None);

        Assert.Single(results);
        var wi = results[0];
        Assert.NotNull(wi.AcceptanceCriteria);
        Assert.Equal(2, wi.AcceptanceCriteria.Count);
        // Should use the AC field, not the description checklist
        Assert.Equal("Field criterion one", wi.AcceptanceCriteria["1"]);
        Assert.Equal("Field criterion two", wi.AcceptanceCriteria["2"]);
    }

    [Fact]
    public async Task QueryWorkItemsAsync_EmptyDescription_ReturnsNullAcceptanceCriteria()
    {
        // Work item with no description and no AC field.
        var batchJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 60,
              "rev": 1,
              "fields": {
                "System.Id": 60,
                "System.Title": "Minimal item",
                "System.State": "New",
                "System.Tags": "agent-ready",
                "System.AreaPath": "Project",
                "System.IterationPath": "Project\\Sprint",
                "System.WorkItemType": "Task"
              },
              "url": "https://dev.azure.com/org/_apis/wit/workItems/60"
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "wiql", jsonResponse: WiqlResponse(60)),
            (urlContains: "workitemsbatch", jsonResponse: batchJson)
        );

        var results = await client.QueryWorkItemsAsync(
            new BoardsQueryParameters { Project = Project }, CancellationToken.None);

        Assert.Single(results);
        Assert.Null(results[0].AcceptanceCriteria);
    }

    // ──────────────────────────────────────────────
    // Comment fetching (GetCommentsAsync)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetCommentsAsync_FetchesThreadComments()
    {
        var threadsJson = """
        {
          "count": 2,
          "value": [
            {
              "id": 1,
              "comments": [
                {
                  "comment": "Can we clarify the expected behavior for null inputs?",
                  "author": { "displayName": "Alice Reviewer" },
                  "publishedDate": "2024-06-15T10:30:00Z"
                }
              ]
            },
            {
              "id": 2,
              "comments": [
                {
                  "comment": "Yes, null should be treated as empty and return a default value.",
                  "author": { "displayName": "Bob Dev" },
                  "publishedDate": "2024-06-15T14:00:00Z"
                },
                {
                  "comment": "Agreed, I'll update the spec.",
                  "author": { "displayName": "Alice Reviewer" },
                  "publishedDate": "2024-06-16T09:00:00Z"
                }
              ]
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "threads", jsonResponse: threadsJson)
        );

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
        };

        var comments = await client.GetCommentsAsync(workRef, 10, CancellationToken.None);

        Assert.Equal(3, comments.Count);

        // First comment
        Assert.Equal("Alice Reviewer", comments[0].Author);
        Assert.Equal("Can we clarify the expected behavior for null inputs?", comments[0].Text);
        Assert.NotNull(comments[0].PostedAt);
        Assert.Equal(2024, comments[0].PostedAt!.Value.Year);

        // Second comment
        Assert.Equal("Bob Dev", comments[1].Author);
        Assert.Equal("Yes, null should be treated as empty and return a default value.", comments[1].Text);

        // Third comment
        Assert.Equal("Alice Reviewer", comments[2].Author);
        Assert.Equal("Agreed, I'll update the spec.", comments[2].Text);
    }

    [Fact]
    public async Task GetCommentsAsync_BoundedByMaxComments()
    {
        var threadsJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 1,
              "comments": [
                { "comment": "First comment", "author": { "displayName": "A" }, "publishedDate": "2024-01-01T00:00:00Z" },
                { "comment": "Second comment", "author": { "displayName": "B" }, "publishedDate": "2024-01-02T00:00:00Z" },
                { "comment": "Third comment", "author": { "displayName": "C" }, "publishedDate": "2024-01-03T00:00:00Z" },
                { "comment": "Fourth comment", "author": { "displayName": "D" }, "publishedDate": "2024-01-04T00:00:00Z" },
                { "comment": "Fifth comment", "author": { "displayName": "E" }, "publishedDate": "2024-01-05T00:00:00Z" }
              ]
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "threads", jsonResponse: threadsJson)
        );

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
        };

        // Request only 2 comments
        var comments = await client.GetCommentsAsync(workRef, 2, CancellationToken.None);

        Assert.Equal(2, comments.Count);
        Assert.Equal("First comment", comments[0].Text);
        Assert.Equal("Second comment", comments[1].Text);
    }

    [Fact]
    public async Task GetCommentsAsync_EmptyThreads_ReturnsEmptyList()
    {
        var threadsJson = """{"count":0,"value":[]}""";

        var client = CreateClient(
            (urlContains: "threads", jsonResponse: threadsJson)
        );

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
        };

        var comments = await client.GetCommentsAsync(workRef, 50, CancellationToken.None);

        Assert.Empty(comments);
    }

    [Fact]
    public async Task GetCommentsAsync_HttpError_ReturnsEmptyList()
    {
        var client = CreateClientWithStatusCodes(
            (urlContains: "threads",
             statusCode: HttpStatusCode.NotFound,
             body: """{"message":"Work item not found."}""")
        );

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "999",
        };

        // Should not throw — missing comments are best-effort
        var ex = await Record.ExceptionAsync(() =>
            client.GetCommentsAsync(workRef, 50, CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task GetCommentsAsync_MissingProject_ReturnsEmptyList()
    {
        var client = CreateClient();

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
        };

        // Override project to empty to trigger the guard clause
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri(OrgUrl + "/") };
        var authBytes = Encoding.ASCII.GetBytes(":test-pat");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        var options = new AzureDevOpsBoardsOptions
        {
            BaseUrl = OrgUrl,
            Project = string.Empty, // Empty project
            PersonalAccessToken = "test-pat",
        };
        var emptyClient = new AzureDevOpsBoardsClient(http, options, NullLogger<AzureDevOpsBoardsClient>.Instance);

        var comments = await emptyClient.GetCommentsAsync(workRef, 50, CancellationToken.None);

        Assert.Empty(comments);
    }

    [Fact]
    public async Task GetCommentsAsync_CommentWithoutAuthor_UsesNullAuthor()
    {
        var threadsJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 1,
              "comments": [
                {
                  "comment": "Comment with no author field",
                  "publishedDate": "2024-06-15T10:30:00Z"
                }
              ]
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "threads", jsonResponse: threadsJson)
        );

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
        };

        var comments = await client.GetCommentsAsync(workRef, 10, CancellationToken.None);

        Assert.Single(comments);
        Assert.Null(comments[0].Author);
        Assert.Equal("Comment with no author field", comments[0].Text);
    }

    [Fact]
    public async Task GetCommentsAsync_EmptyCommentText_IsSkipped()
    {
        var threadsJson = """
        {
          "count": 1,
          "value": [
            {
              "id": 1,
              "comments": [
                { "comment": "Valid comment", "author": { "displayName": "A" }, "publishedDate": "2024-01-01T00:00:00Z" },
                { "comment": "   ", "author": { "displayName": "B" }, "publishedDate": "2024-01-02T00:00:00Z" },
                { "comment": "Another valid", "author": { "displayName": "C" }, "publishedDate": "2024-01-03T00:00:00Z" }
              ]
            }
          ]
        }
        """;

        var client = CreateClient(
            (urlContains: "threads", jsonResponse: threadsJson)
        );

        var workRef = new ExternalWorkRef
        {
            Source = "AzureDevOpsBoards",
            ExternalId = "42",
        };

        var comments = await client.GetCommentsAsync(workRef, 10, CancellationToken.None);

        // Whitespace-only comment should be skipped
        Assert.Equal(2, comments.Count);
        Assert.Equal("Valid comment", comments[0].Text);
        Assert.Equal("Another valid", comments[1].Text);
    }

    // ──────────────────────────────────────────────
    // GetValidStatesAsync — Process API path (HTTP-mocked)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetValidStatesAsync_ProcessApiSequence_ReturnsGroupedByWit()
    {
        // Arrange: mock the 3-step Process API sequence:
        //   (1) GET project → capabilities.processTemplate.templateTypeId
        //   (2) GET process WITs for that process type id
        //   (3) GET per-WIT states (in parallel)
        const string processTypeId = "b7b7c690-4f2f-4a0e-9a3d-1234567890ab";

        var projectResponse = $$"""
            {
              "id": "proj-123",
              "name": "{{Project}}",
              "capabilities": {
                "processTemplate": {
                  "templateTypeId": "{{processTypeId}}"
                }
              }
            }
            """;
        projectResponse = projectResponse
            .Replace("{{Project}}", Project)
            .Replace("{{processTypeId}}", processTypeId);

        var witListResponse = $$"""
            {
              "count": 2,
              "value": [
                { "id": "wit-bug", "name": "Bug" },
                { "id": "wit-story", "name": "User Story" }
              ]
            }
            """;

        // Per-WIT state responses (Process API: fields.System.State.name.allowedValues)
        var bugStatesResponse = $$"""
            {
              "id": "wit-bug",
              "name": "Bug",
              "fields": {
                "System.State": {
                  "name": {
                    "refName": "System.State",
                    "allowedValues": [ "Resolved", "New", "Closed", "Active" ]
                  }
                }
              }
            }
            """;

        var storyStatesResponse = $$"""
            {
              "id": "wit-story",
              "name": "User Story",
              "fields": {
                "System.State": {
                  "name": {
                    "refName": "System.State",
                    "allowedValues": [ "Done", "To Do", "In Progress" ]
                  }
                }
              }
            }
            """;

        var client = CreateClientWithProcessApiResponses(
            processTypeId,
            projectResponse,
            witListResponse,
            new Dictionary<string, string>
            {
                ["Bug"] = bugStatesResponse,
                ["User Story"] = storyStatesResponse,
            }
        );

        // Act
        var result = await client.GetValidStatesAsync(Project, CancellationToken.None);

        // Assert: grouped by WIT, alpha-sorted WITs
        Assert.Equal(2, result.Count);
        var keys = result.Keys.ToList();
        Assert.Equal("Bug", keys[0]);
        Assert.Equal("User Story", keys[1]);

        // Assert: states alpha-sorted within each WIT
        var bugStates = result["Bug"];
        string[] expectedBugStates = ["Active", "Closed", "New", "Resolved"];
        Assert.Equal(expectedBugStates, bugStates);

        var storyStates = result["User Story"];
        string[] expectedStoryStates = ["Done", "In Progress", "To Do"];
        Assert.Equal(expectedStoryStates, storyStates);

        // Assert: bare state names only — no IDs, URLs, or metadata leaked
        foreach (var kvp in result)
        {
            foreach (var state in kvp.Value)
            {
                Assert.DoesNotContain("http", state, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("/", state);
                Assert.DoesNotContain("\"", state);
            }
        }
    }

    [Fact]
    public async Task GetValidStatesAsync_PinsProcessApiRequestPaths()
    {
        // Arrange: capture all request URLs to pin the exact API paths
        var capturedUrls = new List<string>();
        const string processTypeId = "proc-abc-123";

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            var url = req.RequestUri?.AbsoluteUri ?? string.Empty;
            capturedUrls.Add(url);

            // (1) Project lookup
            if (url.Contains("_apis/projects/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "id": "proj-123",
                          "name": "{{Project}}",
                          "capabilities": {
                            "processTemplate": {
                              "templateTypeId": "{{processTypeId}}"
                            }
                          }
                        }
                        """,
                        Encoding.UTF8, "application/json"),
                };
            }

            // (2) WIT list
            if (url.Contains("/workItemTypes?"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "count": 1,
                          "value": [
                            { "id": "wit-task", "name": "Task" }
                          ]
                        }
                        """,
                        Encoding.UTF8, "application/json"),
                };
            }

            // (3) Per-WIT states
            if (url.Contains("/workItemTypes/Task?"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "id": "wit-task",
                          "name": "Task",
                          "fields": {
                            "System.State": {
                              "name": {
                                "refName": "System.State",
                                "allowedValues": [ "Done", "To Do" ]
                              }
                            }
                          }
                        }
                        """,
                        Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = CreateClientWithHandler(handler);

        // Act
        await client.GetValidStatesAsync(Project, CancellationToken.None);

        // Assert: exact API path sequence
        // (1) Project lookup with includeProcessSettings
        Assert.Contains(capturedUrls, u => u.Contains("_apis/projects/")
            && u.Contains("api-version=7.1")
            && u.Contains("includeProcessSettings=true"));

        // (2) WIT list with process type id and preview API version
        Assert.Contains(capturedUrls, u => u.Contains("/work/processes/")
            && u.Contains("proc-abc-123")
            && u.Contains("/workItemTypes?")
            && u.Contains("api-version=7.1-preview.3"));

        // (3) Per-WIT states with fields=System.State
        Assert.Contains(capturedUrls, u => u.Contains("/workItemTypes/Task?")
            && u.Contains("api-version=7.1-preview.3")
            && u.Contains("fields=System.State"));
    }

    [Fact]
    public async Task GetValidStatesAsync_PerWitStateCalls_RunInParallel()
    {
        // Arrange: track concurrency of per-WIT state calls
        const string processTypeId = "proc-concurrent";
        int maxConcurrentRequests = 0;
        int currentConcurrentRequests = 0;
        object lockObj = new();

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            var url = req.RequestUri?.AbsoluteUri ?? string.Empty;

            // (1) Project lookup
            if (url.Contains("_apis/projects/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "id": "proj-123",
                          "name": "{{Project}}",
                          "capabilities": {
                            "processTemplate": {
                              "templateTypeId": "{{processTypeId}}"
                            }
                          }
                        }
                        """,
                        Encoding.UTF8, "application/json"),
                };
            }

            // (2) WIT list — return 3 WITs to test parallel fetching
            if (url.Contains("/workItemTypes?"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "count": 3,
                          "value": [
                            { "id": "wit-bug", "name": "Bug" },
                            { "id": "wit-story", "name": "User Story" },
                            { "id": "wit-task", "name": "Task" }
                          ]
                        }
                        """,
                        Encoding.UTF8, "application/json"),
                };
            }

            // (3) Per-WIT states — track concurrency
            if (url.Contains("/workItemTypes/"))
            {
                lock (lockObj)
                {
                    currentConcurrentRequests++;
                    if (currentConcurrentRequests > maxConcurrentRequests)
                        maxConcurrentRequests = currentConcurrentRequests;
                }

                // Small delay to allow concurrent requests to overlap
                await Task.Delay(50);

                lock (lockObj)
                {
                    currentConcurrentRequests--;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "fields": {
                            "System.State": {
                              "name": {
                                "allowedValues": [ "New", "Done" ]
                              }
                            }
                          }
                        }
                        """,
                        Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = CreateClientWithHandler(handler);

        // Act
        await client.GetValidStatesAsync(Project, CancellationToken.None);

        // Assert: per-WIT state calls ran in parallel (at least 2 concurrent)
        Assert.True(maxConcurrentRequests >= 2,
            $"Expected parallel state fetches but max concurrency was {maxConcurrentRequests}");
    }

    [Fact]
    public async Task GetValidStatesAsync_FallbackProcessSettings_ReturnsStates()
    {
        // Arrange: project response uses processSettings.processId fallback
        // (not capabilities.processTemplate.templateTypeId)
        const string processTypeId = "fallback-proc-id";

        var projectResponse = $$"""
            {
              "id": "proj-123",
              "name": "{{Project}}",
              "processSettings": {
                "processId": "{{processTypeId}}"
              }
            }
            """;

        var witListResponse = $$"""
            {
              "count": 1,
              "value": [
                { "id": "wit-bug", "name": "Bug" }
              ]
            }
            """;

        var bugStatesResponse = $$"""
            {
              "fields": {
                "System.State": {
                  "name": {
                    "allowedValues": [ "New", "Active" ]
                  }
                }
              }
            }
            """;

        var client = CreateClientWithProcessApiResponses(
            processTypeId,
            projectResponse,
            witListResponse,
            new Dictionary<string, string>
            {
                ["Bug"] = bugStatesResponse,
            }
        );

        // Act
        var result = await client.GetValidStatesAsync(Project, CancellationToken.None);

        // Assert: fallback processSettings.processId worked
        Assert.Single(result);
        string[] expectedBugStates2 = ["Active", "New"];
        Assert.Equal(expectedBugStates2, result["Bug"]);
    }

    [Fact]
    public async Task GetValidStatesAsync_TopLevelProcessIdFallback_ReturnsStates()
    {
        // Arrange: project response has only top-level processId (no capabilities or processSettings)
        const string processTypeId = "top-level-proc-id";

        var projectResponse = $$"""
            {
              "id": "proj-123",
              "name": "{{Project}}",
              "processId": "{{processTypeId}}"
            }
            """;

        var witListResponse = $$"""
            {
              "count": 1,
              "value": [
                { "id": "wit-task", "name": "Task" }
              ]
            }
            """;

        var taskStatesResponse = $$"""
            {
              "fields": {
                "System.State": {
                  "name": {
                    "allowedValues": [ "To Do", "Done" ]
                  }
                }
              }
            }
            """;

        var client = CreateClientWithProcessApiResponses(
            processTypeId,
            projectResponse,
            witListResponse,
            new Dictionary<string, string>
            {
                ["Task"] = taskStatesResponse,
            }
        );

        // Act
        var result = await client.GetValidStatesAsync(Project, CancellationToken.None);

        // Assert: top-level processId fallback worked
        Assert.Single(result);
        string[] expectedTaskStates = ["Done", "To Do"];
        Assert.Equal(expectedTaskStates, result["Task"]);
    }

    [Fact]
    public async Task GetValidStatesAsync_EmptyWitList_ReturnsEmptyDictionary()
    {
        // Arrange: project valid, but WIT list returns empty
        const string processTypeId = "proc-empty";

        var projectResponse = $$"""
            {
              "capabilities": {
                "processTemplate": {
                  "templateTypeId": "{{processTypeId}}"
                }
              }
            }
            """;

        var witListResponse = $$"""
            {
              "count": 0,
              "value": []
            }
            """;

        var client = CreateClientWithProcessApiResponses(
            processTypeId,
            projectResponse,
            witListResponse,
            new Dictionary<string, string>()
        );

        // Act
        var result = await client.GetValidStatesAsync(Project, CancellationToken.None);

        // Assert: empty dictionary (no WITs to query)
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetValidStatesAsync_NoProcessTypeId_ThrowsHttpRequestException()
    {
        // Arrange: project response has no process template info
        var projectResponse = $$"""
            {
              "id": "proj-123",
              "name": "{{Project}}"
            }
            """;

        var client = CreateClientWithProcessApiResponses(
            null!,
            projectResponse,
            $$"""{"value":[]}""",
            new Dictionary<string, string>()
        );

        // Act + Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetValidStatesAsync(Project, CancellationToken.None));

        Assert.Contains("process template type ID", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetValidStatesAsync_ProjectLookupFails_ThrowsHttpRequestException()
    {
        // Arrange: project lookup returns 401
        var client = CreateClientWithStatusCodes(
            (urlContains: "_apis/projects/",
             statusCode: HttpStatusCode.Unauthorized,
             body: $$"""{"message":"Unauthorized"}""")
        );

        // Act + Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetValidStatesAsync(Project, CancellationToken.None));

        Assert.Contains("Project lookup failed", ex.Message);
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task GetValidStatesAsync_WitListFails_ThrowsHttpRequestException()
    {
        // Arrange: project lookup succeeds, WIT list returns 403
        const string processTypeId = "proc-forbidden";

        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            var url = req.RequestUri?.AbsoluteUri ?? string.Empty;

            if (url.Contains("_apis/projects/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        {
                          "capabilities": {
                            "processTemplate": {
                              "templateTypeId": "{{processTypeId}}"
                            }
                          }
                        }
                        """,
                        Encoding.UTF8, "application/json"),
                };
            }

            // WIT list returns 403
            if (url.Contains("/workItemTypes?"))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        $$"""{"message":"Forbidden"}""",
                        Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = CreateClientWithHandler(handler);

        // Act + Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetValidStatesAsync(Project, CancellationToken.None));

        Assert.Contains("work item types", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task GetValidStatesAsync_WitWithNoStates_ExcludedFromResult()
    {
        // Arrange: one WIT has states, another has none
        const string processTypeId = "proc-partial";

        var projectResponse = $$"""
            {
              "capabilities": {
                "processTemplate": {
                  "templateTypeId": "{{processTypeId}}"
                }
              }
            }
            """;

        var witListResponse = $$"""
            {
              "count": 2,
              "value": [
                { "id": "wit-bug", "name": "Bug" },
                { "id": "wit-epic", "name": "Epic" }
              ]
            }
            """;

        // Bug has states, Epic has none (no allowedValues)
        var bugStatesResponse = $$"""
            {
              "fields": {
                "System.State": {
                  "name": {
                    "allowedValues": [ "New", "Closed" ]
                  }
                }
              }
            }
            """;

        var epicStatesResponse = $$"""
            {
              "fields": {
                "System.State": {
                  "name": {}
                }
              }
            }
            """;

        var client = CreateClientWithProcessApiResponses(
            processTypeId,
            projectResponse,
            witListResponse,
            new Dictionary<string, string>
            {
                ["Bug"] = bugStatesResponse,
                ["Epic"] = epicStatesResponse,
            }
        );

        // Act
        var result = await client.GetValidStatesAsync(Project, CancellationToken.None);

        // Assert: only WITs with states are included
        Assert.Single(result);
        Assert.True(result.ContainsKey("Bug"));
        Assert.False(result.ContainsKey("Epic"));
        string[] expectedBugStates3 = ["Closed", "New"];
        Assert.Equal(expectedBugStates3, result["Bug"]);
    }

    [Fact]
    public async Task GetValidStatesAsync_EmptyProject_ReturnsEmptyDictionary()
    {
        var client = CreateClient();

        // Act
        var result = await client.GetValidStatesAsync(string.Empty, CancellationToken.None);

        // Assert: empty project returns empty dict (no HTTP call)
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetValidStatesAsync_NullProject_ReturnsEmptyDictionary()
    {
        var client = CreateClient();

        // Act
        var result = await client.GetValidStatesAsync(null!, CancellationToken.None);

        // Assert: null project returns empty dict (no HTTP call)
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetValidStatesAsync_NoLegacyFlatFields_OnlyBareStateNames()
    {
        // Arrange: mock response includes extra fields (id, url, metadata) that should NOT leak
        const string processTypeId = "proc-legacy";

        var projectResponse = $$"""
            {
              "capabilities": {
                "processTemplate": {
                  "templateTypeId": "{{processTypeId}}"
                }
              }
            }
            """;

        var witListResponse = $$"""
            {
              "count": 1,
              "value": [
                { "id": "wit-bug", "name": "Bug" }
              ]
            }
            """;

        // ADO response with extra fields that could leak if not handled correctly
        var bugStatesResponse = $$"""
            {
              "id": "wit-bug",
              "url": "https://dev.azure.com/org/_apis/work/processes/proc-legacy/workItemTypes/Bug",
              "fields": {
                "System.State": {
                  "name": {
                    "refName": "System.State",
                    "description": "Work item state",
                    "allowedValues": [ "Resolved", "New", "Closed", "Active" ]
                  }
                }
              }
            }
            """;

        var client = CreateClientWithProcessApiResponses(
            processTypeId,
            projectResponse,
            witListResponse,
            new Dictionary<string, string>
            {
                ["Bug"] = bugStatesResponse,
            }
        );

        // Act
        var result = await client.GetValidStatesAsync(Project, CancellationToken.None);

        // Assert: only bare state names, no legacy fields
        Assert.Single(result);
        var states = result["Bug"];
        Assert.Equal(4, states.Count);

        // Each state should be a plain string — no JSON, URLs, or IDs
        foreach (var state in states)
        {
            Assert.Matches("^[A-Z][a-zA-Z ]+$", state);
            Assert.DoesNotContain("http", state, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"", state);
            Assert.DoesNotContain("{", state);
        }

        // Verify the expected states are present
        Assert.Contains("Active", states);
        Assert.Contains("Closed", states);
        Assert.Contains("New", states);
        Assert.Contains("Resolved", states);
    }

    // ──────────────────────────────────────────────
    // GetValidStatesAsync helper — CreateClientWithProcessApiResponses
    // ──────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="AzureDevOpsBoardsClient"/> wired to a capture handler
    /// that routes Process API requests: project lookup, WIT list, and per-WIT states.
    /// </summary>
    private static AzureDevOpsBoardsClient CreateClientWithProcessApiResponses(
        string processTypeId,
        string projectResponseJson,
        string witListResponseJson,
        Dictionary<string, string> witStatesResponses)
    {
        var handler = new CaptureHttpMessageHandler(async (req) =>
        {
            var url = req.RequestUri?.AbsoluteUri ?? string.Empty;

            // (1) Project lookup
            if (url.Contains("_apis/projects/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        projectResponseJson,
                        Encoding.UTF8, "application/json"),
                };
            }

            // (2) WIT list (ends with ?api-version or ?api-version& — has query string)
            if (url.Contains("/workItemTypes?") || url.EndsWith("/workItemTypes", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        witListResponseJson,
                        Encoding.UTF8, "application/json"),
                };
            }

            // (3) Per-WIT states — match by WIT name in URL (URL-encoded)
            foreach (var (witName, responseJson) in witStatesResponses)
            {
                var encodedWitName = Uri.EscapeDataString(witName);
                if (url.Contains($"/workItemTypes/{encodedWitName}"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            responseJson,
                            Encoding.UTF8, "application/json"),
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        return CreateClientWithHandler(handler);
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
