using AgentController.Application;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// <see cref="IPrLabelSource"/> implementation that returns deterministic
/// <see cref="PrLabel"/> instances from the <c>localFeedback</c> configuration
/// section.
///
/// Mirrors <see cref="LocalFeedbackSource"/>: label definitions are sourced from
/// the <see cref="LocalFeedbackSignalDefinition.Labels"/> property of each signal.
/// This enables the marker gate in the filter pipeline to run offline end to end
/// without requiring an Azure DevOps connection.
///
/// Registered as a singleton via
/// <see cref="AgentControllerServiceCollectionExtensions.AddAgentControllerLocalFeedbackSource"/>.
/// </summary>
internal sealed class LocalPrLabelSource : IPrLabelSource
{
    private readonly IOptionsMonitor<LocalFeedbackOptions> _options;
    private readonly object _initLock = new();
    private bool _initialized;

    /// <summary>
    /// Validated and cached label definitions, keyed by PullRequestId.
    /// Populated on first call.
    /// </summary>
    private Dictionary<string, IReadOnlyList<PrLabel>> _labels = new(StringComparer.Ordinal);

    public LocalPrLabelSource(IOptionsMonitor<LocalFeedbackOptions> options)
    {
        _options = options;
    }

    public Task<IReadOnlyList<PrLabel>> GetLabelsAsync(
        PrUnderTest pr,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();

        if (_labels.TryGetValue(pr.PullRequestId, out var labels))
        {
            return Task.FromResult<IReadOnlyList<PrLabel>>(labels);
        }

        // No labels configured for this PR — marker gate will fail-closed.
        return Task.FromResult<IReadOnlyList<PrLabel>>(Array.Empty<PrLabel>());
    }

    /// <summary>
    /// Lazily extract label definitions from configuration.
    /// Thread-safe; initialization happens exactly once.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            var definitions = _options.CurrentValue.Signals;
            var labels = new Dictionary<string, IReadOnlyList<PrLabel>>(StringComparer.Ordinal);

            foreach (var def in definitions)
            {
                if (string.IsNullOrWhiteSpace(def.PullRequestId))
                {
                    continue;
                }

                if (def.Labels.Count == 0)
                {
                    // No labels for this PR — store empty list explicitly
                    // so the marker gate can distinguish "no labels" from "unknown PR".
                    labels[def.PullRequestId] = Array.Empty<PrLabel>();
                    continue;
                }

                var prLabels = new List<PrLabel>();
                foreach (var labelDef in def.Labels)
                {
                    prLabels.Add(new PrLabel
                    {
                        Name = labelDef.Name,
                    });
                }

                labels[def.PullRequestId] = prLabels;
            }

            _labels = labels;
            _initialized = true;
        }
    }
}
