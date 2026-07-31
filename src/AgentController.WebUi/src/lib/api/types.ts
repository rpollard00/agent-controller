export type CloneTransport = 'unspecified' | 'ssh' | 'httpsPat' | 'local';

/** Reference to a named, optionally pinned secret version. */
export interface SecretReference {
  name: string;
  version: number | null;
}

/** Provider-neutral reviewer identity configured for a managed repository. */
export interface ReviewerIdentity {
  kind: string;
  value: string;
}

export interface RepositoryProfile {
  key: string;
  cloneUrl: string;
  webUrl: string | null;
  defaultBranch: string;
  transport: CloneTransport;
  environmentProfile: string;
  runtimeProfile: string;
  repositoryHostConnectionKey: string | null;
  remoteIdentity: string | null;
  runtimeEnvironmentKey: string | null;
  reviewerIdentities: ReviewerIdentity[];
  sshKeyReference: SecretReference | null;
  sshKeyInheritEnvironment: boolean;
  project: string | null;
}

/** Source of the credential reference selected for a concrete clone transport. */
export type RepositoryCloneCredentialSource =
  | 'none'
  | 'sshKey'
  | 'connectionPersonalAccessToken';

/** Stable code for a repository configuration problem that blocks cloning. */
export type RepositoryCloneTransportIssueCode =
  | 'unsupportedCloneUrl'
  | 'configuredTransportMismatch'
  | 'missingSshKeyReference'
  | 'missingRepositoryHostConnection'
  | 'repositoryHostConnectionNotFound'
  | 'repositoryHostConnectionDisabled'
  | 'missingPersonalAccessTokenReference';

export interface RepositoryCloneTransportIssue {
  code: RepositoryCloneTransportIssueCode;
  field: string;
  message: string;
}

/** Effective clone transport and credential metadata; never contains secret values. */
export interface RepositoryCloneTransportResolution {
  transport: CloneTransport;
  credentialSource: RepositoryCloneCredentialSource;
  credentialReference: SecretReference | null;
  blockingIssues: RepositoryCloneTransportIssue[];
  isReady: boolean;
}

/** Stable category for a clone preflight failure. */
export type ClonePreflightFailureCode =
  | 'invalidCloneUrl'
  | 'transportConfigurationInvalid'
  | 'credentialNotFound'
  | 'credentialTypeMismatch'
  | 'credentialInvalid'
  | 'credentialUnavailable'
  | 'toolUnavailable'
  | 'remoteUnreachable'
  | 'authenticationFailed'
  | 'remoteRejected';

/** Result of a credential-aware git ls-remote probe; never contains secret values. */
export interface ClonePreflightResult {
  success: boolean;
  reason: string;
  failureCode: ClonePreflightFailureCode | null;
  transport: CloneTransport;
  cloneUrl: string;
  credentialSource: RepositoryCloneCredentialSource;
  credentialReference: SecretReference | null;
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
  webUrl: string | null;
  sshUrl: string | null;
  cloneTransportHint: CloneTransportHint;
}

/** Card source represented on the aggregate runs dashboard. */
export type RunCardKind = 'run' | 'rework-soak';

/** Server-computed state category used to display and filter run cards. */
export type RunCardCategory = 'executing' | 'pending' | 'attention' | 'completed';

/** Mode used to request work on an existing pull request. */
export type ReworkRequestMode = 'revival' | 'assistance';

/** Feedback debounce/materialization lifecycle state. */
export type ReworkFeedbackStatus = 'watching' | 'soaked' | 'superseded' | 'materialized';

/** Materialized rework-cycle lifecycle state. */
export type ReworkCycleStatus = 'pending' | 'consumed';

/** Provider-neutral reference to the existing pull request being continued. */
export interface PullRequestReference {
  environmentKey: string;
  repositoryKey: string;
  pullRequestId: string;
  pullRequestUrl: string;
  sourceBranch: string;
  targetBranch: string;
  sourceCommitSha: string;
  canonicalKey: string;
  hasCanonicalIdentity: boolean;
}

