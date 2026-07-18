using System.Text.Json.Serialization;
using AgentController.Application.Abstractions;
using AgentController.Application.Commands;
using AgentController.Application.Queries;
using AgentController.Application.Results;
using AgentController.Domain;
using AgentController.Domain.Secrets;

namespace AgentController.Api;

/// <summary>
/// Maps the web UI API for managed repository, work source, and runtime environment profiles.
/// Endpoints delegate all validation and relationship behavior to application handlers.
/// </summary>
public static class WebUiControllers
{
    private const string RepositoriesPath = "/api/webui/repositories";
    private const string WorkSourceEnvironmentsPath = "/api/webui/work-source-environments";
    private const string RuntimeEnvironmentsPath = "/api/webui/runtime-environments";
    private const string SecretsPath = "/api/webui/secrets";

    public static IEndpointRouteBuilder MapWebUiControllers(this IEndpointRouteBuilder app)
    {
        MapRepositoryControllers(app.MapGroup(RepositoriesPath));
        MapWorkSourceEnvironmentControllers(app.MapGroup(WorkSourceEnvironmentsPath));
        MapRuntimeEnvironmentControllers(app.MapGroup(RuntimeEnvironmentsPath));
        MapSecretsControllers(app.MapGroup(SecretsPath));
        return app;
    }

    private static void MapRepositoryControllers(RouteGroupBuilder group)
    {
        group.MapPost(
            "",
            async (
                RepositoryProfile profile,
                ICommandHandler<CreateRepositoryCommand, RepositoryOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new CreateRepositoryCommand(profile),
                    cancellationToken
                );
                return MapResult(
                    result,
                    repository =>
                        Results.Created(
                            $"{RepositoriesPath}/{Uri.EscapeDataString(repository!.Key)}",
                            repository
                        )
                );
            }
        );

        group.MapGet(
            "",
            async (
                IQueryHandler<ListRepositoriesQuery, IReadOnlyList<RepositoryProfile>> handler,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    await handler.ExecuteAsync(new ListRepositoriesQuery(), cancellationToken)
                )
        );

