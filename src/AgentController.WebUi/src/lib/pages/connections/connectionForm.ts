import type {
  AzureDevOpsConnectionSettings,
  ConnectionCapability,
  ConnectionProfile,
  ConnectionSecretReference,
} from '../../api/types';

export interface ConnectionFormValues {
  key: string;
  displayName: string;
  enabled: boolean;
  provider: string;
  capabilities: ConnectionCapability[];
  organizationUrl: string;
  secretName: string;
  secretVersion: number | null;
}

export type ConnectionFormErrors = Record<string, string[]>;

const ALL_CAPABILITIES: ConnectionCapability[] = ['Repositories', 'WorkTracking', 'ExecutionHost'];

export function createConnectionFormValues(
  profile?: ConnectionProfile,
): ConnectionFormValues {
  const adoSettings =
    profile?.providerSettings && profile.providerSettings.organizationUrl
      ? profile.providerSettings
      : null;
  const ref = adoSettings?.personalAccessTokenReference ?? { name: '', version: null };
  return {
    key: profile?.key ?? '',
    displayName: profile?.displayName ?? '',
    enabled: profile?.enabled ?? true,
    provider: profile?.provider ?? 'AzureDevOps',
    capabilities: profile?.capabilities ?? ['Repositories', 'WorkTracking'],
    organizationUrl: adoSettings?.organizationUrl ?? '',
    secretName: ref.name ?? '',
    secretVersion: ref.version ?? null,
  };
}

export function validateConnectionForm(
  values: ConnectionFormValues,
): ConnectionFormErrors {
  const errors: ConnectionFormErrors = {};

  addRequiredError(errors, 'key', values.key, 'A key is required.');
  addRequiredError(errors, 'displayName', values.displayName, 'A display name is required.');

  if (values.capabilities.length === 0) {
    errors.capabilities = ['At least one capability is required.'];
  }

  if (values.provider === 'AzureDevOps') {
    addRequiredError(
      errors,
      'organizationUrl',
      values.organizationUrl,
      'An Azure DevOps organization URL is required.',
    );
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

export function toConnectionProfile(
  values: ConnectionFormValues,
  original?: ConnectionProfile,
): ConnectionProfile {
  const now = new Date().toISOString();
  const providerSettings: AzureDevOpsConnectionSettings | null =
    values.provider === 'AzureDevOps'
      ? {
          organizationUrl: values.organizationUrl.trim().replace(/\/+$/, ''),
          personalAccessTokenReference: {
            name: values.secretName.trim(),
            version: values.secretVersion ?? null,
          } as ConnectionSecretReference,
        }
      : null;

  return {
    key: values.key.trim(),
    displayName: values.displayName.trim(),
    enabled: values.enabled,
    provider: values.provider.trim() || 'AzureDevOps',
    capabilities: values.capabilities.length > 0 ? values.capabilities : ['Repositories'],
    providerSettings,
    createdAt: original?.createdAt ?? now,
    updatedAt: original?.updatedAt ?? now,
  };
}

export function getCapabilityLabel(capability: ConnectionCapability): string {
  switch (capability) {
    case 'Repositories': return 'Repository hosting and discovery';
    case 'WorkTracking': return 'Work-item tracking';
    case 'ExecutionHost': return 'Runtime execution host';
    default: return capability;
  }
}

function addRequiredError(
  errors: ConnectionFormErrors,
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
