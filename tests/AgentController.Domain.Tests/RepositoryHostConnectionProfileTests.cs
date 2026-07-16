using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentController.Domain.Tests;

public class RepositoryHostConnectionProfileTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public void RepositoryHostConnectionProfile_HasSafeDefaults()
    {
        var before = DateTimeOffset.UtcNow;
        var profile = new RepositoryHostConnectionProfile();
        var after = DateTimeOffset.UtcNow;

        Assert.True(profile.Enabled);
        Assert.Equal("AzureDevOpsRepos", profile.Provider);
        Assert.Empty(profile.Key);
        Assert.Empty(profile.DisplayName);
        Assert.Empty(profile.OrganizationUrl);
        Assert.Empty(profile.Project);
        Assert.Empty(profile.PersonalAccessTokenReference.Kind);
        Assert.Empty(profile.PersonalAccessTokenReference.Id);
        Assert.InRange(profile.CreatedAt, before, after);
        Assert.InRange(profile.UpdatedAt, profile.CreatedAt, after);
    }

    [Fact]
    public void RepositoryHostConnectionProfile_RecordEquality()
    {
        var timestamp = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var patRef = SecretReference.EnvironmentVariable("ADO_REPOS_PAT");
        var profileA = new RepositoryHostConnectionProfile
        {
            Key = "ado-primary",
            DisplayName = "Primary ADO Repos",
            Enabled = true,
            Provider = "AzureDevOpsRepos",
            OrganizationUrl = "https://dev.azure.com/example",
            Project = "Agent Controller",
            PersonalAccessTokenReference = patRef,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };

        var profileB = new RepositoryHostConnectionProfile
        {
            Key = "ado-primary",
            DisplayName = "Primary ADO Repos",
            Enabled = true,
            Provider = "AzureDevOpsRepos",
            OrganizationUrl = "https://dev.azure.com/example",
            Project = "Agent Controller",
            PersonalAccessTokenReference = patRef,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };

        var profileC = profileA with
        {
            DisplayName = "Renamed ADO Repos",
        };

        Assert.Equal(profileA, profileB);
        Assert.NotEqual(profileA, profileC);
        Assert.NotSame(profileA, profileC);
    }

    [Fact]
    public void RepositoryHostConnectionProfile_WithExpressionDoesNotMutateOriginal()
    {
        var patRef = SecretReference.Database("guid-123");
        var original = new RepositoryHostConnectionProfile
        {
            Key = "ado-repos",
            DisplayName = "ADO Repos",
            Provider = "AzureDevOpsRepos",
            OrganizationUrl = "https://dev.azure.com/org",
            Project = "Project",
            PersonalAccessTokenReference = patRef,
        };

        var updated = original with { Enabled = false };

        Assert.True(original.Enabled);
        Assert.False(updated.Enabled);
        Assert.NotEqual(original, updated);
    }

    [Fact]
    public void RepositoryHostConnectionProfile_SerializesSecretReferenceNotRawValue()
    {
        var createdAt = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        var profile = new RepositoryHostConnectionProfile
        {
            Key = "ado-repos",
            DisplayName = "ADO Repos Connection",
            Enabled = false,
            Provider = "AzureDevOpsRepos",
            OrganizationUrl = "https://dev.azure.com/myorg",
            Project = "MyProject",
            PersonalAccessTokenReference = SecretReference.EnvironmentVariable("ADO_PAT"),
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddHours(1),
        };

        var json = JsonSerializer.Serialize(profile, JsonOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Verify the secret reference is serialized as structured data, not a raw token.
        var patRef = root.GetProperty("personalAccessTokenReference");
        Assert.Equal("EnvVar", patRef.GetProperty("kind").GetString());
        Assert.Equal("ADO_PAT", patRef.GetProperty("id").GetString());

        // Verify no raw secret value leaks into the JSON (only the reference is present).
        Assert.DoesNotContain("secretValue", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawSecret", json, StringComparison.OrdinalIgnoreCase);

        // Round-trip.
        var roundTripped = JsonSerializer.Deserialize<RepositoryHostConnectionProfile>(json, JsonOptions);
        Assert.NotNull(roundTripped);
        Assert.Equal(profile.Key, roundTripped.Key);
        Assert.Equal(profile.DisplayName, roundTripped.DisplayName);
        Assert.Equal(profile.Enabled, roundTripped.Enabled);
        Assert.Equal(profile.Provider, roundTripped.Provider);
        Assert.Equal(profile.OrganizationUrl, roundTripped.OrganizationUrl);
        Assert.Equal(profile.Project, roundTripped.Project);
        Assert.Equal(profile.PersonalAccessTokenReference.Kind, roundTripped.PersonalAccessTokenReference.Kind);
        Assert.Equal(profile.PersonalAccessTokenReference.Id, roundTripped.PersonalAccessTokenReference.Id);
        Assert.Equal(profile.CreatedAt, roundTripped.CreatedAt);
        Assert.Equal(profile.UpdatedAt, roundTripped.UpdatedAt);
    }

    [Fact]
    public void SecretReference_FactoryMethods()
    {
        var envRef = SecretReference.EnvironmentVariable("MY_SECRET");
        Assert.Equal("EnvVar", envRef.Kind);
        Assert.Equal("MY_SECRET", envRef.Id);

        var dbRef = SecretReference.Database("abc-123");
        Assert.Equal("Db", dbRef.Kind);
        Assert.Equal("abc-123", dbRef.Id);
    }

    [Fact]
    public void SecretReference_RecordEquality()
    {
        var a = SecretReference.EnvironmentVariable("PAT");
        var b = SecretReference.EnvironmentVariable("PAT");
        var c = SecretReference.Database("PAT");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
