# Secrets Management

Agent Controller stores credentials as **named, versioned secrets encrypted at rest** in the database. Each secret has a unique name and one or more versions, each carrying an encrypted copy of the secret value. Secrets are referenced by name from work-source and repository host configurations.

## Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                    Consumer Code                     │
│  (WorkSource PAT resolution, API endpoints, etc.)   │
├──────────────────────┬──────────────────────────────┤
│                      │                              │
│  ISecretStore        │  ISecretManager              │
│  (read port)         │  (admin port)                │
│                      │                              │
│  ResolveAsync(       │  CreateAsync(name, value)    │
│    name, version?)   │  CreateVersionAsync(...)     │
│  ) → string?         │  ListAsync() → SecretInfo[]  │
│                      │  ListVersionsAsync(name)     │
├──────────────────────┴──────────────────────────────┤
│                  Provider (selected)                 │
│                                                     │
│  DbNamedSecretProvider (default, only provider)     │
│  ┌───────────────────────────────────────────────┐  │
│  │  AesGcmEnvelopeEncryption                     │  │
│  │  ┌─────────────────────────────────────────┐  │  │
│  │  │  IKeyEncryptionKeySource                │  │  │
│  │  │  (FileKeyEncryptionKeySource)           │  │  │
│  │  └─────────────────────────────────────────┘  │  │
│  └───────────────────────────────────────────────┘  │
│                                                     │
│  EF Core DbContext → NamedSecrets / SecretVersions  │
└─────────────────────────────────────────────────────┘
```

## Provider-Neutral Ports

The secrets system is built around two provider-neutral interfaces defined in the Domain layer:

### ISecretStore (Read Port)

`ISecretStore` is the runtime read path. Consumers call it to resolve a plaintext secret value by name:

```csharp
public interface ISecretStore
{
    Task<string?> ResolveAsync(
        string name,
        int? version = null,
        CancellationToken cancellationToken = default
    );
}
```

- Resolves by secret **name** (required) and optional **version** number.
- When `version` is omitted, resolves to the **latest** version.
- Returns `null` if the secret or version does not exist.
- Used by work-source runtime code (e.g., `AzureDevOpsPatResolver`) to obtain PAT values for API calls.

### ISecretManager (Admin Port)

`ISecretManager` is the management path for creating and listing secrets:

```csharp
public interface ISecretManager
{
    Task<bool> CreateAsync(string name, string value, ...);
    Task<int?> CreateVersionAsync(string name, string value, ...);
    Task<IReadOnlyList<SecretInfo>> ListAsync(...);
    Task<IReadOnlyList<SecretVersionInfo>?> ListVersionsAsync(string name, ...);
}
```

- `CreateAsync` — creates a new secret with an initial value (version 1). Returns `false` if a secret with that name already exists.
- `CreateVersionAsync` — creates a new version of an existing secret. Returns the new version number, or `null` if the secret does not exist.
- `ListAsync` — lists all secrets by name (metadata only: name, latest version, created/updated timestamps).
- `ListVersionsAsync` — lists all versions of a secret (metadata only: version number, created timestamp).
- **Stored values are never decrypted for display.** All list operations return metadata only.

### InMemorySecretStore (Test Double)

`InMemorySecretStore` implements both `ISecretStore` and `ISecretManager` in memory. It is **not registered in production DI** — it is instantiated directly only in unit tests to verify consumer behavior without a real database or encryption provider.

## SecretReference Value Object

`SecretReference` is the domain type used to reference a secret from a work-source or repository host configuration:

```csharp
public sealed record SecretReference
{
    public string Name { get; init; }    // required: the secret name
    public int? Version { get; init; }   // optional: pin to a specific version
    public bool IsSpecified { get; }     // true if Name is non-empty

