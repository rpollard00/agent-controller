using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Normalization and validation shared by managed repository host connection handlers.</summary>
internal static class RepositoryHostConnectionProfileValidation
{
    private const int MaximumKeyLength = 128;
    private const int MaximumDisplayNameLength = 256;
    private const int MaximumOrganizationUrlLength = 2048;
    private const int MaximumProjectLength = 256;
    private const int MaximumSecretReferenceNameLength = 256;

    public static RepositoryHostConnectionProfileValidationResult ValidateAndNormalize(
        RepositoryHostConnectionProfile profile
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
            provider = "AzureDevOpsRepos";
        }

        var secretRef = NormalizeSecretReference(profile.PersonalAccessTokenReference, errors);

        var normalized = profile with
        {
            Key = key,
            DisplayName = displayName,
            Provider = provider,
            OrganizationUrl = organizationUrl,
            Project = project,
            PersonalAccessTokenReference = secretRef,
        };

        return new RepositoryHostConnectionProfileValidationResult(normalized, errors.ToDictionary());
    }

    public static RepositoryHostConnectionKeyValidationResult ValidateAndNormalizeKey(string? value)
    {
        var errors = new ValidationErrors();
        var key = NormalizeKey(value);
        ValidateKey(key, errors);
        return new RepositoryHostConnectionKeyValidationResult(key, errors.ToDictionary());
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

    private static Domain.Secrets.SecretReference NormalizeSecretReference(
        Domain.Secrets.SecretReference reference,
        ValidationErrors errors
    )
    {
        var name = NormalizeText(reference?.Name ?? string.Empty);

        if (name.Length == 0)
        {
            errors.Add(
                "personalAccessTokenReference.name",
                "A named secret reference is required (e.g. 'ado-repos-pat')."
            );
        }
        else if (name.Length > MaximumSecretReferenceNameLength)
        {
            errors.Add(
                "personalAccessTokenReference.name",
                $"The secret reference name must be {MaximumSecretReferenceNameLength} characters or fewer."
            );
        }

        return Domain.Secrets.SecretReference.ByName(name);
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

internal sealed record RepositoryHostConnectionProfileValidationResult(
    RepositoryHostConnectionProfile Profile,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}

internal sealed record RepositoryHostConnectionKeyValidationResult(
    string Key,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}
