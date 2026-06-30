using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Validates configured ADO board states against the actual valid states
/// for the configured project and work item type at host startup.
/// 
/// Queries ADO to enumerate the valid System.State values, then throws
/// during startup if ActiveState, CompletedState, or any EligibleStates
/// value is not a valid state for that WIT/project.
/// 
/// This validation only runs when the work source provider is "AzureDevOpsBoards".
/// It is skipped when the <c>AGENT_CONTROLLER_SKIP_ADO_STATE_VALIDATION</c>
/// environment variable is set (useful for CI/test environments without ADO access).
/// </summary>
internal sealed partial class AzureDevOpsBoardStateStartupValidator : IHostedService
{
    private readonly IOptions<WorkSourceOptions> _workSourceOptions;
    private readonly IOptions<AzureDevOpsBoardsOptions> _boardsOptions;
    private readonly ILogger<AzureDevOpsBoardStateStartupValidator> _logger;

    public AzureDevOpsBoardStateStartupValidator(
        IOptions<WorkSourceOptions> workSourceOptions,
        IOptions<AzureDevOpsBoardsOptions> boardsOptions,
        ILogger<AzureDevOpsBoardStateStartupValidator> logger)
    {
        _workSourceOptions = workSourceOptions;
        _boardsOptions = boardsOptions;
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

        // Resolve PAT
        string? pat;
        try
        {
            pat = boards.ResolvePersonalAccessToken();
        }
        catch (InvalidOperationException ex)
        {
            // PAT resolution failure is already caught by AzureDevOpsBoardsValidator.
            // Skip state validation if we can't authenticate.
            SkipValidationPatFailed(_logger, ex);
            return;
        }

        if (string.IsNullOrWhiteSpace(pat))
        {
            SkipValidationNoPat(_logger);
            return;
        }

        var orgUrl = workSource.OrganizationUrl;
        var project = workSource.Project;

        if (string.IsNullOrWhiteSpace(orgUrl) || string.IsNullOrWhiteSpace(project))
        {
            SkipValidationMissingConfig(_logger);
            return;
        }

        var workItemType = string.IsNullOrWhiteSpace(workSource.WorkItemType)
            ? WorkSourceOptions.DefaultWorkItemType
            : workSource.WorkItemType;

        ValidateStarting(_logger, project!, workItemType);

        // Query ADO for valid states
        var validStates = await FetchValidStatesAsync(orgUrl!, project!, workItemType, pat, cancellationToken);

        if (validStates.Count == 0)
        {
            WarnNoValidStates(_logger, project!, workItemType);
            return;
        }

#pragma warning disable CA1873 // LoggerMessage source-gen has its own IsEnabled guard
        ValidStatesEnumerated(_logger, project!, workItemType, string.Join(", ", validStates));
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

        if (workSource.EligibleStates is { Count: > 0 })
        {
            foreach (var state in workSource.EligibleStates)
            {
                if (!string.IsNullOrWhiteSpace(state) && !validStatesSet.Contains(state))
                {
                    failures.Add(
                        $"EligibleStates value '{state}' is not a valid System.State value. " +
                        $"Valid states: [{string.Join(", ", validStates)}].");
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Azure DevOps board state configuration is invalid for project '{project}', " +
                $"work item type '{workItemType}':\n" +
                string.Join("\n", failures.Select((f, i) => $"  {i + 1}. {f}")));
        }

#pragma warning disable CA1873 // LoggerMessage source-gen has its own IsEnabled guard
        ValidationPassed(_logger,
            workSource.ActiveState ?? "(not set)",
            workSource.CompletedState ?? "(not set)",
            string.Join(", ", workSource.EligibleStates));
#pragma warning restore CA1873
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Nothing to clean up on stop.
        return Task.CompletedTask;
    }

    // ─── Fetch valid states from ADO ─────────────────────────────

    /// <summary>
    /// Fetches valid System.State values from ADO by querying the Process API.
    /// </summary>
    private static async Task<IReadOnlyList<string>> FetchValidStatesAsync(
        [NotNull] string organizationUrl,
        [NotNull] string project,
        string workItemType,
        string personalAccessToken,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        http.BaseAddress = new Uri(organizationUrl.TrimEnd('/') + "/");

        var authBytes = Encoding.ASCII.GetBytes($":{personalAccessToken}");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        try
        {
            // (1) Get project details to find the process ID.
            var projectResponse = await http.GetAsync(
                $"_apis/projects/{Uri.EscapeDataString(project)}?api-version=7.1&includeProcessSettings=true",
                cancellationToken);

            if (!projectResponse.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var projectJson = await projectResponse.Content.ReadAsStringAsync(cancellationToken);
            using var projectDoc = JsonDocument.Parse(projectJson);

            var processId = ExtractProcessId(projectDoc.RootElement);

            if (string.IsNullOrWhiteSpace(processId))
            {
                // Fallback: enumerate processes and find the one for this project.
                processId = await FindProcessIdForProjectAsync(http, project, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(processId))
            {
                return Array.Empty<string>();
            }

            // (2) Get the work item type definition with System.State field.
            var witResponse = await http.GetAsync(
                $"_apis/work/processes/{Uri.EscapeDataString(processId)}/workItemTypes/{Uri.EscapeDataString(workItemType)}?api-version=7.1-preview.3&fields=System.State",
                cancellationToken);

            if (!witResponse.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var witJson = await witResponse.Content.ReadAsStringAsync(cancellationToken);
            using var witDoc = JsonDocument.Parse(witJson);

            return ExtractAllowedStates(witDoc.RootElement);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<string>();
        }
        catch (HttpRequestException)
        {
            return Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Extracts the process ID from a project API response.
    /// Tries processSettings.processId first, then top-level processId.
    /// </summary>
    private static string? ExtractProcessId(JsonElement projectElement)
    {
        // Try processSettings.processId (most common location).
        if (projectElement.TryGetProperty("processSettings", out var processSettings)
            && processSettings.TryGetProperty("processId", out var processIdEl)
            && processIdEl.ValueKind == JsonValueKind.String)
        {
            return processIdEl.GetString();
        }

        // Fallback: top-level processId.
        if (projectElement.TryGetProperty("processId", out var topProcessIdEl)
            && topProcessIdEl.ValueKind == JsonValueKind.String)
        {
            return topProcessIdEl.GetString();
        }

        return null;
    }

    /// <summary>
    /// Enumerates processes and finds the one associated with the given project.
    /// </summary>
    private static async Task<string?> FindProcessIdForProjectAsync(
        HttpClient http,
        string project,
        CancellationToken cancellationToken)
    {
        var processesResponse = await http.GetAsync(
            "_apis/work/processes?api-version=7.1-preview.3",
            cancellationToken);

        if (!processesResponse.IsSuccessStatusCode)
        {
            return null;
        }

        var processesJson = await processesResponse.Content.ReadAsStringAsync(cancellationToken);
        using var processesDoc = JsonDocument.Parse(processesJson);

        if (processesDoc.RootElement.TryGetProperty("value", out var processesArray)
            && processesArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var process in processesArray.EnumerateArray())
            {
                if (!process.TryGetProperty("id", out var pidEl)
                    || pidEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var pid = pidEl.GetString();

                // Check if this process has the project in its default template pools.
                if (process.TryGetProperty("defaultTemplate", out var templateEl)
                    && templateEl.ValueKind == JsonValueKind.Object
                    && templateEl.TryGetProperty("projectPools", out var poolsEl)
                    && poolsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pool in poolsEl.EnumerateArray())
                    {
                        if (pool.TryGetProperty("name", out var poolNameEl)
                            && poolNameEl.ValueKind == JsonValueKind.String
                            && poolNameEl.GetString()!.Equals(project, StringComparison.OrdinalIgnoreCase))
                        {
                            return pid;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts allowed values for System.State from a work item type definition response.
    /// </summary>
    private static List<string> ExtractAllowedStates(JsonElement witElement)
    {
        var allowedValues = new List<string>();

        if (witElement.TryGetProperty("fields", out var fields)
            && fields.ValueKind == JsonValueKind.Object
            && fields.TryGetProperty("System.State", out var stateField)
            && stateField.ValueKind == JsonValueKind.Object
            && stateField.TryGetProperty("name", out var nameField)
            && nameField.ValueKind == JsonValueKind.Object
            && nameField.TryGetProperty("allowedValues", out var allowedValuesEl)
            && allowedValuesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in allowedValuesEl.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    var state = value.GetString();
                    if (!string.IsNullOrWhiteSpace(state))
                    {
                        allowedValues.Add(state);
                    }
                }
            }
        }

        return allowedValues;
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Validating configured ADO board states for project='{Project}', workItemType='{WorkItemType}'.")]
    private static partial void ValidateStarting(
        ILogger logger, string project, string workItemType);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Could not enumerate valid ADO states for project='{Project}', workItemType='{WorkItemType}'. " +
                  "Skipping board state validation. This may indicate a connectivity or permission issue.")]
    private static partial void WarnNoValidStates(
        ILogger logger, string project, string workItemType);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Valid ADO states for '{Project}/{WorkItemType}': [{States}].")]
    private static partial void ValidStatesEnumerated(
        ILogger logger, string project, string workItemType, string states);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "ADO board state validation passed: ActiveState='{ActiveState}', " +
                  "CompletedState='{CompletedState}', EligibleStates=[{EligibleStates}].")]
    private static partial void ValidationPassed(
        ILogger logger, string activeState, string completedState, string eligibleStates);
}
