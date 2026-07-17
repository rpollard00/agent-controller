import type { WorkSourceEnvironmentProfile } from '../../api/types';

export interface WorkSourceEnvironmentFormValues {
  key: string;
  displayName: string;
  enabled: boolean;
  provider: string;
  connectionKey: string;
  project: string;
  tagPrefix: string;
  activeState: string;
  completedState: string;
}

export type WorkSourceEnvironmentFormErrors = Record<string, string[]>;

export function createWorkSourceEnvironmentFormValues(
  profile?: WorkSourceEnvironmentProfile,
): WorkSourceEnvironmentFormValues {
  return {
    key: profile?.key ?? '',
    displayName: profile?.displayName ?? '',
    enabled: profile?.enabled ?? true,
    provider: profile?.provider ?? 'AzureDevOpsBoards',
    connectionKey: profile?.connectionKey ?? '',
    project: profile?.project ?? '',
    tagPrefix: profile?.tagPrefix ?? '',
    activeState: profile?.activeState ?? '',
    completedState: profile?.completedState ?? '',
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
      'connectionKey',
      values.connectionKey,
      'A connection is required.',
    );
    addRequiredError(errors, 'project', values.project, 'An Azure DevOps project is required.');
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
  return {
    key: values.key.trim(),
    displayName: values.displayName.trim(),
    enabled: values.enabled,
    provider: values.provider.trim() || 'AzureDevOpsBoards',
    connectionKey: values.connectionKey.trim(),
    project: values.project.trim(),
    tagPrefix: values.tagPrefix.trim() || 'agent',
    activeState: nullableText(values.activeState),
    completedState: nullableText(values.completedState),
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

function nullableText(value: string): string | null {
  const normalized = value.trim();
  return normalized || null;
}
