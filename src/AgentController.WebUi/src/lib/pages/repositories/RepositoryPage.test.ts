import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../../App.svelte';
import { ApiError, type ResourceClient, type WebUiApiClient } from '../../api/client';
import type {
  ClonePreflightResult,
  ConnectionProfile,
  ConnectionProject,
  RepositoryProfile,
  RuntimeEnvironmentProfile,
  SecretInfo,
  SecretVersionInfo,
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
  sshKeyReference: null,
  project: null,
};

const connection: ConnectionProfile = {
  key: 'ado-main',
  displayName: 'Primary ADO',
  enabled: true,
  provider: 'AzureDevOps',
  capabilities: ['Repositories', 'WorkTracking'],
  providerSettings: {
    provider: 'AzureDevOps',
    organizationUrl: 'https://dev.azure.com/example',
    personalAccessTokenReference: { name: 'ADO_PAT', version: null },
  },
  createdAt: '2026-07-13T00:00:00Z',
  updatedAt: '2026-07-13T00:00:00Z',
};

const projects: ConnectionProject[] = [
  { id: 'proj-1', name: 'Agent Controller' },
];

const secrets: SecretInfo[] = [
  {
    name: 'ado-pat',
    secretType: 'personal-access-token',
    latestVersion: 3,
    createdAt: '2026-07-13T00:00:00Z',
    updatedAt: '2026-07-15T00:00:00Z',
  },
  {
    name: 'repository-deploy-key',
    secretType: 'ssh-key',
    latestVersion: 2,
    createdAt: '2026-07-13T00:00:00Z',
    updatedAt: '2026-07-15T00:00:00Z',
  },
];

const sshKeyVersions: SecretVersionInfo[] = [
  {
    version: 1,
    secretType: 'ssh-key',
    publicKey: 'ssh-ed25519 AAAA version-1',
    createdAt: '2026-07-13T00:00:00Z',
  },
  {
    version: 2,
    secretType: 'ssh-key',
    publicKey: 'ssh-ed25519 AAAA version-2',
    createdAt: '2026-07-15T00:00:00Z',
  },
];