        group.MapGet(
            "/{key}",
            async (
                string key,
                IQueryHandler<GetRepositoryByKeyQuery, RepositoryOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new GetRepositoryByKeyQuery(key),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapPut(
            "/{key}",
            async (
                string key,
                RepositoryProfile profile,
                ICommandHandler<UpdateRepositoryCommand, RepositoryOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new UpdateRepositoryCommand(key, profile),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapDelete(
            "/{key}",
            async (
                string key,
                ICommandHandler<DeleteRepositoryCommand, RepositoryOperationResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new DeleteRepositoryCommand(key),
                    cancellationToken
                );
                return MapResult(result, _ => Results.NoContent());
            }
        );
    }

    private static void MapWorkSourceEnvironmentControllers(RouteGroupBuilder group)
    {
        group.MapPost(
            "",
            async (
                WorkSourceEnvironmentProfile profile,
                ICommandHandler<
                    CreateWorkSourceEnvironmentCommand,
                    WorkSourceEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new CreateWorkSourceEnvironmentCommand(profile),
                    cancellationToken
                );
                return MapResult(
                    result,
                    environment =>
                        Results.Created(
                            $"{WorkSourceEnvironmentsPath}/{Uri.EscapeDataString(environment!.Key)}",
                            environment
                        )
                );
            }
        );

        group.MapGet(
            "",
            async (
                IQueryHandler<
                    ListWorkSourceEnvironmentsQuery,
                    IReadOnlyList<WorkSourceEnvironmentProfile>
                > handler,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    await handler.ExecuteAsync(
                        new ListWorkSourceEnvironmentsQuery(),
                        cancellationToken
                    )
                )
        );

        group.MapGet(
            "/{key}",
            async (
                string key,
                IQueryHandler<
                    GetWorkSourceEnvironmentByKeyQuery,
                    WorkSourceEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new GetWorkSourceEnvironmentByKeyQuery(key),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapPut(
            "/{key}",
            async (
                string key,
                WorkSourceEnvironmentProfile profile,
                ICommandHandler<
                    UpdateWorkSourceEnvironmentCommand,
                    WorkSourceEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new UpdateWorkSourceEnvironmentCommand(key, profile),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapDelete(
            "/{key}",
            async (
                string key,
                ICommandHandler<
                    DeleteWorkSourceEnvironmentCommand,
                    WorkSourceEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new DeleteWorkSourceEnvironmentCommand(key),
                    cancellationToken
                );
                return MapResult(result, _ => Results.NoContent());
            }
        );

    }

    private static void MapRuntimeEnvironmentControllers(RouteGroupBuilder group)
    {
        group.MapPost(
            "",
            async (
                RuntimeEnvironmentProfile profile,
                ICommandHandler<
                    CreateRuntimeEnvironmentCommand,
                    RuntimeEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new CreateRuntimeEnvironmentCommand(profile),
                    cancellationToken
                );
                return MapResult(
                    result,
                    environment =>
                        Results.Created(
                            $"{RuntimeEnvironmentsPath}/{Uri.EscapeDataString(environment!.Key)}",
                            environment
                        )
                );
            }
        );

        group.MapGet(
            "",
            async (
                IQueryHandler<
                    ListRuntimeEnvironmentsQuery,
                    IReadOnlyList<RuntimeEnvironmentProfile>
                > handler,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    await handler.ExecuteAsync(
                        new ListRuntimeEnvironmentsQuery(),
                        cancellationToken
                    )
                )
        );

        group.MapGet(
            "/{key}",
            async (
                string key,
                IQueryHandler<
                    GetRuntimeEnvironmentByKeyQuery,
                    RuntimeEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.ExecuteAsync(
                    new GetRuntimeEnvironmentByKeyQuery(key),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapPut(
            "/{key}",
            async (
                string key,
                RuntimeEnvironmentProfile profile,
                ICommandHandler<
                    UpdateRuntimeEnvironmentCommand,
                    RuntimeEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new UpdateRuntimeEnvironmentCommand(key, profile),
                    cancellationToken
                );
                return MapResult(result, Results.Ok);
            }
        );

        group.MapDelete(
            "/{key}",
            async (
                string key,
                ICommandHandler<
                    DeleteRuntimeEnvironmentCommand,
                    RuntimeEnvironmentOperationResult
                > handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new DeleteRuntimeEnvironmentCommand(key),
                    cancellationToken
                );
                return MapResult(result, _ => Results.NoContent());
            }
        );
    }

    private static IResult MapResult(
        RepositoryOperationResult result,
        Func<RepositoryProfile?, IResult> onSuccess
    ) =>
        result.Status switch
        {
            RepositoryOperationStatus.Succeeded => onSuccess(result.Repository),
            RepositoryOperationStatus.ValidationFailed => ValidationProblem(
                result.ValidationErrors
            ),
            RepositoryOperationStatus.NotFound => NotFoundProblem(result.Detail),
            RepositoryOperationStatus.Conflict => ConflictProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported repository operation status '{result.Status}'."
            ),
        };

    private static IResult MapResult(
        WorkSourceEnvironmentOperationResult result,
        Func<WorkSourceEnvironmentProfile?, IResult> onSuccess
    ) =>
        result.Status switch
        {
            WorkSourceEnvironmentOperationStatus.Succeeded => onSuccess(result.Environment),
            WorkSourceEnvironmentOperationStatus.ValidationFailed => ValidationProblem(
                result.ValidationErrors
            ),
            WorkSourceEnvironmentOperationStatus.NotFound => NotFoundProblem(result.Detail),
            WorkSourceEnvironmentOperationStatus.Conflict => ConflictProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported work source environment operation status '{result.Status}'."
            ),
        };

    private static IResult MapResult(
        RuntimeEnvironmentOperationResult result,
        Func<RuntimeEnvironmentProfile?, IResult> onSuccess
    ) =>
        result.Status switch
        {
            RuntimeEnvironmentOperationStatus.Succeeded => onSuccess(result.Environment),
            RuntimeEnvironmentOperationStatus.ValidationFailed => ValidationProblem(
                result.ValidationErrors
            ),
            RuntimeEnvironmentOperationStatus.NotFound => NotFoundProblem(result.Detail),
            RuntimeEnvironmentOperationStatus.Conflict => ConflictProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported runtime environment operation status '{result.Status}'."
            ),
        };

    private static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            statusCode: StatusCodes.Status400BadRequest,
            title: "Validation failed."
        );

