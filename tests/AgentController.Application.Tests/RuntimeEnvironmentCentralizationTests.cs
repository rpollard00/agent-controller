using AgentController.Domain;

namespace AgentController.Application.Tests;

/// <summary>
/// Regression coverage for the WI-1 centralization of Pi Materia execution settings.
/// Pi executable, controller callback URL, PTY, and environment-variable forwarding are
/// controller-owned: create/update must not require them, stale stored overrides must not
/// alter execution, and they must not be persisted per-profile. Loadouts remain a
/// user-level, profile-specific control and are still validated and persisted.
/// </summary>
public sealed class RuntimeEnvironmentCentralizationTests
{
    [Fact]
    public void Validate_PiMateriaProfileWithoutControllerOwnedSettings_SucceedsAndPersistsLoadouts()
    {
        var profile = new RuntimeEnvironmentProfile
        {
            Key = "pi-only",
            DisplayName = "Pi only",
            EnvironmentProvider = "LocalWorkspace",
            RuntimeProvider = "PiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.NewWork] = "my-loadout",
                },
            },
        };

        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal("my-loadout", result.Profile.RuntimeSettings.Loadouts[ExecutionKind.NewWork]);
        Assert.Null(result.Profile.RuntimeSettings.PiExecutablePath);
        Assert.Null(result.Profile.RuntimeSettings.ControllerBaseUrl);
        Assert.Null(result.Profile.RuntimeSettings.PtyWrapperPath);
        Assert.Null(result.Profile.RuntimeSettings.PtyWrapperArgs);
        Assert.Empty(result.Profile.RuntimeSettings.ForwardEnvironmentVariables);
    }

    [Fact]
    public void Validate_PiMateriaProfileDropsStaleControllerOwnedOverridesButKeepsLoadouts()
    {
        // Legacy/stale technical settings are accepted for compatibility but dropped so
        // they can never alter execution. Loadouts remain per-profile and are persisted.
        var profile = new RuntimeEnvironmentProfile
        {
            Key = "pi-stale",
            DisplayName = "Pi stale overrides",
            EnvironmentProvider = "LocalWorkspace",
            RuntimeProvider = "PiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = "/stale/pi",
                ControllerBaseUrl = "http://stale.example.test/",
                PtyWrapperPath = "stale-script",
                PtyWrapperArgs = "-stale",
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.NewWork] = "kept-loadout",
                },
                ForwardEnvironmentVariables = new Dictionary<string, string>
                {
                    ["STALE_TARGET"] = "STALE_SOURCE",
                },
            },
        };

        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal("kept-loadout", result.Profile.RuntimeSettings.Loadouts[ExecutionKind.NewWork]);
        Assert.Null(result.Profile.RuntimeSettings.PiExecutablePath);
        Assert.Null(result.Profile.RuntimeSettings.ControllerBaseUrl);
        Assert.Null(result.Profile.RuntimeSettings.PtyWrapperPath);
        Assert.Null(result.Profile.RuntimeSettings.PtyWrapperArgs);
        Assert.Empty(result.Profile.RuntimeSettings.ForwardEnvironmentVariables);
    }

    [Fact]
    public void Validate_InvalidControllerOwnedSettingsDoNotBlockCreation()
    {
        // Garbage controller-owned values must not produce validation errors; they are
        // simply ignored. Only loadouts remain validated per-profile.
        var profile = new RuntimeEnvironmentProfile
        {
            Key = "pi-garbage",
            DisplayName = "Pi garbage overrides",
            EnvironmentProvider = "LocalWorkspace",
            RuntimeProvider = "PiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = "ftp://not-a-path",
                ControllerBaseUrl = "not a url",
                PtyWrapperPath = "bad\npath",
                PtyWrapperArgs = new string('x', 9999),
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.NewWork] = "valid-loadout",
                },
                ForwardEnvironmentVariables = new Dictionary<string, string>
                {
                    ["1BAD"] = "raw-secret-value",
                },
            },
        };

        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(profile);

        Assert.True(result.IsValid);
        Assert.DoesNotContain("runtimeSettings.piExecutablePath", result.Errors.Keys);
        Assert.DoesNotContain("runtimeSettings.controllerBaseUrl", result.Errors.Keys);
        Assert.DoesNotContain("runtimeSettings.ptyWrapperPath", result.Errors.Keys);
        Assert.DoesNotContain("runtimeSettings.ptyWrapperArgs", result.Errors.Keys);
        Assert.DoesNotContain("runtimeSettings.forwardEnvironmentVariables", result.Errors.Keys);
        Assert.Equal("valid-loadout", result.Profile.RuntimeSettings.Loadouts[ExecutionKind.NewWork]);
    }

    [Fact]
    public void Validate_PiMateriaProfileRequiresNewWorkLoadout()
    {
        var reworkOnly = new RuntimeEnvironmentProfile
        {
            Key = "pi-rework-only",
            DisplayName = "Pi rework only",
            EnvironmentProvider = "LocalWorkspace",
            RuntimeProvider = "PiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.Rework] = "rework-only",
                },
            },
        };

        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(reworkOnly);

        Assert.False(result.IsValid);
        Assert.Contains("runtimeSettings.loadouts", result.Errors.Keys);
    }

    [Fact]
    public void Validate_PiMateriaProfileRejectsUnknownAndEmptyLoadouts()
    {
        var profile = new RuntimeEnvironmentProfile
        {
            Key = "pi-bad-loadouts",
            DisplayName = "Pi bad loadouts",
            EnvironmentProvider = "LocalWorkspace",
            RuntimeProvider = "PiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.Rework] = "  ",
                    [(ExecutionKind)999] = "unknown",
                },
            },
        };

        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(profile);

        Assert.False(result.IsValid);
        Assert.Contains("runtimeSettings.loadouts", result.Errors.Keys);
    }

    [Fact]
    public void Validate_MockPiMateriaProfileDoesNotRequireLoadoutsOrControllerOwnedSettings()
    {
        var profile = new RuntimeEnvironmentProfile
        {
            Key = "mock-empty",
            DisplayName = "Mock empty",
            EnvironmentProvider = "LocalWorkspace",
            RuntimeProvider = "MockPiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                Loadouts = new Dictionary<ExecutionKind, string>(),
            },
        };

        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(profile);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Profile.RuntimeSettings.Loadouts);
        Assert.Null(result.Profile.RuntimeSettings.PiExecutablePath);
        Assert.Null(result.Profile.RuntimeSettings.ControllerBaseUrl);
        Assert.Null(result.Profile.RuntimeSettings.PtyWrapperPath);
        Assert.Null(result.Profile.RuntimeSettings.PtyWrapperArgs);
        Assert.Empty(result.Profile.RuntimeSettings.ForwardEnvironmentVariables);
    }

    [Fact]
    public void Validate_RuntimeSettingsContainerIsRequired()
    {
        var profile = new RuntimeEnvironmentProfile
        {
            Key = "missing-runtime",
            DisplayName = "Missing runtime settings",
            EnvironmentProvider = "LocalWorkspace",
            RuntimeProvider = "MockPiMateria",
            RuntimeSettings = null!,
        };

        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(profile);

        Assert.False(result.IsValid);
        Assert.Contains("runtimeSettings", result.Errors.Keys);
    }

    [Fact]
    public void Validate_LoadoutIsNormalizedAndLengthValidated()
    {
        var profile = new RuntimeEnvironmentProfile
        {
            Key = "pi-normalize",
            DisplayName = "Pi normalize",
            EnvironmentProvider = "LocalWorkspace",
            RuntimeProvider = "PiMateria",
            RuntimeSettings = new RuntimeProviderSettings
            {
                Loadouts = new Dictionary<ExecutionKind, string>
                {
                    [ExecutionKind.NewWork] = "  trimmed-loadout  ",
                },
            },
        };

        var result = RuntimeEnvironmentProfileValidation.ValidateAndNormalize(profile);

        Assert.True(result.IsValid);
        Assert.Equal("trimmed-loadout", result.Profile.RuntimeSettings.Loadouts[ExecutionKind.NewWork]);
    }
}
