namespace AgentController.Infrastructure.Options;

/// <summary>
/// Repository profiles keyed by repository name.
/// Section: "repositories"
/// Registered as <c>IOptions&lt;Dictionary&lt;string, RepositoryProfileOptions&gt;&gt;</c>.
/// </summary>
public static class RepositoriesOptions
{
    public const string SectionName = "repositories";
}
