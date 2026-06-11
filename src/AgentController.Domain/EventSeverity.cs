namespace AgentController.Domain;

/// <summary>
/// Severity level for lifecycle and runtime events.
/// </summary>
public enum EventSeverity
{
    /// <summary>Informational message, no action required.</summary>
    Info = 0,

    /// <summary>Warning that may require attention.</summary>
    Warning,

    /// <summary>Error that caused or may cause a failure.</summary>
    Error,

    /// <summary>Critical error requiring immediate operator attention.</summary>
    Critical,
}
