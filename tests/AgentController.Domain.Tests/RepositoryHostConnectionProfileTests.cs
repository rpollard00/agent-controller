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
        Assert.Empty(profile.PersonalAccessTokenReference.Name);
        Assert.Null(profile.PersonalAccessTokenReference.Version);
        Assert.False(profile.PersonalAccessTokenReference.IsSpecified);
        Assert.InRange(profile.CreatedAt, before, after);
        Assert.InRange(profile.UpdatedAt, profile.CreatedAt, after);
    }

    [Fact]
    public void RepositoryHostConnectionProfile_RecordEquality()
    {
        var timestamp = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var patRef = AgentController.Domain.Secrets.SecretReference.ByName("ado-repos-pat");
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
        var patRef = AgentController.Domain.Secrets.SecretReference.ByName("ado-repos-pat");
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
            PersonalAccessTokenReference = AgentController.Domain.Secrets.SecretReference.ByName("ado-pat"),
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddHours(1),
        };

        var json = JsonSerializer.Serialize(profile, JsonOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Verify the secret reference is serialized as structured data, not a raw token.
        var patRef = root.GetProperty("personalAccessTokenReference");
        Assert.Equal("ado-pat", patRef.GetProperty("name").GetString());

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
        Assert.Equal(profile.PersonalAccessTokenReference.Name, roundTripped.PersonalAccessTokenReference.Name);
        Assert.Equal(profile.CreatedAt, roundTripped.CreatedAt);
        Assert.Equal(profile.UpdatedAt, roundTripped.UpdatedAt);
    }

    [Fact]
    public void SecretReference_FactoryMethods()
    {
        var namedRef = AgentController.Domain.Secrets.SecretReference.ByName("MY_SECRET");
        Assert.Equal("MY_SECRET", namedRef.Name);
        Assert.True(namedRef.IsSpecified);

        var versionedRef = AgentController.Domain.Secrets.SecretReference.ByNameAndVersion("MY_SECRET", 2);
        Assert.Equal("MY_SECRET", versionedRef.Name);
        Assert.Equal(2, versionedRef.Version);

        var emptyRef = AgentController.Domain.Secrets.SecretReference.Empty;
        Assert.Empty(emptyRef.Name);
        Assert.False(emptyRef.IsSpecified);
    }

    [Fact]
    public void SecretReference_RecordEquality()
    {
        var a = AgentController.Domain.Secrets.SecretReference.ByName("PAT");
        var b = AgentController.Domain.Secrets.SecretReference.ByName("PAT");
        var c = AgentController.Domain.Secrets.SecretReference.ByName("OTHER");

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
