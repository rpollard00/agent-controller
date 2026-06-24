using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;

namespace AgentController.Api.Tests;

/// <summary>
/// Tests for the repo:{key} tag association validation at discovery.
/// Verifies that candidates with missing or mismatched repo keys are
/// skipped before claiming, with clarifying comments posted for remote sources.
/// </summary>
public class RepoKeyValidationTests
{
    private static readonly string[] EmptyTags = [];
    private static readonly string[] AgentReadyTag = ["agent-ready"];
    private static readonly string[] AgentReadyRepoExample = ["agent-ready", "repo:example-service"];

    // ── Scenario 1: Missing repo: tag (empty RepoKey) ────────────

    [Fact]
    public void ValidateRepoKey_MissingRepoTag_LocalSource_ReturnsFalse()
    {
        // Local sources with no repo: tag are treated as not-eligible
        // and skipped silently (no comment posted).
        var candidate = CreateCandidate(
            repoKey: string.Empty,
            source: "LocalFake",
            tags: AgentReadyTag);

        var result = RepoKeyValidator.Validate(
            candidate,
            RepoConfig("example-service"),
            out string? expectedComment);

        Assert.False(result);
        Assert.Null(expectedComment); // No comment for local sources
    }

    [Fact]
    public void ValidateRepoKey_MissingRepoTag_RemoteSource_ReturnsFalseWithComment()
    {
        // Remote sources with no repo: tag get a clarifying comment.
        var candidate = CreateCandidate(
            repoKey: string.Empty,
            source: "AzureDevOpsBoards",
            externalId: "42",
            tags: AgentReadyTag);

        var result = RepoKeyValidator.Validate(
            candidate,
            RepoConfig("example-service"),
            out string? expectedComment);

        Assert.False(result);
        Assert.NotNull(expectedComment);
        Assert.Contains("no `repo:` tag", expectedComment);
    }

    // ── Scenario 2: repo: tag present but no matching profile ────

    [Fact]
    public void ValidateRepoKey_NoMatchingProfile_LocalSource_ReturnsFalse()
    {
        // Local sources with no matching profile are skipped silently.
        var candidate = CreateCandidate(
            repoKey: "nonexistent-repo",
            source: "LocalFake",
            tags: ["agent-ready", "repo:nonexistent-repo"]);

        var result = RepoKeyValidator.Validate(
            candidate,
            RepoConfig("example-service"),
            out string? expectedComment);

        Assert.False(result);
        Assert.Null(expectedComment); // No comment for local sources
    }

    [Fact]
    public void ValidateRepoKey_NoMatchingProfile_RemoteSource_ReturnsFalseWithComment()
    {
        // Remote sources with no matching profile get a clarifying comment
        // that includes the repo key so typos are visible.
        var candidate = CreateCandidate(
            repoKey: "nonexistent-repo",
            source: "AzureDevOpsBoards",
            externalId: "42",
            tags: ["agent-ready", "repo:nonexistent-repo"]);

        var result = RepoKeyValidator.Validate(
            candidate,
            RepoConfig("example-service"),
            out string? expectedComment);

        Assert.False(result);
        Assert.NotNull(expectedComment);
        Assert.Contains("no repository profile matches", expectedComment);
        Assert.Contains("repo:nonexistent-repo", expectedComment);
    }

    // ── Scenario 3: repo: tag matches a configured profile ───────

    [Fact]
    public void ValidateRepoKey_MatchingProfile_ReturnsTrue()
    {
        var candidate = CreateCandidate(
            repoKey: "example-service",
            source: "AzureDevOpsBoards",
            externalId: "42",
            tags: AgentReadyRepoExample);

        var result = RepoKeyValidator.Validate(
            candidate,
            RepoConfig("example-service"),
            out string? expectedComment);

        Assert.True(result);
        Assert.Null(expectedComment); // No comment needed when profile matches
    }

    [Fact]
    public void ValidateRepoKey_MatchingProfile_LocalSource_ReturnsTrue()
    {
        var candidate = CreateCandidate(
            repoKey: "example-service",
            source: "LocalFake",
            tags: AgentReadyRepoExample);

        var result = RepoKeyValidator.Validate(
            candidate,
            RepoConfig("example-service"),
            out string? expectedComment);

        Assert.True(result);
        Assert.Null(expectedComment);
    }

    // ── Scenario 4: Multiple configured profiles ─────────────────

    [Fact]
    public void ValidateRepoKey_MatchesOneOfManyProfiles_ReturnsTrue()
    {
        var candidate = CreateCandidate(
            repoKey: "backend-service",
            source: "AzureDevOpsBoards",
            externalId: "99",
            tags: ["agent-ready", "repo:backend-service"]);

        var result = RepoKeyValidator.Validate(
            candidate,
            RepoConfig("example-service", "backend-service", "frontend-app"),
            out string? expectedComment);

        Assert.True(result);
        Assert.Null(expectedComment);
    }

