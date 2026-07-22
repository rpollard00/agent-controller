using AgentController.Domain.Secrets;

namespace AgentController.Domain;

/// <summary>Identifies where clone credentials are configured.</summary>
public enum RepositoryCloneCredentialSource
{
    /// <summary>The effective transport does not use managed credentials.</summary>
    None = 0,

    /// <summary>An SSH-key reference stored on the repository profile.</summary>
    SshKey = 1,

    /// <summary>A PAT reference stored on the repository's host connection.</summary>
    ConnectionPersonalAccessToken = 2,
}

/// <summary>Stable codes for configuration problems that block a repository clone.</summary>
public enum RepositoryCloneTransportIssueCode
{
    /// <summary>The clone location cannot be classified as SSH, HTTP(S), or local.</summary>
    UnsupportedCloneUrl = 1,

    /// <summary>The explicitly configured transport conflicts with the clone URL.</summary>
    ConfiguredTransportMismatch = 2,

    /// <summary>An SSH clone has no SSH-key secret reference.</summary>
    MissingSshKeyReference = 3,

    /// <summary>An HTTP(S) clone has no repository host connection.</summary>
    MissingRepositoryHostConnection = 4,

    /// <summary>The repository's configured host connection does not exist.</summary>
    RepositoryHostConnectionNotFound = 5,

    /// <summary>The repository's configured host connection is disabled.</summary>
    RepositoryHostConnectionDisabled = 6,

    /// <summary>The repository host connection has no PAT reference.</summary>
    MissingPersonalAccessTokenReference = 7,
}

/// <summary>A field-specific configuration problem that blocks cloning.</summary>
public sealed record RepositoryCloneTransportIssue
{
    /// <summary>Machine-readable issue code.</summary>
    public required RepositoryCloneTransportIssueCode Code { get; init; }

    /// <summary>Repository field associated with the problem.</summary>
    public required string Field { get; init; }

    /// <summary>Safe, actionable operator-facing description.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Concrete transport and credential reference resolved for a repository clone.
/// Secret values are deliberately never part of this result.
/// </summary>
public sealed record RepositoryCloneTransportResolution
{
    /// <summary>The effective concrete transport.</summary>
    public required CloneTransport Transport { get; init; }

    /// <summary>Where the transport's credential reference came from.</summary>
    public required RepositoryCloneCredentialSource CredentialSource { get; init; }

    /// <summary>
    /// Credential reference used by the transport. A missing version means consumers
    /// should resolve the latest version.
    /// </summary>
    public SecretReference? CredentialReference { get; init; }

    /// <summary>Configuration problems that must be fixed before cloning.</summary>
    public IReadOnlyList<RepositoryCloneTransportIssue> BlockingIssues { get; init; } = [];

    /// <summary>Whether all transport-specific credential references are configured.</summary>
    public bool IsReady => BlockingIssues.Count == 0;
}

/// <summary>
/// Canonical domain policy for resolving a repository's concrete clone transport and
/// transport-specific credential reference.
/// </summary>
public static class RepositoryCloneTransportResolver
{
    /// <summary>
    /// Resolves a repository profile using its associated connection when present.
    /// The resolver validates references only; secret existence and authentication are
    /// checked later by clone preflight.
    /// </summary>
    public static RepositoryCloneTransportResolution Resolve(
        RepositoryProfile repository,
        ConnectionProfile? connection = null
    )
    {
        ArgumentNullException.ThrowIfNull(repository);

        var issues = new List<RepositoryCloneTransportIssue>();
        var inferredTransport = InferTransport(repository.CloneUrl);
        var transport = ResolveConfiguredTransport(repository.Transport, inferredTransport);

        if (inferredTransport == CloneTransport.Unspecified)
        {
            issues.Add(
                Issue(
                    RepositoryCloneTransportIssueCode.UnsupportedCloneUrl,
                    "cloneUrl",
                    "The clone URL cannot be resolved to SSH, HTTPS, or local transport."
                )
            );
        }

        if (
            repository.Transport != CloneTransport.Unspecified
            && Enum.IsDefined(repository.Transport)
            && inferredTransport != CloneTransport.Unspecified
            && repository.Transport != inferredTransport
        )
        {
            issues.Add(
                Issue(
                    RepositoryCloneTransportIssueCode.ConfiguredTransportMismatch,
                    "transport",
                    $"The configured {repository.Transport} transport is not compatible with "
                        + $"the {inferredTransport} clone URL."
                )
            );
        }

        return transport switch
        {
            CloneTransport.Ssh => ResolveSsh(repository, issues),
            CloneTransport.HttpsPat => ResolveHttps(repository, connection, issues),
            CloneTransport.Local => CreateResolution(
                CloneTransport.Local,
                RepositoryCloneCredentialSource.None,
                null,
                issues
            ),
            _ => CreateResolution(transport, RepositoryCloneCredentialSource.None, null, issues),
        };
    }

    /// <summary>
    /// Resolves an explicit transport or infers one from the clone URL. This overload
    /// preserves the existing source-control behavior for callers that do not yet have
    /// repository credential context.
    /// </summary>
    public static CloneTransport ResolveTransport(
        CloneTransport configuredTransport,
        string? cloneUrl
    )
    {
        if (configuredTransport != CloneTransport.Unspecified)
        {
            return configuredTransport;
        }

        var inferred = InferTransport(cloneUrl);
        return inferred == CloneTransport.Unspecified ? CloneTransport.Local : inferred;
    }

