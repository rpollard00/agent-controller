namespace AgentController.Application;

/// <summary>
/// Shared board-item pickup vocabulary used by production discovery and diagnostics.
/// Comparisons deliberately mirror provider pickup: exact and case-insensitive.
/// </summary>
public static class BoardItemPickupPolicy
{
    public const string DefaultTagPrefix = "agent";

    public static IReadOnlyList<string> TerminalStates { get; } =
        Array.AsReadOnly(["Closed", "Removed", "Resolved", "Completed"]);

    public static string NormalizeTagPrefix(string? prefix) =>
        string.IsNullOrWhiteSpace(prefix) ? DefaultTagPrefix : prefix.Trim();

    public static string ReadyTag(string? prefix) => $"{NormalizeTagPrefix(prefix)}-ready";

    public static string ReadyReworkTag(string? prefix) =>
        $"{NormalizeTagPrefix(prefix)}-ready-rework";

    public static IReadOnlyList<string> LifecycleTags(string? prefix)
    {
        var normalized = NormalizeTagPrefix(prefix);
        return [$"{normalized}-active", $"{normalized}-failed", $"{normalized}-needs-human"];
    }

    public static bool IsTerminal(string? state) =>
        state is not null
        && TerminalStates.Contains(state, StringComparer.OrdinalIgnoreCase);

    public static bool HasTag(IReadOnlyList<string> tags, string expected) =>
        tags.Contains(expected, StringComparer.OrdinalIgnoreCase);
}
