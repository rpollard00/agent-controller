import type { RuntimeEnvironmentProfile } from '../../api/types';

export type RuntimeProvider = 'PiMateria' | 'MockPiMateria';
export type EnvironmentProvider = 'LocalWorkspace';
export type ExecutionKind = '' | 'newWork' | 'rework';

export interface LoadoutRow {
  id: string;
  executionKind: ExecutionKind;
  loadout: string;
}

export interface RuntimeEnvironmentFormValues {
  key: string;
  displayName: string;
  enabled: boolean;
  environmentProvider: EnvironmentProvider;
  workspaceRoot: string;
  runtimeProvider: RuntimeProvider;
  loadouts: LoadoutRow[];
}

export type RuntimeEnvironmentFormErrors = Record<string, string[]>;

export function createRuntimeEnvironmentFormValues(
  profile?: RuntimeEnvironmentProfile,
): RuntimeEnvironmentFormValues {
  const loadouts = profile?.runtimeSettings.loadouts;

  return {
    key: profile?.key ?? '',
    displayName: profile?.displayName ?? '',
    enabled: profile?.enabled ?? true,
    environmentProvider: asEnvironmentProvider(profile?.environmentProvider),
    workspaceRoot: profile?.environmentSettings.workspaceRoot ?? '',
    runtimeProvider: asRuntimeProvider(profile?.runtimeProvider),
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
  };
}

export function validateRuntimeEnvironmentForm(
  values: RuntimeEnvironmentFormValues,
): RuntimeEnvironmentFormErrors {
  const errors: RuntimeEnvironmentFormErrors = {};

  addRequiredError(errors, 'key', values.key, 'An environment name is required.');
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
      'Use 1 to 32 characters starting with an ASCII letter, followed by ASCII letters, numbers, hyphens, or underscores.',
    ];
  }

  // Pi Materia process settings (executable, controller URL, PTY, env-var forwarding)
  // are controller-owned, so only loadouts remain as a per-profile runtime control.
  if (values.runtimeProvider === 'PiMateria') {
    validateLoadouts(values.loadouts, errors);
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
      // The form no longer collects controller-owned Pi process settings. They are
      // submitted as null/empty to satisfy the API shape while leaving execution
      // control to the controller; the service ignores and drops these values.
      piExecutablePath: null,
      controllerBaseUrl: null,
      ptyWrapperPath: null,
      ptyWrapperArgs: null,
      loadouts: usesPi
        ? Object.fromEntries(
            values.loadouts.map((row) => [row.executionKind, row.loadout.trim()]),
          )
        : {},
      forwardEnvironmentVariables: {},
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
