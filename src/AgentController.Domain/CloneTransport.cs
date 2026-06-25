namespace AgentController.Domain;

/// <summary>
/// Clone transport type for source-control operations.
/// Drives which environment variables and git options are applied during cloning.
/// </summary>
public enum CloneTransport
{
    /// <summary>
    /// Not explicitly configured — the provider may infer from the clone URL.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// SSH transport (<c>git@host:path</c> or <c>ssh://...</c>).
    /// Uses SSH keys and <c>GIT_SSH_COMMAND</c> for non-interactive auth.
    /// </summary>
    Ssh = 1,

    /// <summary>
    /// HTTPS with Personal Access Token embedded in the URL.
    /// Uses <c>GIT_TERMINAL_PROMPT=0</c> to prevent credential prompts.
    /// </summary>
    HttpsPat = 2,

    /// <summary>
    /// Local filesystem path (<c>file://...</c> or bare path).
    /// No remote auth required.
    /// </summary>
    Local = 3,
}
