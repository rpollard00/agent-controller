using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Normalization and validation shared by unified connection handlers.</summary>
internal static class ConnectionProfileValidation
{
    private const int MaximumKeyLength = 128;
    private const int MaximumDisplayNameLength = 256;
    private const int MaximumProviderLength = 64;
    private const int MaximumOrganizationUrlLength = 1024;

    public static ConnectionProfileValidationResult ValidateAndNormalize(
        ConnectionProfile profile
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

        var provider = NormalizeText(profile.Provider);
        ValidateRequiredText(
            provider,
            "provider",
            "A provider is required.",
            MaximumProviderLength,
            errors
        );

        // Validate provider-specific settings if present.
        if (profile.ProviderSettings is not null)
        {
            ValidateProviderSettings(profile.ProviderSettings, provider, errors);
        }

        var normalized = profile with
        {
            Key = key,
            DisplayName = displayName,
            Provider = provider,
        };

        return new ConnectionProfileValidationResult(normalized, errors.ToDictionary());
    }

    private static void ValidateProviderSettings(
        ConnectionSettings settings,
        string provider,
        ValidationErrors errors
    )
    {
        switch (settings)
        {
            case AzureDevOpsConnectionSettings adoSettings:
                var orgUrl = NormalizeText(adoSettings.OrganizationUrl);
                ValidateRequiredText(
                    orgUrl,
                    "providerSettings.organizationUrl",
                    "An organization URL is required for Azure DevOps connections.",
                    MaximumOrganizationUrlLength,
                    errors
                );
                break;
        }
    }

    public static ConnectionKeyValidationResult ValidateAndNormalizeKey(string? value)
    {
        var errors = new ValidationErrors();
        var key = NormalizeKey(value);
        ValidateKey(key, errors);
        return new ConnectionKeyValidationResult(key, errors.ToDictionary());
    }

    private static string NormalizeKey(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

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

    private static string NormalizeText(string? value) => (value ?? string.Empty).Trim();

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

internal sealed record ConnectionKeyValidationResult(
    string Key,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}

internal sealed record ConnectionProfileValidationResult(
    ConnectionProfile Profile,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}
