import type { CloneTransport, RepositoryProfile } from '../../api/types';

export interface RepositoryFormValues {
  key: string;
  cloneUrl: string;
  transport: CloneTransport;
  defaultBranch: string;
  allowedPaths: string;
  repositoryHostConnectionKey: string;
  runtimeEnvironmentKey: string;
}

export type RepositoryFormErrors = Record<string, string[]>;

export function createRepositoryFormValues(profile?: RepositoryProfile): RepositoryFormValues {
  return {
    key: profile?.key ?? '',
    cloneUrl: profile?.cloneUrl ?? '',
    transport: profile?.transport ?? 'unspecified',
    defaultBranch: profile?.defaultBranch ?? 'main',
    allowedPaths: profile?.allowedPaths.join('\n') ?? '',
    repositoryHostConnectionKey: profile?.repositoryHostConnectionKey ?? '',
    runtimeEnvironmentKey: profile?.runtimeEnvironmentKey ?? '',
  };
}

export function validateRepositoryForm(values: RepositoryFormValues): RepositoryFormErrors {
  const errors: RepositoryFormErrors = {};

  addRequiredError(errors, 'key', values.key, 'A repository key is required.');
  addRequiredError(errors, 'cloneUrl', values.cloneUrl, 'A clone URL or local path is required.');
  addRequiredError(errors, 'defaultBranch', values.defaultBranch, 'A default branch is required.');

  return errors;
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
    allowedPaths: values.allowedPaths
      .split(/\r?\n/)
      .map((path) => path.trim())
      .filter((path) => path.length > 0),
    repositoryHostConnectionKey: nullableKey(values.repositoryHostConnectionKey),
    remoteIdentity: original?.remoteIdentity ?? null,
    runtimeEnvironmentKey: nullableKey(values.runtimeEnvironmentKey),
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