    private static IResult NotFoundProblem(string? detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Resource not found.",
            detail: detail
        );

    private static IResult ConflictProblem(string? detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Resource conflict.",
            detail: detail
        );

    // ── Secrets management endpoints ──

    private static void MapSecretsControllers(RouteGroupBuilder group)
    {
        // GET /api/webui/secrets — list all secrets (metadata only)
        group.MapGet(
            "",
            async (
                IQueryHandler<ListSecretsQuery, IReadOnlyList<Domain.Secrets.SecretInfo>> handler,
                CancellationToken cancellationToken
            ) =>
                Results.Ok(
                    await handler.ExecuteAsync(new ListSecretsQuery(), cancellationToken)
                )
        );

        // GET /api/webui/secrets/{name}/versions — list versions of a secret (metadata only)
        group.MapGet(
            "/{name}/versions",
            async (
                string name,
                IQueryHandler<ListSecretVersionsQuery, IReadOnlyList<Domain.Secrets.SecretVersionInfo>?> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var versions = await handler.ExecuteAsync(
                    new ListSecretVersionsQuery(name),
                    cancellationToken
                );
                return versions is null ? Results.NotFound() : Results.Ok(versions);
            }
        );

        // POST /api/webui/secrets — create a new secret
        group.MapPost(
            "",
            async (
                CreateSecretRequest request,
                ICommandHandler<CreateSecretCommand, CreateSecretResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var domainPayload = request.Payload.ToDomainPayload();
                var result = await handler.HandleAsync(
                    new CreateSecretCommand(request.Name, domainPayload),
                    cancellationToken
                );
                return MapSecretResult(result);
            }
        );

        // POST /api/webui/secrets/{name}/versions — create a new version of an existing secret
        group.MapPost(
            "/{name}/versions",
            async (
                string name,
                CreateSecretVersionRequest request,
                ICommandHandler<CreateSecretVersionCommand, CreateSecretVersionResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var domainPayload = request.Payload.ToDomainPayload();
                var result = await handler.HandleAsync(
                    new CreateSecretVersionCommand(name, domainPayload),
                    cancellationToken
                );
                return MapSecretVersionResult(result);
            }
        );

        // DELETE /api/webui/secrets/{name} — delete a secret (blocked while referenced)
        group.MapDelete(
            "/{name}",
            async (
                string name,
                ICommandHandler<DeleteSecretCommand, DeleteSecretResult> handler,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await handler.HandleAsync(
                    new DeleteSecretCommand(name),
                    cancellationToken
                );
                return MapDeleteSecretResult(result);
            }
        );
    }

