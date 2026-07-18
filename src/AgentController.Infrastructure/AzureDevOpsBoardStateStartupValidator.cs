using System.Linq;
using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Validates configured work source board states against the actual valid states
/// for the configured project at host startup.
///
/// For managed profiles: derives OrganizationUrl from the resolved
/// <see cref="ConnectionProfile"/> and Project from the consumer
/// <see cref="WorkSourceEnvironmentProfile"/>.
/// For configured fallback: reads OrganizationUrl and Project from
/// <see cref="WorkSourceOptions"/> (appsettings).
///
/// Uses <see cref="IAzureDevOpsBoardsClient.GetValidStatesAsync"/> to enumerate
/// the valid System.State values, then throws during startup if ActiveState
/// or CompletedState is not a valid state.
/// Also validates that ActiveState and CompletedState are distinct when both are set.
///
/// This validation only runs when the work source provider is "AzureDevOpsBoards".
/// It is skipped when the <c>AGENT_CONTROLLER_SKIP_ADO_STATE_VALIDATION</c>
/// environment variable is set (useful for CI/test environments without ADO access).
/// </summary>
internal sealed partial class AzureDevOpsBoardStateStartupValidator : IHostedService
{
    private readonly IOptions<WorkSourceOptions> _workSourceOptions;
    private readonly IOptions<AzureDevOpsBoardsOptions> _boardsOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AzureDevOpsBoardStateStartupValidator> _logger;

