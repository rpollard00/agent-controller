using System.Text.Json;

namespace AgentController.Domain.Tests;

public sealed class ReviewerIdentityTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void ReviewerIdentity_CarriesProviderNeutralKindAndValue()
    {
        var identity = new ReviewerIdentity
        {
            Kind = "opaque-kind",
            Value = "opaque-value",
        };

        Assert.Equal("opaque-kind", identity.Kind);
        Assert.Equal("opaque-value", identity.Value);
    }

    [Fact]
    public void RepositoryProfile_DefaultsToAnEmptyReviewerIdentityCollection()
    {
        var profile = new RepositoryProfile();

        Assert.Empty(profile.ReviewerIdentities);
    }

    [Fact]
    public void ReviewerIdentitiesAndAuthorAliases_RoundTripThroughJson()
    {
        var profile = new RepositoryProfile
        {
            Key = "payments",
            ReviewerIdentities =
            [
                new ReviewerIdentity { Kind = "identity-kind", Value = "reviewer-1" },
                new ReviewerIdentity { Kind = "other-kind", Value = "reviewer-2" },
            ],
        };
        var comment = new ReviewThreadComment
        {
            Author = "Reviewer One",
            AuthorIdentities =
            [
                new ReviewerIdentity { Kind = "identity-kind", Value = "reviewer-1" },
                new ReviewerIdentity { Kind = "other-kind", Value = "reviewer-2" },
            ],
            Body = "Please update this.",
        };

        var profileJson = JsonSerializer.Serialize(profile, JsonOptions);
        var commentJson = JsonSerializer.Serialize(comment, JsonOptions);

        using var profileDocument = JsonDocument.Parse(profileJson);
        var serializedIdentities = profileDocument.RootElement
            .GetProperty("reviewerIdentities");
        Assert.Equal(JsonValueKind.Array, serializedIdentities.ValueKind);
        Assert.Equal("identity-kind", serializedIdentities[0].GetProperty("kind").GetString());
        Assert.Equal("reviewer-1", serializedIdentities[0].GetProperty("value").GetString());

        using var commentDocument = JsonDocument.Parse(commentJson);
        var serializedAliases = commentDocument.RootElement
            .GetProperty("authorIdentities");
        Assert.Equal(JsonValueKind.Array, serializedAliases.ValueKind);
        Assert.Equal("Reviewer One", commentDocument.RootElement.GetProperty("author").GetString());

        var roundTrippedProfile = JsonSerializer.Deserialize<RepositoryProfile>(
            profileJson,
            JsonOptions);
        var roundTrippedComment = JsonSerializer.Deserialize<ReviewThreadComment>(
            commentJson,
            JsonOptions);

        Assert.NotNull(roundTrippedProfile);
        Assert.Equal(profile.ReviewerIdentities, roundTrippedProfile.ReviewerIdentities);
        Assert.NotNull(roundTrippedComment);
        Assert.Equal(comment.Author, roundTrippedComment.Author);
        Assert.Equal(comment.AuthorIdentities, roundTrippedComment.AuthorIdentities);
        Assert.Equal(comment.Body, roundTrippedComment.Body);
    }
}
