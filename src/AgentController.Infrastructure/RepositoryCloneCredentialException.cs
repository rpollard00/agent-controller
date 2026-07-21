namespace AgentController.Infrastructure;

/// <summary>Internal failure categories used to turn secret resolution into safe preflight results.</summary>
internal enum RepositoryCloneCredentialFailure
{
    NotFound,
    TypeMismatch,
    InvalidPayload,
    StoreUnavailable,
}

/// <summary>
/// A safe, typed credential-resolution failure. Messages contain reference metadata only,
/// never resolved secret values.
/// </summary>
internal sealed class RepositoryCloneCredentialException(
    RepositoryCloneCredentialFailure failure,
    string message
) : InvalidOperationException(message)
{
    public RepositoryCloneCredentialFailure Failure { get; } = failure;
}
