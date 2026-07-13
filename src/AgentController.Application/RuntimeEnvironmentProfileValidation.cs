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
    private const int MaximumUrlLength = 2048;
    private const int MaximumPtyArgumentsLength = 4096;
    private const int MaximumLoadoutLength = 256;
    private const int MaximumEnvironmentVariableLength = 256;

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

        var piExecutablePath = NormalizeOptionalText(runtimeSettings.PiExecutablePath);
        var controllerBaseUrl = NormalizeUrl(runtimeSettings.ControllerBaseUrl);
        var ptyWrapperPath = NormalizeOptionalText(runtimeSettings.PtyWrapperPath);
        var ptyWrapperArgs = ptyWrapperPath is null
            ? null
            : NormalizeOptionalText(runtimeSettings.PtyWrapperArgs);

        ValidateOptionalText(
            piExecutablePath,
            "runtimeSettings.piExecutablePath",
            MaximumPathLength,
            errors
        );
        ValidateControllerBaseUrl(controllerBaseUrl, runtimeProvider, errors);
        ValidateOptionalText(
            ptyWrapperPath,
            "runtimeSettings.ptyWrapperPath",
            MaximumPathLength,
            errors
        );
        ValidateOptionalText(
            ptyWrapperArgs,
            "runtimeSettings.ptyWrapperArgs",
            MaximumPtyArgumentsLength,
            errors
        );

        if (runtimeProvider == PiMateriaProvider && piExecutablePath is null)
        {
            errors.Add(
                "runtimeSettings.piExecutablePath",
                "A pi executable path or command is required for the PiMateria runtime."
            );
        }

        var loadouts = NormalizeLoadouts(runtimeSettings.Loadouts, errors);
        if (runtimeProvider == PiMateriaProvider && !loadouts.ContainsKey(ExecutionKind.NewWork))
        {
            errors.Add(
                "runtimeSettings.loadouts",
                "A NewWork loadout is required for the PiMateria runtime."
            );
        }

        var forwardedVariables = NormalizeEnvironmentVariableMappings(
            runtimeSettings.ForwardEnvironmentVariables,
            errors
        );

        var normalized = profile with
        {
            Key = key,
            DisplayName = displayName,
            EnvironmentProvider = environmentProvider,
            EnvironmentSettings = new EnvironmentProviderSettings { WorkspaceRoot = workspaceRoot },
            RuntimeProvider = runtimeProvider,
            RuntimeSettings = new RuntimeProviderSettings
            {
                PiExecutablePath = piExecutablePath,
                ControllerBaseUrl = controllerBaseUrl,
                PtyWrapperPath = ptyWrapperPath,
                PtyWrapperArgs = ptyWrapperArgs,
                Loadouts = loadouts,
                ForwardEnvironmentVariables = forwardedVariables,
            },
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

    private static string? NormalizeUrl(string? value) =>
        NormalizeOptionalText(value)?.TrimEnd('/');

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

    private static void ValidateControllerBaseUrl(
        string? controllerBaseUrl,
        string runtimeProvider,
        ValidationErrors errors
    )
    {
        if (controllerBaseUrl is null)
        {
            if (runtimeProvider == PiMateriaProvider)
            {
                errors.Add(
                    "runtimeSettings.controllerBaseUrl",
                    "A controller base URL is required for the PiMateria runtime."
                );
            }

            return;
        }

        if (controllerBaseUrl.Length > MaximumUrlLength)
        {
            errors.Add(
                "runtimeSettings.controllerBaseUrl",
                $"The controller base URL must be {MaximumUrlLength} characters or fewer."
            );
        }

        if (
            controllerBaseUrl.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character)
            )
            || !Uri.TryCreate(controllerBaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
        )
        {
            errors.Add(
                "runtimeSettings.controllerBaseUrl",
                "The controller base URL must be an absolute HTTP or HTTPS URL without credentials, a query, or a fragment."
            );
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

    private static Dictionary<string, string> NormalizeEnvironmentVariableMappings(
        IReadOnlyDictionary<string, string>? mappings,
        ValidationErrors errors
    )
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mappings is null)
        {
            errors.Add(
                "runtimeSettings.forwardEnvironmentVariables",
                "An environment-variable forwarding map is required."
            );
            return normalized;
        }

        foreach (var (originalTarget, originalSource) in mappings)
        {
            var target = NormalizeText(originalTarget);
            var source = NormalizeText(originalSource);
            var mappingIsValid = true;

            if (!IsEnvironmentVariableName(target))
            {
                errors.Add(
                    "runtimeSettings.forwardEnvironmentVariables",
                    $"Target '{target}' must be an environment-variable name containing only letters, numbers, and underscores, and it cannot start with a number."
                );
                mappingIsValid = false;
            }

            if (!IsEnvironmentVariableName(source))
            {
                errors.Add(
                    "runtimeSettings.forwardEnvironmentVariables",
                    $"The source for target '{target}' must be an environment-variable name, not a credential value."
                );
                mappingIsValid = false;
            }

            if (target.Length > MaximumEnvironmentVariableLength)
            {
                errors.Add(
                    "runtimeSettings.forwardEnvironmentVariables",
                    $"Target environment-variable names must be {MaximumEnvironmentVariableLength} characters or fewer."
                );
                mappingIsValid = false;
            }

            if (source.Length > MaximumEnvironmentVariableLength)
            {
                errors.Add(
                    "runtimeSettings.forwardEnvironmentVariables",
                    $"Source environment-variable names must be {MaximumEnvironmentVariableLength} characters or fewer."
                );
                mappingIsValid = false;
            }

            if (mappingIsValid && !normalized.TryAdd(target, source))
            {
                errors.Add(
                    "runtimeSettings.forwardEnvironmentVariables",
                    $"Target environment variable '{target}' is mapped more than once."
                );
            }
        }

        return normalized
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static bool IsEnvironmentVariableName(string value) =>
        value.Length > 0
        && IsEnvironmentVariableStartCharacter(value[0])
        && value.All(IsEnvironmentVariableCharacter);

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
