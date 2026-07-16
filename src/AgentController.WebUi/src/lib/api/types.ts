export type CloneTransport = 'unspecified' | 'ssh' | 'httpsPat' | 'local';

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
  allowedPaths: string[];
}

export interface WorkSourceEnvironmentProfile {
  key: string;
  displayName: string;
  enabled: boolean;
  provider: string;
  organizationUrl: string;
  project: string;
  tagPrefix: string;
  activeState: string | null;
  completedState: string | null;
  patEnvironmentVariable: string;
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

/** Opaque reference to a secret value stored outside the profile. */
export interface SecretReference {
  kind: string;
  id: string;
}

/** Managed configuration for a repository host connection. */
export interface RepositoryHostConnectionProfile {
  key: string;
  displayName: string;
  enabled: boolean;
  provider: string;
  organizationUrl: string;
  project: string;
  personalAccessTokenReference: SecretReference;
  createdAt: string;
  updatedAt: string;
}

/** Provider-neutral connectivity verification result for a repository host. */
export interface RepositoryHostConnectivityResult {
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

/** Metadata for a named secret (no plaintext values). */
export interface SecretInfo {
  name: string;
  latestVersion: number;
  createdAt: string;
  updatedAt: string;
}

/** Metadata for a single secret version (no plaintext value). */
export interface SecretVersionInfo {
  version: number;
  createdAt: string;
}

/** Request payload for creating a new secret. */
export interface CreateSecretRequest {
  name: string;
  value: string;
}

/** Request payload for creating a new version of an existing secret. */
export interface CreateSecretVersionRequest {
  value: string;
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

/** RFC 9457 problem details, including ASP.NET validation extensions. */
export interface ProblemDetails {
  type?: string;
  title: string;
  status: number;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
}
