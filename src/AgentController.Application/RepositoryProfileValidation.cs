using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Normalization and validation shared by repository onboarding handlers.</summary>
internal static class RepositoryProfileValidation
{
    private const int MaximumRepositoryKeyLength = 256;
    private const int MaximumAssociationKeyLength = 128;
    private const int MaximumCloneUrlLength = 2048;
    private const int MaximumBranchLength = 256;
    private const int MaximumAllowedPathLength = 2048;

    public static async Task<RepositoryProfileValidationResult> ValidateAndNormalizeAsync(
        RepositoryProfile profile,
        IRuntimeEnvironmentStore runtimeEnvironmentStore,
        IRepositoryHostConnectionStore? repositoryHostConnectionStore,
        CancellationToken cancellationToken
    )
    {
        var errors = new ValidationErrors();
        var key = NormalizeKey(profile.Key);
        ValidateKey(key, "key", MaximumRepositoryKeyLength, errors);

        var cloneUrl = profile.CloneUrl?.Trim() ?? string.Empty;
        ValidateCloneUrl(cloneUrl, profile.Transport, errors);

        var branch = profile.DefaultBranch?.Trim() ?? string.Empty;
        ValidateBranch(branch, errors);

        if (!Enum.IsDefined(profile.Transport))
        {
            errors.Add("transport", "The clone transport is not supported.");
        }

        var allowedPaths = NormalizeAllowedPaths(profile.AllowedPaths, errors);
        var repositoryHostConnectionKey = NormalizeOptionalKey(profile.RepositoryHostConnectionKey);
        var remoteIdentity = NormalizeRemoteIdentity(profile.RemoteIdentity);
        var runtimeEnvironmentKey = NormalizeOptionalKey(profile.RuntimeEnvironmentKey);

        // Legacy field: no existence check (deprecated, kept for migration backfill only)
        // ValidateOptionalKey(azureDevOpsEnvironmentKey, "azureDevOpsEnvironmentKey", errors);

        ValidateOptionalKey(repositoryHostConnectionKey, "repositoryHostConnectionKey", errors);
        ValidateOptionalKey(remoteIdentity, "remoteIdentity", errors);
        ValidateOptionalKey(runtimeEnvironmentKey, "runtimeEnvironmentKey", errors);

        // Validate that the repository host connection exists when specified.
        if (
            repositoryHostConnectionKey is not null
            && !errors.Contains("repositoryHostConnectionKey")
            && repositoryHostConnectionStore is not null
            && await repositoryHostConnectionStore.GetByKeyAsync(
                repositoryHostConnectionKey,
                cancellationToken
            )
                is null
        )
        {
            errors.Add(
                "repositoryHostConnectionKey",
                $"Repository host connection '{repositoryHostConnectionKey}' does not exist."
            );
        }

        if (
            runtimeEnvironmentKey is not null
            && !errors.Contains("runtimeEnvironmentKey")
            && await runtimeEnvironmentStore.GetByKeyAsync(runtimeEnvironmentKey, cancellationToken)
                is null
        )
        {
            errors.Add(
                "runtimeEnvironmentKey",
                $"Runtime environment '{runtimeEnvironmentKey}' does not exist."
            );
        }

        var normalized = profile with
        {
            Key = key,
            CloneUrl = cloneUrl,
            DefaultBranch = branch,
            EnvironmentProfile = profile.EnvironmentProfile?.Trim() ?? string.Empty,
            RuntimeProfile = profile.RuntimeProfile?.Trim() ?? string.Empty,
            RepositoryHostConnectionKey = repositoryHostConnectionKey,
            RemoteIdentity = remoteIdentity,
            RuntimeEnvironmentKey = runtimeEnvironmentKey,
            AllowedPaths = allowedPaths,
        };

        return new RepositoryProfileValidationResult(normalized, errors.ToDictionary());
    }

    public static KeyValidationResult ValidateAndNormalizeKey(string? value)
    {
        var errors = new ValidationErrors();
        var normalized = NormalizeKey(value);
        ValidateKey(normalized, "key", MaximumRepositoryKeyLength, errors);
        return new KeyValidationResult(normalized, errors.ToDictionary());
    }

