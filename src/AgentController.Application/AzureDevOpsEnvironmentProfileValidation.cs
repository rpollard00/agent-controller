using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Normalization and validation shared by managed Azure DevOps environment handlers.</summary>
internal static class AzureDevOpsEnvironmentProfileValidation
{
    private const int MaximumKeyLength = 128;
    private const int MaximumDisplayNameLength = 256;
    private const int MaximumOrganizationUrlLength = 2048;
    private const int MaximumProjectLength = 256;
    private const int MaximumWorkItemTypeLength = 128;
    private const int MaximumBoardValueLength = 256;
    private const int MaximumEnvironmentVariableLength = 256;

    public static AzureDevOpsEnvironmentProfileValidationResult ValidateAndNormalize(
        WorkSourceEnvironmentProfile profile
    )
    {
        var errors = new ValidationErrors();
        var key = NormalizeKey(profile.Key);
        ValidateKey(key, errors);

        var displayName = NormalizeText(profile.DisplayName);
        ValidateRequiredText(
            displayName,
            "displayName",
            "A display name is required.",
            MaximumDisplayNameLength,
            errors
        );

        var organizationUrl = NormalizeOrganizationUrl(profile.OrganizationUrl);
        ValidateOrganizationUrl(organizationUrl, errors);

        var project = NormalizeText(profile.Project);
        ValidateRequiredText(
            project,
            "project",
            "An Azure DevOps project is required.",
            MaximumProjectLength,
            errors
        );

        var provider = NormalizeText(profile.Provider);
        if (provider.Length == 0)
        {
            provider = "AzureDevOpsBoards";
        }

        var completedStates = NormalizeBoardValues(profile.CompletedStates, "completedStates", errors);

        var activeState = NormalizeOptionalBoardValue(profile.ActiveState, "activeState", errors);
        var completedState = NormalizeOptionalBoardValue(
            profile.CompletedState,
            "completedState",
            errors
        );

        if (
            activeState is not null
            && completedState is not null
            && string.Equals(activeState, completedState, StringComparison.OrdinalIgnoreCase)
        )
        {
            errors.Add(
                "completedState",
                "The completed state must be different from the active state."
            );
        }

        var tagPrefix = NormalizeTagPrefix(profile.TagPrefix, errors);

        var patEnvironmentVariable = NormalizeText(profile.PatEnvironmentVariable);
        ValidateEnvironmentVariableName(patEnvironmentVariable, errors);

        var normalized = profile with
        {
            Key = key,
            DisplayName = displayName,
            Provider = provider,
            OrganizationUrl = organizationUrl,
            Project = project,
            CompletedStates = completedStates,
            ActiveState = activeState,
            CompletedState = completedState,
            TagPrefix = tagPrefix,
            PatEnvironmentVariable = patEnvironmentVariable,
        };

        return new AzureDevOpsEnvironmentProfileValidationResult(normalized, errors.ToDictionary());
    }

