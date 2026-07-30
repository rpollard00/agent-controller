using AgentController.Application;
using AgentController.Application.Abstractions;
using AgentController.Application.Queries;

namespace AgentController.Api;

/// <summary>Read-only pull-request diagnostics used by the operator UI.</summary>
internal static class WebUiPullRequestDebugEndpoints
{
    private const string Path = "/api/webui/debug/pull-requests";

    public static IEndpointRouteBuilder MapWebUiPullRequestDebugEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Path);
        group.MapGet("", ListAsync);
        group.MapGet("/{sourceControlEnvironmentKey}/{repositoryKey}/{pullRequestId}", GetAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        string? sourceControlEnvironmentKey,
        bool? includeInactive,
        int? page,
        int? pageSize,
        PullRequestSourceOptionsProvider sourceOptionsProvider,
        IQueryHandler<ListPullRequestDiagnosticsQuery, PullRequestDiagnosticsPage> diagnosticsHandler,
        CancellationToken cancellationToken)
    {
        var sources = await sourceOptionsProvider.ListAsync(cancellationToken);
        var validation = ValidateListRequest(sources, sourceControlEnvironmentKey, page, pageSize);
        if (validation.Errors.Count > 0)
        {
            return ValidationProblem(validation.Errors);
        }

        var result = await diagnosticsHandler.ExecuteAsync(new ListPullRequestDiagnosticsQuery
        {
            SourceControlEnvironmentKey = validation.EnvironmentKey,
            IncludeInactive = includeInactive ?? false,
            Page = page ?? 1,
            PageSize = pageSize ?? ManagedPullRequestDiscoveryQuery.DefaultPageSize,
        }, cancellationToken);

        return Results.Ok(new PullRequestsDebugPageResponse
        {
            SourceOptions = sources,
            Items = result.Items,
            Failures = result.Failures,
            Page = result.Page,
            PageSize = result.PageSize,
            Total = result.Total,
            ObservedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task<IResult> GetAsync(
        string sourceControlEnvironmentKey,
        string repositoryKey,
        string pullRequestId,
        PullRequestSourceOptionsProvider sourceOptionsProvider,
        IQueryHandler<GetPullRequestDiagnosticsQuery, PullRequestDiagnosticDetail?> diagnosticsHandler,
        CancellationToken cancellationToken)
    {
        sourceControlEnvironmentKey = Uri.UnescapeDataString(sourceControlEnvironmentKey);
        repositoryKey = Uri.UnescapeDataString(repositoryKey);
        pullRequestId = Uri.UnescapeDataString(pullRequestId);

        var sources = await sourceOptionsProvider.ListAsync(cancellationToken);
        var source = FindSource(sources, sourceControlEnvironmentKey);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (source is null)
        {
            errors["sourceControlEnvironmentKey"] = ["Select a configured source control environment."];
        }
        if (string.IsNullOrWhiteSpace(repositoryKey))
        {
            errors["repositoryKey"] = ["A repository is required."];
        }
        if (string.IsNullOrWhiteSpace(pullRequestId))
        {
            errors["pullRequestId"] = ["A pull request ID is required."];
        }
        if (errors.Count > 0)
        {
            return ValidationProblem(errors);
        }

        var result = await diagnosticsHandler.ExecuteAsync(new GetPullRequestDiagnosticsQuery
        {
            SourceControlEnvironmentKey = source!.Key,
            RepositoryKey = repositoryKey,
            PullRequestId = pullRequestId,
        }, cancellationToken);

        return result is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Pull request not found.",
                detail: "The pull request is not visible in the selected source control environment and repository.")
            : Results.Ok(result);
    }

    private static ValidatedListRequest ValidateListRequest(
        IReadOnlyList<PullRequestSourceOption> sources,
        string? environmentKey,
        int? page,
        int? pageSize)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? canonicalEnvironmentKey = null;
        if (environmentKey is not null)
        {
            var source = FindSource(sources, environmentKey);
            if (source is null)
            {
                errors["sourceControlEnvironmentKey"] = ["Select a configured source control environment."];
            }
            else
            {
                canonicalEnvironmentKey = source.Key;
            }
        }
        if (page is <= 0)
        {
            errors["page"] = ["Page must be at least 1."];
        }
        if (pageSize is <= 0 or > ManagedPullRequestDiscoveryQuery.MaximumPageSize)
        {
            errors["pageSize"] = [$"Page size must be between 1 and {ManagedPullRequestDiscoveryQuery.MaximumPageSize}."];
        }
        return new ValidatedListRequest(canonicalEnvironmentKey, errors);
    }

    private static PullRequestSourceOption? FindSource(
        IEnumerable<PullRequestSourceOption> sources,
        string key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : sources.FirstOrDefault(source => source.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            statusCode: StatusCodes.Status400BadRequest,
            title: "Validation failed.");

    private sealed record ValidatedListRequest(
        string? EnvironmentKey,
        IReadOnlyDictionary<string, string[]> Errors);
}

/// <summary>Operator-safe response for one page of pull-request diagnostics.</summary>
public sealed record PullRequestsDebugPageResponse
{
    public IReadOnlyList<PullRequestSourceOption> SourceOptions { get; init; } = [];
    public IReadOnlyList<PullRequestDiagnosticSummary> Items { get; init; } = [];
    public IReadOnlyList<ManagedPullRequestDiscoveryFailure> Failures { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
}