/** Aggregate dashboard projection for an agent run or rework-feedback soak. */
export interface RunCardItem {
  id: string;
  kind: RunCardKind;
  status: string;
  category: RunCardCategory;
  workItemTitle: string | null;
  workItemUrl: string | null;
  workItemSource: string | null;
  repoKey: string | null;
  repositoryUrl: string | null;
  runtimeType: string | null;
  runtimeProfileName: string | null;
  environmentProviderType: string | null;
  runAttempt: number;
  requestMode: ReworkRequestMode | null;
  pullRequest: PullRequestReference | null;
  cycleNumber: number | null;
  assistanceStoryWorkItemId: string | null;
  assistanceStoryExternalId: string | null;
  assistanceStoryUrl: string | null;
  feedbackStatus: ReworkFeedbackStatus | null;
  cycleStatus: ReworkCycleStatus | null;
  consumingRunId: string | null;
  lastEventType: string | null;
  lastEventMessage: string | null;
  lastEventAt: string | null;
  soakEligibleAt: string | null;
  createdAt: string;
  updatedAt: string;
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

/** Metadata for one provider-specific reviewer identity kind. */
export type ReviewerIdentityValidationCategory = 'email' | 'guid' | 'opaque';

export interface ReviewerIdentityKindMetadata {
  kind: string;
  label: string;
  hint: string | null;
  placeholder: string | null;
  validationCategory: ReviewerIdentityValidationCategory;
}

/** Credential-free reviewer identity policy metadata for a managed connection. */
export interface ReviewerIdentityPolicyMetadata {
  provider: string;
  isSupported: boolean;
  supportedIdentityKinds: ReviewerIdentityKindMetadata[];
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

/** A configured work source available to board-item diagnostics. */
export interface BoardDebugSourceOption {
  key: string;
  displayName: string;
}

/** A configured source-control integration available to pull-request diagnostics. */
export interface PullRequestDebugSourceOption {
  key: string;
  name: string;
}

/** An operator-safe discovery failure scoped to one work-source environment. */
export interface BoardDebugSourceFailure {
  workSourceEnvironmentKey: string;
  project: string;
  message: string;
}

/** An operator-safe discovery failure scoped to one managed repository. */
export interface PullRequestDebugSourceFailure {
  sourceControlEnvironmentKey: string;
  repositoryKey: string;
  message: string;
}

/** Concise board-item pickup result. */
export type BoardItemMatchResult = 'eligible' | 'missingTags' | 'excluded';

/** Concise request markers found on a pull request. */
export type PullRequestRequestMatch = 'none' | 'revival' | 'assistance' | 'both';

/** Final result of pull-request pickup diagnostics. */
export type PullRequestDiagnosticOutcome =
  | 'notEligible'
  | 'eligible'
  | 'assistanceTakesPrecedence';

/** One pass/fail pickup-policy check. */
export interface DiagnosticCheck {
  code: string;
  label: string;
  passed: boolean;
  reason: string;
}

export type BoardItemDiagnosticCheck = DiagnosticCheck;
export type PullRequestDiagnosticCheck = DiagnosticCheck;

export interface BoardItemDiagnosticSummary {
  id: string;
  title: string;
  url: string | null;
  project: string;
  workSourceEnvironmentKey: string;
  repositoryKey: string | null;
  state: string | null;
  match: BoardItemMatchResult;
}

export interface BoardItemDiagnosticDetail extends BoardItemDiagnosticSummary {
  tags: string[];
  eligible: boolean;
  checks: BoardItemDiagnosticCheck[];
  recognizedRepositoryTags: string[];
  recognizedReadyTag: string;
  recognizedReadyReworkTag: string;
}

export interface BoardItemsDebugPageResponse {
  sourceOptions: BoardDebugSourceOption[];
  items: BoardItemDiagnosticSummary[];
  failures: BoardDebugSourceFailure[];
  page: number;
  pageSize: number;
  total: number;
  observedAt: string;
}

export interface PullRequestDiagnosticSummary {
  pullRequestId: string;
  title: string;
  url: string | null;
  sourceControlEnvironmentKey: string;
  repositoryKey: string;
  status: string;
  request: PullRequestRequestMatch;
}

export interface PullRequestWorkItemReference {
  workItemId: string;
  workItemUrl: string;
}

export type FeedbackMarkerCheckStatus =
  | 'alreadyValidated'
  | 'present'
  | 'missing'
  | 'fetchFailed'
  | 'pullRequestNotFound'
  | 'notAttempted';

/** Body-free feedback counts from the same policy used for production pickup. */
export interface ReviewFeedbackCheckTrace {
  pullRequestId: string;
  markerStatus: FeedbackMarkerCheckStatus;
  reviewerAllowlistConfigured: boolean;
  totalThreadCount: number;
  activeThreadCount: number;
  allowlistedReviewerThreadCount: number;
  nonEmptyContentThreadCount: number;
  qualifyingThreadCount: number;
  isAccepted: boolean;
}

export interface PullRequestTrackingState {
  requestMode: ReworkRequestMode;
  feedbackStatus: ReworkFeedbackStatus;
  cycleStatus: ReworkCycleStatus | null;
}

export interface PullRequestDiagnosticDetail extends PullRequestDiagnosticSummary {
  sourceBranch: string;
  targetBranch: string;
  labels: string[];
  linkedWorkItems: PullRequestWorkItemReference[];
  outcome: PullRequestDiagnosticOutcome;
  eligible: boolean;
  checks: PullRequestDiagnosticCheck[];
  recognizedRevivalLabel: string;
  recognizedAssistanceLabel: string;
  feedbackTrace: ReviewFeedbackCheckTrace | null;
  tracking: PullRequestTrackingState | null;
}

export interface PullRequestsDebugPageResponse {
  sourceOptions: PullRequestDebugSourceOption[];
  items: PullRequestDiagnosticSummary[];
  failures: PullRequestDebugSourceFailure[];
  page: number;
  pageSize: number;
  total: number;
  observedAt: string;
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
