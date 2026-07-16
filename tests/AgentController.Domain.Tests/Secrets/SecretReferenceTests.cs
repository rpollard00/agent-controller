namespace AgentController.Domain.Tests.Secrets;

public class SecretReferenceTests
{
    [Fact]
    public void Empty_HasEmptyNameAndNoVersion()
    {
        var reference = Domain.Secrets.SecretReference.Empty;

        Assert.Empty(reference.Name);
        Assert.Null(reference.Version);
        Assert.False(reference.IsSpecified);
    }

    [Fact]
    public void ByName_CreatesReferenceWithLatestVersion()
    {
        var reference = Domain.Secrets.SecretReference.ByName("ado-pat");

        Assert.Equal("ado-pat", reference.Name);
        Assert.Null(reference.Version);
        Assert.True(reference.IsSpecified);
    }

    [Fact]
    public void ByNameAndVersion_CreatesReferenceWithPinnedVersion()
    {
        var reference = Domain.Secrets.SecretReference.ByNameAndVersion("github-token", 3);

        Assert.Equal("github-token", reference.Name);
        Assert.Equal(3, reference.Version);
        Assert.True(reference.IsSpecified);
    }

    [Fact]
    public void IsSpecified_ReturnsFalseForWhitespaceName()
    {
        var reference = new Domain.Secrets.SecretReference { Name = "  " };

        Assert.False(reference.IsSpecified);
    }

    [Fact]
    public void RecordEquality_SameNameAndVersionAreEqual()
    {
        var a = Domain.Secrets.SecretReference.ByName("my-secret");
        var b = Domain.Secrets.SecretReference.ByName("my-secret");

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentVersionsAreNotEqual()
    {
        var a = Domain.Secrets.SecretReference.ByNameAndVersion("my-secret", 1);
        var b = Domain.Secrets.SecretReference.ByNameAndVersion("my-secret", 2);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentNamesAreNotEqual()
    {
        var a = Domain.Secrets.SecretReference.ByName("secret-a");
        var b = Domain.Secrets.SecretReference.ByName("secret-b");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RecordEquality_NameWithVersionVsNameWithoutVersion_AreNotEqual()
    {
        var a = Domain.Secrets.SecretReference.ByName("my-secret");
        var b = Domain.Secrets.SecretReference.ByNameAndVersion("my-secret", 1);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void WithExpression_PreservesNameAndChangesVersion()
    {
        var original = Domain.Secrets.SecretReference.ByName("my-secret");
        var updated = original with { Version = 5 };

        Assert.Equal("my-secret", updated.Name);
        Assert.Equal(5, updated.Version);
        Assert.Null(original.Version); // Original unchanged
    }

    [Fact]
    public void WorkSourceEnvironmentProfile_HasDefaultSecretReference()
    {
        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "test",
            DisplayName = "Test",
            OrganizationUrl = "https://dev.azure.com/example",
            Project = "TestProject",
        };

        Assert.False(profile.PersonalAccessTokenReference.IsSpecified);
        Assert.Empty(profile.PersonalAccessTokenReference.Name);
        Assert.Null(profile.PersonalAccessTokenReference.Version);
    }

    [Fact]
    public void WorkSourceEnvironmentProfile_CanSetSecretReference()
    {
        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "test",
            DisplayName = "Test",
            OrganizationUrl = "https://dev.azure.com/example",
            Project = "TestProject",
            PersonalAccessTokenReference = Domain.Secrets.SecretReference.ByNameAndVersion("ado-pat", 2),
        };

        Assert.True(profile.PersonalAccessTokenReference.IsSpecified);
        Assert.Equal("ado-pat", profile.PersonalAccessTokenReference.Name);
        Assert.Equal(2, profile.PersonalAccessTokenReference.Version);
    }
}