const workSourceEnvironment: WorkSourceEnvironmentProfile = {
  key: 'ado-main',
  displayName: 'Primary boards',
  enabled: true,
  provider: 'AzureDevOpsBoards',
  connectionKey: 'ado-main',
  project: 'Agent Controller',
  tagPrefix: 'agent',
  activeState: 'Active',
  completedState: 'Done',
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
    checkClonePreflight: ReturnType<typeof vi.fn>;
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
    getCloneTransport: vi.fn(async () => ({
      transport: 'httpsPat' as const,
      credentialSource: 'connectionPersonalAccessToken' as const,
      credentialReference: { name: 'ado-pat', version: null },
      blockingIssues: [],
      isReady: true,
    })),
    checkClonePreflight: vi.fn(async (): Promise<ClonePreflightResult> => ({
      success: true,
      reason: '',
      failureCode: null,
      transport: 'httpsPat',
      cloneUrl: 'https://example.test/web.repo.git',
      credentialSource: 'connectionPersonalAccessToken',
      credentialReference: { name: 'ado-pat', version: null },
    })),
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
      connections: {
        ...staticResource<ConnectionProfile>([connection]),
        verifyConnection: async () => ({
          success: true,
          authMechanism: 'PersonalAccessToken',
          errors: [],
        }),
        listProjects: async () => projects,
        listRepositories: async () => [],
        listBranches: async () => [],
        onboardRepository: async () => repository,
      },
      runtimeEnvironments: staticResource([runtimeEnvironment]),
      secrets: {
        list: vi.fn(async () => secrets),
        listVersions: vi.fn(async (name: string) =>
          name === 'repository-deploy-key' ? sshKeyVersions : []),
        create: vi.fn(async () => ({ name: 'test' })),
        createVersion: vi.fn(async () => ({ name: 'test', version: 1 })),
        delete: vi.fn(async () => undefined),
      },
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

async function completeRequiredCreateFields(
  cloneUrl = 'https://example.test/new.repo.git',
): Promise<void> {
  await fireEvent.input(await screen.findByLabelText(/Repository Name/), {
    target: { value: 'new.repo' },
  });
  await fireEvent.input(screen.getByLabelText(/Clone URL or local path/), {
    target: { value: cloneUrl },
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
    await completeRequiredCreateFields('git@example.test:owner/new.repo.git');
    expect(screen.getByText('Automatic uses SSH based on the clone URL.')).toBeVisible();

    await fireEvent.click(screen.getByRole('button', { name: /SSH key secret/ }));
    expect(screen.queryByRole('option', { name: /ado-pat/ })).not.toBeInTheDocument();
    await fireEvent.click(
      screen.getByRole('option', { name: /repository-deploy-key/ }),
    );
    await fireEvent.change(await screen.findByLabelText('Secret version'), {
      target: { value: '1' },
    });
    await fireEvent.change(screen.getByLabelText('Repository Host'), {
      target: { value: 'ado-main' },
    });
    // Wait for projects to load then select one
    await waitFor(() => expect(screen.getByLabelText(/^Project$/)).not.toBeDisabled());
    await fireEvent.change(screen.getByLabelText(/^Project$/), {
      target: { value: 'Agent Controller' },
    });
    await fireEvent.change(screen.getByLabelText('Runtime environment'), {
      target: { value: 'runtime-main' },
    });
    await fireEvent.click(screen.getByRole('button', { name: 'Onboard repository' }));

    await waitFor(() => expect(api.repositories.create).toHaveBeenCalledOnce());
    expect(api.repositories.create).toHaveBeenCalledWith(
      expect.objectContaining({
        key: 'new.repo',
        cloneUrl: 'git@example.test:owner/new.repo.git',
        defaultBranch: 'main',
        transport: 'unspecified',
        sshKeyReference: { name: 'repository-deploy-key', version: 1 },
        repositoryHostConnectionKey: 'ado-main',
        project: 'Agent Controller',
        runtimeEnvironmentKey: 'runtime-main',
      }),
      expect.any(AbortSignal),
    );
    expect(await screen.findByText('Repository “new.repo” was onboarded.')).toBeVisible();
    expect(window.location.pathname).toBe('/repositories/new.repo');
  });

  it('validates required fields before submitting', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await fireEvent.click(
      await screen.findByRole('button', { name: 'Onboard repository' }),
    );

    expect(await screen.findByText('Complete the required fields')).toBeVisible();
    expect(screen.getByText('A Repository Name is required.')).toBeVisible();
    expect(screen.getByText('A clone URL or local path is required.')).toBeVisible();
    expect(api.repositories.create).not.toHaveBeenCalled();
  });

  it('requires an SSH key when the clone URL resolves to SSH', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await completeRequiredCreateFields('ssh://git@example.test/owner/new.repo.git');
    expect(screen.getByText('Automatic uses SSH based on the clone URL.')).toBeVisible();
    expect(screen.getByLabelText(/SSH key secret/)).toBeVisible();

    await fireEvent.click(screen.getByRole('button', { name: 'Onboard repository' }));

    expect(await screen.findByText('Select an SSH key secret for SSH clone transport.')).toBeVisible();
    expect(api.repositories.create).not.toHaveBeenCalled();
  });

  it('surfaces an incompatible secret type and the server validation error', async () => {
    window.history.replaceState({}, '', '/repositories/ssh.repo/edit');
    const incompatibleRepository: RepositoryProfile = {
      ...repository,
      key: 'ssh.repo',
      cloneUrl: 'git@example.test:owner/ssh.repo.git',
      transport: 'ssh',
      sshKeyReference: { name: 'ado-pat', version: 3 },
    };
    const api = createApi([incompatibleRepository]);
    api.repositories.update = vi.fn(async () => {
      throw new ApiError({
        title: 'Validation failed.',
        status: 400,
        errors: {
          sshKeyReference: [
            "Secret 'ado-pat' is a 'personal-access-token' secret. Repository SSH credentials must reference an SSH-key secret.",
          ],
        },
      });
    });
    render(App, { client: api.client });

    expect(
      await screen.findByText('Selected secret type is PAT; expected SSH key.'),
    ).toBeVisible();
    expect(screen.getByLabelText(/SSH key secret/)).toHaveAttribute(
      'aria-describedby',
      'repository-sshKeyReference-type-error',
    );

    await fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    const errors = await screen.findAllByText(
      /Repository SSH credentials must reference an SSH-key secret/,
    );
    expect(errors).toHaveLength(2);
    for (const error of errors) expect(error).toBeVisible();
  });

  it('updates repository settings while preserving the immutable key', async () => {
    window.history.replaceState({}, '', '/repositories/web.repo/edit');
    const api = createApi();
    render(App, { client: api.client });

    const keyInput = await screen.findByLabelText(/Repository Name/);
    expect(keyInput).toHaveAttribute('readonly');
    expect(screen.getByText(/Keys are immutable/)).toBeVisible();

    await fireEvent.input(screen.getByLabelText(/Default branch/), {
      target: { value: 'develop' },
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

  it('runs clone preflight and surfaces actionable credential failures', async () => {
    window.history.replaceState({}, '', '/repositories/web.repo');
    const api = createApi();
    api.repositories.checkClonePreflight.mockResolvedValue({
      success: false,
      reason: "PAT secret 'ado-pat' (version 3) was not found.",
      failureCode: 'credentialNotFound',
      transport: 'httpsPat',
      cloneUrl: repository.cloneUrl,
      credentialSource: 'connectionPersonalAccessToken',
      credentialReference: { name: 'ado-pat', version: 3 },
    });
    render(App, { client: api.client });

    await fireEvent.click(
      await screen.findByRole('button', { name: 'Run clone preflight' }),
    );

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Clone preflight failed');
    expect(alert).toHaveTextContent("PAT secret 'ado-pat' (version 3) was not found.");
    expect(screen.getByText('credentialNotFound')).toBeVisible();
    expect(screen.getByText('PAT · ado-pat · version 3')).toBeVisible();
    expect(api.repositories.checkClonePreflight).toHaveBeenCalledWith(
      'web.repo',
      expect.any(AbortSignal),
    );
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
