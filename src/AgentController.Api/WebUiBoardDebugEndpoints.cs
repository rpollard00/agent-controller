using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Queries;
using AgentController.Domain;

namespace AgentController.Api;

/// <summary>Read-only board-item diagnostics used by the operator UI.</summary>
internal static class WebUiBoardDebugEndpoints
{
    private const string Path = "/api/webui/debug/board-items";

    public static IEndpointRouteBuilder MapWebUiBoardDebugEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Path);

        group.MapGet("", ListAsync);
        group.MapGet("/{workSourceEnvironmentKey}/{itemId}", GetAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        string? workSourceEnvironmentKey,
        bool? includeTerminal,
        int? page,
        int? pageSize,
        IQueryHandler<ListWorkSourceEnvironmentsQuery, IReadOnlyList<WorkSourceEnvironmentProfile>> environmentHandler,
        IQueryHandler<ListBoardItemDiagnosticsQuery, BoardItemDiagnosticsPage> diagnosticsHandler,
        CancellationToken cancellationToken)
    {
        var environments = await environmentHandler.ExecuteAsync(
            new ListWorkSourceEnvironmentsQuery(),
            cancellationToken);
        var validation = ValidateListRequest(
            environments,
            workSourceEnvironmentKey,
            page,
            pageSize);
        if (validation.Errors.Count > 0)
        {
            return ValidationProblem(validation.Errors);
        }

        var result = await diagnosticsHandler.ExecuteAsync(
            new ListBoardItemDiagnosticsQuery
            {
                WorkSourceEnvironmentKey = validation.EnvironmentKey,
                IncludeTerminal = includeTerminal ?? false,
                Page = page ?? 1,
                PageSize = pageSize ?? ManagedBoardItemDiscoveryQuery.DefaultPageSize,
            },
            cancellationToken);

        return Results.Ok(new BoardItemsDebugPageResponse
        {
            SourceOptions = ToOptions(environments),
            Items = result.Items,
            Failures = result.Failures,
            Page = result.Page,
            PageSize = result.PageSize,
            Total = result.Total,
            ObservedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task<IResult> GetAsync(
        string workSourceEnvironmentKey,
        string itemId,
        IQueryHandler<ListWorkSourceEnvironmentsQuery, IReadOnlyList<WorkSourceEnvironmentProfile>> environmentHandler,
        IQueryHandler<GetBoardItemDiagnosticsQuery, BoardItemDiagnosticDetail?> diagnosticsHandler,
        CancellationToken cancellationToken)
    {
        var environments = await environmentHandler.ExecuteAsync(
            new ListWorkSourceEnvironmentsQuery(),
            cancellationToken);
        var environment = FindEnvironment(environments, workSourceEnvironmentKey);
        if (environment is null)
        {
            return ValidationProblem(new Dictionary<string, string[]>
            {
                ["workSourceEnvironmentKey"] = ["Select a configured work source environment."],
            });
        }

        if (string.IsNullOrWhiteSpace(itemId))
        {
            return ValidationProblem(new Dictionary<string, string[]>
            {
                ["itemId"] = ["A work item ID is required."],
            });
        }

        var result = await diagnosticsHandler.ExecuteAsync(
            new GetBoardItemDiagnosticsQuery
            {
                WorkSourceEnvironmentKey = environment.Key,
                ItemId = itemId,
            },
            cancellationToken);

        return result is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Board item not found.",
                detail: "The board item is not visible in the selected work source environment.")
            : Results.Ok(result);
    }

    private static ValidatedListRequest ValidateListRequest(
        IReadOnlyList<WorkSourceEnvironmentProfile> environments,
        string? environmentKey,
        int? page,
        int? pageSize)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? canonicalEnvironmentKey = null;

        if (environmentKey is not null)
        {
            var environment = FindEnvironment(environments, environmentKey);
            if (environment is null)
            {
                errors["workSourceEnvironmentKey"] =
                    ["Select a configured work source environment."];
            }
            else
            {
                canonicalEnvironmentKey = environment.Key;
            }
        }

        if (page is <= 0)
        {
            errors["page"] = ["Page must be at least 1."];
        }

        if (pageSize is <= 0 or > ManagedBoardItemDiscoveryQuery.MaximumPageSize)
        {
            errors["pageSize"] =
                [$"Page size must be between 1 and {ManagedBoardItemDiscoveryQuery.MaximumPageSize}."];
        }

        return new ValidatedListRequest(canonicalEnvironmentKey, errors);
    }

    private static WorkSourceEnvironmentProfile? FindEnvironment(
        IEnumerable<WorkSourceEnvironmentProfile> environments,
        string key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : environments.FirstOrDefault(environment =>
                string.Equals(environment.Key, key, StringComparison.OrdinalIgnoreCase));

    private static BoardDebugSourceOption[] ToOptions(
        IEnumerable<WorkSourceEnvironmentProfile> environments) =>
        environments
            .Select(environment => new BoardDebugSourceOption
            {
                Key = environment.Key,
                DisplayName = environment.DisplayName,
            })
            .ToArray();

    private static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            statusCode: StatusCodes.Status400BadRequest,
            title: "Validation failed.");

    private sealed record ValidatedListRequest(
        string? EnvironmentKey,
        IReadOnlyDictionary<string, string[]> Errors);
}

/// <summary>A configured work source available to the debug selector.</summary>
public sealed record BoardDebugSourceOption
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>Operator-safe response for one page of board-item diagnostics.</summary>
public sealed record BoardItemsDebugPageResponse
{
    public IReadOnlyList<BoardDebugSourceOption> SourceOptions { get; init; } = [];
    public IReadOnlyList<BoardItemDiagnosticSummary> Items { get; init; } = [];
    public IReadOnlyList<ManagedBoardItemDiscoveryFailure> Failures { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
}
