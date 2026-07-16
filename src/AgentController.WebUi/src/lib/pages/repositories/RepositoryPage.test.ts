import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../../App.svelte';
import { ApiError, type ResourceClient, type WebUiApiClient } from '../../api/client';
import type {
  RepositoryHostConnectionProfile,
  RepositoryProfile,
  RuntimeEnvironmentProfile,
  WorkSourceEnvironmentProfile,
} from '../../api/types';

const repository: RepositoryProfile = {
  key: 'web.repo',
  cloneUrl: 'https://example.test/web.repo.git',
  defaultBranch: 'main',
  transport: 'httpsPat',
  environmentProfile: 'legacy-environment',
  runtimeProfile: 'legacy-runtime',
  repositoryHostConnectionKey: null,
  remoteIdentity: null,
  runtimeEnvironmentKey: 'runtime-main',
  allowedPaths: ['src'],
};

const repositoryHostConnection: RepositoryHostConnectionProfile = {
  key: 'ado-main',
  displayName: 'Primary repos',
  enabled: true,
  provider: 'AzureDevOpsRepos',
  organizationUrl: 'https://dev.azure.com/example',
  project: 'Agent Controller',
  personalAccessTokenReference: { kind: 'EnvVar', id: 'ADO_PAT' },
  createdAt: '2026-07-13T00:00:00Z',
  updatedAt: '2026-07-13T00:00:00Z',
};

const workSourceEnvironment: WorkSourceEnvironmentProfile = {
  key: 'ado-main',
  displayName: 'Primary boards',
  enabled: true,
  provider: 'AzureDevOpsBoards',
  organizationUrl: 'https://dev.azure.com/example',
  project: 'Agent Controller',
  tagPrefix: 'agent',
  activeState: 'Active',
  completedState: 'Done',
  personalAccessTokenReference: { name: 'ADO_PAT', version: null },
  createdAt: '2026-07-13T00:00:00Z',
  updatedAt: '2026-07-13T00:00:00Z',
};

const runtimeEnvironment: RuntimeEnvironmentProfile = {
  key: 'runtime-main',
  displayName: 'Primary runtime',
  enabled: true,
  environmentProvider: 'local',
  environmentSettings: { workspaceRoot: '/work' },
  runtimeProvider: 'pi',
  runtimeSettings: {
    piExecutablePath: '/usr/bin/pi',
    controllerBaseUrl: null,
    ptyWrapperPath: null,
    ptyWrapperArgs: null,
    loadouts: {},
    forwardEnvironmentVariables: {},
  },
  createdAt: '2026-07-13T00:00:00Z',
  updatedAt: '2026-07-13T00:00:00Z',
};

interface MockApi {
  client: WebUiApiClient;
  repositories: ResourceClient<RepositoryProfile> & {
    create: ReturnType<typeof vi.fn>;
    update: ReturnType<typeof vi.fn>;
    delete: ReturnType<typeof vi.fn>;
  };
}

