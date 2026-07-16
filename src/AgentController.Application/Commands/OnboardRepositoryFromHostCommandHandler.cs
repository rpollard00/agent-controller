using AgentController.Application.Abstractions;
using AgentController.Application.Results;
using AgentController.Domain;

namespace AgentController.Application.Commands;

/// <summary>
/// Looks up a repository from a connected host and produces a draft RepositoryProfile
/// with CloneUrl, DefaultBranch, and Transport pre-filled from the host discovery data.
/// </summary>
public sealed class OnboardRepositoryFromHostCommandHandler(
    IRepositoryHostConnectionStore connectionStore,
    IRepositoryHostResolver hostResolver,
    IRepositoryStore repositoryStore,
    IRuntimeEnvironmentStore runtimeEnvironmentStore
) : ICommandHandler<OnboardRepositoryFromHostCommand, RepositoryOperationResult>
{
    private readonly IRepositoryHostConnectionStore _connectionStore = connectionStore;
    private readonly IRepositoryHostResolver _hostResolver = hostResolver;
    private readonly IRepositoryStore _repositoryStore = repositoryStore;
    private readonly IRuntimeEnvironmentStore _runtimeEnvironmentStore = runtimeEnvironmentStore;

    public async Task<RepositoryOperationResult> HandleAsync(
        OnboardRepositoryFromHostCommand command,
        CancellationToken cancellationToken
    )
    {
        // 1. Validate and resolve the connection key.
        var keyValidation = RepositoryHostConnectionProfileValidation.ValidateAndNormalizeKey(
            command.ConnectionKey
        );
        if (!keyValidation.IsValid)
        {
            return RepositoryOperationResult.ValidationFailed(keyValidation.Errors);
        }

        var profile = await _connectionStore.GetByKeyAsync(keyValidation.Key, cancellationToken);
        if (profile is null)
        {
            return RepositoryOperationResult.NotFound(
                $"Repository host connection '{keyValidation.Key}' was not found."
            );
        }

        // 2. Enumerate repositories from the host.
        var repositories = await _hostResolver.ListRepositoriesAsync(profile, cancellationToken);

        // 3. Find the selected repository by its provider-specific id.
        var selected = repositories.FirstOrDefault(
            r => string.Equals(r.Id, command.RepositoryId, StringComparison.OrdinalIgnoreCase)
        );
        if (selected is null)
        {
            return RepositoryOperationResult.NotFound(
                $"Repository '{command.RepositoryId}' was not found on the connected host."
            );
        }

        // 4. Build a draft RepositoryProfile from the discovered repository.
        var repositoryKey = DeriveRepositoryKey(command.RepositoryKey, selected);
        var transport = MapCloneTransportHint(selected.CloneTransportHint);

        var draftProfile = new RepositoryProfile
        {
            Key = repositoryKey,
            CloneUrl = selected.RemoteUrl,
            DefaultBranch = selected.DefaultBranch,
            Transport = transport,
            RepositoryHostConnectionKey = keyValidation.Key,
            RemoteIdentity = selected.Id,
            AllowedPaths = [],
        };

        // 5. Validate and normalize the draft profile.
        var validation = await RepositoryProfileValidation.ValidateAndNormalizeAsync(
            draftProfile,
            _runtimeEnvironmentStore,
            _connectionStore,
            cancellationToken
        );

        if (!validation.IsValid)
        {
            return RepositoryOperationResult.ValidationFailed(validation.Errors);
        }

        // 6. Persist the repository profile.
        var created = await _repositoryStore.CreateAsync(validation.Profile, cancellationToken);

        return created
            ? RepositoryOperationResult.Succeeded(validation.Profile)
            : RepositoryOperationResult.Conflict(
                $"Repository '{validation.Profile.Key}' already exists."
            );
    }

    private static string DeriveRepositoryKey(string? explicitKey, HostRepository hostRepo)
    {
        if (!string.IsNullOrWhiteSpace(explicitKey))
        {
            var normalized = explicitKey.Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(normalized)
                ? DeriveFromName(hostRepo.Name)
                : normalized;
        }

        return DeriveFromName(hostRepo.Name);
    }

    private static string DeriveFromName(string repoName)
    {
        // Derive a safe key from the repo name: lowercase, replace invalid chars with '-'.
        var normalized = repoName.Trim().ToLowerInvariant();
        var key = new string(
            normalized
                .Select(c => IsSafeKeyCharacter(c) ? c : '-')
                .ToArray()
        );

        // Collapse consecutive hyphens and trim leading/trailing hyphens.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(key, "-+", "-").Trim('-');

        // Fallback if the name was all invalid characters.
        return string.IsNullOrEmpty(collapsed) ? $"repo-{Guid.NewGuid():N}" : collapsed;
    }

    private static bool IsSafeKeyCharacter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '.' or '_' or '-';

    private static CloneTransport MapCloneTransportHint(CloneTransportHint hint) =>
        hint switch
        {
            CloneTransportHint.Ssh => CloneTransport.Ssh,
            CloneTransportHint.HttpsPat => CloneTransport.HttpsPat,
            _ => CloneTransport.Unspecified,
        };
}