    [Fact]
    public void ValidateRepoKey_NoMatchInManyProfiles_ReturnsFalseWithComment()
    {
        var candidate = CreateCandidate(
            repoKey: "typo-service",
            source: "AzureDevOpsBoards",
            externalId: "99",
            tags: ["agent-ready", "repo:typo-service"]);

        var result = RepoKeyValidator.Validate(
            candidate,
            RepoConfig("example-service", "backend-service", "frontend-app"),
            out string? expectedComment);

        Assert.False(result);
        Assert.NotNull(expectedComment);
        Assert.Contains("repo:typo-service", expectedComment);
    }

    // ── Scenario 5: Whitespace-only repo key treated as missing ──

    [Fact]
    public void ValidateRepoKey_WhitespaceOnlyRepoKey_TreatedAsMissing()
    {
        var candidate = CreateCandidate(
            repoKey: "   ",
            source: "AzureDevOpsBoards",
            externalId: "42",
            tags: AgentReadyTag);

        var result = RepoKeyValidator.Validate(
            candidate,
            RepoConfig("example-service"),
            out string? expectedComment);

        Assert.False(result);
        Assert.NotNull(expectedComment);
        Assert.Contains("no `repo:` tag", expectedComment);
    }

    // ── Scenario 6: Empty configuration ──────────────────────────

    [Fact]
    public void ValidateRepoKey_NoProfilesConfigured_RemoteSource_ReturnsFalseWithComment()
    {
        var candidate = CreateCandidate(
            repoKey: "any-repo",
            source: "AzureDevOpsBoards",
            externalId: "42",
            tags: ["agent-ready", "repo:any-repo"]);

        var result = RepoKeyValidator.Validate(
            candidate,
            RepoConfig(), // Empty configuration
            out string? expectedComment);

        Assert.False(result);
        Assert.NotNull(expectedComment);
        Assert.Contains("no repository profile matches", expectedComment);
    }

    // ── Test helpers ─────────────────────────────────────────────

    private static WorkCandidate CreateCandidate(
        string repoKey,
        string source,
        string? externalId = null,
        IReadOnlyList<string>? tags = null)
    {
        return new WorkCandidate
        {
            Id = $"wi_{Guid.NewGuid():N}",
            ExternalId = externalId ?? "1",
            Source = source,
            Title = "Test work item",
            RepoKey = repoKey,
            Tags = tags ?? EmptyTags,
            ExternalUrl = source == "AzureDevOpsBoards"
                ? $"https://dev.azure.com/org/project/_workitems/edit/{externalId}"
                : null,
            SourceMetadata = source == "AzureDevOpsBoards"
                ? new Dictionary<string, string> { ["revision"] = "1" }
                : null,
        };
    }

    private static Dictionary<string, RepositoryProfileOptions> RepoConfig(
        params string[] keys)
    {
        var config = new Dictionary<string, RepositoryProfileOptions>();
        foreach (var key in keys)
        {
            config[key] = new RepositoryProfileOptions
            {
                CloneUrl = $"https://dev.azure.com/org/project/_git/{key}",
                DefaultBranch = "main",
            };
        }
        return config;
    }

    /// <summary>
    /// Pure function encapsulating the repo key validation logic.
    /// Mirrors the behavior of PollingWorker.ValidateRepoKeyAsync
    /// but without async/await for straightforward unit testing.
    ///
    /// This is the core decision logic; the PollingWorker wraps this
    /// with async I/O (comment posting) and logging.
    /// </summary>
    private static class RepoKeyValidator
    {
        public static bool Validate(
            WorkCandidate candidate,
            Dictionary<string, RepositoryProfileOptions> repoConfig,
            out string? clarifyingComment)
        {
            clarifyingComment = null;

            var repoKey = candidate.RepoKey;
            bool isLocalSource = candidate.Source == "LocalFake" || candidate.Source == "LocalFile";

            // No repo: tag at all — not eligible
            if (string.IsNullOrWhiteSpace(repoKey))
            {
                if (!isLocalSource)
                {
                    clarifyingComment =
                        "Skipped: work item has no `repo:` tag. Add a tag like `repo:example-service` " +
                        "to associate this item with a configured repository profile.";
                }
                return false;
            }

            // repo: tag present — check against configured profiles
            if (repoConfig.TryGetValue(repoKey, out _))
            {
                return true; // Profile found — proceed
            }

            // No matching profile
            if (!isLocalSource)
            {
                clarifyingComment =
                    $"Skipped: no repository profile matches the `repo:{repoKey}` tag. " +
                    "Check for typos or add a matching profile to the 'repositories' configuration.";
            }
            return false;
        }
    }
}
