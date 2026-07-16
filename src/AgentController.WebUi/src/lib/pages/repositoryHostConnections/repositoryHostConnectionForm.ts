import type { RepositoryHostConnectionProfile, SecretReference } from '../../api/types';

export interface RepositoryHostConnectionFormValues {
  key: string;
  displayName: string;
  enabled: boolean;
  provider: string;
  organizationUrl: string;
  project: string;
  secretKind: string;
  secretId: string;
}

export type RepositoryHostConnectionFormErrors = Record<string, string[]>;

export function createRepositoryHostConnectionFormValues(
  profile?: RepositoryHostConnectionProfile,
): RepositoryHostConnectionFormValues {
  const ref = profile?.personalAccessTokenReference ?? { kind: 'EnvVar', id: '' };
  return {
    key: profile?.key ?? '',
    displayName: profile?.displayName ?? '',
    enabled: profile?.enabled ?? true,
    provider: profile?.provider ?? 'AzureDevOpsRepos',
    organizationUrl: profile?.organizationUrl ?? '',
    project: profile?.project ?? '',
    secretKind: ref.kind ?? 'EnvVar',
    secretId: ref.id ?? '',
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
      'secretId',
      values.secretId,
      'A secret reference identifier is required.',
    );

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
      kind: values.secretKind.trim() || 'EnvVar',
      id: values.secretId.trim(),
    } as SecretReference,
    createdAt: original?.createdAt ?? now,
    updatedAt: original?.updatedAt ?? now,
  };
}

function toSecretReference(values: RepositoryHostConnectionFormValues): SecretReference {
  return {
    kind: values.secretKind.trim() || 'EnvVar',
    id: values.secretId.trim(),
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
