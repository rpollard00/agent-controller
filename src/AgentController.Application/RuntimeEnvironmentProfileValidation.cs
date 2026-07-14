using AgentController.Domain;

namespace AgentController.Application;

/// <summary>Normalization and validation shared by managed runtime environment handlers.</summary>
internal static class RuntimeEnvironmentProfileValidation
{
    private const string LocalWorkspaceProvider = "LocalWorkspace";
    private const string PiMateriaProvider = "PiMateria";
    private const string MockPiMateriaProvider = "MockPiMateria";
    // The runtime environment key is treated as the environment name: 1-32 characters,
    // the first an ASCII letter and the remainder ASCII letters, numbers, hyphens, or
    // underscores. The field keeps its internal API/storage role and edit immutability.
    private const int MaximumKeyLength = 32;
    private const int MaximumDisplayNameLength = 256;
    private const int MaximumProviderNameLength = 128;
    private const int MaximumPathLength = 2048;
    private const int MaximumLoadoutLength = 256;

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

        var runtimeSettings = profile.RuntimeSettings;
        if (runtimeSettings is null)
        {
            errors.Add("runtimeSettings", "Runtime-provider settings are required.");
            runtimeSettings = new RuntimeProviderSettings();
        }

        // Loadouts are a user-level, profile-specific control and remain per-profile.
        // Pi Materia process settings (executable, controller URL, PTY, env-var forwarding)
        // are controller-owned: accept legacy values in requests for compatibility, but do
        // not validate or persist them so stale stored overrides cannot alter execution.
        var loadouts = NormalizeLoadouts(runtimeSettings.Loadouts, errors);
        if (runtimeProvider == PiMateriaProvider && !loadouts.ContainsKey(ExecutionKind.NewWork))
        {
            errors.Add(
                "runtimeSettings.loadouts",
                "A NewWork loadout is required for the PiMateria runtime."
            );
        }

        var normalized = profile with
        {
            Key = key,
            DisplayName = displayName,
            EnvironmentProvider = environmentProvider,
            EnvironmentSettings = new EnvironmentProviderSettings { WorkspaceRoot = workspaceRoot },
            RuntimeProvider = runtimeProvider,
            RuntimeSettings = new RuntimeProviderSettings { Loadouts = loadouts },
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
            errors.Add("key", "An environment name is required.");
            return;
        }

        if (key.Length > MaximumKeyLength)
        {
            errors.Add(
                "key",
                $"The environment name must be {MaximumKeyLength} characters or fewer."
            );
        }

        if (!IsAsciiLetter(key[0]))
        {
            errors.Add("key", "The environment name must start with a letter.");
        }

        if (key.Any(character => !IsEnvironmentNameCharacter(character)))
        {
            errors.Add(
                "key",
                "The environment name may contain only letters, numbers, hyphens, and underscores."
            );
        }
    }

    private static bool IsEnvironmentNameCharacter(char character) =>
        IsAsciiLetterOrDigit(character) || character is '_' or '-';

    private static bool IsAsciiLetter(char character) => character is >= 'a' and <= 'z';

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

    private static Dictionary<ExecutionKind, string> NormalizeLoadouts(
        IReadOnlyDictionary<ExecutionKind, string>? loadouts,
        ValidationErrors errors
    )
    {
        var normalized = new Dictionary<ExecutionKind, string>();
        if (loadouts is null)
        {
            errors.Add("runtimeSettings.loadouts", "A loadout map is required.");
            return normalized;
        }

        foreach (var (executionKind, originalLoadout) in loadouts.OrderBy(pair => pair.Key))
        {
            if (!Enum.IsDefined(executionKind))
            {
                errors.Add(
                    "runtimeSettings.loadouts",
                    $"Execution kind '{executionKind}' is not supported."
                );
                continue;
            }

            var loadout = NormalizeText(originalLoadout);
            if (loadout.Length == 0)
            {
                errors.Add(
                    "runtimeSettings.loadouts",
                    $"The {executionKind} loadout cannot be empty."
                );
                continue;
            }

            ValidateText(loadout, "runtimeSettings.loadouts", MaximumLoadoutLength, errors);
            normalized.Add(executionKind, loadout);
        }

        return normalized;
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