    private static string NormalizeTagPrefix(string? value, ValidationErrors errors)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return "agent";
        }

        if (normalized.Length > 32)
        {
            errors.Add("tagPrefix", "The tag prefix must be 32 characters or fewer.");
        }

        if (
            normalized.Any(character =>
                character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '-'
            )
        )
        {
            errors.Add(
                "tagPrefix",
                "The tag prefix may contain only lowercase letters, numbers, and hyphens."
            );
        }

        return normalized;
    }

    public static AzureDevOpsEnvironmentKeyValidationResult ValidateAndNormalizeKey(string? value)
    {
        var errors = new ValidationErrors();
        var key = NormalizeKey(value);
        ValidateKey(key, errors);
        return new AzureDevOpsEnvironmentKeyValidationResult(key, errors.ToDictionary());
    }

    private static string NormalizeKey(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeText(string? value) => (value ?? string.Empty).Trim();

    private static string NormalizeOrganizationUrl(string? value) =>
        NormalizeText(value).TrimEnd('/');

    private static void ValidateKey(string key, ValidationErrors errors)
    {
        if (key.Length == 0)
        {
            errors.Add("key", "A key is required.");
            return;
        }

        if (key.Length > MaximumKeyLength)
        {
            errors.Add("key", $"The key must be {MaximumKeyLength} characters or fewer.");
        }

        if (!IsAsciiLetterOrDigit(key[0]) || !IsAsciiLetterOrDigit(key[^1]))
        {
            errors.Add("key", "The key must start and end with a letter or number.");
        }

        if (key.Any(character => !IsKeyCharacter(character)))
        {
            errors.Add(
                "key",
                "The key may contain only letters, numbers, periods, underscores, and hyphens."
            );
        }
    }

    private static bool IsKeyCharacter(char character) =>
        IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static void ValidateRequiredText(
        string value,
        string field,
        string requiredMessage,
        int maximumLength,
        ValidationErrors errors
    )
    {
        if (value.Length == 0)
        {
            errors.Add(field, requiredMessage);
            return;
        }

        ValidateText(value, field, maximumLength, errors);
    }

    private static void ValidateText(
        string value,
        string field,
        int maximumLength,
        ValidationErrors errors
    )
    {
        if (value.Length > maximumLength)
        {
            errors.Add(field, $"The value must be {maximumLength} characters or fewer.");
        }

        if (value.Any(char.IsControl))
        {
            errors.Add(field, "The value cannot contain control characters.");
        }
    }

    private static void ValidateOrganizationUrl(string organizationUrl, ValidationErrors errors)
    {
        if (organizationUrl.Length == 0)
        {
            errors.Add("organizationUrl", "An Azure DevOps organization URL is required.");
            return;
        }

        if (organizationUrl.Length > MaximumOrganizationUrlLength)
        {
            errors.Add(
                "organizationUrl",
                $"The organization URL must be {MaximumOrganizationUrlLength} characters or fewer."
            );
        }

        if (
            organizationUrl.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character)
            )
            || !Uri.TryCreate(organizationUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
        )
        {
            errors.Add(
                "organizationUrl",
                "The organization URL must be an absolute HTTP or HTTPS URL without credentials, a query, or a fragment."
            );
        }
    }

    private static List<string> NormalizeBoardValues(
        IReadOnlyList<string>? values,
        string field,
        ValidationErrors errors
    )
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var originalValue in values)
        {
            var value = NormalizeText(originalValue);
            if (value.Length == 0)
            {
                errors.Add(field, "Board values cannot be empty.");
                continue;
            }

            ValidateText(value, field, MaximumBoardValueLength, errors);
            if (seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private static string? NormalizeOptionalBoardValue(
        string? originalValue,
        string field,
        ValidationErrors errors
    )
    {
        var value = NormalizeText(originalValue);
        if (value.Length == 0)
        {
            return null;
        }

        ValidateText(value, field, MaximumBoardValueLength, errors);
        return value;
    }

    private static void ValidateNoOverlap(
        IReadOnlyList<string> eligible,
        IReadOnlyList<string> excluded,
        string field,
        string valueKind,
        ValidationErrors errors
    )
    {
        var eligibleValues = new HashSet<string>(eligible, StringComparer.OrdinalIgnoreCase);
        foreach (var value in excluded.Where(eligibleValues.Contains))
        {
            errors.Add(field, $"The {valueKind} '{value}' cannot be both eligible and excluded.");
        }
    }

    private static void ValidateEnvironmentVariableName(
        string environmentVariable,
        ValidationErrors errors
    )
    {
        if (environmentVariable.Length == 0)
        {
            errors.Add(
                "patEnvironmentVariable",
                "The name of an environment variable containing the PAT is required."
            );
            return;
        }

        if (environmentVariable.Length > MaximumEnvironmentVariableLength)
        {
            errors.Add(
                "patEnvironmentVariable",
                $"The environment-variable name must be {MaximumEnvironmentVariableLength} characters or fewer."
            );
        }

        if (
            !IsEnvironmentVariableStartCharacter(environmentVariable[0])
            || environmentVariable.Any(character => !IsEnvironmentVariableCharacter(character))
        )
        {
            errors.Add(
                "patEnvironmentVariable",
                "The PAT reference must be an environment-variable name containing only letters, numbers, and underscores, and it cannot start with a number."
            );
        }
    }

    private static bool IsEnvironmentVariableStartCharacter(char character) =>
        character is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsEnvironmentVariableCharacter(char character) =>
        IsEnvironmentVariableStartCharacter(character) || character is >= '0' and <= '9';

    private sealed class ValidationErrors
    {
        private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

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

internal sealed record AzureDevOpsEnvironmentProfileValidationResult(
    WorkSourceEnvironmentProfile Profile,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}

internal sealed record AzureDevOpsEnvironmentKeyValidationResult(
    string Key,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}
