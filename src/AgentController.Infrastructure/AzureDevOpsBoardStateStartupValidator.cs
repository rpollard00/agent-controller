using System.Linq;
using AgentController.Application;
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
        var boards = _boardsOptions.Value;

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

        // Resolve PAT — skip if not available (PAT validation is handled elsewhere).
        string? pat;
        try
        {
            pat = boards.ResolvePersonalAccessToken();
        }
        catch (InvalidOperationException ex)
        {
            SkipValidationPatFailed(_logger, ex);
            return;
        }

        if (string.IsNullOrWhiteSpace(pat))
        {
            SkipValidationNoPat(_logger);
            return;
        }

        var project = workSource.Project;

        if (string.IsNullOrWhiteSpace(workSource.OrganizationUrl) || string.IsNullOrWhiteSpace(project))
        {
            SkipValidationMissingConfig(_logger);
            return;
        }

        // Query ADO for valid states via the scoped IAzureDevOpsBoardsClient.
        // The client is registered with the correct base URL and PAT from options.
        // GetValidStatesAsync returns states grouped by work item type;
        // flatten to a union of all bare state names for validation.
        // On connectivity failure the client throws — skip validation with a warning.
        IReadOnlyDictionary<string, IReadOnlyList<string>> groupedStates;
        try
        {
            groupedStates = await FetchValidStatesGroupedAsync(project!, cancellationToken);
        }
        catch (Exception ex)
        {
            // Any failure (HTTP error, JSON parse, network, cancellation) skips validation.
            WarnStateQueryFailed(_logger, project!, ex.Message);
            return;
        }

        // Flatten grouped map: union all state-name lists across types.
        var validStates = groupedStates
            .SelectMany(kvp => kvp.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validStates.Count == 0)
        {
            WarnNoValidStates(_logger, project!);
            return;
        }

#pragma warning disable CA1873 // LoggerMessage source-gen has its own IsEnabled guard
        ValidStatesEnumerated(_logger, project!, string.Join(", ", validStates));
#pragma warning restore CA1873

        // Validate configured states
        var failures = new List<string>();
        var validStatesSet = new HashSet<string>(validStates, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(workSource.ActiveState) &&
            !validStatesSet.Contains(workSource.ActiveState))
        {
            failures.Add(
                $"ActiveState '{workSource.ActiveState}' is not a valid System.State value. " +
                $"Valid states: [{string.Join(", ", validStates)}].");
        }

        if (!string.IsNullOrWhiteSpace(workSource.CompletedState) &&
            !validStatesSet.Contains(workSource.CompletedState))
        {
            failures.Add(
                $"CompletedState '{workSource.CompletedState}' is not a valid System.State value. " +
                $"Valid states: [{string.Join(", ", validStates)}].");
        }

        // ActiveState and CompletedState must be distinct when both are configured.
        if (!string.IsNullOrWhiteSpace(workSource.ActiveState)
            && !string.IsNullOrWhiteSpace(workSource.CompletedState)
            && workSource.ActiveState.Equals(workSource.CompletedState, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                $"ActiveState and CompletedState must be distinct, but both are set to '{workSource.ActiveState}'.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Azure DevOps board state configuration is invalid for project '{project}':\n" +
                string.Join("\n", failures.Select((f, i) => $"  {i + 1}. {f}")));
        }

#pragma warning disable CA1873 // LoggerMessage source-gen has its own IsEnabled guard
        ValidationPassed(_logger,
            workSource.ActiveState ?? "(not set)",
            workSource.CompletedState ?? "(not set)");
#pragma warning restore CA1873
    }

    /// <summary>
    /// Fetches valid states grouped by work item type by resolving the scoped
    /// <see cref="IAzureDevOpsBoardsClient"/> through <see cref="IServiceScopeFactory"/>.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> FetchValidStatesGroupedAsync(
        string project,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClient>();
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
        Message = "Skipping ADO board state validation: organizationUrl or project is not configured.")]
    private static partial void SkipValidationMissingConfig(ILogger logger);

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
}
