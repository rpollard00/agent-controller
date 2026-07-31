using System.Net.Mail;
using AgentController.Application.Abstractions;
using AgentController.Domain;

namespace AgentController.Application;

/// <summary>
/// Reviewer identity policy for Azure DevOps Repos.
/// Azure DevOps exposes the same person through a uniqueName (email-like value),
/// an identity GUID, and a graph descriptor.
/// </summary>
public sealed class AzureDevOpsReviewerIdentityPolicy : IReviewerIdentityPolicy
{
    /// <summary>Provider discriminator used by unified connection profiles.</summary>
    public const string ProviderKey = "AzureDevOps";

    /// <summary>Canonical identity kind for Azure DevOps uniqueName values.</summary>
    public const string EmailKind = "email";

    /// <summary>Canonical identity kind for Azure DevOps identity GUID values.</summary>
    public const string IdentityIdKind = "identityId";

    /// <summary>Canonical identity kind for Azure DevOps graph descriptors.</summary>
    public const string DescriptorKind = "descriptor";

    private static readonly IReadOnlyList<ReviewerIdentityKindDefinition> Kinds =
    [
        new()
        {
            Kind = EmailKind,
            Label = "Email / uniqueName",
            Hint = "The Azure DevOps uniqueName, usually an email address.",
            Placeholder = "reviewer@example.com",
            ValidationCategory = ReviewerIdentityValidationCategory.Email,
        },
        new()
        {
            Kind = IdentityIdKind,
            Label = "Identity ID",
            Hint = "The Azure DevOps identity GUID.",
            Placeholder = "00000000-0000-0000-0000-000000000000",
            ValidationCategory = ReviewerIdentityValidationCategory.Guid,
        },
        new()
        {
            Kind = DescriptorKind,
            Label = "Graph descriptor",
            Hint = "The opaque Azure DevOps graph descriptor.",
            Placeholder = "aad.YWJj",
            ValidationCategory = ReviewerIdentityValidationCategory.Opaque,
        },
    ];

    private static readonly Dictionary<string, string> KindAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EmailKind] = EmailKind,
            ["uniqueName"] = EmailKind,
            ["unique-name"] = EmailKind,
            [IdentityIdKind] = IdentityIdKind,
            ["id"] = IdentityIdKind,
            ["identity-id"] = IdentityIdKind,
            ["guid"] = IdentityIdKind,
            [DescriptorKind] = DescriptorKind,
            ["graphDescriptor"] = DescriptorKind,
            ["graph-descriptor"] = DescriptorKind,
        };

    /// <inheritdoc />
    public string Provider => ProviderKey;

    /// <inheritdoc />
    public IReadOnlyList<ReviewerIdentityKindDefinition> SupportedIdentityKinds => Kinds;

    /// <inheritdoc />
    public IReadOnlyList<ReviewerIdentityKindDefinition> SupportedKinds => Kinds;

    /// <inheritdoc />
    public ReviewerIdentityValidationResult ValidateAndNormalize(ReviewerIdentity identity)
    {
        var errors = new List<string>();
        var kind = NormalizeKind(identity.Kind);
        var value = identity.Value?.Trim() ?? string.Empty;

        if (kind is null)
        {
            errors.Add(
                $"The identity kind must be one of: {string.Join(", ", Kinds.Select(candidate => candidate.Kind))}."
            );
        }

        if (value.Length == 0)
        {
            errors.Add("The identity value is required.");
        }
        else if (value.Any(char.IsControl))
        {
            errors.Add("The identity value cannot contain control characters.");
        }

        var normalizedValue = value;
        switch (kind)
        {
            case EmailKind:
                if (!IsEmailLike(value))
                {
                    errors.Add("The email / uniqueName value must be a valid email-like address.");
                }
                else
                {
                    normalizedValue = value.ToLowerInvariant();
                }
                break;

            case IdentityIdKind:
                if (!Guid.TryParse(value, out var identityId))
                {
                    errors.Add("The identity ID must be a valid GUID.");
                }
                else
                {
                    normalizedValue = identityId.ToString("D");
                }
                break;

            case DescriptorKind:
                if (value.Any(char.IsWhiteSpace))
                {
                    errors.Add("The graph descriptor cannot contain whitespace.");
                }
                break;
        }

        if (value.Length > 1024)
        {
            errors.Add("The identity value must be 1024 characters or fewer.");
        }

        if (errors.Count > 0 || kind is null)
        {
            return new ReviewerIdentityValidationResult { Errors = errors };
        }

        return new ReviewerIdentityValidationResult
        {
            NormalizedIdentity = new ReviewerIdentity { Kind = kind, Value = normalizedValue },
        };
    }

    /// <inheritdoc />
    public bool Matches(ReviewerIdentity configured, ReviewerIdentity author)
    {
        var configuredResult = ValidateAndNormalize(configured);
        var authorResult = ValidateAndNormalize(author);
        if (!configuredResult.IsValid || !authorResult.IsValid)
        {
            return false;
        }

        var configuredIdentity = configuredResult.NormalizedIdentity!;
        var authorIdentity = authorResult.NormalizedIdentity!;
        if (!string.Equals(configuredIdentity.Kind, authorIdentity.Kind, StringComparison.Ordinal))
        {
            return false;
        }

        return configuredIdentity.Kind switch
        {
            EmailKind or IdentityIdKind => string.Equals(
                configuredIdentity.Value,
                authorIdentity.Value,
                StringComparison.OrdinalIgnoreCase
            ),
            DescriptorKind => string.Equals(
                configuredIdentity.Value,
                authorIdentity.Value,
                StringComparison.Ordinal
            ),
            _ => false,
        };
    }

    private static string? NormalizeKind(string? value)
    {
        var kind = value?.Trim();
        return kind is not null && KindAliases.TryGetValue(kind, out var canonical)
            ? canonical
            : null;
    }

    private static bool IsEmailLike(string value)
    {
        if (value.Length == 0 || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        try
        {
            var address = new MailAddress(value);
            return string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase)
                && value.Contains('@', StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Canonical Azure DevOps reviewer identity kind constants.</summary>
public static class AzureDevOpsReviewerIdentityKinds
{
    /// <summary>Azure DevOps uniqueName/email identity.</summary>
    public const string Email = AzureDevOpsReviewerIdentityPolicy.EmailKind;

    /// <summary>Azure DevOps identity GUID.</summary>
    public const string IdentityId = AzureDevOpsReviewerIdentityPolicy.IdentityIdKind;

    /// <summary>Azure DevOps graph descriptor.</summary>
    public const string Descriptor = AzureDevOpsReviewerIdentityPolicy.DescriptorKind;

    /// <summary>Alias for <see cref="Email"/>.</summary>
    public const string UniqueName = Email;

    /// <summary>Alias for <see cref="IdentityId"/>.</summary>
    public const string Id = IdentityId;

    /// <summary>Alias for <see cref="Descriptor"/>.</summary>
    public const string GraphDescriptor = Descriptor;
}
