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
    private readonly IOptionsMonitor<AzureDevOpsBoardsOptions> _azureDevOps;

    public ConfiguredProfileSource(
        IOptionsMonitor<Dictionary<string, RepositoryProfileOptions>> repositories,
        IOptionsMonitor<EnvironmentProviderOptions> environment,
        IOptionsMonitor<RuntimeOptions> runtime,
        IOptionsMonitor<WorkSourceOptions> workSource,
        IOptionsMonitor<AzureDevOpsBoardsOptions> azureDevOps
    )
    {
        _repositories = repositories;
        _environment = environment;
        _runtime = runtime;
        _workSource = workSource;
        _azureDevOps = azureDevOps;
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
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = runtime.PiExecutablePath,
                ControllerBaseUrl = runtime.ControllerBaseUrl,
                PtyWrapperPath = runtime.PtyWrapperPath,
                PtyWrapperArgs = runtime.PtyWrapperArgs,
                Loadouts = new Dictionary<ExecutionKind, string>(runtime.Loadouts),
                ForwardEnvironmentVariables = new Dictionary<string, string>(
                    runtime.ForwardEnvironmentVariables
                ),
            },
        };
    }

    public AzureDevOpsEnvironmentProfile? GetAzureDevOpsEnvironment()
    {
        var workSource = _workSource.CurrentValue;
        var azureDevOps = _azureDevOps.CurrentValue;

        if (
            string.IsNullOrWhiteSpace(workSource.OrganizationUrl)
            && string.IsNullOrWhiteSpace(workSource.Project)
        )
        {
            return null;
        }

        return new AzureDevOpsEnvironmentProfile
        {
            Key = "appsettings",
            DisplayName = "Configured Azure DevOps environment",
            Enabled = true,
            OrganizationUrl = workSource.OrganizationUrl ?? string.Empty,
            Project = workSource.Project ?? string.Empty,
            WorkItemType = workSource.WorkItemType,
            EligibleTags = workSource.EligibleTags,
            ExcludedTags = workSource.ExcludedTags,
            EligibleStates = workSource.EligibleStates,
            ActiveState = workSource.ActiveState,
            CompletedState = workSource.CompletedState,
            PatEnvironmentVariable = GetEnvironmentVariableReference(
                azureDevOps.PersonalAccessToken
            ),
        };
    }

    private static string GetEnvironmentVariableReference(string configuredValue)
    {
        const string prefix = "ENV:";
        return configuredValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? configuredValue[prefix.Length..].Trim()
            : string.Empty;
    }
}
