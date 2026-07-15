export type CloneTransport = 'unspecified' | 'ssh' | 'httpsPat' | 'local';

export interface RepositoryProfile {
  key: string;
  cloneUrl: string;
  defaultBranch: string;
  transport: CloneTransport;
  environmentProfile: string;
  runtimeProfile: string;
  azureDevOpsEnvironmentKey: string | null;
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

/** RFC 9457 problem details, including ASP.NET validation extensions. */
export interface ProblemDetails {
  type?: string;
  title: string;
  status: number;
  detail?: string;
  instance?: string;
  errors?: Record<string, string[]>;
}
