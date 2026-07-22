import type { CloneTransport, RepositoryProfile, SecretReference } from '../../api/types';

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
  };
}

export function validateRepositoryForm(values: RepositoryFormValues): RepositoryFormErrors {
  const errors: RepositoryFormErrors = {};

  addRequiredError(errors, 'key', values.key, 'A Repository Name is required.');
  addRequiredError(errors, 'cloneUrl', values.cloneUrl, 'A clone URL or local path is required.');
  addRequiredError(errors, 'defaultBranch', values.defaultBranch, 'A default branch is required.');

  if (requiresSshKey(values) && !values.sshKeyName.trim()) {
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
    transport: values.transport,
    defaultBranch: values.defaultBranch.trim(),
    repositoryHostConnectionKey: nullableKey(values.repositoryHostConnectionKey),
    project: nullableKey(values.project),
    remoteIdentity: original?.remoteIdentity ?? null,
    runtimeEnvironmentKey: nullableKey(values.runtimeEnvironmentKey),
    sshKeyReference: toSecretReference(values.sshKeyName, values.sshKeyVersion),
    environmentProfile: original?.environmentProfile ?? '',
    runtimeProfile: original?.runtimeProfile ?? '',
  };
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
