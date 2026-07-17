using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>Maps legacy appsettings sections into the domain profile fallback shape.</summary>
internal sealed class ConfiguredProfileSource : IConfiguredProfileSource
{
    private readonly IOptionsMonitor<Dictionary<string, RepositoryProfileOptions>> _repositories;
    private readonly IOptionsMonitor<EnvironmentProviderOptions> _environment;
    private readonly IOptionsMonitor<RuntimeOptions> _runtime;
    private readonly IOptionsMonitor<WorkSourceOptions> _workSource;

    public ConfiguredProfileSource(
        IOptionsMonitor<Dictionary<string, RepositoryProfileOptions>> repositories,
        IOptionsMonitor<EnvironmentProviderOptions> environment,
        IOptionsMonitor<RuntimeOptions> runtime,
        IOptionsMonitor<WorkSourceOptions> workSource
    )
    {
        _repositories = repositories;
        _environment = environment;
        _runtime = runtime;
        _workSource = workSource;
    }

    public RepositoryProfile? GetRepository(string key)
    {
        var configured = _repositories.CurrentValue.FirstOrDefault(entry =>
            entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
        );

        if (configured.Value is null)
        {
            return null;
        }

        return new RepositoryProfile
        {
            Key = configured.Key.Trim().ToLowerInvariant(),
            CloneUrl = configured.Value.CloneUrl,
            DefaultBranch = configured.Value.DefaultBranch,
            Transport = configured.Value.Transport,
            EnvironmentProfile = configured.Value.EnvironmentProfile,
            RuntimeProfile = configured.Value.RuntimeProfile,
            AllowedPaths = configured.Value.AllowedPaths,
        };
    }

    public RuntimeEnvironmentProfile GetRuntimeEnvironment(RepositoryProfile repository)
    {
        var runtime = _runtime.CurrentValue;
        var environment = _environment.CurrentValue;

        return new RuntimeEnvironmentProfile
        {
            Key = string.IsNullOrWhiteSpace(repository.RuntimeProfile)
                ? "default"
                : repository.RuntimeProfile,
            DisplayName = "Configured runtime environment",
            Enabled = true,
            EnvironmentProvider = environment.Provider,
            EnvironmentSettings = new EnvironmentProviderSettings
            {
                // A null profile override preserves agentController:runRoot behavior.
                WorkspaceRoot = null,
            },
            RuntimeProvider = runtime.Provider,
            // Loadouts are projected so the configured profile carries the operator's
            // per-profile loadout map. Pi Materia process behavior (executable, controller
            // URL, PTY, environment-variable forwarding) is consumed directly from
            // RuntimeOptions by the runtime and is not projected into a per-environment override.
            RuntimeSettings = new RuntimeProviderSettings
            {
                Loadouts = new Dictionary<ExecutionKind, string>(runtime.Loadouts),
            },
        };
    }

    public WorkSourceEnvironmentProfile? GetWorkSourceEnvironment()
    {
        var workSource = _workSource.CurrentValue;

        if (string.IsNullOrWhiteSpace(workSource.Project))
        {
            return null;
        }

        return new WorkSourceEnvironmentProfile
        {
            Key = "appsettings",
            DisplayName = "Configured work source environment",
            Enabled = true,
            Provider = "AzureDevOpsBoards",
            TagPrefix = workSource.TagPrefix,
            ConnectionKey = string.Empty,
            Project = workSource.Project ?? string.Empty,
            ActiveState = workSource.ActiveState,
            CompletedState = workSource.CompletedState,
        };
    }
}