    private static string NormalizeKey(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string? NormalizeOptionalKey(string? value)
    {
        var normalized = NormalizeKey(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? NormalizeRemoteIdentity(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static void ValidateKey(
        string key,
        string field,
        int maximumLength,
        ValidationErrors errors
    )
    {
        if (key.Length == 0)
        {
            errors.Add(field, "A key is required.");
            return;
        }

        if (key.Length > maximumLength)
        {
            errors.Add(field, $"The key must be {maximumLength} characters or fewer.");
        }

        if (!IsAsciiLetterOrDigit(key[0]) || !IsAsciiLetterOrDigit(key[^1]))
        {
            errors.Add(field, "The key must start and end with a letter or number.");
        }

        if (key.Any(character => !IsKeyCharacter(character)))
        {
            errors.Add(
                field,
                "The key may contain only letters, numbers, periods, underscores, and hyphens."
            );
        }
    }

    private static void ValidateOptionalKey(string? key, string field, ValidationErrors errors)
    {
        if (key is not null)
        {
            ValidateKey(key, field, MaximumAssociationKeyLength, errors);
        }
    }

    private static bool IsKeyCharacter(char character) =>
        IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static void ValidateCloneUrl(
        string cloneUrl,
        CloneTransport transport,
        ValidationErrors errors
    )
    {
        if (cloneUrl.Length == 0)
        {
            errors.Add("cloneUrl", "A clone URL or local repository path is required.");
            return;
        }

        if (cloneUrl.Length > MaximumCloneUrlLength)
        {
            errors.Add(
                "cloneUrl",
                $"The clone URL must be {MaximumCloneUrlLength} characters or fewer."
            );
        }

        if (cloneUrl.Any(char.IsControl))
        {
            errors.Add("cloneUrl", "The clone URL cannot contain control characters.");
            return;
        }

        var locationKind = ClassifyCloneLocation(cloneUrl);
        if (locationKind == CloneLocationKind.Invalid)
        {
            errors.Add(
                "cloneUrl",
                "The clone URL must be a valid HTTP, SSH, file URL, or local repository path."
            );
            return;
        }

        if (!Enum.IsDefined(transport) || transport == CloneTransport.Unspecified)
        {
            return;
        }

        var compatible = transport switch
        {
            CloneTransport.Ssh => locationKind == CloneLocationKind.Ssh,
            CloneTransport.HttpsPat => locationKind == CloneLocationKind.Http,
            CloneTransport.Local => locationKind == CloneLocationKind.Local,
            _ => false,
        };

        if (!compatible)
        {
            errors.Add(
                "transport",
                $"Clone URL '{cloneUrl}' is not compatible with the {transport} transport."
            );
        }
    }

    private static CloneLocationKind ClassifyCloneLocation(string cloneUrl)
    {
        if (
            cloneUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || cloneUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
        {
            return IsValidRemoteUri(cloneUrl, "http", "https")
                ? CloneLocationKind.Http
                : CloneLocationKind.Invalid;
        }

        if (cloneUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return IsValidRemoteUri(cloneUrl, "ssh")
                ? CloneLocationKind.Ssh
                : CloneLocationKind.Invalid;
        }

        if (cloneUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(cloneUrl, UriKind.Absolute, out var fileUri) && fileUri.IsFile
                ? CloneLocationKind.Local
                : CloneLocationKind.Invalid;
        }

        if (IsScpStyleUrl(cloneUrl))
        {
            return CloneLocationKind.Ssh;
        }

        if (cloneUrl.Contains("://", StringComparison.Ordinal))
        {
            return CloneLocationKind.Invalid;
        }

        return CloneLocationKind.Local;
    }

    private static bool IsValidRemoteUri(string value, params string[] schemes)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && !value.Any(char.IsWhiteSpace);
    }

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

    private static void ValidateBranch(string branch, ValidationErrors errors)
    {
        if (branch.Length == 0)
        {
            errors.Add("defaultBranch", "A default branch is required.");
            return;
        }

        if (branch.Length > MaximumBranchLength)
        {
            errors.Add(
                "defaultBranch",
                $"The default branch must be {MaximumBranchLength} characters or fewer."
            );
        }

        var invalid =
            branch == "@"
            || branch.StartsWith('-')
            || branch.StartsWith('/')
            || branch.EndsWith('/')
            || branch.EndsWith('.')
            || branch.Contains("..", StringComparison.Ordinal)
            || branch.Contains("//", StringComparison.Ordinal)
            || branch.Contains("@{", StringComparison.Ordinal)
            || branch.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || branch.Any(character => character is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            || branch
                .Split('/')
                .Any(part =>
                    part.StartsWith('.')
                    || part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
                );

        if (invalid)
        {
            errors.Add("defaultBranch", "The default branch is not a valid Git branch name.");
        }
    }

    private static List<string> NormalizeAllowedPaths(
        IReadOnlyList<string>? paths,
        ValidationErrors errors
    )
    {
        if (paths is null || paths.Count == 0)
        {
            return [];
        }

        var normalizedPaths = new List<string>(paths.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var originalPath in paths)
        {
            var normalized = NormalizeAllowedPath(originalPath, errors);
            if (normalized is not null && seen.Add(normalized))
            {
                normalizedPaths.Add(normalized);
            }
        }

        return normalizedPaths;
    }

    private static string? NormalizeAllowedPath(string? value, ValidationErrors errors)
    {
        var path = value?.Trim().Replace('\\', '/') ?? string.Empty;
        if (path.Length == 0)
        {
            errors.Add("allowedPaths", "Allowed paths cannot be empty.");
            return null;
        }

        if (path.Length > MaximumAllowedPathLength)
        {
            errors.Add(
                "allowedPaths",
                $"Allowed paths must be {MaximumAllowedPathLength} characters or fewer."
            );
            return null;
        }

        if (
            path.StartsWith('/')
            || path.StartsWith('~')
            || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        )
        {
            errors.Add("allowedPaths", $"Allowed path '{value}' must be repository-relative.");
            return null;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == ".."))
        {
            errors.Add(
                "allowedPaths",
                $"Allowed path '{value}' cannot traverse outside the repository."
            );
            return null;
        }

        if (parts.Any(part => part.Any(char.IsControl) || part.Contains(':')))
        {
            errors.Add("allowedPaths", $"Allowed path '{value}' contains invalid characters.");
            return null;
        }

        var normalizedParts = parts.Where(part => part != ".").ToArray();
        if (normalizedParts.Length == 0)
        {
            errors.Add(
                "allowedPaths",
                "Allowed paths must identify a path below the repository root."
            );
            return null;
        }

        return string.Join('/', normalizedParts);
    }

    private enum CloneLocationKind
    {
        Invalid,
        Http,
        Ssh,
        Local,
    }

    private sealed class ValidationErrors
    {
        private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

        public bool Contains(string field) => _errors.ContainsKey(field);

        public void Add(string field, string error)
        {
            if (!_errors.TryGetValue(field, out var fieldErrors))
            {
                fieldErrors = [];
                _errors.Add(field, fieldErrors);
            }

            fieldErrors.Add(error);
        }

        public Dictionary<string, string[]> ToDictionary() =>
            _errors.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal
            );
    }
}

internal sealed record RepositoryProfileValidationResult(
    RepositoryProfile Profile,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}

internal sealed record KeyValidationResult(string Key, IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
