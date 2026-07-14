import type { RuntimeEnvironmentProfile } from '../../api/types';

export type RuntimeProvider = 'PiMateria' | 'MockPiMateria';
export type EnvironmentProvider = 'LocalWorkspace';
export type ExecutionKind = '' | 'newWork' | 'rework';

export interface LoadoutRow {
  id: string;
  executionKind: ExecutionKind;
  loadout: string;
}

export interface EnvironmentVariableRow {
  id: string;
  target: string;
  source: string;
}

export interface RuntimeEnvironmentFormValues {
  key: string;
  displayName: string;
  enabled: boolean;
  environmentProvider: EnvironmentProvider;
  workspaceRoot: string;
  runtimeProvider: RuntimeProvider;
  piExecutablePath: string;
  controllerBaseUrl: string;
  ptyWrapperPath: string;
  ptyWrapperArgs: string;
  loadouts: LoadoutRow[];
  forwardEnvironmentVariables: EnvironmentVariableRow[];
}

export type RuntimeEnvironmentFormErrors = Record<string, string[]>;

export function createRuntimeEnvironmentFormValues(
  profile?: RuntimeEnvironmentProfile,
): RuntimeEnvironmentFormValues {
  const loadouts = profile?.runtimeSettings.loadouts;
  const mappings = profile?.runtimeSettings.forwardEnvironmentVariables;

  return {
    key: profile?.key ?? '',
    displayName: profile?.displayName ?? '',
    enabled: profile?.enabled ?? true,
    environmentProvider: asEnvironmentProvider(profile?.environmentProvider),
    workspaceRoot: profile?.environmentSettings.workspaceRoot ?? '',
    runtimeProvider: asRuntimeProvider(profile?.runtimeProvider),
    piExecutablePath: profile?.runtimeSettings.piExecutablePath ?? 'pi',
    controllerBaseUrl: profile?.runtimeSettings.controllerBaseUrl ?? '',
    ptyWrapperPath: profile?.runtimeSettings.ptyWrapperPath ?? 'script',
    ptyWrapperArgs: profile?.runtimeSettings.ptyWrapperArgs ?? '-qfc',
    loadouts: loadouts
      ? (Object.entries(loadouts) as [Exclude<ExecutionKind, ''>, string][]).map(
          ([executionKind, loadout], index) => ({
            id: `loadout-${index}`,
            executionKind,
            loadout,
          }),
        )
      : [
          { id: 'loadout-0', executionKind: 'newWork', loadout: 'ADO-Build-NewWork' },
          { id: 'loadout-1', executionKind: 'rework', loadout: 'ADO-Build-Rework' },
        ],
    forwardEnvironmentVariables: mappings
      ? Object.entries(mappings).map(([target, source], index) => ({
          id: `variable-${index}`,
          target,
          source,
        }))
      : [
          {
            id: 'variable-0',
            target: 'AZURE_DEVOPS_EXT_PAT',
            source: 'AZURE_DEVOPS_PAT',
          },
          {
            id: 'variable-1',
            target: 'AZURE_DEVOPS_PAT',
            source: 'AZURE_DEVOPS_PAT',
          },
        ],
  };
}

export function validateRuntimeEnvironmentForm(
  values: RuntimeEnvironmentFormValues,
): RuntimeEnvironmentFormErrors {
  const errors: RuntimeEnvironmentFormErrors = {};

  addRequiredError(errors, 'key', values.key, 'A key is required.');
  addRequiredError(errors, 'displayName', values.displayName, 'A display name is required.');
  addRequiredError(
    errors,
    'environmentProvider',
    values.environmentProvider,
    'An environment provider is required.',
  );
  addRequiredError(
    errors,
    'runtimeProvider',
    values.runtimeProvider,
    'A runtime provider is required.',
  );

  const key = values.key.trim().toLowerCase();
  if (key && !/^[a-z][a-z0-9_-]{0,31}$/.test(key)) {
    errors.key = [
      'Use 1 to 32 characters starting with a letter and only letters, numbers, hyphens, or underscores.',
    ];
  }

  if (values.runtimeProvider === 'PiMateria') {
    addRequiredError(
      errors,
      'runtimeSettings.piExecutablePath',
      values.piExecutablePath,
      'A pi executable path or command is required.',
    );
    addRequiredError(
      errors,
      'runtimeSettings.controllerBaseUrl',
      values.controllerBaseUrl,
      'A controller base URL is required.',
    );

    if (values.controllerBaseUrl.trim() && !isValidControllerUrl(values.controllerBaseUrl)) {
      errors['runtimeSettings.controllerBaseUrl'] = [
        'Enter an absolute HTTP or HTTPS URL without credentials, a query, or a fragment.',
      ];
    }

    validateLoadouts(values.loadouts, errors);
    validateEnvironmentVariables(values.forwardEnvironmentVariables, errors);
  }

  return errors;
}

