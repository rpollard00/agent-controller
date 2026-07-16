import type { PersonalAccessTokenSecretReference, WorkSourceEnvironmentProfile } from '../../api/types';

export interface WorkSourceEnvironmentFormValues {
  key: string;
  displayName: string;
  enabled: boolean;
  provider: string;
  organizationUrl: string;
  project: string;
  tagPrefix: string;
  activeState: string;
  completedState: string;
  secretName: string;
  secretVersion: number | null;
}

export type WorkSourceEnvironmentFormErrors = Record<string, string[]>;

export function createWorkSourceEnvironmentFormValues(
  profile?: WorkSourceEnvironmentProfile,
): WorkSourceEnvironmentFormValues {
  const ref = profile?.personalAccessTokenReference ?? { name: '', version: null };
  return {
    key: profile?.key ?? '',
    displayName: profile?.displayName ?? '',
    enabled: profile?.enabled ?? true,
    provider: profile?.provider ?? 'AzureDevOpsBoards',
    organizationUrl: profile?.organizationUrl ?? '',
    project: profile?.project ?? '',
    tagPrefix: profile?.tagPrefix ?? '',
    activeState: profile?.activeState ?? '',
    completedState: profile?.completedState ?? '',
    secretName: ref.name ?? '',
    secretVersion: ref.version ?? null,
  };
}

export function validateWorkSourceEnvironmentForm(
  values: WorkSourceEnvironmentFormValues,
): WorkSourceEnvironmentFormErrors {
  const errors: WorkSourceEnvironmentFormErrors = {};

  addRequiredError(errors, 'key', values.key, 'A key is required.');
  addRequiredError(errors, 'displayName', values.displayName, 'A display name is required.');

  // Provider-specific validation
  if (values.provider === 'AzureDevOpsBoards') {
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

    if (values.organizationUrl.trim() && !isValidOrganizationUrl(values.organizationUrl)) {
      errors.organizationUrl = [
        'Enter an absolute HTTP or HTTPS organization URL without credentials, a query, or a fragment.',
      ];
    }

    if (
      values.secretVersion !== null &&
      values.secretVersion !== undefined &&
      values.secretVersion < 1
    ) {
      errors.secretVersion = [
        'The secret version must be 1 or greater.',
      ];
    }
  }

  if (
    values.activeState.trim() &&
    values.completedState.trim() &&
    values.activeState.trim().toLowerCase() === values.completedState.trim().toLowerCase()
  ) {
    errors.completedState = ['The completed state must be different from the active state.'];
  }

  return errors;
}

export function toWorkSourceEnvironmentProfile(
  values: WorkSourceEnvironmentFormValues,
  original?: WorkSourceEnvironmentProfile,
): WorkSourceEnvironmentProfile {
  const now = new Date().toISOString();
  const secretRef: PersonalAccessTokenSecretReference = {
    name: values.secretName.trim(),
    version: values.secretVersion ?? null,
  };
  return {
    key: values.key.trim(),
    displayName: values.displayName.trim(),
    enabled: values.enabled,
    provider: values.provider.trim() || 'AzureDevOpsBoards',
    organizationUrl: values.organizationUrl.trim().replace(/\/+$/, ''),
    project: values.project.trim(),
    tagPrefix: values.tagPrefix.trim() || 'agent',
    activeState: nullableText(values.activeState),
    completedState: nullableText(values.completedState),
    personalAccessTokenReference: secretRef,
    createdAt: original?.createdAt ?? now,
    updatedAt: original?.updatedAt ?? now,
  };
}

function addRequiredError(
  errors: WorkSourceEnvironmentFormErrors,
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

function nullableText(value: string): string | null {
  const normalized = value.trim();
  return normalized || null;
}
