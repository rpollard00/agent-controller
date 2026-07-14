using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Normalization and validation shared by managed runtime environment handlers.</summary>
internal static class RuntimeEnvironmentProfileValidation
{
    private const string LocalWorkspaceProvider = "LocalWorkspace";
    private const string PiMateriaProvider = "PiMateria";
    private const string MockPiMateriaProvider = "MockPiMateria";
    private const int MaximumKeyLength = 128;
    private const int MaximumDisplayNameLength = 256;
    private const int MaximumProviderNameLength = 128;
    private const int MaximumPathLength = 2048;

    public static RuntimeEnvironmentProfileValidationResult ValidateAndNormalize(
        RuntimeEnvironmentProfile profile
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

        var environmentProvider = NormalizeEnvironmentProvider(profile.EnvironmentProvider);
        ValidateEnvironmentProvider(environmentProvider, errors);

        var runtimeProvider = NormalizeRuntimeProvider(profile.RuntimeProvider);
        ValidateRuntimeProvider(runtimeProvider, errors);

        var environmentSettings = profile.EnvironmentSettings;
        if (environmentSettings is null)
        {
            errors.Add("environmentSettings", "Environment-provider settings are required.");
            environmentSettings = new EnvironmentProviderSettings();
        }

        var workspaceRoot = NormalizeOptionalText(environmentSettings.WorkspaceRoot);
        ValidateOptionalText(
            workspaceRoot,
            "environmentSettings.workspaceRoot",
            MaximumPathLength,
            errors
        );

        // Pi Materia process configuration belongs to the controller. Accept legacy
        // settings in requests for compatibility, but do not validate or persist them.
        var normalized = profile with
        {
            Key = key,
            DisplayName = displayName,
            EnvironmentProvider = environmentProvider,
            EnvironmentSettings = new EnvironmentProviderSettings { WorkspaceRoot = workspaceRoot },
            RuntimeProvider = runtimeProvider,
            RuntimeSettings = new RuntimeProviderSettings(),
        };

        return new RuntimeEnvironmentProfileValidationResult(normalized, errors.ToDictionary());
    }

    public static RuntimeEnvironmentKeyValidationResult ValidateAndNormalizeKey(string? value)
    {
        var errors = new ValidationErrors();
        var key = NormalizeKey(value);
        ValidateKey(key, errors);
        return new RuntimeEnvironmentKeyValidationResult(key, errors.ToDictionary());
    }

    private static string NormalizeKey(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeText(string? value) => (value ?? string.Empty).Trim();

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = NormalizeText(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string NormalizeEnvironmentProvider(string? value)
    {
        var provider = NormalizeText(value);
        return provider.Equals(LocalWorkspaceProvider, StringComparison.OrdinalIgnoreCase)
            ? LocalWorkspaceProvider
            : provider;
    }

    private static string NormalizeRuntimeProvider(string? value)
    {
        var provider = NormalizeText(value);
        if (provider.Equals(PiMateriaProvider, StringComparison.OrdinalIgnoreCase))
        {
            return PiMateriaProvider;
        }

        return provider.Equals(MockPiMateriaProvider, StringComparison.OrdinalIgnoreCase)
            ? MockPiMateriaProvider
            : provider;
    }

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

    private static void ValidateEnvironmentProvider(string provider, ValidationErrors errors)
    {
        if (provider.Length == 0)
        {
            errors.Add("environmentProvider", "An environment provider is required.");
            return;
        }

        if (provider.Length > MaximumProviderNameLength)
        {
            errors.Add(
                "environmentProvider",
                $"The environment provider must be {MaximumProviderNameLength} characters or fewer."
            );
        }

        if (provider != LocalWorkspaceProvider)
        {
            errors.Add(
                "environmentProvider",
                $"Environment provider '{provider}' is not supported. Supported providers: {LocalWorkspaceProvider}."
            );
        }
    }

    private static void ValidateRuntimeProvider(string provider, ValidationErrors errors)
    {
        if (provider.Length == 0)
        {
            errors.Add("runtimeProvider", "A runtime provider is required.");
            return;
        }

        if (provider.Length > MaximumProviderNameLength)
        {
            errors.Add(
                "runtimeProvider",
                $"The runtime provider must be {MaximumProviderNameLength} characters or fewer."
            );
        }

        if (provider is not PiMateriaProvider and not MockPiMateriaProvider)
        {
            errors.Add(
                "runtimeProvider",
                $"Runtime provider '{provider}' is not supported. Supported providers: {PiMateriaProvider}, {MockPiMateriaProvider}."
            );
        }
    }

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

    private static void ValidateOptionalText(
        string? value,
        string field,
        int maximumLength,
        ValidationErrors errors
    )
    {
        if (value is not null)
        {
            ValidateText(value, field, maximumLength, errors);
        }
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

internal sealed record RuntimeEnvironmentProfileValidationResult(
    RuntimeEnvironmentProfile Profile,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}

internal sealed record RuntimeEnvironmentKeyValidationResult(
    string Key,
    IReadOnlyDictionary<string, string[]> Errors
)
{
    public bool IsValid => Errors.Count == 0;
}
