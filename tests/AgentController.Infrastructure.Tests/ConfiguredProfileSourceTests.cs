using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

/// <summary>
/// Regression coverage for WI-1: the configured (appsettings) runtime-environment profile
/// projects per-profile loadouts from <see cref="RuntimeOptions"/> but does not project
/// controller-owned process settings (executable, controller URL, PTY, env-var forwarding).
/// </summary>
public sealed class ConfiguredProfileSourceTests
{
    [Fact]
    public void GetRuntimeEnvironment_ProjectsLoadoutsButNotControllerOwnedSettings()
    {
        var runtime = new RuntimeOptions
        {
            Provider = "PiMateria",
            PiExecutablePath = "/controller/pi",
            ControllerBaseUrl = "http://controller.example.test",
            PtyWrapperPath = "script",
            PtyWrapperArgs = "-qfc",
            Loadouts = new Dictionary<ExecutionKind, string>
            {
                [ExecutionKind.NewWork] = "Controller-NewWork",
                [ExecutionKind.Rework] = "Controller-Rework",
            },
            ForwardEnvironmentVariables = new Dictionary<string, string>
            {
                ["AZURE_DEVOPS_EXT_PAT"] = "AZURE_DEVOPS_PAT",
            },
        };
        var source = new ConfiguredProfileSource(
            OptionsMonitor(new Dictionary<string, RepositoryProfileOptions>()),
            OptionsMonitor(new EnvironmentProviderOptions { Provider = "LocalWorkspace" }),
            OptionsMonitor(runtime),
            OptionsMonitor(new WorkSourceOptions()),
            OptionsMonitor(new AzureDevOpsBoardsOptions())
        );

        var profile = source.GetRuntimeEnvironment(
            new RepositoryProfile { Key = "widget", RuntimeProfile = "default" }
        );

        Assert.Equal("default", profile.Key);
        Assert.Equal("PiMateria", profile.RuntimeProvider);
        // Loadouts are projected so the configured profile carries the operator's
        // per-profile loadout map (user-level control retained).
        Assert.Equal(
            "Controller-NewWork",
            profile.RuntimeSettings.Loadouts[ExecutionKind.NewWork]
        );
        Assert.Equal(
            "Controller-Rework",
            profile.RuntimeSettings.Loadouts[ExecutionKind.Rework]
        );
        // Controller-owned process settings are not projected into the per-profile override;
        // they are consumed directly from RuntimeOptions by the runtime.
        Assert.Null(profile.RuntimeSettings.PiExecutablePath);
        Assert.Null(profile.RuntimeSettings.ControllerBaseUrl);
        Assert.Null(profile.RuntimeSettings.PtyWrapperPath);
        Assert.Null(profile.RuntimeSettings.PtyWrapperArgs);
        Assert.Empty(profile.RuntimeSettings.ForwardEnvironmentVariables);
    }

    private static StaticMonitor<T> OptionsMonitor<T>(T value)
        where T : class => new StaticMonitor<T>(value);

    private sealed class StaticMonitor<TOptions>(TOptions currentValue) : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        public TOptions CurrentValue => currentValue;

        public TOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
