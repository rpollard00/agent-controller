using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;

namespace AgentController.Infrastructure;

/// <summary>
/// Azure DevOps Boards implementation of <see cref="IWorkSource"/>.
/// Discovers eligible work items, claims them for exclusive execution,
/// and projects controller status back to Azure DevOps Boards.
///
/// Registered as a singleton via
/// <see cref="AgentControllerServiceCollectionExtensions.AddAgentControllerAzureDevOpsBoardsWorkSource"/>.
/// Because the managed client factory is scoped, each method creates its own
/// <see cref="IServiceScope"/> to resolve a fresh client instance per operation.
/// </summary>
internal sealed class AzureDevOpsBoardsWorkSource : IWorkSource, IManagedBoardItemDiscovery
{
    private const string ManagedEnvironmentResolutionFailure =
        "No enabled managed Azure DevOps work source environment could be resolved.";

    private readonly IServiceScopeFactory _scopeFactory;

    public AzureDevOpsBoardsWorkSource(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
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

        if (environments.Count == 0)
        {
            return [];
        }

        var factory = scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClientFactory>();
        var candidates = new List<WorkCandidate>();

        foreach (var environment in environments)
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

    public async Task<ManagedBoardItemDiscoveryPage> ListAsync(
        ManagedBoardItemDiscoveryQuery query,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1)
            throw new ArgumentOutOfRangeException(nameof(query), "Page must be at least one.");
        if (query.PageSize < 1 || query.PageSize > ManagedBoardItemDiscoveryQuery.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(query), "Page size must be between 1 and 100.");

        cancellationToken.ThrowIfCancellationRequested();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetService<IManagedProfileResolver>();
        var environments = resolver is null
            ? []
            : await resolver.ListConfiguredWorkSourceEnvironmentsAsync(cancellationToken);
        var selectedEnvironments = environments
            .Where(environment => string.IsNullOrWhiteSpace(query.WorkSourceEnvironmentKey)
                || string.Equals(
                    environment.Profile.Key,
                    query.WorkSourceEnvironmentKey.Trim(),
                    StringComparison.OrdinalIgnoreCase
                ))
            .OrderBy(environment => environment.Profile.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var factory = scope.ServiceProvider.GetRequiredService<IAzureDevOpsBoardsClientFactory>();
        var references = new List<DiagnosticReference>();
        var failures = new List<ManagedBoardItemDiscoveryFailure>();
        foreach (var environment in selectedEnvironments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var client = await factory.CreateAsync(environment, cancellationToken);
                using var disposableClient = client as IDisposable;
                var discovered = await client.QueryWorkItemReferencesAsync(
                    new BoardsQueryParameters
                    {
                        Project = environment.Profile.Project,
                        ExcludedStates = query.IncludeTerminal ? null : BoardTerminalStates.Values,
                        MaxResults = int.MaxValue,
                    },
                    cancellationToken
                );
                references.AddRange(discovered.Select(reference =>
                    new DiagnosticReference(environment, reference.Id)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failures.Add(ToFailure(environment));
            }
        }

        var ordered = references
            .OrderBy(reference => reference.Environment.Profile.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.Id)
            .ToArray();
        var offset = ((long)query.Page - 1) * query.PageSize;
        var pageReferences = offset >= ordered.LongLength
            ? []
            : ordered.Skip((int)offset).Take(query.PageSize).ToArray();
        var snapshotsByKey = new Dictionary<string, ManagedBoardItemSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in pageReferences.GroupBy(reference => reference.Environment.Profile.Key, StringComparer.OrdinalIgnoreCase))
        {
            var environment = group.First().Environment;
            try
            {
                var client = await factory.CreateAsync(environment, cancellationToken);
                using var disposableClient = client as IDisposable;
                var items = await client.GetWorkItemsAsync(
                    environment.Profile.Project,
                    group.Select(reference => reference.Id).ToArray(),
                    cancellationToken
                );
                foreach (var item in items)
                {
                    snapshotsByKey[ReferenceKey(environment.Profile.Key, item.ExternalId)] =
                        new ManagedBoardItemSnapshot
                        {
                            Item = item with
                            {
                                SourceMetadata = AddEnvironmentKey(item.SourceMetadata, environment.Profile.Key),
                            },
                            WorkSourceEnvironmentKey = environment.Profile.Key,
                            Project = environment.Profile.Project,
                        };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                if (!failures.Any(failure => string.Equals(
                    failure.WorkSourceEnvironmentKey,
                    environment.Profile.Key,
                    StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add(ToFailure(environment));
                }
            }
        }

        return new ManagedBoardItemDiscoveryPage
        {
            Items = pageReferences
                .Select(reference => snapshotsByKey.GetValueOrDefault(
                    ReferenceKey(reference.Environment.Profile.Key, reference.Id)))
                .Where(snapshot => snapshot is not null)
                .Cast<ManagedBoardItemSnapshot>()
                .ToArray(),
            Failures = failures
                .OrderBy(failure => failure.WorkSourceEnvironmentKey, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Page = query.Page,
            PageSize = query.PageSize,
            Total = ordered.Length,
        };
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
        if (selection is null)
        {
            return new ClaimResult
            {
                Success = false,
                FailureReason = ManagedEnvironmentResolutionFailure,
            };
        }

        using var disposableClient = selection.Client as IDisposable;

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

        return await selection.Client.TryClaimWorkItemAsync(
            workRef,
            claim with { TagPrefix = GetTagPrefix(selection.Environment.Profile) },
            cancellationToken
        );
    }

    public async Task<CreatedWorkItemResult> CreateAssistanceStoryAsync(
        CreateAssistanceStoryRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveRequiredClientAsync(
            scope.ServiceProvider,
            request.EnvironmentKey,
            cancellationToken
        );
        using var disposableClient = selection.Client as IDisposable;

        var profile = selection.Environment.Profile;
        var created = await selection.Client.CreateWorkItemAsync(
            new BoardsCreateWorkItemParameters
            {
                Project = profile.Project,
                WorkItemType = request.WorkItemType,
                RepoKey = request.RepoKey,
                Title = request.Title,
                Description = request.Description,
                Tags = AssistanceStoryCreation.BuildManagedTags(
                    request,
                    GetTagPrefix(profile)
                ),
                Relations = request.Relations,
            },
            cancellationToken
        );

        return created with
        {
            Candidate = created.Candidate with
            {
                SourceMetadata = AddEnvironmentKey(
                    created.Candidate.SourceMetadata,
                    profile.Key
                ),
            },
        };
    }

    public async Task<WorkCandidate> MakeAssistanceStoryReadyAsync(
        WorkCandidate candidate,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var environmentKey = GetEnvironmentKey(candidate.SourceMetadata);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveRequiredClientAsync(
            scope.ServiceProvider,
            environmentKey,
            cancellationToken
        );
        using var disposableClient = selection.Client as IDisposable;

        var readyTag = WorkSourceOptions.TagReadyRework(
            GetTagPrefix(selection.Environment.Profile)
        );
        var updated = await selection.Client.UpdateWorkItemStatusAsync(
            ToExternalWorkRef(candidate, environmentKey),
            new ExternalWorkStatus { Tags = [readyTag] },
            cancellationToken
        );
        if (!updated)
        {
            throw new InvalidOperationException(
                $"Could not publish assistance story '{candidate.ExternalId}' with its ready-rework tag."
            );
        }

        return candidate with
        {
            Tags = candidate.Tags
                .Append(readyTag)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    public async Task UpdateStatusAsync(
        ExternalWorkRef workRef,
        ExternalWorkStatus status,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveRequiredClientAsync(
            scope.ServiceProvider,
            workRef.EnvironmentKey,
            cancellationToken
        );
        using var disposableClient = selection.Client as IDisposable;

        await selection.Client.UpdateWorkItemStatusAsync(workRef, status, cancellationToken);
    }

    public async Task AddCommentAsync(
        ExternalWorkRef workRef,
        string comment,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveRequiredClientAsync(
            scope.ServiceProvider,
            workRef.EnvironmentKey,
            cancellationToken
        );
        using var disposableClient = selection.Client as IDisposable;

        await selection.Client.AddCommentAsync(workRef, comment, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        ExternalWorkRef workRef,
        int maxComments,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveRequiredClientAsync(
            scope.ServiceProvider,
            workRef.EnvironmentKey,
            cancellationToken
        );
        using var disposableClient = selection.Client as IDisposable;

        return await selection.Client.GetCommentsAsync(workRef, maxComments, cancellationToken);
    }

    public async Task ReleaseClaimAsync(
        ReleaseClaimRequest request,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var selection = await ResolveRequiredClientAsync(
            scope.ServiceProvider,
            request.WorkRef.EnvironmentKey,
            cancellationToken
        );
        using var disposableClient = selection.Client as IDisposable;

        await selection.Client.ReleaseClaimWorkItemAsync(
            request with { TagPrefix = GetTagPrefix(selection.Environment.Profile) },
            cancellationToken
        );
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
        if (selection is null)
        {
            return new ReworkReactivateResult
            {
                Success = false,
                FailureReason = ManagedEnvironmentResolutionFailure,
            };
        }

        using var disposableClient = selection.Client as IDisposable;

        var targetState = selection.Environment.Profile.ActiveState;
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
        // get the correct lifecycle tag names.
        var tagPrefix = GetTagPrefix(selection.Environment.Profile);

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
        WorkSourceEnvironmentProfile profile
    )
    {
        var tagPrefix = GetTagPrefix(profile);
        return new BoardsQueryParameters
        {
            Project = query.Project ?? profile.Project,
            ExcludedStates = query.States is { Count: > 0 }
                ? null
                : BoardTerminalStates.Values,
            Tags = query.Tags is { Count: > 0 } ? query.Tags : null,
            AnyTags = query.Tags is { Count: > 0 }
                ? null
                :
                [
                    WorkSourceOptions.TagReady(tagPrefix),
                    WorkSourceOptions.TagReadyRework(tagPrefix),
                ],
            ExcludedTags = query.ExcludedTags is { Count: > 0 }
                ? query.ExcludedTags
                : WorkSourceOptions.LifecycleTags(tagPrefix),
            MaxResults = query.MaxResults,
        };
    }

    private static string GetTagPrefix(WorkSourceEnvironmentProfile profile) =>
        string.IsNullOrWhiteSpace(profile.TagPrefix)
            ? WorkSourceOptions.DefaultTagPrefix
            : profile.TagPrefix.Trim();

    private static async Task<ClientSelection?> ResolveClientAsync(
        IServiceProvider services,
        string? environmentKey,
        CancellationToken cancellationToken
    )
    {
        var resolver = services.GetService<IManagedProfileResolver>();
        var environment = resolver is null
            ? null
            : await resolver.ResolveWorkSourceEnvironmentAsync(
                environmentKey,
                cancellationToken
            );

        if (environment is null)
        {
            return null;
        }

        var factory = services.GetRequiredService<IAzureDevOpsBoardsClientFactory>();
        return new ClientSelection(
            await factory.CreateAsync(environment, cancellationToken),
            environment
        );
    }

    private static async Task<ClientSelection> ResolveRequiredClientAsync(
        IServiceProvider services,
        string? environmentKey,
        CancellationToken cancellationToken
    )
    {
        return await ResolveClientAsync(services, environmentKey, cancellationToken)
            ?? throw new InvalidOperationException(ManagedEnvironmentResolutionFailure);
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

    private static ExternalWorkRef ToExternalWorkRef(
        WorkCandidate candidate,
        string? environmentKey
    )
    {
        var revision = candidate.SourceMetadata?.TryGetValue("revision", out var value) == true
            ? value
            : null;
        return new ExternalWorkRef
        {
            Source = candidate.Source,
            ExternalId = candidate.ExternalId,
            Url = candidate.ExternalUrl,
            Revision = revision,
            EnvironmentKey = environmentKey,
        };
    }

    private static ManagedBoardItemDiscoveryFailure ToFailure(
        ResolvedWorkSourceEnvironment environment
    ) => new()
    {
        WorkSourceEnvironmentKey = environment.Profile.Key,
        Project = environment.Profile.Project,
        Message = "Board items could not be read from this work source environment.",
    };

    private static string ReferenceKey(string environmentKey, string itemId) =>
        $"{environmentKey}\u001f{itemId}";

    private static string ReferenceKey(string environmentKey, int itemId) =>
        $"{environmentKey}\u001f{itemId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private sealed record DiagnosticReference(
        ResolvedWorkSourceEnvironment Environment,
        int Id
    );

    private sealed record ClientSelection(
        IAzureDevOpsBoardsClient Client,
        ResolvedWorkSourceEnvironment Environment
    );
}
