import type { PersonalAccessTokenSecretReference, RepositoryHostConnectionProfile } from '../../api/types';

export interface RepositoryHostConnectionFormValues {
  key: string;
  displayName: string;
  enabled: boolean;
  provider: string;
  organizationUrl: string;
  project: string;
  secretName: string;
  secretVersion: number | null;
}

export type RepositoryHostConnectionFormErrors = Record<string, string[]>;

export function createRepositoryHostConnectionFormValues(
  profile?: RepositoryHostConnectionProfile,
): RepositoryHostConnectionFormValues {
  const ref = profile?.personalAccessTokenReference ?? { name: '', version: null };
  return {
    key: profile?.key ?? '',
    displayName: profile?.displayName ?? '',
    enabled: profile?.enabled ?? true,
    provider: profile?.provider ?? 'AzureDevOpsRepos',
    organizationUrl: profile?.organizationUrl ?? '',
    project: profile?.project ?? '',
    secretName: ref.name ?? '',
    secretVersion: ref.version ?? null,
  };
}

export function validateRepositoryHostConnectionForm(
  values: RepositoryHostConnectionFormValues,
): RepositoryHostConnectionFormErrors {
  const errors: RepositoryHostConnectionFormErrors = {};

  addRequiredError(errors, 'key', values.key, 'A key is required.');
  addRequiredError(errors, 'displayName', values.displayName, 'A display name is required.');

  if (values.provider === 'AzureDevOpsRepos') {
    addRequiredError(
      errors,
      'organizationUrl',
      values.organizationUrl,
      'An Azure DevOps organization URL is required.',
    );
    addRequiredError(errors, 'project', values.project, 'An Azure DevOps project is required.');
    addRequiredError(
      errors,
      'secretName',
      values.secretName,
      'A secret reference for the PAT is required.',
    );

    if (
      values.secretVersion !== null &&
      values.secretVersion !== undefined &&
      values.secretVersion < 1
    ) {
      errors.secretVersion = [
        'The secret version must be 1 or greater.',
      ];
    }

    if (values.organizationUrl.trim() && !isValidOrganizationUrl(values.organizationUrl)) {
      errors.organizationUrl = [
        'Enter an absolute HTTP or HTTPS organization URL without credentials, a query, or a fragment.',
      ];
    }
  }

  return errors;
}

export function toRepositoryHostConnectionProfile(
  values: RepositoryHostConnectionFormValues,
  original?: RepositoryHostConnectionProfile,
): RepositoryHostConnectionProfile {
  const now = new Date().toISOString();
  return {
    key: values.key.trim(),
    displayName: values.displayName.trim(),
    enabled: values.enabled,
    provider: values.provider.trim() || 'AzureDevOpsRepos',
    organizationUrl: values.organizationUrl.trim().replace(/\/+$/, ''),
    project: values.project.trim(),
    personalAccessTokenReference: {
      name: values.secretName.trim(),
      version: values.secretVersion ?? null,
    } as PersonalAccessTokenSecretReference,
    createdAt: original?.createdAt ?? now,
    updatedAt: original?.updatedAt ?? now,
  };
}

function addRequiredError(
  errors: RepositoryHostConnectionFormErrors,
  field: string,
  value: string,
  message: string,
): void {
  if (!value.trim()) errors[field] = [message];
}

function isValidOrganizationUrl(value: string): boolean {
  try {
    const url = new URL(value.trim());
    return (
      (url.protocol === 'http:' || url.protocol === 'https:') &&
      Boolean(url.hostname) &&
      !url.username &&
      !url.password &&
      !url.search &&
      !url.hash
    );
  } catch {
    return false;
  }
}
