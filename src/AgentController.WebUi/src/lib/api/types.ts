export type CloneTransport = 'unspecified' | 'ssh' | 'httpsPat' | 'local';

/** Reference to a named, optionally pinned secret version. */
export interface SecretReference {
  name: string;
  version: number | null;
}

export interface RepositoryProfile {
  key: string;
  cloneUrl: string;
  defaultBranch: string;
  transport: CloneTransport;
  environmentProfile: string;
  runtimeProfile: string;
  repositoryHostConnectionKey: string | null;
  remoteIdentity: string | null;
  runtimeEnvironmentKey: string | null;
  sshKeyReference: SecretReference | null;
  allowedPaths: string[];
  project: string | null;
}

export interface WorkSourceEnvironmentProfile {
  key: string;
  displayName: string;
  enabled: boolean;
  provider: string;
  connectionKey: string;
  project: string;
  tagPrefix: string;
  activeState: string | null;
  completedState: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface EnvironmentProviderSettings {
  workspaceRoot: string | null;
}

export interface RuntimeProviderSettings {
  piExecutablePath: string | null;
  controllerBaseUrl: string | null;
  ptyWrapperPath: string | null;
  ptyWrapperArgs: string | null;
  loadouts: Partial<Record<'newWork' | 'rework', string>>;
  forwardEnvironmentVariables: Record<string, string>;
}

export interface RuntimeEnvironmentProfile {
  key: string;
  displayName: string;
  enabled: boolean;
  environmentProvider: string;
  environmentSettings: EnvironmentProviderSettings;
  runtimeProvider: string;
  runtimeSettings: RuntimeProviderSettings;
  createdAt: string;
  updatedAt: string;
}

/** Provider-neutral connectivity verification result. */
export interface WorkSourceConnectivityResult {
  success: boolean;
  authMechanism: string;
  httpStatus?: number;
  errors: string[];
  payload?: Record<string, unknown>;
}

/** Clone transport hint from a repository host. */
export type CloneTransportHint = 'unspecified' | 'ssh' | 'httpsPat';

/** Provider-neutral description of a remote repository. */
export interface HostRepository {
  id: string;
  name: string;
  defaultBranch: string;
  remoteUrl: string;
  cloneTransportHint: CloneTransportHint;
}

/** Stable discriminator shared with the typed secrets API. */
export type SecretType = 'personal-access-token' | 'ssh-key';

/** Metadata for a named secret (no plaintext values). */
export interface SecretInfo {
  name: string;
  latestVersion: number;
  createdAt: string;
  updatedAt: string;
  secretType: SecretType;
}

/** Metadata for a single secret version (no plaintext value). */
export interface SecretVersionInfo {
  version: number;
  createdAt: string;
  secretType: SecretType;
  /** SSH public key (safe for display), null for PAT secrets. */
  publicKey: string | null;
}

/** Base payload for PAT/SSH-key secret creation. The `type` discriminator field selects the shape. */
export type CreateSecretPayload =
  | { type: 'personal-access-token'; value: string }
  | { type: 'ssh-key'; privateKey: string; publicKey: string; passphrase: string | null };

/** Request payload for creating a new secret. */
export interface CreateSecretRequest {
  name: string;
  payload: CreateSecretPayload;
}

/** Request payload for creating a new version of an existing secret. */
export interface CreateSecretVersionRequest {
  payload: CreateSecretPayload;
}

/** Response after successfully creating a secret. */
export interface CreatedSecretResponse {
  name: string;
}

/** Response after successfully creating a secret version. */
export interface CreatedSecretVersionResponse {
  name: string;
  version: number;
}

/** Capability a unified connection can provide. */
export type ConnectionCapability = 'Repositories' | 'WorkTracking' | 'ExecutionHost';

/** Reference to a named, versioned secret for a connection PAT. */
export type ConnectionSecretReference = SecretReference;

/** Azure DevOps provider settings for a connection. */
export interface AzureDevOpsConnectionSettings {
  provider: 'AzureDevOps';
  organizationUrl: string;
  personalAccessTokenReference: ConnectionSecretReference;
}

/** Unified, provider-discriminated connection profile. */
export interface ConnectionProfile {
  key: string;
  displayName: string;
  enabled: boolean;
  provider: string;
  capabilities: ConnectionCapability[];
  providerSettings: AzureDevOpsConnectionSettings | null;
  createdAt: string;
  updatedAt: string;
}

/** Minimal project descriptor from a connection provider. */
export interface ConnectionProject {
  id: string;
  name: string;
}

/** Provider-neutral connectivity verification result for a unified connection. */
export interface ConnectionConnectivityResult {
  success: boolean;
  authMechanism: string;
  httpStatus?: number;
  errors: string[];
  payload?: Record<string, unknown>;
}

/** RFC 9457 problem details, including ASP.NET validation extensions. */
export interface ProblemDetails {
  type?: string;
  title: string;
  status: number;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
}
