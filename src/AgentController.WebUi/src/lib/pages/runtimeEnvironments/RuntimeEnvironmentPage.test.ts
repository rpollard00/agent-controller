import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../../App.svelte';
import { ApiError, type ResourceClient, type WebUiApiClient } from '../../api/client';
import type {
  RepositoryProfile,
  RuntimeEnvironmentProfile,
  WorkSourceEnvironmentProfile,
} from '../../api/types';

const environment: RuntimeEnvironmentProfile = {
  key: 'runtime-main',
  displayName: 'Primary runtime',
  enabled: true,
  environmentProvider: 'LocalWorkspace',
  environmentSettings: { workspaceRoot: '/srv/agent-controller/workspaces' },
  runtimeProvider: 'PiMateria',
  runtimeSettings: {
    piExecutablePath: '/usr/local/bin/pi',
    controllerBaseUrl: 'https://controller.example.test',
    ptyWrapperPath: 'script',
    ptyWrapperArgs: '-qfc',
    loadouts: {
      newWork: 'ADO-Build-NewWork',
      rework: 'ADO-Build-Rework',
    },
    forwardEnvironmentVariables: {
      AZURE_DEVOPS_EXT_PAT: 'AZURE_DEVOPS_PAT',
      AZURE_DEVOPS_PAT: 'AZURE_DEVOPS_PAT',
    },
  },
  createdAt: '2026-07-13T00:00:00Z',
  updatedAt: '2026-07-13T00:00:00Z',
};

function createApi(initialEnvironments: RuntimeEnvironmentProfile[] = [environment]) {
  let profiles = [...initialEnvironments];

  const runtimeEnvironments = {
    list: vi.fn(async () => [...profiles]),
    get: vi.fn(async (key: string) => {
      const profile = profiles.find((candidate) => candidate.key === key);
      if (!profile) throw new Error(`Missing runtime environment ${key}.`);
      return profile;
    }),
    create: vi.fn(async (profile: RuntimeEnvironmentProfile) => {
      profiles.push(profile);
      return profile;
    }),
    update: vi.fn(async (_key: string, profile: RuntimeEnvironmentProfile) => {
      profiles = profiles.map((candidate) => (candidate.key === profile.key ? profile : candidate));
      return profile;
    }),
    delete: vi.fn(async (key: string) => {
      profiles = profiles.filter((candidate) => candidate.key !== key);
    }),
  };

  const client: WebUiApiClient = {
    repositories: staticResource<RepositoryProfile>([]),
    workSourceEnvironments: staticResource<WorkSourceEnvironmentProfile>([]),
    runtimeEnvironments,
  };

  return { client, environments: runtimeEnvironments };
}

function staticResource<T>(profiles: T[]): ResourceClient<T> {
  return {
    list: vi.fn(async () => profiles),
    get: vi.fn(async () => profiles[0]),
    create: vi.fn(async (profile: T) => profile),
    update: vi.fn(async (_key: string, profile: T) => profile),
    delete: vi.fn(async () => undefined),
  };
}

async function completeIdentityFields(): Promise<void> {
  await fireEvent.input(await screen.findByLabelText(/Environment name/), {
    target: { value: 'runtime-secondary' },
  });
  await fireEvent.input(screen.getByLabelText(/Display name/), {
    target: { value: 'Secondary runtime' },
  });
}

