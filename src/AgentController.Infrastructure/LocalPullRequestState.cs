using AgentController.Application;
using AgentController.Domain;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Shared in-memory pull-request state for deterministic local discovery and label mutation.
/// </summary>
internal sealed class LocalPullRequestState
{
    private readonly object _sync = new();
    private readonly IOptionsMonitor<LocalPullRequestOptions> _options;
    private Dictionary<string, ManagedPullRequestSnapshot>? _snapshots;

    public LocalPullRequestState(IOptionsMonitor<LocalPullRequestOptions> options)
    {
        _options = options;
    }

    public IReadOnlyList<ManagedPullRequestSnapshot> List(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            EnsureInitialized(cancellationToken);
            return _snapshots!
                .Values
                .OrderBy(snapshot => snapshot.CanonicalKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public void MutateLabels(
        PullRequestReference pullRequest,
        PullRequestLabelMutation mutation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();

        if (!pullRequest.HasCanonicalIdentity)
        {
            throw new ArgumentException(
                "A canonical pull-request identity is required for label mutation.",
                nameof(pullRequest)
            );
        }

        lock (_sync)
        {
            EnsureInitialized(cancellationToken);
            if (!_snapshots!.TryGetValue(pullRequest.CanonicalKey, out var snapshot))
            {
                throw new InvalidOperationException(
                    $"Local pull request '{pullRequest.CanonicalKey}' is not configured."
                );
            }

            var labels = snapshot.Labels.ToList();
            foreach (var label in Normalize(mutation.LabelsToRemove))
            {
                labels.RemoveAll(existing =>
                    existing.Equals(label, StringComparison.OrdinalIgnoreCase)
                );
            }

            foreach (var label in Normalize(mutation.LabelsToAdd))
            {
                if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                {
                    labels.Add(label);
                }
            }

            _snapshots[pullRequest.CanonicalKey] = snapshot with { Labels = labels.ToArray() };
        }
    }

    private void EnsureInitialized(CancellationToken cancellationToken)
    {
        if (_snapshots is not null)
        {
            return;
        }

        var snapshots = new Dictionary<string, ManagedPullRequestSnapshot>(
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var definition in _options.CurrentValue.Definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = Map(definition);
            if (snapshot.PullRequest.HasCanonicalIdentity)
            {
                snapshots.TryAdd(snapshot.CanonicalKey, snapshot);
            }
        }

        _snapshots = snapshots;
    }

    private static ManagedPullRequestSnapshot Map(LocalPullRequestDefinition definition)
    {
        var labels = Normalize(definition.Labels);
        var linkedWorkItems = definition.LinkedWorkItems
            .Where(link => !string.IsNullOrWhiteSpace(link.WorkItemId))
            .GroupBy(link => link.WorkItemId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(link => new PullRequestWorkItemReference
            {
                WorkItemId = Clean(link.WorkItemId),
                WorkItemUrl = Clean(link.WorkItemUrl),
            })
            .ToArray();

        return new ManagedPullRequestSnapshot
        {
            PullRequest = new PullRequestReference
            {
                EnvironmentKey = Clean(definition.EnvironmentKey),
                RepositoryKey = Clean(definition.RepositoryKey),
                PullRequestId = Clean(definition.PullRequestId),
                PullRequestUrl = Clean(definition.PullRequestUrl),
                SourceBranch = Clean(definition.SourceBranch),
                TargetBranch = Clean(definition.TargetBranch),
                SourceCommitSha = Clean(definition.SourceCommitSha),
            },
            Title = Clean(definition.Title),
            Labels = labels,
            LinkedWorkItems = linkedWorkItems,
        };
    }

    private static string[] Normalize(IEnumerable<string> labels) =>
        labels
            .Select(Clean)
            .Where(label => label.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