    public AzureDevOpsBoardStateStartupValidator(
        IOptions<WorkSourceOptions> workSourceOptions,
        IOptions<AzureDevOpsBoardsOptions> boardsOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<AzureDevOpsBoardStateStartupValidator> logger)
    {
        _workSourceOptions = workSourceOptions;
        _boardsOptions = boardsOptions;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var workSource = _workSourceOptions.Value;

        // Only validate when using AzureDevOpsBoards work source.
        if (!workSource.Provider.Equals("AzureDevOpsBoards", StringComparison.OrdinalIgnoreCase))
        {
            SkipValidationNonAdoProvider(_logger, workSource.Provider);
            return;
        }

        // Allow explicit opt-out for test/CI environments.
        if (!string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("AGENT_CONTROLLER_SKIP_ADO_STATE_VALIDATION")))
        {
            SkipValidationEnvVar(_logger);
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        // Try managed profiles first.
        var resolver = services.GetService<IManagedProfileResolver>();
        if (resolver is not null)
        {
            var environments = await resolver.ListWorkSourceEnvironmentsAsync(cancellationToken);
            var managedEnvironments = environments
                .Where(env => env.IsManaged && env.Connection is not null)
                .ToList();

            if (managedEnvironments.Count > 0)
            {
                var allFailures = new List<string>();

                foreach (var env in managedEnvironments)
                {
                    var profile = env.Profile;
                    var connection = env.Connection!;
                    var envProject = profile.Project;

                    var settings = connection.ProviderSettings as AzureDevOpsConnectionSettings;
                    if (settings is null || string.IsNullOrWhiteSpace(settings.OrganizationUrl))
                    {
                        WarnMissingConnectionConfig(_logger, connection.Key);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(envProject))
                    {
                        WarnMissingProject(_logger, profile.Key);
                        continue;
                    }

                    // Create a client for this connection + project and fetch valid states.
                    IReadOnlyDictionary<string, IReadOnlyList<string>> envGroupedStates;
                    try
                    {
                        envGroupedStates = await FetchValidStatesForEnvironmentAsync(
                            services,
                            env,
                            envProject,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        WarnStateQueryFailed(_logger, envProject, ex.Message);
                        continue;
                    }

                    var envValidStates = envGroupedStates
                        .SelectMany(kvp => kvp.Value)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (envValidStates.Count == 0)
                    {
                        WarnNoValidStates(_logger, envProject);
                        continue;
                    }

#pragma warning disable CA1873
                    ValidStatesEnumerated(_logger, envProject, string.Join(", ", envValidStates));
#pragma warning restore CA1873

                    // Validate this profile's states.
                    var envFailures = ValidateStates(
                        profile.Key,
                        envProject,
                        profile.ActiveState,
                        profile.CompletedState,
                        envValidStates);

                    allFailures.AddRange(envFailures);
                }

                if (allFailures.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Azure DevOps board state configuration is invalid:\n" +
                        string.Join("\n", allFailures.Select((f, i) => $"  {i + 1}. {f}")));
                }

                ValidationPassedManaged(_logger, managedEnvironments.Count);
                return;
            }
        }

        // Fall back to configured (appsettings) path.
        // Resolve the connection to derive OrganizationUrl and PAT.
        var connectionKey = workSource.ConnectionKey;

        if (string.IsNullOrWhiteSpace(connectionKey))
        {
            SkipValidationMissingConnectionKey(_logger);
            return;
        }

        var connectionStore = services.GetRequiredService<IConnectionStore>();
        var fallbackConnection = await connectionStore.GetByKeyAsync(connectionKey, cancellationToken);
        if (fallbackConnection is null || fallbackConnection.ProviderSettings is not AzureDevOpsConnectionSettings adoSettings)
        {
            WarnMissingConnectionConfig(_logger, connectionKey ?? "(null)");
            return;
        }

        var project = workSource.Project;
        if (string.IsNullOrWhiteSpace(project))
        {
            WarnMissingProject(_logger, "(configured)");
            return;
        }

        // Resolve PAT from the connection's secret reference.
        var secretStore = services.GetRequiredService<Domain.Secrets.ISecretStore>();
        string? pat;
        try
        {
            pat = await secretStore.ResolveAsync(
                adoSettings.PersonalAccessTokenReference.Name,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            SkipValidationPatFailed(_logger, ex);
            return;
        }

        if (string.IsNullOrWhiteSpace(pat))
        {
            SkipValidationNoPat(_logger);
            return;
        }

        // Query ADO for valid states using the connection's OrganizationUrl + consumer Project.
        IReadOnlyDictionary<string, IReadOnlyList<string>> groupedStates;
        try
        {
            groupedStates = await FetchValidStatesForConnectionAsync(
                services,
                adoSettings.OrganizationUrl,
                project,
                pat,
                cancellationToken);
        }
        catch (Exception ex)
        {
            WarnStateQueryFailed(_logger, project, ex.Message);
            return;
        }

        var validStates = groupedStates
            .SelectMany(kvp => kvp.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validStates.Count == 0)
        {
            WarnNoValidStates(_logger, project);
            return;
        }

#pragma warning disable CA1873
        ValidStatesEnumerated(_logger, project, string.Join(", ", validStates));
#pragma warning restore CA1873

        var failures = ValidateStates(
            "(configured)",
            project,
            workSource.ActiveState,
            workSource.CompletedState,
            validStates);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Azure DevOps board state configuration is invalid for project '{project}':\n" +
                string.Join("\n", failures.Select((f, i) => $"  {i + 1}. {f}")));
        }

#pragma warning disable CA1873
        ValidationPassed(_logger,
            workSource.ActiveState ?? "(not set)",
            workSource.CompletedState ?? "(not set)");
#pragma warning restore CA1873
    }

    /// <summary>
    /// Validates ActiveState and CompletedState against the set of valid states.
    /// Returns a list of failure messages (empty if all checks pass).
    /// </summary>
    private static List<string> ValidateStates(
        string profileLabel,
        string project,
        string? activeState,
        string? completedState,
        IReadOnlyList<string> validStates)
    {
        var failures = new List<string>();
        var validStatesSet = new HashSet<string>(validStates, StringComparer.OrdinalIgnoreCase);
        var prefix = !profileLabel.Equals("(configured)", StringComparison.Ordinal)
            ? $"[profile '{profileLabel}', project '{project}'] "
            : $"[project '{project}'] ";

        if (!string.IsNullOrWhiteSpace(activeState) &&
            !validStatesSet.Contains(activeState))
        {
            failures.Add(
                $"{prefix}ActiveState '{activeState}' is not a valid System.State value. " +
                $"Valid states: [{string.Join(", ", validStates)}].");
        }

        if (!string.IsNullOrWhiteSpace(completedState) &&
            !validStatesSet.Contains(completedState))
        {
            failures.Add(
                $"{prefix}CompletedState '{completedState}' is not a valid System.State value. " +
                $"Valid states: [{string.Join(", ", validStates)}].");
        }

        if (!string.IsNullOrWhiteSpace(activeState)
            && !string.IsNullOrWhiteSpace(completedState)
            && activeState.Equals(completedState, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                $"{prefix}ActiveState and CompletedState must be distinct, " +
                $"but both are set to '{activeState}'.");
        }

        return failures;
    }

    /// <summary>
    /// Fetches valid states for a managed work-source environment by creating
    /// a client from the resolved connection and consumer project.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FetchValidStatesForEnvironmentAsync(
        IServiceProvider services,
        ResolvedWorkSourceEnvironment environment,
        string project,
        CancellationToken cancellationToken)
    {
        var factory = services.GetRequiredService<IAzureDevOpsBoardsClientFactory>();
        var client = await factory.CreateAsync(environment, cancellationToken);
        using var disposable = client as IDisposable;
        return await client.GetValidStatesAsync(project, cancellationToken);
    }

    /// <summary>
    /// Fetches valid states for the configured (appsettings) fallback path
    /// by creating a client from the resolved connection's OrganizationUrl + PAT.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FetchValidStatesForConnectionAsync(
        IServiceProvider services,
        string organizationUrl,
        string project,
        string pat,
        CancellationToken cancellationToken)
    {
        var clientFactory = services.GetRequiredService<AzureDevOpsClientFactory>();
        var logger = services.GetRequiredService<ILogger<AzureDevOpsBoardStateStartupValidator>>();
        var client = clientFactory.Create(organizationUrl, project, pat);
        using var disposable = client as IDisposable;
        return await client.GetValidStatesAsync(project, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Nothing to clean up on stop.
        return Task.CompletedTask;
    }

    // ─── LoggerMessage definitions ───────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Skipping ADO board state validation: work source provider is '{Provider}'.")]
    private static partial void SkipValidationNonAdoProvider(
        ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Skipping ADO board state validation: AGENT_CONTROLLER_SKIP_ADO_STATE_VALIDATION is set.")]
    private static partial void SkipValidationEnvVar(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipping ADO board state validation: PAT resolution failed.")]
    private static partial void SkipValidationPatFailed(
        ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipping ADO board state validation: no PAT configured.")]
    private static partial void SkipValidationNoPat(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipping ADO board state validation: connectionKey is not configured.")]
    private static partial void SkipValidationMissingConnectionKey(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "ADO state query failed for project='{Project}': {Error}. " +
                  "Skipping board state validation.")]
    private static partial void WarnStateQueryFailed(
        ILogger logger, string project, string error);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not enumerate valid ADO states for project='{Project}'. " +
                  "Skipping board state validation. This may indicate a connectivity or permission issue.")]
    private static partial void WarnNoValidStates(
        ILogger logger, string project);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Valid ADO states for '{Project}': [{States}].")]
    private static partial void ValidStatesEnumerated(
        ILogger logger, string project, string states);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "ADO board state validation passed: ActiveState='{ActiveState}', " +
                  "CompletedState='{CompletedState}'.")]
    private static partial void ValidationPassed(
        ILogger logger, string activeState, string completedState);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "ADO board state validation passed for {EnvironmentCount} managed environment(s).")]
    private static partial void ValidationPassedManaged(
        ILogger logger, int environmentCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipping ADO state validation for connection '{ConnectionKey}': " +
                  "missing OrganizationUrl or AzureDevOps settings.")]
    private static partial void WarnMissingConnectionConfig(
        ILogger logger, string connectionKey);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipping ADO state validation for profile '{ProfileKey}': " +
                  "project is not configured.")]
    private static partial void WarnMissingProject(
        ILogger logger, string profileKey);
}
