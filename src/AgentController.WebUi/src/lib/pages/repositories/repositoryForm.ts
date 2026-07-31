import type {
  CloneTransport,
  RepositoryProfile,
  ReviewerIdentity,
  ReviewerIdentityKindMetadata,
  ReviewerIdentityPolicyMetadata,
  SecretReference,
} from '../../api/types';

export interface RepositoryFormValues {
  key: string;
  cloneUrl: string;
  transport: CloneTransport;
  defaultBranch: string;
  repositoryHostConnectionKey: string;
  project: string;
  selectedRepositoryId: string;
  runtimeEnvironmentKey: string;
  sshKeyName: string;
  sshKeyVersion: number | null;
  sshKeyInheritEnvironment: boolean;
  reviewerIdentities: ReviewerIdentity[];
}

export type RepositoryFormErrors = Record<string, string[]>;

export function createRepositoryFormValues(profile?: RepositoryProfile): RepositoryFormValues {
  return {
    key: profile?.key ?? '',
    cloneUrl: profile?.cloneUrl ?? '',
    transport: profile?.transport ?? 'unspecified',
    defaultBranch: profile?.defaultBranch ?? 'main',
    repositoryHostConnectionKey: profile?.repositoryHostConnectionKey ?? '',
    project: profile?.project ?? '',
    selectedRepositoryId: '',
    runtimeEnvironmentKey: profile?.runtimeEnvironmentKey ?? '',
    sshKeyName: profile?.sshKeyReference?.name ?? '',
    sshKeyVersion: profile?.sshKeyReference?.version ?? null,
    sshKeyInheritEnvironment: profile?.sshKeyInheritEnvironment ?? false,
    reviewerIdentities: profile?.reviewerIdentities?.map(cloneReviewerIdentity) ?? [],
  };
}

export function validateRepositoryForm(values: RepositoryFormValues): RepositoryFormErrors {
  const errors: RepositoryFormErrors = {};

  addRequiredError(errors, 'key', values.key, 'A Repository Name is required.');
  addRequiredError(errors, 'cloneUrl', values.cloneUrl, 'A clone URL or local path is required.');
  addRequiredError(errors, 'defaultBranch', values.defaultBranch, 'A default branch is required.');
  addRequiredError(
    errors,
    'runtimeEnvironmentKey',
    values.runtimeEnvironmentKey,
    'A runtime environment is required.',
  );

  if (!values.sshKeyInheritEnvironment && requiresSshKey(values) && !values.sshKeyName.trim()) {
    errors.sshKeyReference = ['Select an SSH key secret for SSH clone transport.'];
  }

  return errors;
}

/** Whether the form is in host-driven mode (a repository host connection is selected). */
export function isHostDriven(values: Pick<RepositoryFormValues, 'repositoryHostConnectionKey'>): boolean {
  return Boolean(values.repositoryHostConnectionKey);
}

/**
 * Mirrors the URL-only portion of the canonical domain transport resolver so an unsaved
 * repository can show immediate guidance. The API remains authoritative when the profile
 * is persisted.
 */
export function inferCloneTransport(cloneUrl: string): CloneTransport {
  const value = cloneUrl.trim();
  if (!value || /[\u0000-\u001f\u007f]/u.test(value)) return 'unspecified';

  if (/^ssh:\/\//iu.test(value)) {
    return isValidRemoteUrl(value, ['ssh:']) ? 'ssh' : 'unspecified';
  }

  if (isScpStyleUrl(value)) return 'ssh';

  if (/^https?:\/\//iu.test(value)) {
    return isValidRemoteUrl(value, ['https:', 'http:']) ? 'httpsPat' : 'unspecified';
  }

  if (/^file:\/\//iu.test(value)) {
    try {
      return new URL(value).protocol === 'file:' ? 'local' : 'unspecified';
    } catch {
      return 'unspecified';
    }
  }

  return value.includes('://') ? 'unspecified' : 'local';
}

export function resolveRepositoryFormTransport(values: RepositoryFormValues): CloneTransport {
  return values.transport === 'unspecified'
    ? inferCloneTransport(values.cloneUrl)
    : values.transport;
}

export function requiresSshKey(values: RepositoryFormValues): boolean {
  return inferCloneTransport(values.cloneUrl) === 'ssh'
    || resolveRepositoryFormTransport(values) === 'ssh';
}

export function toRepositoryProfile(
  values: RepositoryFormValues,
  original?: RepositoryProfile,
): RepositoryProfile {
  return {
    key: values.key.trim(),
    cloneUrl: values.cloneUrl.trim(),
    webUrl: original?.webUrl ?? null,
    transport: values.transport,
    defaultBranch: values.defaultBranch.trim(),
    repositoryHostConnectionKey: nullableKey(values.repositoryHostConnectionKey),
    project: nullableKey(values.project),
    remoteIdentity: original?.remoteIdentity ?? null,
    runtimeEnvironmentKey: values.runtimeEnvironmentKey.trim(),
    reviewerIdentities: values.reviewerIdentities.map(cloneReviewerIdentity),
    sshKeyReference: toSecretReference(values.sshKeyName, values.sshKeyVersion),
    sshKeyInheritEnvironment: values.sshKeyInheritEnvironment,
    environmentProfile: original?.environmentProfile ?? '',
    runtimeProfile: original?.runtimeProfile ?? '',
  };
}