function createApi(initialRepositories: RepositoryProfile[] = [repository]): MockApi {
  let profiles = [...initialRepositories];

  const repositories = {
    list: vi.fn(async () => [...profiles]),
    get: vi.fn(async (key: string) => {
      const profile = profiles.find((candidate) => candidate.key === key);
      if (!profile) throw new Error(`Missing repository ${key}.`);
      return profile;
    }),
    create: vi.fn(async (profile: RepositoryProfile) => {
      profiles.push(profile);
      return profile;
    }),
    update: vi.fn(async (_key: string, profile: RepositoryProfile) => {
      profiles = profiles.map((candidate) => (candidate.key === profile.key ? profile : candidate));
      return profile;
    }),
    delete: vi.fn(async (key: string) => {
      profiles = profiles.filter((candidate) => candidate.key !== key);
    }),
  };

  return {
    client: {
      repositories,
      workSourceEnvironments: {
        ...staticResource([workSourceEnvironment]),
        verifyConnection: async () => ({
          success: true,
          authMechanism: 'PersonalAccessToken',
          errors: [],
        }),
      },
      repositoryHostConnections: {
        ...staticResource<RepositoryHostConnectionProfile>([repositoryHostConnection]),
        verifyConnection: async () => ({
          success: true,
          authMechanism: 'PersonalAccessToken',
          errors: [],
        }),
        listRepositories: async () => [],
        onboardRepository: async () => repository,
      },
      runtimeEnvironments: staticResource([runtimeEnvironment]),
    },
    repositories,
  };
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

async function completeRequiredCreateFields(): Promise<void> {
  await fireEvent.input(await screen.findByLabelText(/Repository key/), {
    target: { value: 'new.repo' },
  });
  await fireEvent.input(screen.getByLabelText(/Clone URL or local path/), {
    target: { value: 'https://example.test/new.repo.git' },
  });
}

describe('repository onboarding screens', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/repositories');
  });

  it('creates a repository with path and managed environment selections', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(await screen.findByRole('heading', { level: 1, name: 'Onboard repository' })).toBeVisible();
    await completeRequiredCreateFields();
    await fireEvent.change(screen.getByLabelText(/Clone transport/), {
      target: { value: 'ssh' },
    });
    await fireEvent.input(screen.getByLabelText(/Allowed paths/), {
      target: { value: 'src\ntests/integration' },
    });
    await fireEvent.change(screen.getByLabelText('Repository host connection'), {
      target: { value: 'ado-main' },
    });
    await fireEvent.change(screen.getByLabelText('Runtime environment'), {
      target: { value: 'runtime-main' },
    });
    await fireEvent.click(screen.getByRole('button', { name: 'Onboard repository' }));

    await waitFor(() => expect(api.repositories.create).toHaveBeenCalledOnce());
    expect(api.repositories.create).toHaveBeenCalledWith(
      expect.objectContaining({
        key: 'new.repo',
        cloneUrl: 'https://example.test/new.repo.git',
        defaultBranch: 'main',
        transport: 'ssh',
        allowedPaths: ['src', 'tests/integration'],
        repositoryHostConnectionKey: 'ado-main',
        runtimeEnvironmentKey: 'runtime-main',
      }),
      expect.any(AbortSignal),
    );
    expect(await screen.findByText('Repository “new.repo” was onboarded.')).toBeVisible();
    expect(window.location.pathname).toBe('/repositories/new.repo');
    expect(screen.queryByText('Primary boards', { exact: false })).not.toBeInTheDocument();
  });

  it('validates required fields before submitting', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await fireEvent.click(
      await screen.findByRole('button', { name: 'Onboard repository' }),
    );

    expect(await screen.findByText('Complete the required fields')).toBeVisible();
    expect(screen.getByText('A repository key is required.')).toBeVisible();
    expect(screen.getByText('A clone URL or local path is required.')).toBeVisible();
    expect(api.repositories.create).not.toHaveBeenCalled();
  });

  it('updates repository settings while preserving the immutable key', async () => {
    window.history.replaceState({}, '', '/repositories/web.repo/edit');
    const api = createApi();
    render(App, { client: api.client });

    const keyInput = await screen.findByLabelText(/Repository key/);
    expect(keyInput).toHaveAttribute('readonly');
    expect(screen.getByText(/Keys are immutable/)).toBeVisible();

    await fireEvent.input(screen.getByLabelText(/Default branch/), {
      target: { value: 'develop' },
    });
    await fireEvent.input(screen.getByLabelText(/Allowed paths/), {
      target: { value: 'src/web' },
    });
    await fireEvent.change(screen.getByLabelText('Runtime environment'), {
      target: { value: '' },
    });
    await fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.repositories.update).toHaveBeenCalledOnce());
    expect(api.repositories.update).toHaveBeenCalledWith(
      'web.repo',
      expect.objectContaining({
        key: 'web.repo',
        defaultBranch: 'develop',
        allowedPaths: ['src/web'],
        runtimeEnvironmentKey: null,
        environmentProfile: 'legacy-environment',
        runtimeProfile: 'legacy-runtime',
      }),
      expect.any(AbortSignal),
    );
    expect(await screen.findByText('Repository “web.repo” was updated.')).toBeVisible();
  });

  it('displays server ProblemDetails and field errors from a rejected save', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const api = createApi([]);
    api.repositories.create = vi.fn(async () => {
      throw new ApiError({
        title: 'Validation failed.',
        status: 400,
        detail: 'Correct the highlighted repository fields.',
        errors: { cloneUrl: ['Clone URL is not valid for the selected transport.'] },
      });
    });
    render(App, { client: api.client });

    await screen.findByRole('heading', { level: 1, name: 'Onboard repository' });
    await completeRequiredCreateFields();
    await fireEvent.click(screen.getByRole('button', { name: 'Onboard repository' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Could not onboard repository');
    expect(alert).toHaveTextContent('Correct the highlighted repository fields.');
    expect(alert).toHaveTextContent('cloneUrl: Clone URL is not valid for the selected transport.');
    expect(screen.getByText('Clone URL is not valid for the selected transport.')).toBeVisible();
  });

  it('requires confirmation before deleting a repository', async () => {
    const api = createApi();
    render(App, { client: api.client });

    expect(await screen.findByRole('link', { name: 'web.repo' })).toBeVisible();
    await fireEvent.click(screen.getByRole('button', { name: 'Delete web.repo' }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete repository?' });
    expect(api.repositories.delete).not.toHaveBeenCalled();
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete repository' }));

    await waitFor(() => expect(api.repositories.delete).toHaveBeenCalledWith(
      'web.repo',
      expect.any(AbortSignal),
    ));
    expect(await screen.findByRole('heading', { name: 'No repositories yet' })).toBeVisible();
    expect(screen.getByText('Repository “web.repo” was deleted.')).toBeVisible();
  });
});
