using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Normalization and validation shared by managed work source environment handlers.</summary>
internal static class WorkSourceEnvironmentProfileValidation
{
    private const int MaximumKeyLength = 128;
    private const int MaximumDisplayNameLength = 256;
    private const int MaximumOrganizationUrlLength = 2048;
    private const int MaximumProjectLength = 256;
    private const int MaximumBoardValueLength = 256;
    private const int MaximumSecretNameLength = 256;

    public static WorkSourceEnvironmentProfileValidationResult ValidateAndNormalize(
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

        var secretRef = NormalizeSecretReference(
            profile.PersonalAccessTokenReference,
            errors);

        // Validate that a secret reference is specified.
        if (!secretRef.IsSpecified)
        {
            errors.Add(
                "personalAccessTokenReference",
                "A secret reference for the PAT is required."
            );
        }

        var normalized = profile with
        {
            Key = key,
            DisplayName = displayName,
            Provider = provider,
            OrganizationUrl = organizationUrl,
            Project = project,
            ActiveState = activeState,
            CompletedState = completedState,
            TagPrefix = tagPrefix,
            PersonalAccessTokenReference = secretRef,
        };

        return new WorkSourceEnvironmentProfileValidationResult(normalized, errors.ToDictionary());
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

    public static WorkSourceEnvironmentKeyValidationResult ValidateAndNormalizeKey(string? value)
    {
        var errors = new ValidationErrors();
        var key = NormalizeKey(value);
        ValidateKey(key, errors);
        return new WorkSourceEnvironmentKeyValidationResult(key, errors.ToDictionary());
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

    private static Domain.Secrets.SecretReference NormalizeSecretReference(
        Domain.Secrets.SecretReference reference,
        ValidationErrors errors)
    {
        var name = (reference.Name ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return Domain.Secrets.SecretReference.Empty;
        }

        if (name.Length > MaximumSecretNameLength)
        {
            errors.Add(
                "personalAccessTokenReference",
                $"The secret name must be {MaximumSecretNameLength} characters or fewer."
            );
        }

        if (name.Any(char.IsControl))
        {
            errors.Add(
                "personalAccessTokenReference",
                "The secret name cannot contain control characters."
            );
        }

        var version = reference.Version;
        if (version.HasValue && version.Value < 1)
        {
            errors.Add(
                "personalAccessTokenReference",
                "The secret version must be 1 or greater."
            );
            version = null;
        }

        return new Domain.Secrets.SecretReference { Name = name, Version = version };
    }

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

internal sealed record WorkSourceEnvironmentProfileValidationResult(
    WorkSourceEnvironmentProfile Profile,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}

internal sealed record WorkSourceEnvironmentKeyValidationResult(
    string Key,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}