export interface ReviewerIdentityValidationResult {
  normalizedIdentity?: ReviewerIdentity;
  error?: string;
}

/**
 * Apply the credential-free policy metadata locally so the editor behaves like
 * the selected provider before a repository is submitted.
 */
export function validateAndNormalizeReviewerIdentity(
  identity: ReviewerIdentity,
  policy: ReviewerIdentityPolicyMetadata | undefined,
): ReviewerIdentityValidationResult {
  if (!policy || !policy.isSupported) {
    return { error: 'Reviewer identities are not supported by the selected repository host.' };
  }

  const kind = findIdentityKind(policy.supportedIdentityKinds, identity.kind);
  if (!kind) {
    return { error: 'Select a supported reviewer identity kind.' };
  }

  const value = identity.value.trim();
  if (!value) return { error: 'An identity value is required.' };
  if ([...value].some((character) => {
    const code = character.charCodeAt(0);
    return code < 32 || code === 127;
  })) {
    return { error: 'The identity value cannot contain control characters.' };
  }
  if (value.length > 1024) {
    return { error: 'The identity value must be 1024 characters or fewer.' };
  }

  let normalizedValue = value;
  switch (kind.validationCategory) {
    case 'email':
      if (!isEmailLike(value)) {
        return { error: 'Enter a valid email / uniqueName value.' };
      }
      normalizedValue = value.toLowerCase();
      break;
    case 'guid': {
      const normalizedGuid = normalizeGuid(value);
      if (!normalizedGuid) return { error: 'Enter a valid identity GUID.' };
      normalizedValue = normalizedGuid;
      break;
    }
    case 'opaque':
      if (/\s/u.test(value)) {
        return { error: 'The graph descriptor cannot contain whitespace.' };
      }
      break;
  }

  return {
    normalizedIdentity: { kind: kind.kind, value: normalizedValue },
  };
}

export function reviewerIdentityKey(identity: ReviewerIdentity): string {
  return JSON.stringify([identity.kind, identity.value]);
}

export function reviewerIdentityKind(
  policy: ReviewerIdentityPolicyMetadata | undefined,
  kind: string,
): ReviewerIdentityKindMetadata | undefined {
  return policy ? findIdentityKind(policy.supportedIdentityKinds, kind) : undefined;
}

function findIdentityKind(
  kinds: ReviewerIdentityKindMetadata[],
  value: string,
): ReviewerIdentityKindMetadata | undefined {
  const normalized = value.trim();
  return kinds.find((kind) => kind.kind === normalized)
    ?? kinds.find((kind) => kind.kind.toLowerCase() === normalized.toLowerCase());
}

function isEmailLike(value: string): boolean {
  return /^[^\s@,]+@[^\s@,]+$/u.test(value);
}

function normalizeGuid(value: string): string | undefined {
  const compact = value.replace(/[{}()-]/gu, '');
  if (!/^[0-9a-f]{32}$/iu.test(compact)) return undefined;
  return `${compact.slice(0, 8)}-${compact.slice(8, 12)}-${compact.slice(12, 16)}-${compact.slice(16, 20)}-${compact.slice(20)}`.toLowerCase();
}

function cloneReviewerIdentity(identity: ReviewerIdentity): ReviewerIdentity {
  return { kind: identity.kind, value: identity.value };
}

function addRequiredError(
  errors: RepositoryFormErrors,
  field: string,
  value: string,
  message: string,
): void {
  if (!value.trim()) errors[field] = [message];
}

function nullableKey(value: string): string | null {
  const normalized = value.trim();
  return normalized || null;
}

function toSecretReference(name: string, version: number | null): SecretReference | null {
  const normalizedName = name.trim();
  return normalizedName ? { name: normalizedName, version } : null;
}

function isValidRemoteUrl(value: string, protocols: string[]): boolean {
  if (/\s/u.test(value)) return false;

  try {
    const url = new URL(value);
    return protocols.includes(url.protocol.toLowerCase()) && Boolean(url.hostname);
  } catch {
    return false;
  }
}

function isScpStyleUrl(value: string): boolean {
  if (/\s/u.test(value)) return false;

  const atIndex = value.indexOf('@');
  const colonIndex = value.indexOf(':', atIndex + 1);
  return atIndex > 0
    && colonIndex > atIndex + 1
    && colonIndex < value.length - 1
    && !value.slice(0, atIndex).includes('/')
    && !value.slice(atIndex + 1, colonIndex).includes('/');
}
