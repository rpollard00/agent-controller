using AgentController.Domain.Secrets;

namespace AgentController.Domain;

/// <summary>Stable categories for an actionable clone-preflight failure.</summary>
public enum ClonePreflightFailureCode
{
    /// <summary>The clone URL is missing or invalid.</summary>
    InvalidCloneUrl = 1,

    /// <summary>The inferred transport or its credential reference is misconfigured.</summary>
    TransportConfigurationInvalid = 2,

    /// <summary>The referenced secret or pinned version does not exist.</summary>
    CredentialNotFound = 3,

    /// <summary>The referenced secret has the wrong typed payload.</summary>
    CredentialTypeMismatch = 4,

    /// <summary>The credential payload is empty, malformed, or otherwise unusable.</summary>
    CredentialInvalid = 5,

    /// <summary>The configured secret provider could not resolve credentials.</summary>
    CredentialUnavailable = 6,

    /// <summary>A required local source-control command is unavailable.</summary>
    ToolUnavailable = 7,

    /// <summary>The remote host could not be reached.</summary>
    RemoteUnreachable = 8,

    /// <summary>The remote rejected the configured credential.</summary>
    AuthenticationFailed = 9,

    /// <summary>The remote probe failed for a reason not known to be connectivity or authentication.</summary>
    RemoteRejected = 10,
}

/// <summary>
/// Result of a source-control clone preflight check. Secret values are never included;
/// only the configured credential reference may be surfaced to operators.
/// </summary>
public sealed record ClonePreflightResult
{
    /// <summary>Whether all preflight checks passed.</summary>
    public bool Success { get; init; }

    /// <summary>Concrete, safe operator-facing failure reason.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Machine-readable failure category, or null when the preflight passed.</summary>
    public ClonePreflightFailureCode? FailureCode { get; init; }

    /// <summary>The resolved transport type that was checked.</summary>
    public CloneTransport Transport { get; init; }

    /// <summary>The normalized, credential-free clone URL that was checked.</summary>
    public string CloneUrl { get; init; } = string.Empty;

    /// <summary>Where the checked credential reference was configured.</summary>
    public RepositoryCloneCredentialSource CredentialSource { get; init; }

    /// <summary>
    /// The checked secret name and optional pinned version. Plaintext secret values are
    /// deliberately never part of a preflight result.
    /// </summary>
    public SecretReference? CredentialReference { get; init; }

    /// <summary>Create a successful preflight result.</summary>
    public static ClonePreflightResult Ok(
        CloneTransport transport,
        string cloneUrl,
        RepositoryCloneCredentialSource credentialSource = RepositoryCloneCredentialSource.None,
        SecretReference? credentialReference = null
    ) =>
        new()
        {
            Success = true,
            Reason = string.Empty,
            FailureCode = null,
            Transport = transport,
            CloneUrl = cloneUrl,
            CredentialSource = credentialSource,
            CredentialReference = credentialReference,
        };

    /// <summary>Create a failed preflight result with a concrete reason.</summary>
    public static ClonePreflightResult Failed(
        CloneTransport transport,
        string cloneUrl,
        string reason,
        ClonePreflightFailureCode failureCode = ClonePreflightFailureCode.RemoteRejected,
        RepositoryCloneCredentialSource credentialSource = RepositoryCloneCredentialSource.None,
        SecretReference? credentialReference = null
    ) =>
        new()
        {
            Success = false,
            Reason = reason,
            FailureCode = failureCode,
            Transport = transport,
            CloneUrl = cloneUrl,
            CredentialSource = credentialSource,
            CredentialReference = credentialReference,
        };
}
