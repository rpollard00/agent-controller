using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Azure DevOps Boards implementation of <see cref="IWorkSource"/>.
/// Discovers eligible work items, claims them for exclusive execution,
/// and projects controller status back to Azure DevOps Boards.
///
/// Registered as a singleton via
/// <see cref="AgentControllerServiceCollectionExtensions.AddAgentControllerAzureDevOpsBoardsWorkSource"/>.
/// Because the underlying <see cref="IAzureDevOpsBoardsClient"/> is scoped,
/// each method creates its own <see cref="IServiceScope"/> to resolve a fresh
/// client instance per operation.
/// </summary>
internal sealed class AzureDevOpsBoardsWorkSource : IWorkSource
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<WorkSourceOptions> _options;

    public AzureDevOpsBoardsWorkSource(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<WorkSourceOptions> options)
    {
        _scopeFactory = scopeFactory;
        _options = options;
    }

    public async Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
        WorkQuery query,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        // Merge configured filters into the query, letting caller overrides win
        // where explicitly provided.
        var parameters = new BoardsQueryParameters
        {
            Project = query.Project ?? options.Project ?? string.Empty,
            States = query.States is { Count: > 0 }
                ? query.States
                : (options.EligibleStates is { Count: > 0 }
                    ? options.EligibleStates
                    : null),
            Tags = query.Tags is { Count: > 0 }
                ? query.Tags
                : (options.EligibleTags is { Count: > 0 }
                    ? options.EligibleTags
                    : null),
            ExcludedTags = query.ExcludedTags is { Count: > 0 }
                ? query.ExcludedTags
                : (options.ExcludedTags is { Count: > 0 }
                    ? options.ExcludedTags
                    : null),
            MaxResults = query.MaxResults,
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClient>();

        return await client.QueryWorkItemsAsync(parameters, cancellationToken);
    }

    public async Task<ClaimResult> TryClaimAsync(
        WorkCandidate candidate,
        ClaimRequest claim,
        CancellationToken cancellationToken)
    {
        // Validate required Azure DevOps configuration
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.Project))
        {
            return new ClaimResult
            {
                Success = false,
                FailureReason = "Azure DevOps project is not configured in workSource:project.",
            };
        }

        if (string.IsNullOrWhiteSpace(options.OrganizationUrl))
        {
            return new ClaimResult
            {
                Success = false,
                FailureReason = "Azure DevOps organization URL is not configured in workSource:organizationUrl.",
            };
        }

        var revision = candidate.SourceMetadata?.TryGetValue("revision", out var rev) == true
            ? rev
            : null;

        var workRef = new ExternalWorkRef
        {
            Source = candidate.Source,
            ExternalId = candidate.ExternalId,
            Url = candidate.ExternalUrl,
            Revision = revision,
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClient>();

        return await client.TryClaimWorkItemAsync(workRef, claim, cancellationToken);
    }

    public async Task UpdateStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.Project))
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClient>();

        await client.UpdateWorkItemStatusAsync(workRef, status, cancellationToken);
    }

    public async Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.Project))
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClient>();

        await client.AddCommentAsync(workRef, comment, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        ExternalWorkRef workRef,
        int maxComments,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClient>();

        return await client.GetCommentsAsync(workRef, maxComments, cancellationToken);
    }
}
