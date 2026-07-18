namespace AgentController.Application.Results;

/// <summary>
/// Status of a secret management operation.
/// </summary>
public enum SecretOperationStatus
{
    /// <summary>The operation completed successfully.</summary>
    Succeeded,

    /// <summary>Input validation failed.</summary>
    ValidationFailed,

    /// <summary>A secret with the same name already exists, or the secret is in use.</summary>
    Conflict,

    /// <summary>The secret was not found.</summary>
    NotFound,
}

/// <summary>
/// Result of creating a new named secret.
/// </summary>
public sealed record CreateSecretResult
{
    /// <summary>Operation status.</summary>
    public SecretOperationStatus Status { get; init; }

    /// <summary>The secret name on success.</summary>
    public string? SecretName { get; init; }

    /// <summary>Validation errors when status is ValidationFailed.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; init; } =
        new Dictionary<string, string[]>();

    /// <summary>Optional detail message for error cases.</summary>
    public string? Detail { get; init; }

    public static CreateSecretResult Succeeded(string secretName) =>
        new()
        {
            Status = SecretOperationStatus.Succeeded,
            SecretName = secretName,
        };

    public static CreateSecretResult ValidationFailed(IReadOnlyDictionary<string, string[]> errors) =>
        new()
        {
            Status = SecretOperationStatus.ValidationFailed,
            ValidationErrors = errors,
        };

    public static CreateSecretResult Conflict(string? detail = null) =>
        new()
        {
            Status = SecretOperationStatus.Conflict,
            Detail = detail,
        };

    public static CreateSecretResult NotFound(string? detail = null) =>
        new()
        {
            Status = SecretOperationStatus.NotFound,
            Detail = detail,
        };
}

/// <summary>
/// Result of creating a new version of an existing secret.
/// </summary>
public sealed record CreateSecretVersionResult
{
    /// <summary>Operation status.</summary>
    public SecretOperationStatus Status { get; init; }

    /// <summary>The secret name on success.</summary>
    public string? SecretName { get; init; }

    /// <summary>The new version number on success.</summary>
    public int? Version { get; init; }

    /// <summary>Validation errors when status is ValidationFailed.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; init; } =
        new Dictionary<string, string[]>();

    /// <summary>Optional detail message for error cases.</summary>
    public string? Detail { get; init; }

    public static CreateSecretVersionResult Succeeded(string secretName, int version) =>
        new()
        {
            Status = SecretOperationStatus.Succeeded,
            SecretName = secretName,
            Version = version,
        };

    public static CreateSecretVersionResult ValidationFailed(IReadOnlyDictionary<string, string[]> errors) =>
        new()
        {
            Status = SecretOperationStatus.ValidationFailed,
            ValidationErrors = errors,
        };

    public static CreateSecretVersionResult Conflict(string? detail = null) =>
        new()
        {
            Status = SecretOperationStatus.Conflict,
            Detail = detail,
        };

    public static CreateSecretVersionResult NotFound(string? detail = null) =>
        new()
        {
            Status = SecretOperationStatus.NotFound,
            Detail = detail,
        };
}

/// <summary>
/// Result of deleting a named secret and all of its versions.
/// </summary>
public sealed record DeleteSecretResult
{
    /// <summary>Operation status.</summary>
    public SecretOperationStatus Status { get; init; }

    /// <summary>Optional detail message for error cases.</summary>
    public string? Detail { get; init; }

    /// <summary>Validation errors when status is ValidationFailed.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; init; } =
        new Dictionary<string, string[]>();

    public static DeleteSecretResult Succeeded() =>
        new()
        {
            Status = SecretOperationStatus.Succeeded,
        };

    public static DeleteSecretResult ValidationFailed(IReadOnlyDictionary<string, string[]> errors) =>
        new()
        {
            Status = SecretOperationStatus.ValidationFailed,
            ValidationErrors = errors,
        };

    public static DeleteSecretResult Conflict(string? detail = null) =>
        new()
        {
            Status = SecretOperationStatus.Conflict,
            Detail = detail,
        };

    public static DeleteSecretResult NotFound(string? detail = null) =>
        new()
        {
            Status = SecretOperationStatus.NotFound,
            Detail = detail,
        };
}
