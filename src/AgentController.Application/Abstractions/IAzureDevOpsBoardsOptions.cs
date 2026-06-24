namespace AgentController.Application.Abstractions;

/// <summary>
/// Minimal abstraction over Azure DevOps Boards authentication options.
/// Allows the Application layer to resolve the PAT without depending on
/// the Infrastructure options types directly.
/// </summary>
public interface IAzureDevOpsBoardsOptions
{
    /// <summary>
    /// Resolves the effective PAT value by expanding "ENV:VARIABLE_NAME" references.
    /// Returns <c>null</c> when the configured value is empty or whitespace.
    /// Throws <see cref="InvalidOperationException"/> when an ENV: reference points
    /// to a missing or empty environment variable.
    /// </summary>
    string? ResolvePersonalAccessToken();
}