export function toRuntimeEnvironmentProfile(
  values: RuntimeEnvironmentFormValues,
  original?: RuntimeEnvironmentProfile,
): RuntimeEnvironmentProfile {
  const now = new Date().toISOString();
  const usesPi = values.runtimeProvider === 'PiMateria';

  return {
    key: values.key.trim().toLowerCase(),
    displayName: values.displayName.trim(),
    enabled: values.enabled,
    environmentProvider: values.environmentProvider,
    environmentSettings: {
      workspaceRoot: nullableText(values.workspaceRoot),
    },
    runtimeProvider: values.runtimeProvider,
    runtimeSettings: {
      piExecutablePath: usesPi ? nullableText(values.piExecutablePath) : null,
      controllerBaseUrl: usesPi
        ? nullableText(values.controllerBaseUrl)?.replace(/\/+$/, '') ?? null
        : null,
      ptyWrapperPath: usesPi ? nullableText(values.ptyWrapperPath) : null,
      ptyWrapperArgs:
        usesPi && values.ptyWrapperPath.trim() ? nullableText(values.ptyWrapperArgs) : null,
      loadouts: usesPi
        ? Object.fromEntries(
            values.loadouts.map((row) => [row.executionKind, row.loadout.trim()]),
          )
        : {},
      forwardEnvironmentVariables: usesPi
        ? Object.fromEntries(
            values.forwardEnvironmentVariables.map((row) => [row.target.trim(), row.source.trim()]),
          )
        : {},
    },
    createdAt: original?.createdAt ?? now,
    updatedAt: original?.updatedAt ?? now,
  };
}

function asEnvironmentProvider(provider?: string): EnvironmentProvider {
  return provider === 'LocalWorkspace' ? provider : 'LocalWorkspace';
}

function asRuntimeProvider(provider?: string): RuntimeProvider {
  return provider === 'MockPiMateria' ? provider : 'PiMateria';
}

function addRequiredError(
  errors: RuntimeEnvironmentFormErrors,
  field: string,
  value: string,
  message: string,
): void {
  if (!value.trim()) errors[field] = [message];
}

function isValidControllerUrl(value: string): boolean {
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

function validateLoadouts(
  rows: LoadoutRow[],
  errors: RuntimeEnvironmentFormErrors,
): void {
  const messages: string[] = [];
  const kinds = new Set<ExecutionKind>();

  for (const row of rows) {
    if (!row.executionKind) messages.push('Choose an execution kind for every loadout.');
    if (!row.loadout.trim()) messages.push('Enter a loadout name for every execution kind.');
    if (row.executionKind && kinds.has(row.executionKind)) {
      messages.push(`The ${executionKindLabel(row.executionKind)} execution kind is mapped more than once.`);
    }
    kinds.add(row.executionKind);
  }

  if (!kinds.has('newWork')) messages.push('A New work loadout is required.');
  if (messages.length > 0) errors['runtimeSettings.loadouts'] = unique(messages);
}

function validateEnvironmentVariables(
  rows: EnvironmentVariableRow[],
  errors: RuntimeEnvironmentFormErrors,
): void {
  const messages: string[] = [];
  const targets = new Set<string>();

  for (const row of rows) {
    const target = row.target.trim();
    const source = row.source.trim();
    if (!isEnvironmentVariableName(target)) {
      messages.push('Every target must be an environment-variable name.');
    }
    if (!isEnvironmentVariableName(source)) {
      messages.push('Every source must be an environment-variable name, never a secret value.');
    }
    if (target && targets.has(target)) {
      messages.push(`Target environment variable “${target}” is mapped more than once.`);
    }
    targets.add(target);
  }

  if (messages.length > 0) {
    errors['runtimeSettings.forwardEnvironmentVariables'] = unique(messages);
  }
}

function isEnvironmentVariableName(value: string): boolean {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(value);
}

function executionKindLabel(value: Exclude<ExecutionKind, ''>): string {
  return value === 'newWork' ? 'New work' : 'Rework';
}

function unique(messages: string[]): string[] {
  return [...new Set(messages)];
}

function nullableText(value: string): string | null {
  const normalized = value.trim();
  return normalized || null;
}
