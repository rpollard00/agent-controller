namespace AgentController.Application.Abstractions;

/// <summary>
/// Application-facing projection of the feedback soak configuration.
/// </summary>
public sealed class FeedbackSoakOptionsView
{
    /// <summary>Minimum quiet period before watched feedback becomes eligible.</summary>
    public TimeSpan SoakDuration { get; set; } = TimeSpan.FromMinutes(5);
}
