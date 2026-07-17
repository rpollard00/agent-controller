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
        IOptionsMonitor<WorkSourceOptions> options
    )
    {
        _scopeFactory = scopeFactory;
        _options = options;
    }

    public async Task<IReadOnlyList<WorkCandidate>> FindEligibleAsync(
        WorkQuery query,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetService<IManagedProfileResolver>();
        var environments = resolver is null
            ? Array.Empty<ResolvedWorkSourceEnvironment>()
            : await resolver.ListWorkSourceEnvironmentsAsync(cancellationToken);
        var managedEnvironments = environments.Where(environment => environment.IsManaged).ToList();

        if (managedEnvironments.Count == 0)
        {
            var options = _options.CurrentValue;
            var parameters = BuildQueryParameters(query, options);
            var configuredClient =
                scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClient>();
            return await configuredClient.QueryWorkItemsAsync(parameters, cancellationToken);
        }

        var factory = scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClientFactory>();
        var candidates = new List<WorkCandidate>();

        foreach (var environment in managedEnvironments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = environment.Profile;
            var boardsClient = await factory.CreateAsync(environment, cancellationToken);
            using var disposableClient = boardsClient as IDisposable;
            var parameters = BuildQueryParameters(query, profile);
            var remaining = Math.Max(0, query.MaxResults - candidates.Count);
            if (remaining == 0)
            {
                break;
            }

            parameters = parameters with { MaxResults = remaining };
            var discovered = await boardsClient.QueryWorkItemsAsync(parameters, cancellationToken);

            candidates.AddRange(
                discovered
                    .Take(remaining)
                    .Select(candidate =>
                        candidate with
                        {
                            SourceMetadata = AddEnvironmentKey(
                                candidate.SourceMetadata,
                                profile.Key
                            ),
                        }
                    )
            );
        }

        return candidates;
    }

    public async Task<ClaimResult> TryClaimAsync(
        WorkCandidate candidate,
        ClaimRequest claim,
        CancellationToken cancellationToken
    )
    {
        var environmentKey = GetEnvironmentKey(candidate.SourceMetadata);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveClientAsync(
            scope.ServiceProvider,
            environmentKey,
            cancellationToken
        );
        using var disposableClient = selection.OwnsClient ? selection.Client as IDisposable : null;

        if (!selection.IsManaged)
        {
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
                    FailureReason =
                        "Azure DevOps organization URL is not configured in workSource:organizationUrl.",
                };
            }
        }

        var revision =
            candidate.SourceMetadata?.TryGetValue("revision", out var rev) == true ? rev : null;

        var workRef = new ExternalWorkRef
        {
            Source = candidate.Source,
            ExternalId = candidate.ExternalId,
            Url = candidate.ExternalUrl,
            Revision = revision,
            EnvironmentKey = environmentKey,
        };

        return await selection.Client.TryClaimWorkItemAsync(workRef, claim, cancellationToken);
    }

    public async Task UpdateStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveClientAsync(
            scope.ServiceProvider,
            workRef.EnvironmentKey,
            cancellationToken
        );
        using var disposableClient = selection.OwnsClient ? selection.Client as IDisposable : null;

        await selection.Client.UpdateWorkItemStatusAsync(workRef, status, cancellationToken);
    }

    public async Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveClientAsync(
            scope.ServiceProvider,
            workRef.EnvironmentKey,
            cancellationToken
        );
        using var disposableClient = selection.OwnsClient ? selection.Client as IDisposable : null;

        await selection.Client.AddCommentAsync(workRef, comment, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        ExternalWorkRef workRef,
        int maxComments,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveClientAsync(
            scope.ServiceProvider,
            workRef.EnvironmentKey,
            cancellationToken
        );
        using var disposableClient = selection.OwnsClient ? selection.Client as IDisposable : null;

        return await selection.Client.GetCommentsAsync(workRef, maxComments, cancellationToken);
    }

    public async Task ReleaseClaimAsync(
        ReleaseClaimRequest request,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveClientAsync(
            scope.ServiceProvider,
            request.WorkRef.EnvironmentKey,
            cancellationToken
        );
        using var disposableClient = selection.OwnsClient ? selection.Client as IDisposable : null;

        await selection.Client.ReleaseClaimWorkItemAsync(request, cancellationToken);
    }

    public async Task<ReworkReactivateResult> ReactivateForReworkAsync(
        ReworkReactivateRequest request,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveClientAsync(
            scope.ServiceProvider,
            request.WorkRef.EnvironmentKey,
            cancellationToken
        );
        using var disposableClient = selection.OwnsClient ? selection.Client as IDisposable : null;

        var options = _options.CurrentValue;
        if (!selection.IsManaged && string.IsNullOrWhiteSpace(options.Project))
        {
            return new ReworkReactivateResult
            {
                Success = false,
                FailureReason = "Azure DevOps project is not configured in workSource:project.",
            };
        }

        // Determine target state from the selected managed profile or appsettings.
        var targetState =
            selection.Environment?.Profile.ActiveState
            ?? options.ActiveState;
        if (string.IsNullOrWhiteSpace(targetState))
        {
            return new ReworkReactivateResult
            {
                Success = false,
                FailureReason =
                    "No active state configured; cannot determine target state for reactivation.",
            };
        }

        var workRef = request.WorkRef;

        // (1) Single atomic PATCH: transition state + strip agent lifecycle tags + re-add agent-ready.
        //     This eliminates the stale-revision race where a rev-bump between two separate
        //     PATCHes caused the tag-strip to be silently skipped.
        //     On failure (412 or non-success) the PATCH returns false and we surface
        //     [rework_tag_strip_failed] so the cycle is NOT marked reactivated.
        // Use prefix-aware tag helpers so managed profiles with custom TagPrefix
        // still get correct lifecycle tag names. Managed profiles use their own
        // TagPrefix; unmanaged (configured) environments use the appsettings TagPrefix.
        var tagPrefix = selection.IsManaged
            ? (string.IsNullOrWhiteSpace(selection.Environment!.Profile.TagPrefix)
                ? WorkSourceOptions.DefaultTagPrefix
                : selection.Environment.Profile.TagPrefix)
            : options.TagPrefix;

        var mergedOk = await selection.Client.UpdateWorkItemStatusAsync(
            workRef,
            new ExternalWorkStatus
            {
                Status = targetState,
                Tags = [WorkSourceOptions.TagReady(tagPrefix)],
                RemovedTags =
                [
                    WorkSourceOptions.TagActive(tagPrefix),
                    WorkSourceOptions.TagFailed(tagPrefix),
                    WorkSourceOptions.TagNeedsHuman(tagPrefix),
                    $"{tagPrefix}-worker:*",
                ],
            },
            cancellationToken
        );

        if (!mergedOk)
        {
            return new ReworkReactivateResult
            {
                Success = false,
                FailureReason =
                    $"[rework_tag_strip_failed] Cannot transition work item to '{targetState}' "
                    + "and strip agent lifecycle tags in a single PATCH. "
                    + "The board may have been modified concurrently or the process model may not allow this state change.",
            };
        }

        // (2) Post rework-start comment.
        var comment =
            $"Rework cycle {request.CycleNumber} started: "
            + $"{request.ThreadCount} review threads bundled from PR {request.PullRequestUrl}.";
        await selection.Client.AddCommentAsync(workRef, comment, cancellationToken);

        return new ReworkReactivateResult { Success = true };
    }

    private static BoardsQueryParameters BuildQueryParameters(
        WorkQuery query,
        WorkSourceOptions options
    )
    {
        return new BoardsQueryParameters
        {
            Project = query.Project ?? options.Project ?? string.Empty,
            ExcludedStates = query.States is { Count: > 0 }
                ? null
                : BoardTerminalStates.Values,
            Tags = query.Tags is { Count: > 0 } ? query.Tags : null,
            ExcludedTags = query.ExcludedTags is { Count: > 0 }
                ? query.ExcludedTags
                : null,
            MaxResults = query.MaxResults,
        };
    }

    private static BoardsQueryParameters BuildQueryParameters(
        WorkQuery query,
        WorkSourceEnvironmentProfile profile
    )
    {
        var tagPrefix = string.IsNullOrWhiteSpace(profile.TagPrefix)
            ? WorkSourceOptions.DefaultTagPrefix
            : profile.TagPrefix;
        return new BoardsQueryParameters
        {
            Project = query.Project ?? profile.Project,
            ExcludedStates = query.States is { Count: > 0 }
                ? null
                : BoardTerminalStates.Values,
            Tags = query.Tags is { Count: > 0 } ? query.Tags : [WorkSourceOptions.TagReady(tagPrefix)],
            ExcludedTags = query.ExcludedTags is { Count: > 0 }
                ? query.ExcludedTags
                : WorkSourceOptions.LifecycleTags(tagPrefix),
            MaxResults = query.MaxResults,
        };
    }

    private static async Task<ClientSelection> ResolveClientAsync(
        IServiceProvider services,
        string? environmentKey,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            var resolver = services.GetService<IManagedProfileResolver>();
            var environment = resolver is null
                ? null
                : await resolver.ResolveWorkSourceEnvironmentAsync(
                    environmentKey,
                    cancellationToken
                );

            if (environment?.IsManaged == true)
            {
                var factory = services.GetRequiredService<IAzureDevOpsBoardsClientFactory>();
                return new ClientSelection(
                    await factory.CreateAsync(environment, cancellationToken),
                    environment,
                    OwnsClient: true
                );
            }
        }

        return new ClientSelection(
            services.GetRequiredService<IAzureDevOpsBoardsClient>(),
            Environment: null,
            OwnsClient: false
        );
    }

    private static Dictionary<string, string> AddEnvironmentKey(
        IReadOnlyDictionary<string, string>? metadata,
        string key
    )
    {
        var result = metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(metadata);
        result["workSourceEnvironmentKey"] = key;
        return result;
    }

    private static string? GetEnvironmentKey(IReadOnlyDictionary<string, string>? metadata)
    {
        return metadata?.TryGetValue("workSourceEnvironmentKey", out var key) == true ? key : null;
    }

    private static IReadOnlyList<string>? NullIfEmpty(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? values : null;

    private sealed record ClientSelection(
        IAzureDevOpsBoardsClient Client,
        ResolvedWorkSourceEnvironment? Environment,
        bool OwnsClient
    )
    {
        public bool IsManaged => Environment?.IsManaged == true;
    }
}
