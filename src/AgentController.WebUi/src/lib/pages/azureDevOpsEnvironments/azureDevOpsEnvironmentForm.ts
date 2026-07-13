import type { AzureDevOpsEnvironmentProfile } from '../../api/types';

export interface AzureDevOpsEnvironmentFormValues {
  key: string;
  displayName: string;
  enabled: boolean;
  organizationUrl: string;
  project: string;
  workItemType: string;
  eligibleTags: string;
  excludedTags: string;
  eligibleStates: string;
  excludedStates: string;
  activeState: string;
  completedState: string;
  patEnvironmentVariable: string;
}

export type AzureDevOpsEnvironmentFormErrors = Record<string, string[]>;

export function createAzureDevOpsEnvironmentFormValues(
  profile?: AzureDevOpsEnvironmentProfile,
): AzureDevOpsEnvironmentFormValues {
  return {
    key: profile?.key ?? '',
    displayName: profile?.displayName ?? '',
    enabled: profile?.enabled ?? true,
    organizationUrl: profile?.organizationUrl ?? '',
    project: profile?.project ?? '',
    workItemType: profile?.workItemType ?? 'User Story',
    eligibleTags: profile?.eligibleTags.join('\n') ?? '',
    excludedTags: profile?.excludedTags.join('\n') ?? '',
    eligibleStates: profile?.eligibleStates.join('\n') ?? '',
    excludedStates: profile?.excludedStates.join('\n') ?? '',
    activeState: profile?.activeState ?? '',
    completedState: profile?.completedState ?? '',
    patEnvironmentVariable: profile?.patEnvironmentVariable ?? '',
  };
}

export function validateAzureDevOpsEnvironmentForm(
  values: AzureDevOpsEnvironmentFormValues,
): AzureDevOpsEnvironmentFormErrors {
  const errors: AzureDevOpsEnvironmentFormErrors = {};

  addRequiredError(errors, 'key', values.key, 'A key is required.');
  addRequiredError(errors, 'displayName', values.displayName, 'A display name is required.');
  addRequiredError(
    errors,
    'organizationUrl',
    values.organizationUrl,
    'An Azure DevOps organization URL is required.',
  );
  addRequiredError(errors, 'project', values.project, 'An Azure DevOps project is required.');
  addRequiredError(errors, 'workItemType', values.workItemType, 'A work-item type is required.');
  addRequiredError(
    errors,
    'patEnvironmentVariable',
    values.patEnvironmentVariable,
    'The PAT environment-variable name is required.',
  );

  if (values.organizationUrl.trim() && !isValidOrganizationUrl(values.organizationUrl)) {
    errors.organizationUrl = [
      'Enter an absolute HTTP or HTTPS organization URL without credentials, a query, or a fragment.',
    ];
  }

  if (
    values.patEnvironmentVariable.trim() &&
    !/^[A-Za-z_][A-Za-z0-9_]*$/.test(values.patEnvironmentVariable.trim())
  ) {
    errors.patEnvironmentVariable = [
      'Use an environment-variable name containing letters, numbers, and underscores that does not start with a number.',
    ];
  }

  if (
    values.activeState.trim() &&
    values.completedState.trim() &&
    values.activeState.trim().toLowerCase() === values.completedState.trim().toLowerCase()
  ) {
    errors.completedState = ['The completed state must be different from the active state.'];
  }

  addOverlapErrors(
    errors,
    parseBoardValues(values.eligibleTags),
    parseBoardValues(values.excludedTags),
    'excludedTags',
    'tag',
  );
  addOverlapErrors(
    errors,
    parseBoardValues(values.eligibleStates),
    parseBoardValues(values.excludedStates),
    'excludedStates',
    'state',
  );

  return errors;
}

export function toAzureDevOpsEnvironmentProfile(
  values: AzureDevOpsEnvironmentFormValues,
  original?: AzureDevOpsEnvironmentProfile,
): AzureDevOpsEnvironmentProfile {
  const now = new Date().toISOString();
  return {
    key: values.key.trim(),
    displayName: values.displayName.trim(),
    enabled: values.enabled,
    organizationUrl: values.organizationUrl.trim().replace(/\/+$/, ''),
    project: values.project.trim(),
    workItemType: values.workItemType.trim(),
    eligibleTags: parseBoardValues(values.eligibleTags),
    excludedTags: parseBoardValues(values.excludedTags),
    eligibleStates: parseBoardValues(values.eligibleStates),
    excludedStates: parseBoardValues(values.excludedStates),
    activeState: nullableText(values.activeState),
    completedState: nullableText(values.completedState),
    patEnvironmentVariable: values.patEnvironmentVariable.trim(),
    createdAt: original?.createdAt ?? now,
    updatedAt: original?.updatedAt ?? now,
  };
}

function addRequiredError(
  errors: AzureDevOpsEnvironmentFormErrors,
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

function parseBoardValues(value: string): string[] {
  return value
    .split(/\r?\n/)
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

function addOverlapErrors(
  errors: AzureDevOpsEnvironmentFormErrors,
  eligible: string[],
  excluded: string[],
  field: string,
  valueKind: string,
): void {
  const eligibleValues = new Set(eligible.map((value) => value.toLowerCase()));
  const overlap = excluded.find((value) => eligibleValues.has(value.toLowerCase()));
  if (overlap) errors[field] = [`The ${valueKind} “${overlap}” cannot be both eligible and excluded.`];
}

function nullableText(value: string): string | null {
  const normalized = value.trim();
  return normalized || null;
}
