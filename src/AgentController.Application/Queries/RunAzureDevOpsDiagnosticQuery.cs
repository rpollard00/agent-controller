namespace AgentController.Application.Queries;

/// <summary>Triggers an Azure DevOps connectivity and configuration diagnostic.</summary>
/// <param name="EnvironmentKey">
/// Optional managed Azure DevOps environment key. When omitted, the first enabled managed
/// environment is preferred and appsettings remains the fallback.
/// </param>
public sealed record RunAzureDevOpsDiagnosticQuery(string? EnvironmentKey = null);