    /// <summary>Infers a concrete transport solely from a valid clone location.</summary>
    public static CloneTransport InferTransport(string? cloneUrl)
    {
        var value = cloneUrl?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Any(char.IsControl))
        {
            return CloneTransport.Unspecified;
        }

        if (value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return IsValidRemoteUri(value, "ssh") ? CloneTransport.Ssh : CloneTransport.Unspecified;
        }

        if (IsScpStyleUrl(value))
        {
            return CloneTransport.Ssh;
        }

        if (
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        )
        {
            return IsValidRemoteUri(value, "https", "http")
                ? CloneTransport.HttpsPat
                : CloneTransport.Unspecified;
        }

        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var fileUri) && fileUri.IsFile
                ? CloneTransport.Local
                : CloneTransport.Unspecified;
        }

        return value.Contains("://", StringComparison.Ordinal)
            ? CloneTransport.Unspecified
            : CloneTransport.Local;
    }

    private static CloneTransport ResolveConfiguredTransport(
        CloneTransport configuredTransport,
        CloneTransport inferredTransport
    ) =>
        configuredTransport != CloneTransport.Unspecified && Enum.IsDefined(configuredTransport)
            ? configuredTransport
            : inferredTransport;

    private static RepositoryCloneTransportResolution ResolveSsh(
        RepositoryProfile repository,
        List<RepositoryCloneTransportIssue> issues
    )
    {
        // When the profile opts into environment-inherited SSH, no managed key
        // reference is required — the runner's ssh-agent or default key files
        // provide authentication out of band.
        if (repository.SshKeyInheritEnvironment)
        {
            return CreateResolution(
                CloneTransport.Ssh,
                RepositoryCloneCredentialSource.None,
                null,
                issues
            );
        }

        var reference = repository.SshKeyReference;
        if (reference is null || !reference.IsSpecified)
        {
            issues.Add(
                Issue(
                    RepositoryCloneTransportIssueCode.MissingSshKeyReference,
                    "sshKeyReference",
                    "SSH clone transport requires an SSH-key secret reference."
                )
            );
            reference = null;
        }

        return CreateResolution(
            CloneTransport.Ssh,
            RepositoryCloneCredentialSource.SshKey,
            reference,
            issues
        );
    }

    private static RepositoryCloneTransportResolution ResolveHttps(
        RepositoryProfile repository,
        ConnectionProfile? connection,
        List<RepositoryCloneTransportIssue> issues
    )
    {
        if (string.IsNullOrWhiteSpace(repository.RepositoryHostConnectionKey))
        {
            issues.Add(
                Issue(
                    RepositoryCloneTransportIssueCode.MissingRepositoryHostConnection,
                    "repositoryHostConnectionKey",
                    "HTTPS clone transport requires a repository host connection with a PAT."
                )
            );
            return CreateResolution(
                CloneTransport.HttpsPat,
                RepositoryCloneCredentialSource.ConnectionPersonalAccessToken,
                null,
                issues
            );
        }

        if (connection is null)
        {
            issues.Add(
                Issue(
                    RepositoryCloneTransportIssueCode.RepositoryHostConnectionNotFound,
                    "repositoryHostConnectionKey",
                    $"Repository host connection '{repository.RepositoryHostConnectionKey}' was not found."
                )
            );
            return CreateResolution(
                CloneTransport.HttpsPat,
                RepositoryCloneCredentialSource.ConnectionPersonalAccessToken,
                null,
                issues
            );
        }

        if (!connection.Enabled)
        {
            issues.Add(
                Issue(
                    RepositoryCloneTransportIssueCode.RepositoryHostConnectionDisabled,
                    "repositoryHostConnectionKey",
                    $"Repository host connection '{connection.Key}' is disabled."
                )
            );
        }

        var reference = (
            connection.ProviderSettings as IPersonalAccessTokenConnectionSettings
        )?.PersonalAccessTokenReference;
        if (reference is null || !reference.IsSpecified)
        {
            issues.Add(
                Issue(
                    RepositoryCloneTransportIssueCode.MissingPersonalAccessTokenReference,
                    "repositoryHostConnectionKey",
                    $"Repository host connection '{connection.Key}' does not have a PAT secret reference."
                )
            );
            reference = null;
        }

        return CreateResolution(
            CloneTransport.HttpsPat,
            RepositoryCloneCredentialSource.ConnectionPersonalAccessToken,
            reference,
            issues
        );
    }

    private static RepositoryCloneTransportResolution CreateResolution(
        CloneTransport transport,
        RepositoryCloneCredentialSource credentialSource,
        SecretReference? credentialReference,
        List<RepositoryCloneTransportIssue> issues
    ) =>
        new()
        {
            Transport = transport,
            CredentialSource = credentialSource,
            CredentialReference = credentialReference,
            BlockingIssues = [.. issues],
        };

    private static RepositoryCloneTransportIssue Issue(
        RepositoryCloneTransportIssueCode code,
        string field,
        string message
    ) =>
        new()
        {
            Code = code,
            Field = field,
            Message = message,
        };

    private static bool IsValidRemoteUri(string value, params string[] schemes) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(uri.Host)
        && !value.Any(char.IsWhiteSpace);

    private static bool IsScpStyleUrl(string value)
    {
        var atIndex = value.IndexOf('@');
        var colonIndex = value.IndexOf(':', atIndex + 1);
        return atIndex > 0
            && colonIndex > atIndex + 1
            && colonIndex < value.Length - 1
            && !value.Any(char.IsWhiteSpace)
            && !value[..atIndex].Contains('/')
            && !value[(atIndex + 1)..colonIndex].Contains('/');
    }
}
