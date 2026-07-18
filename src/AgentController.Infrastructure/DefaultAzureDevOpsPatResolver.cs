using AgentController.Domain.Secrets;

namespace AgentController.Infrastructure;

/// <summary>
/// Default concrete implementation of <see cref="AzureDevOpsPatResolver"/>.
/// Routes resolution through <see cref="ISecretStore"/> for named,
/// envelope-encrypted secrets.
/// </summary>
internal sealed class DefaultAzureDevOpsPatResolver(ISecretStore secretStore)
    : AzureDevOpsPatResolver
{
    public override async Task<string?> ResolveFromSecretReferenceAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        if (!reference.IsSpecified)
        {
            return null;
        }

        var payload = await secretStore.ResolveAsync(
            reference.Name,
            reference.Version,
            cancellationToken);

        return payload is PersonalAccessTokenPayload pat ? pat.Value : null;
    }
}
