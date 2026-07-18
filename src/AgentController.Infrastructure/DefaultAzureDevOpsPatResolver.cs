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
    public override Task<string?> ResolveFromSecretReferenceAsync(
        SecretReference reference,
        CancellationToken cancellationToken
    )
    {
        if (!reference.IsSpecified)
        {
            return Task.FromResult<string?>(null);
        }

        return secretStore.ResolveAsync(
            reference.Name,
            reference.Version,
            cancellationToken);
    }
}