    private static IResult MapSecretResult(CreateSecretResult result) =>
        result.Status switch
        {
            SecretOperationStatus.Succeeded => Results.Created(
                $"{SecretsPath}/{Uri.EscapeDataString(result.SecretName!)}",
                new { name = result.SecretName }
            ),
            SecretOperationStatus.ValidationFailed => ValidationProblem(result.ValidationErrors),
            SecretOperationStatus.Conflict => ConflictProblem(result.Detail),
            SecretOperationStatus.NotFound => NotFoundProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported secret operation status '{result.Status}'."
            ),
        };

    private static IResult MapSecretVersionResult(CreateSecretVersionResult result) =>
        result.Status switch
        {
            SecretOperationStatus.Succeeded => Results.Ok(
                new { name = result.SecretName, version = result.Version }
            ),
            SecretOperationStatus.ValidationFailed => ValidationProblem(result.ValidationErrors),
            SecretOperationStatus.Conflict => ConflictProblem(result.Detail),
            SecretOperationStatus.NotFound => NotFoundProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported secret version operation status '{result.Status}'."
            ),
        };

    private static IResult MapDeleteSecretResult(DeleteSecretResult result) =>
        result.Status switch
        {
            SecretOperationStatus.Succeeded => Results.NoContent(),
            SecretOperationStatus.ValidationFailed => ValidationProblem(result.ValidationErrors),
            SecretOperationStatus.Conflict => ConflictProblem(result.Detail),
            SecretOperationStatus.NotFound => NotFoundProblem(result.Detail),
            _ => throw new InvalidOperationException(
                $"Unsupported secret delete operation status '{result.Status}'."
            ),
        };

    // ── Request DTOs for secrets management ──

    /// <summary>Request to create a new named secret with a typed payload.</summary>
    private sealed record CreateSecretRequest(
        /// <summary>The unique secret name.</summary>
        string Name,

        /// <summary>
        /// The typed payload. Must include a <c>"type"</c> discriminator:
        /// <c>"personal-access-token"</c> (with <c>"value"</c>) or <c>"ssh-key"</c>
        /// (with <c>"privateKey"</c>, <c>"publicKey"</c>, and optional <c>"passphrase"</c>).
        /// </summary>
        CreateSecretPayload Payload
    );

    /// <summary>Request to create a new version of an existing secret.</summary>
    private sealed record CreateSecretVersionRequest(
        /// <summary>
        /// The typed payload. Must include a <c>"type"</c> discriminator:
        /// <c>"personal-access-token"</c> (with <c>"value"</c>) or <c>"ssh-key"</c>
        /// (with <c>"privateKey"</c>, <c>"publicKey"</c>, and optional/explicit <c>"passphrase"</c>).
        /// </summary>
        CreateSecretPayload Payload
    );
}

/// <summary>
/// Base type for discriminated create-secret payloads sent from the Web UI.
/// Each subtype maps to a <see cref="SecretPayload"/> domain type.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CreateSecretPatPayload), typeDiscriminator: "personal-access-token")]
[JsonDerivedType(typeof(CreateSecretSshPayload), typeDiscriminator: "ssh-key")]
internal abstract record CreateSecretPayload
{
    /// <summary>Converts this request payload to a domain <see cref="SecretPayload"/>.</summary>
    public abstract SecretPayload ToDomainPayload();
}

/// <summary>PAT payload for create-secret requests.</summary>
internal sealed record CreateSecretPatPayload : CreateSecretPayload
{
    /// <summary>The plaintext token value.</summary>
    public required string Value { get; init; }

    /// <inheritdoc />
    public override SecretPayload ToDomainPayload() =>
        new PersonalAccessTokenPayload { Value = Value };
}

/// <summary>SSH-key payload for create-secret requests.</summary>
internal sealed record CreateSecretSshPayload : CreateSecretPayload
{
    /// <summary>The SSH private key (PEM-encoded).</summary>
    public required string PrivateKey { get; init; }

    /// <summary>The SSH public key (safe for display in metadata).</summary>
    public required string PublicKey { get; init; }

    /// <summary>
    /// Optional passphrase for the encrypted private key.
    /// Must be explicitly supplied as a value or <c>null</c>.
    /// </summary>
    [JsonRequired]
    public string? Passphrase { get; init; }

    /// <inheritdoc />
    public override SecretPayload ToDomainPayload() =>
        new SshKeyPayload
        {
            PrivateKey = PrivateKey,
            PublicKey = PublicKey,
            Passphrase = Passphrase,
        };
}