describe('runtime environment screens', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/runtime-environments');
  });

  it('lists environments and shows loadout readouts without internal process or variable settings', async () => {
    const responseWithUnexpectedSecret = {
      ...environment,
      runtimeSettings: {
        ...environment.runtimeSettings,
        resolvedEnvironmentVariables: { AZURE_DEVOPS_PAT: 'super-secret-value' },
      },
    } as RuntimeEnvironmentProfile;
    const api = createApi([responseWithUnexpectedSecret]);
    render(App, { client: api.client });

    const environmentLink = await screen.findByRole('link', { name: 'Primary runtime' });
    expect(screen.getByText('Enabled')).toBeVisible();

    await fireEvent.click(environmentLink);

    expect(await screen.findByRole('heading', { level: 1, name: 'runtime-main' })).toBeVisible();
    expect(screen.getByRole('heading', { name: 'Loadout mappings' })).toBeVisible();
    expect(screen.getByText('ADO-Build-NewWork')).toBeVisible();
    // Internal Pi-process and environment-variable settings are never surfaced.
    expect(screen.queryByRole('heading', { name: 'Pi process settings' })).not.toBeInTheDocument();
    expect(screen.queryByText('/usr/local/bin/pi')).not.toBeInTheDocument();
    expect(screen.queryByText('https://controller.example.test')).not.toBeInTheDocument();
    expect(screen.queryByText('Controller base URL')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'Environment-variable forwarding' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('Secret values are never shown')).not.toBeInTheDocument();
    // The page description avoids internal process/variable copy.
    expect(
      screen.queryByText(/runtime process settings|variable references/),
    ).not.toBeInTheDocument();
    // Resolved secret values are never surfaced, even when present in the response.
    expect(screen.queryByText('super-secret-value')).not.toBeInTheDocument();
    expect(window.location.pathname).toBe('/runtime-environments/runtime-main');
  });

  it('creates a Pi environment after editing the loadout map', async () => {
    window.history.replaceState({}, '', '/runtime-environments/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Add runtime environment' }),
    ).toBeVisible();
    expect(screen.getByText('Loadout mappings')).toBeVisible();
    expect(screen.getByPlaceholderText('contoso-dev')).toBeVisible();
    expect(screen.getByPlaceholderText('Contoso Software Development')).toBeVisible();

    await completeIdentityFields();
    await fireEvent.input(screen.getAllByLabelText('Loadout name')[0], {
      target: { value: 'Custom-NewWork' },
    });
    await fireEvent.click(screen.getByRole('button', { name: 'Remove loadout mapping 2' }));
    await fireEvent.click(screen.getByRole('button', { name: 'Create environment' }));

    await waitFor(() => expect(api.environments.create).toHaveBeenCalledOnce());
    expect(api.environments.create).toHaveBeenCalledWith(
      expect.objectContaining({
        key: 'runtime-secondary',
        displayName: 'Secondary runtime',
        enabled: true,
        environmentProvider: 'LocalWorkspace',
        environmentSettings: { workspaceRoot: null },
        runtimeProvider: 'PiMateria',
        runtimeSettings: expect.objectContaining({
          piExecutablePath: null,
          controllerBaseUrl: null,
          ptyWrapperPath: null,
          ptyWrapperArgs: null,
          loadouts: { newWork: 'Custom-NewWork' },
          forwardEnvironmentVariables: {},
        }),
      }),
      expect.any(AbortSignal),
    );
    expect(await screen.findByText('Runtime environment “Secondary runtime” was created.')).toBeVisible();
    expect(window.location.pathname).toBe('/runtime-environments/runtime-secondary');
  });

  it('switches to the mock provider and omits Pi-only settings', async () => {
    window.history.replaceState({}, '', '/runtime-environments/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await completeIdentityFields();
    await fireEvent.change(screen.getByLabelText(/Runtime provider/), {
      target: { value: 'MockPiMateria' },
    });

    expect(screen.getByText('No loadout mapping required')).toBeVisible();
    expect(screen.queryByLabelText(/Pi executable/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add variable mapping' })).not.toBeInTheDocument();
    await fireEvent.click(screen.getByRole('button', { name: 'Create environment' }));

    await waitFor(() => expect(api.environments.create).toHaveBeenCalledOnce());
    expect(api.environments.create).toHaveBeenCalledWith(
      expect.objectContaining({
        runtimeProvider: 'MockPiMateria',
        runtimeSettings: {
          piExecutablePath: null,
          controllerBaseUrl: null,
          ptyWrapperPath: null,
          ptyWrapperArgs: null,
          loadouts: {},
          forwardEnvironmentVariables: {},
        },
      }),
      expect.any(AbortSignal),
    );
  });

  it('rejects an invalid environment name before submitting', async () => {
    window.history.replaceState({}, '', '/runtime-environments/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await fireEvent.input(await screen.findByLabelText(/Environment name/), {
      target: { value: '1bad.name' },
    });
    await fireEvent.input(screen.getByLabelText(/Display name/), {
      target: { value: 'Bad runtime' },
    });
    await fireEvent.click(screen.getByRole('button', { name: 'Create environment' }));

    expect(
      await screen.findByText(
        'Use 1 to 32 characters starting with an ASCII letter, followed by ASCII letters, numbers, hyphens, or underscores.',
      ),
    ).toBeVisible();
    expect(api.environments.create).not.toHaveBeenCalled();
  });

  it('edits identity and workspace settings while preserving the immutable key', async () => {
    window.history.replaceState({}, '', '/runtime-environments/runtime-main/edit');
    const api = createApi();
    render(App, { client: api.client });

    const keyInput = await screen.findByLabelText(/Environment name/);
    expect(keyInput).toHaveAttribute('readonly');

    await fireEvent.input(screen.getByLabelText(/Display name/), {
      target: { value: 'Updated runtime' },
    });
    await fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.environments.update).toHaveBeenCalledOnce());
    expect(api.environments.update).toHaveBeenCalledWith(
      'runtime-main',
      expect.objectContaining({
        key: 'runtime-main',
        displayName: 'Updated runtime',
        environmentSettings: { workspaceRoot: null },
        runtimeSettings: expect.objectContaining({
          piExecutablePath: null,
          controllerBaseUrl: null,
          ptyWrapperPath: null,
          ptyWrapperArgs: null,
          loadouts: {
            newWork: 'ADO-Build-NewWork',
            rework: 'ADO-Build-Rework',
          },
          forwardEnvironmentVariables: {},
        }),
        createdAt: environment.createdAt,
      }),
      expect.any(AbortSignal),
    );
    expect(await screen.findByText('Runtime environment “Updated runtime” was updated.')).toBeVisible();
  });

  it('shows field-level server validation errors', async () => {
    window.history.replaceState({}, '', '/runtime-environments/runtime-main/edit');
    const api = createApi();
    api.environments.update.mockRejectedValueOnce(
      new ApiError({
        title: 'Validation failed.',
        status: 400,
        detail: 'Correct the highlighted runtime fields.',
        errors: {
          displayName: [
            'A display name with that value is already in use.',
          ],
        },
      }),
    );
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Save changes' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Could not update environment');
    expect(alert).toHaveTextContent('Correct the highlighted runtime fields.');
    expect(screen.getByText('A display name with that value is already in use.')).toBeVisible();
    expect(screen.getByLabelText(/Display name/)).toHaveAttribute('aria-invalid', 'true');
  });

  it('enables and disables an environment from the list', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Disable Primary runtime' }));

    await waitFor(() => expect(api.environments.update).toHaveBeenCalledOnce());
    expect(api.environments.update).toHaveBeenCalledWith(
      'runtime-main',
      expect.objectContaining({ enabled: false }),
      expect.any(AbortSignal),
    );
    expect(await screen.findByText('Environment disabled')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Enable Primary runtime' })).toBeVisible();
  });

  it('deletes an environment only after confirmation', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Delete Primary runtime' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete runtime environment?' });
    expect(api.environments.delete).not.toHaveBeenCalled();
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete environment' }));

    await waitFor(() =>
      expect(api.environments.delete).toHaveBeenCalledWith(
        'runtime-main',
        expect.any(AbortSignal),
      ),
    );
    expect(await screen.findByRole('heading', { name: 'No runtime environments yet' })).toBeVisible();
    expect(screen.getByText('Runtime environment “Primary runtime” was deleted.')).toBeVisible();
  });

  it('keeps the profile and explains repository reference conflicts on delete', async () => {
    const api = createApi();
    api.environments.delete.mockRejectedValueOnce(
      new ApiError({
        title: 'Resource conflict.',
        status: 409,
        detail: "Runtime environment 'runtime-main' is referenced by repository 'web.repo'.",
      }),
    );
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Delete Primary runtime' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete runtime environment?' });
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete environment' }));

    expect(
      await within(dialog).findByText(
        "Runtime environment 'runtime-main' is referenced by repository 'web.repo'.",
      ),
    ).toBeVisible();
    expect(api.environments.delete).toHaveBeenCalledWith(
      'runtime-main',
      expect.any(AbortSignal),
    );
    expect(screen.getByRole('link', { name: 'Primary runtime' })).toBeVisible();
  });
});
