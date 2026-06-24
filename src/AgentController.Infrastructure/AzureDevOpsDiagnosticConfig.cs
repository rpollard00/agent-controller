using AgentController.Application.Abstractions;
using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure;

/// <summary>
/// Bridges <see cref="WorkSourceOptions"/> and <see cref="AzureDevOpsBoardsOptions"/>
/// into the <see cref="IAzureDevOpsDiagnosticConfig"/> abstraction used by the
/// Application-layer diagnostic query handler.
/// </summary>
internal sealed class AzureDevOpsDiagnosticConfig(
        IOptions<WorkSourceOptions> workSourceOpts,
        IOptions<AzureDevOpsBoardsOptions> boardsOpts)
    : IAzureDevOpsDiagnosticConfig
{
    public string? OrganizationUrl => workSourceOpts.Value.OrganizationUrl;

    public string? Project => workSourceOpts.Value.Project;

    public string? ResolvePersonalAccessToken()
        => boardsOpts.Value.ResolvePersonalAccessToken();
}
