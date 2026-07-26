using AgentController.Domain;
using AgentController.Infrastructure.Options;

namespace AgentController.Infrastructure;

/// <summary>Shared validation and managed-tag construction for assistance stories.</summary>
internal static class AssistanceStoryCreation
{
    public static IReadOnlyList<string> BuildManagedTags(
        CreateAssistanceStoryRequest request,
        string? tagPrefix
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.RepoKey, nameof(request.RepoKey));
        Require(request.Title, nameof(request.Title));
        Require(request.WorkItemType, nameof(request.WorkItemType));

        var prefix = string.IsNullOrWhiteSpace(tagPrefix)
            ? WorkSourceOptions.DefaultTagPrefix
            : tagPrefix.Trim();
        var tags = new List<string>
        {
            $"repo:{request.RepoKey.Trim()}",
        };
        if (request.ReadyForClaim)
        {
            tags.Add(WorkSourceOptions.TagReadyRework(prefix));
        }

        tags.AddRange(
            request.CorrelationTags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
        );

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }
}
