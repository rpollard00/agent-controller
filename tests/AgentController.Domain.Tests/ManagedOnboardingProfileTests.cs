using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentController.Domain.Tests;

public class ManagedOnboardingProfileTests
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public void WorkSourceEnvironmentProfile_HasSafeDefaults()
    {
        var before = DateTimeOffset.UtcNow;
        var profile = new WorkSourceEnvironmentProfile();
        var after = DateTimeOffset.UtcNow;

        Assert.True(profile.Enabled);
        Assert.Equal("AzureDevOpsBoards", profile.Provider);
        Assert.Equal("agent", profile.TagPrefix);
        Assert.Null(profile.ActiveState);
        Assert.Null(profile.CompletedState);
        Assert.Empty(profile.ConnectionKey);
        Assert.InRange(profile.CreatedAt, before, after);
        Assert.InRange(profile.UpdatedAt, profile.CreatedAt, after);
    }

    [Fact]
    public void RuntimeEnvironmentProfile_HasProviderDefaultsWithoutCredentialValues()
    {
        var profile = new RuntimeEnvironmentProfile();

        Assert.True(profile.Enabled);
        Assert.Empty(profile.EnvironmentProvider);
        Assert.Empty(profile.RuntimeProvider);
        Assert.Null(profile.EnvironmentSettings.WorkspaceRoot);
        // Pi Materia process settings are controller-owned; the per-profile record carries
        // no defaults for them so stale stored overrides cannot influence execution.
        Assert.Null(profile.RuntimeSettings.PiExecutablePath);
        Assert.Null(profile.RuntimeSettings.ControllerBaseUrl);
        Assert.Null(profile.RuntimeSettings.PtyWrapperPath);
        Assert.Null(profile.RuntimeSettings.PtyWrapperArgs);
        Assert.Empty(profile.RuntimeSettings.ForwardEnvironmentVariables);
        // Loadouts remain a user-level, profile-specific control and keep safe defaults.
        Assert.Equal("ADO-Build-NewWork", profile.RuntimeSettings.Loadouts[ExecutionKind.NewWork]);
        Assert.Equal("ADO-Build-Rework", profile.RuntimeSettings.Loadouts[ExecutionKind.Rework]);
    }

    [Fact]
    public void RepositoryProfile_ManagedAssociationsAreOptionalAndLegacyNamesRemainAvailable()
    {
        var profile = new RepositoryProfile
        {
            Key = "example",
            CloneUrl = "https://example.test/example.git",
            EnvironmentProfile = "legacy-environment",
            RuntimeProfile = "legacy-runtime",
        };

        Assert.Equal("legacy-environment", profile.EnvironmentProfile);
        Assert.Equal("legacy-runtime", profile.RuntimeProfile);
        Assert.Null(profile.RepositoryHostConnectionKey);
        Assert.Null(profile.RuntimeEnvironmentKey);
    }

    [Fact]
    public void WorkSourceEnvironmentProfile_SerializesConnectionKey()
    {
        var createdAt = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var profile = new WorkSourceEnvironmentProfile
        {
            Key = "primary-ado",
            DisplayName = "Primary Azure DevOps",
            Enabled = false,
            Provider = "AzureDevOpsBoards",
            TagPrefix = "ac",
            ConnectionKey = "azuredevops-example-org",
            Project = "Agent Controller",
            ActiveState = "Active",
            CompletedState = "Closed",
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddHours(1),
        };

        var json = JsonSerializer.Serialize(profile, JsonOptions);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("organizationUrl", out _));
        Assert.False(root.TryGetProperty("personalAccessToken", out _));
        Assert.False(root.TryGetProperty("pat", out _));
        Assert.True(root.TryGetProperty("connectionKey", out var connKeyProp));
        Assert.Equal("azuredevops-example-org", connKeyProp.GetString());

        var roundTripped = JsonSerializer.Deserialize<WorkSourceEnvironmentProfile>(json, JsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(profile.Key, roundTripped.Key);
        Assert.Equal(profile.DisplayName, roundTripped.DisplayName);
        Assert.Equal(profile.Enabled, roundTripped.Enabled);
        Assert.Equal(profile.Provider, roundTripped.Provider);
        Assert.Equal(profile.TagPrefix, roundTripped.TagPrefix);
        Assert.Equal(profile.ConnectionKey, roundTripped.ConnectionKey);
        Assert.Equal(profile.Project, roundTripped.Project);
        Assert.Equal(profile.ActiveState, roundTripped.ActiveState);
        Assert.Equal(profile.CompletedState, roundTripped.CompletedState);
        Assert.Equal(profile.CreatedAt, roundTripped.CreatedAt);
        Assert.Equal(profile.UpdatedAt, roundTripped.UpdatedAt);
    }

    [Fact]
    public void RuntimeEnvironmentProfile_SerializesStructuredProviderSettings()
    {
        var timestamp = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var profile = new RuntimeEnvironmentProfile
        {
            Key = "local-pi",
            DisplayName = "Local pi-materia",
            EnvironmentProvider = "LocalWorkspace",
            EnvironmentSettings = new EnvironmentProviderSettings
            {
                WorkspaceRoot = "/var/lib/agent-controller/runs",
            },
            RuntimeProvider = "PiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = "/usr/local/bin/pi",
                ControllerBaseUrl = "https://controller.example.test",
                PtyWrapperPath = "script",
                PtyWrapperArgs = "-qfc",
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.NewWork] = "new-work",
                    [ExecutionKind.Rework] = "rework",
                },
                ForwardEnvironmentVariables = new Dictionary<string, string>
                {
                    ["AZURE_DEVOPS_EXT_PAT"] = "CONTROLLER_ADO_PAT",
                },
            },
            CreatedAt = timestamp,
            UpdatedAt = timestamp.AddMinutes(5),
        };

        var json = JsonSerializer.Serialize(profile, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<RuntimeEnvironmentProfile>(json, JsonOptions);

        using var document = JsonDocument.Parse(json);
        var runtimeSettings = document.RootElement.GetProperty("runtimeSettings");
        Assert.Equal(
            "CONTROLLER_ADO_PAT",
            runtimeSettings
                .GetProperty("forwardEnvironmentVariables")
                .GetProperty("AZURE_DEVOPS_EXT_PAT")
                .GetString());
        Assert.DoesNotContain("secret-value", json, StringComparison.Ordinal);

        Assert.NotNull(roundTripped);
        Assert.Equal(profile.Key, roundTripped.Key);
        Assert.Equal(profile.EnvironmentProvider, roundTripped.EnvironmentProvider);
        Assert.Equal(profile.EnvironmentSettings.WorkspaceRoot, roundTripped.EnvironmentSettings.WorkspaceRoot);
        Assert.Equal(profile.RuntimeProvider, roundTripped.RuntimeProvider);
        Assert.Equal(profile.RuntimeSettings.PiExecutablePath, roundTripped.RuntimeSettings.PiExecutablePath);
        Assert.Equal(profile.RuntimeSettings.ControllerBaseUrl, roundTripped.RuntimeSettings.ControllerBaseUrl);
        Assert.Equal("new-work", roundTripped.RuntimeSettings.Loadouts[ExecutionKind.NewWork]);
        Assert.Equal(
            "CONTROLLER_ADO_PAT",
            roundTripped.RuntimeSettings.ForwardEnvironmentVariables["AZURE_DEVOPS_EXT_PAT"]);
        Assert.Equal(profile.CreatedAt, roundTripped.CreatedAt);
        Assert.Equal(profile.UpdatedAt, roundTripped.UpdatedAt);
    }

    [Fact]
    public void RepositoryProfile_DeserializesLegacyPayloadAndRoundTripsManagedAssociations()
    {
        const string legacyJson = """
            {
              "key": "example",
              "cloneUrl": "https://example.test/example.git",
              "defaultBranch": "main",
              "transport": "httpsPat",
              "environmentProfile": "legacy-environment",
              "runtimeProfile": "legacy-runtime"
            }
            """;

        var legacy = JsonSerializer.Deserialize<RepositoryProfile>(legacyJson, JsonOptions);

        Assert.NotNull(legacy);
        Assert.Equal("legacy-environment", legacy.EnvironmentProfile);
        Assert.Equal("legacy-runtime", legacy.RuntimeProfile);
        Assert.Null(legacy.RepositoryHostConnectionKey);
        Assert.Null(legacy.RuntimeEnvironmentKey);

        var managed = legacy with
        {
            RepositoryHostConnectionKey = "primary-ado",
            RuntimeEnvironmentKey = "local-pi",
        };
        var json = JsonSerializer.Serialize(managed, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<RepositoryProfile>(json, JsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal("primary-ado", roundTripped.RepositoryHostConnectionKey);
        Assert.Equal("local-pi", roundTripped.RuntimeEnvironmentKey);
        Assert.Equal("legacy-environment", roundTripped.EnvironmentProfile);
        Assert.Equal("legacy-runtime", roundTripped.RuntimeProfile);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