    public static SecretReference ByName(string name);
    public static SecretReference ByNameAndVersion(string name, int version);
    public static SecretReference Empty { get; }
}
```

- When `Version` is omitted, the latest version is resolved at runtime via `ISecretStore`.
- Replaces the legacy `patEnvironmentVariable` string concept.

## Database Schema

Secrets are persisted in two tables:

### NamedSecrets

| Column      | Type     | Description                          |
|-------------|----------|--------------------------------------|
| `Id`        | string   | Primary key (GUID)                   |
| `Name`      | string   | Unique secret name (max 256 chars)   |
| `CreatedAt` | datetime | When the secret was first created    |

Unique index on `Name`.

### SecretVersions

| Column            | Type     | Description                                      |
|-------------------|----------|--------------------------------------------------|
| `Id`              | string   | Primary key (GUID)                               |
| `NamedSecretId`   | string   | Foreign key to `NamedSecrets.Id` (cascade delete) |
| `VersionNumber`   | int      | Monotonically increasing version number (1-based) |
| `EncryptedValue`  | byte[]   | Encrypted secret value (ciphertext + tag)        |
| `Nonce`           | byte[]   | 12-byte nonce for data encryption                |
| `WrappedDek`      | byte[]   | Wrapped DEK ([12-byte nonce \| ciphertext + tag]) |
| `CreatedAt`       | datetime | When this version was created                    |

Unique composite index on `(NamedSecretId, VersionNumber)`. **Plaintext values are never stored at rest.**

## Envelope Encryption

The DB provider uses envelope encryption with AES-256-GCM:

1. A random 256-bit **DEK** (Data Encryption Key) is generated for each secret version.
2. The plaintext value is encrypted with the DEK using AES-GCM, producing a ciphertext blob and a 12-byte nonce.
3. The DEK itself is encrypted (wrapped) by a master **KEK** (Key Encryption Key) using AES-GCM.
4. The stored artifacts are: `EncryptedValue`, `Nonce`, and `WrappedDek`.

This design ensures:
- Each secret version has its own encryption key (DEK).
- The master KEK is never stored in the database.
- Rotating the KEK requires re-encrypting all DEKs (future work).

## Provider Selection and Registration

Provider selection is configured via the `secrets` configuration section:

```json
{
  "secrets": {
    "provider": "Db"
  }
}
```

### Supported Providers

| Provider | Description |
|----------|-------------|
| `Db` (default) | DB-backed `DbNamedSecretProvider` with envelope encryption. Requires a KEK file. |

### Registration

Call `AddAgentControllerNamedSecrets(configuration)` to register the selected provider in DI. This registers both `ISecretStore` and `ISecretManager` as scoped services backed by the chosen provider.

The method fails fast at startup if:
- The provider value is unknown or misconfigured.
- The KEK file is missing, unreadable, or not exactly 32 bytes.

### Configuration for the DB Provider

The DB provider requires a KEK file path, configurable via:

- **appsettings**: `secrets:keyEncryptionKey:file:filePath`
- **Environment variable**: `AGENT_CONTROLLER_SECRET_KEK_FILE_PATH`

```json
{
  "secrets": {
    "provider": "Db",
    "keyEncryptionKey": {
      "file": {
        "filePath": "/path/to/kek.key"
      }
    }
  }
}
```

The KEK file must contain exactly 32 bytes of binary data. See the [KEK Setup Guide](./kek-setup.md) for provisioning instructions.

## Fail-Fast Behavior

The secrets system fails fast at startup rather than silently degrading. There is no fallback to plaintext or in-memory storage.

1. **Unknown provider**: If `secrets:provider` is set to an unrecognized value, `AddAgentControllerNamedSecrets` throws `InvalidOperationException` listing supported providers.
2. **Missing KEK file path**: If neither `secrets:keyEncryptionKey:file:filePath` nor `AGENT_CONTROLLER_SECRET_KEK_FILE_PATH` is configured, a **critical log entry** is emitted (logger category `AgentController.Secrets.Startup`) with actionable setup guidance, and then `InvalidOperationException` is thrown. The application **will not start**.
3. **Invalid KEK file**: If the KEK file is missing, unreadable, or not exactly 32 bytes, `FileKeyEncryptionKeySource` throws `InvalidOperationException` at construction time.

### Critical Log on Missing KEK

When the KEK is not configured, the following critical log line is emitted before the exception:

```
KEK (Key Encryption Key) is not configured. The application cannot start.
To fix this, do one of the following:
  1. Generate a 32-byte key file: openssl rand 32 > kek.key
  2. Set the AGENT_CONTROLLER_SECRET_KEK_FILE_PATH environment variable to the path of the key file,
     OR configure 'secrets:keyEncryptionKey:file:filePath' in appsettings.json/appsettings.{{Environment}}.json.
The KEK file must contain exactly 32 bytes of binary data (e.g., from openssl rand 32).
```

This ensures the failure is unmissable in log output. The subsequent `InvalidOperationException` carries the same configuration key names for programmatic handling.

## Related Documentation

- [KEK Setup Guide](./kek-setup.md) — Provisioning the Key Encryption Key.
- [Boards Setup](./boards-setup.md) — §2 (PAT as a named, versioned secret).
- [Architecture Document](./arch.md) — §11.4 (MVP Security).
