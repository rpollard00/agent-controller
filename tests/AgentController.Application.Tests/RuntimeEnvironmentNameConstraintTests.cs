using AgentController.Domain;

namespace AgentController.Application.Tests;

/// <summary>
/// Regression coverage for the runtime environment name constraint. The existing key is
/// treated as the environment name while retaining its internal API/storage role and edit
/// immutability: 1-32 characters, the first an ASCII letter and the remainder ASCII letters,
/// numbers, hyphens, or underscores. Azure DevOps environment validation is unaffected.
/// </summary>
public sealed class RuntimeEnvironmentNameConstraintTests
{
    [Fact]
    public void Validate_AcceptsSingleCharacterLetterName()
    {
        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(Profile("a"));

        Assert.True(result.IsValid);
        Assert.Equal("a", result.Profile.Key);
    }

    [Fact]
    public void Validate_AcceptsMaximumLengthNameWithAllowedCharacters()
    {
        var key = "a" + new string('z', 31);
        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(Profile(key));

        Assert.True(result.IsValid);
        Assert.Equal(32, result.Profile.Key.Length);
        Assert.Equal(key, result.Profile.Key);
    }

    [Fact]
    public void Validate_RejectsNameExceedingMaximumLength()
    {
        var key = "a" + new string('z', 32);
        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(Profile(key));

        Assert.False(result.IsValid);
        Assert.Contains("key", result.Errors.Keys);
        Assert.Equal(33, key.Length);
    }

    [Theory]
    [InlineData("1env")] // digit
    [InlineData("-env")] // hyphen
    [InlineData("_env")] // underscore
    [InlineData(".env")] // period
    public void Validate_RejectsNameNotStartingWithLetter(string key)
    {
        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(Profile(key));

        Assert.False(result.IsValid);
        Assert.Contains("key", result.Errors.Keys);
    }

    [Theory]
    [InlineData("env name")] // space
    [InlineData("env.name")] // period
    [InlineData("env/name")] // slash
    [InlineData("env+name")] // plus
    [InlineData("env@name")] // at
    [InlineData("env#name")] // hash
    [InlineData("envé")] // non-ascii
    public void Validate_RejectsDisallowedCharacters(string key)
    {
        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(Profile(key));

        Assert.False(result.IsValid);
        Assert.Contains("key", result.Errors.Keys);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("abc")]
    [InlineData("Env123")]
    [InlineData("a-b_c")]
    [InlineData("production")]
    [InlineData("local-pi")]
    public void Validate_AcceptsLettersNumbersHyphensUnderscores(string key)
    {
        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(Profile(key));

        Assert.True(result.IsValid);
        Assert.Equal(key.ToLowerInvariant(), result.Profile.Key);
    }

    [Fact]
    public void Validate_NormalizesTrimmedLowercaseName()
    {
        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(
            Profile("  Local-Pi  ")
        );

        Assert.True(result.IsValid);
        Assert.Equal("local-pi", result.Profile.Key);
    }

    [Fact]
    public void Validate_RejectsEmptyName()
    {
        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(Profile("   "));

        Assert.False(result.IsValid);
        Assert.Contains("key", result.Errors.Keys);
    }

    [Fact]
    public void ValidateKey_EnforcesSameRuleForKeyBasedOperations()
    {
        var valid = RuntimeEnvironmentProfileValidation.ValidateAndNormalizeKey(
            "  Production-1  "
        );
        var tooLong = RuntimeEnvironmentProfileValidation.ValidateAndNormalizeKey(
            "a" + new string('z', 32)
        );
        var badFirstChar = RuntimeEnvironmentProfileValidation.ValidateAndNormalizeKey("1env");
        var badCharacter = RuntimeEnvironmentProfileValidation.ValidateAndNormalizeKey("env.name");

        Assert.True(valid.IsValid);
        Assert.Equal("production-1", valid.Key);
        Assert.False(tooLong.IsValid);
        Assert.Contains("key", tooLong.Errors.Keys);
        Assert.False(badFirstChar.IsValid);
        Assert.Contains("key", badFirstChar.Errors.Keys);
        Assert.False(badCharacter.IsValid);
        Assert.Contains("key", badCharacter.Errors.Keys);
    }

    private static RuntimeEnvironmentProfile Profile(string key) =>
        new()
        {
            Key = key,
            DisplayName = "Test runtime",
            Enabled = true,
            EnvironmentProvider = "LocalWorkspace",
            EnvironmentSettings = new EnvironmentProviderSettings
            {
                WorkspaceRoot = "/var/lib/agent-controller/runs",
            },
            RuntimeProvider = "MockPiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                Loadouts = new Dictionary<ExecutionKind, string>(),
            },
        };
}
