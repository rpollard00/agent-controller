import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../../App.svelte';
import { ApiError, type ResourceClient, type WebUiApiClient } from '../../api/client';
import type {
  ClonePreflightResult,
  ConnectionProfile,
  ConnectionProject,
  HostRepository,
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
  sshKeyInheritEnvironment: false,
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

  // ── Reworked repository flow: host-driven enumeration mode ────────────

  it('host-driven create mode: selects host then project then repository and prefills fields', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const hostRepos: HostRepository[] = [
      {
        id: 'repo-42',
        name: 'my-app',
        defaultBranch: 'develop',
        remoteUrl: 'https://dev.azure.com/example/project/_git/my-app',
        sshUrl: 'git@ssh.dev.azure.com:v3/example/project/my-app',
        cloneTransportHint: 'httpsPat',
      },
    ];
    const api = createApi([]);
    api.client.connections.listProjects = vi.fn(async () => [
      { id: 'proj-1', name: 'Agent Controller' },
    ]);
    api.client.connections.listRepositories = vi.fn(async () => hostRepos);
    api.client.connections.listBranches = vi.fn(async () => ['main', 'develop']);
    render(App, { client: api.client });

    expect(await screen.findByRole('heading', { level: 1, name: 'Onboard repository' })).toBeVisible();

    // Select a repository host connection
    const hostSelect = await screen.findByLabelText('Repository Host');
    await fireEvent.change(hostSelect, {
      target: { value: 'ado-main' },
    });

    // Wait for projects to load and select one
    await waitFor(() => expect(screen.getByLabelText(/^Project$/)).not.toBeDisabled());
    await fireEvent.change(screen.getByLabelText(/^Project$/), {
      target: { value: 'Agent Controller' },
    });

    // Wait for repositories to load and select one
    await waitFor(() => expect(screen.getByLabelText(/^Repository$/)).not.toBeDisabled());
    await fireEvent.change(screen.getByLabelText(/^Repository$/), {
      target: { value: 'repo-42' },
    });

    // Verify repository name is prefilled from selection
    const nameInput = await screen.findByLabelText(/Repository Name/);
    expect(nameInput).toHaveValue('my-app');

    // Verify clone URL is derived from transport (default HTTPS)
    const cloneUrlInput = screen.getByLabelText(/Clone URL or local path/);
    expect(cloneUrlInput).toHaveValue('https://dev.azure.com/example/project/_git/my-app');
    expect(cloneUrlInput).toHaveAttribute('readonly');

    // Verify default branch is prefilled
    expect(screen.getByLabelText(/Default branch/)).toHaveValue('develop');

    // Switch transport to SSH and verify clone URL switches to sshUrl
    await fireEvent.change(screen.getByLabelText(/Clone transport/), {
      target: { value: 'ssh' },
    });
    expect(screen.getByLabelText(/Clone URL or local path/)).toHaveValue(
      'git@ssh.dev.azure.com:v3/example/project/my-app',
    );
  });

  it('host-driven create mode: sshUrl null fallback to remoteUrl', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const hostRepos: HostRepository[] = [
      {
        id: 'repo-7',
        name: 'web-app',
        defaultBranch: 'main',
        remoteUrl: 'https://dev.azure.com/example/project/_git/web-app',
        sshUrl: null,
        cloneTransportHint: 'unspecified',
      },
    ];
    const api = createApi([]);
    api.client.connections.listProjects = vi.fn(async () => [
      { id: 'proj-1', name: 'Agent Controller' },
    ]);
    api.client.connections.listRepositories = vi.fn(async () => hostRepos);
    api.client.connections.listBranches = vi.fn(async () => []);
    render(App, { client: api.client });

    await screen.findByRole('heading', { level: 1, name: 'Onboard repository' });

    // Select host and project
    const hostSelect = await screen.findByLabelText('Repository Host');
    await fireEvent.change(hostSelect, {
      target: { value: 'ado-main' },
    });
    await waitFor(() => expect(screen.getByLabelText(/^Project$/)).not.toBeDisabled());
    await fireEvent.change(screen.getByLabelText(/^Project$/), {
      target: { value: 'Agent Controller' },
    });
    await waitFor(() => expect(screen.getByLabelText(/^Repository$/)).not.toBeDisabled());
    await fireEvent.change(screen.getByLabelText(/^Repository$/), {
      target: { value: 'repo-7' },
    });

    // Switch to SSH – clone URL should fall back to remoteUrl since sshUrl is null
    await fireEvent.change(screen.getByLabelText(/Clone transport/), {
      target: { value: 'ssh' },
    });
    expect(screen.getByLabelText(/Clone URL or local path/)).toHaveValue(
      'https://dev.azure.com/example/project/_git/web-app',
    );
  });

  it('host-driven create mode: branch selector populated from enumerated branches', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const hostRepos: HostRepository[] = [
      {
        id: 'repo-99',
        name: 'feature-app',
        defaultBranch: 'develop',
        remoteUrl: 'https://dev.azure.com/example/project/_git/feature-app',
        sshUrl: null,
        cloneTransportHint: 'httpsPat',
      },
    ];
    const api = createApi([]);
    api.client.connections.listProjects = vi.fn(async () => [
      { id: 'proj-1', name: 'Agent Controller' },
    ]);
    api.client.connections.listRepositories = vi.fn(async () => hostRepos);
    api.client.connections.listBranches = vi.fn(async () => ['main', 'develop', 'feature/new-work']);
    render(App, { client: api.client });

    await screen.findByRole('heading', { level: 1, name: 'Onboard repository' });

    // Select host, project, and repository
    const hostSelect = await screen.findByLabelText('Repository Host');
    await fireEvent.change(hostSelect, {
      target: { value: 'ado-main' },
    });
    await waitFor(() => expect(screen.getByLabelText(/^Project$/)).not.toBeDisabled());
    await fireEvent.change(screen.getByLabelText(/^Project$/), {
      target: { value: 'Agent Controller' },
    });
    await waitFor(() => expect(screen.getByLabelText(/^Repository$/)).not.toBeDisabled());
    await fireEvent.change(screen.getByLabelText(/^Repository$/), {
      target: { value: 'repo-99' },
    });

    // Default branch should be the repo defaultBranch
    await waitFor(() => expect(screen.getByLabelText(/Default branch/)).toHaveValue('develop'));

    // The branch widget should be a <select> in host-driven mode
    const branchSelect = screen.getByLabelText(/Default branch/);
    expect(branchSelect.tagName).toBe('SELECT');

    // Can change to a different enumerated branch
    await fireEvent.change(branchSelect, { target: { value: 'feature/new-work' } });
    expect(branchSelect).toHaveValue('feature/new-work');
  });

  it('manual None-host mode: freeform clone URL and branch input', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await screen.findByRole('heading', { level: 1, name: 'Onboard repository' });

    // Default is None — manual entry
    const hostSelect = await screen.findByLabelText('Repository Host');
    expect(hostSelect).toHaveValue('');
    expect(screen.queryByLabelText(/^Project$/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/^Repository$/)).not.toBeInTheDocument();

    // Clone URL input is writable (not readonly)
    const cloneUrlInput = screen.getByLabelText(/Clone URL or local path/);
    expect(cloneUrlInput).not.toHaveAttribute('readonly');

    // Branch is a free-text input (not a select)
    const branchInput = screen.getByLabelText(/Default branch/);
    expect(branchInput.tagName).toBe('INPUT');

    // Fill in fields manually and submit
    await fireEvent.input(await screen.findByLabelText(/Repository Name/), {
      target: { value: 'manual.repo' },
    });
    await fireEvent.input(cloneUrlInput, {
      target: { value: 'https://example.test/manual.git' },
    });
    await fireEvent.input(branchInput, {
      target: { value: 'main' },
    });
    await fireEvent.click(screen.getByRole('button', { name: 'Onboard repository' }));

    await waitFor(() => expect(api.repositories.create).toHaveBeenCalledOnce());
    expect(api.repositories.create).toHaveBeenCalledWith(
      expect.objectContaining({
        key: 'manual.repo',
        cloneUrl: 'https://example.test/manual.git',
        defaultBranch: 'main',
        repositoryHostConnectionKey: null,
        project: null,
      }),
      expect.any(AbortSignal),
    );
  });

  it('environment-inherit SSH override hides key picker and clears key reference', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await screen.findByRole('heading', { level: 1, name: 'Onboard repository' });

    // Enter an SSH clone URL to trigger SSH key section
    await fireEvent.input(await screen.findByLabelText(/Repository Name/), {
      target: { value: 'ssh-inherit.repo' },
    });
    await fireEvent.input(screen.getByLabelText(/Clone URL or local path/), {
      target: { value: 'ssh://git@example.test/ssh-inherit.repo.git' },
    });

    // SSH section should show with key picker and inherit checkbox
    expect(screen.getByText('SSH authentication')).toBeVisible();
    expect(screen.getByLabelText(/SSH key secret/)).toBeVisible();
    const inheritCheckbox = screen.getByLabelText(/Inherit from environment/);
    expect(inheritCheckbox).toBeVisible();
    expect(inheritCheckbox).not.toBeChecked();

    // Check the inherit checkbox
    await fireEvent.click(inheritCheckbox);

    // Key picker should be replaced by warning
    expect(screen.queryByLabelText(/SSH key secret/)).not.toBeInTheDocument();
    expect(screen.getByText('Environment SSH key required')).toBeVisible();
    expect(screen.getByText(/must be set up in the runner environment/)).toBeVisible();

    // Submit should succeed without requiring an SSH key
    await fireEvent.change(screen.getByLabelText(/Default branch/), {
      target: { value: 'main' },
    });
    await fireEvent.click(screen.getByRole('button', { name: 'Onboard repository' }));

    await waitFor(() => expect(api.repositories.create).toHaveBeenCalledOnce());
    expect(api.repositories.create).toHaveBeenCalledWith(
      expect.objectContaining({
        key: 'ssh-inherit.repo',
        sshKeyReference: null,
        sshKeyInheritEnvironment: true,
      }),
      expect.any(AbortSignal),
    );
  });

  it('edit mode: shows (unavailable) option when host connection no longer exists', async () => {
    const orphanedRepository: RepositoryProfile = {
      ...repository,
      key: 'orphan.repo',
      repositoryHostConnectionKey: 'deleted-host',
      project: 'Deleted Project',
      cloneUrl: 'https://dev.azure.com/old/project/_git/orphan',
    };
    window.history.replaceState({}, '', '/repositories/orphan.repo/edit');
    const api = createApi([orphanedRepository]);
    render(App, { client: api.client });

    await screen.findByRole('heading', { level: 1, name: 'Edit orphan.repo' });

    // The unavailable host should be listed
    const hostSelect = await screen.findByLabelText('Repository Host');
    expect(hostSelect).toHaveValue('deleted-host');
    expect(screen.getByRole('option', { name: 'deleted-host (unavailable)' })).toBeVisible();

    // Repository Name is readonly in edit mode
    expect(screen.getByLabelText(/Repository Name/)).toHaveAttribute('readonly');
  });

  it('enumeration failure shows top-of-form warning alert', async () => {
    window.history.replaceState({}, '', '/repositories/new');
    const api = createApi([]);
    api.client.connections.listProjects = vi.fn(async () => {
      throw new Error('Network failure');
    });
    render(App, { client: api.client });

    await screen.findByRole('heading', { level: 1, name: 'Onboard repository' });

    // Select host
    const hostSelect = await screen.findByLabelText('Repository Host');
    await fireEvent.change(hostSelect, {
      target: { value: 'ado-main' },
    });

    // Wait for the error alert to appear
    await waitFor(() => {
      expect(screen.getByText('Could not load projects')).toBeVisible();
    });
    expect(
      screen.getByText(/Check the connection configuration and permissions/),
    ).toBeVisible();
  });

  it('host-driven edit mode preserves persisted values on initial load', async () => {
    const hostDrivenRepository: RepositoryProfile = {
      key: 'existing.repo',
      cloneUrl: 'https://dev.azure.com/company/project/_git/existing',
      defaultBranch: 'custom-branch',
      transport: 'httpsPat',
      environmentProfile: 'prod-environment',
      runtimeProfile: 'prod-runtime',
      repositoryHostConnectionKey: 'ado-main',
      remoteIdentity: 'repo-guid',
      runtimeEnvironmentKey: 'runtime-main',
      sshKeyReference: null,
      sshKeyInheritEnvironment: false,
      project: 'Agent Controller',
    };
    const hostRepos: HostRepository[] = [
      {
        id: 'existing-id',
        name: 'existing',
        defaultBranch: 'main',
        remoteUrl: 'https://dev.azure.com/company/project/_git/existing',
        sshUrl: 'git@ssh.dev.azure.com:v3/company/project/existing',
        cloneTransportHint: 'httpsPat',
      },
    ];
    const api = createApi([hostDrivenRepository]);
    api.client.connections.listProjects = vi.fn(async () => [
      { id: 'proj-1', name: 'Agent Controller' },
    ]);
    api.client.connections.listRepositories = vi.fn(async () => hostRepos);
    api.client.connections.listBranches = vi.fn(async () => ['main', 'develop', 'custom-branch']);
    window.history.replaceState({}, '', '/repositories/existing.repo/edit');
    render(App, { client: api.client });

    // Check that the page renders the edit form
    const heading = await screen.findByRole('heading', { level: 1 });
    expect(heading.textContent).toMatch(/Edit/i);
    // Verify the form rendered by checking the form element
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Save changes' })).toBeVisible();
    });

    // Host should be pre-selected
    const hostSelect = await screen.findByLabelText('Repository Host');
    expect(hostSelect).toHaveValue('ado-main');

    // Wait for enumeration to complete
    await waitFor(() => {
      expect(screen.getByLabelText(/Default branch/)).toHaveValue('custom-branch');
    });

    // Repository Name is readonly in edit mode
    const nameInput = screen.getByLabelText(/Repository Name/);
    expect(nameInput).toHaveAttribute('readonly');
    expect(nameInput).toHaveValue('existing.repo');

    // Persisted clone URL preserved (not overwritten by enumeration default)
    expect(screen.getByLabelText(/Clone URL or local path/)).toHaveValue(
      'https://dev.azure.com/company/project/_git/existing',
    );

    // Persisted default branch preserved (not overwritten by repo defaultBranch)
    await waitFor(() => {
      expect(screen.getByLabelText(/Default branch/)).toHaveValue('custom-branch');
    });

    // User can still change branch
    const branchSelect = await screen.findByLabelText(/Default branch/);
    await fireEvent.change(branchSelect, { target: { value: 'develop' } });
    expect(branchSelect).toHaveValue('develop');

    // Verify the Save button is visible (form rendered in edit mode)
    expect(await screen.findByRole('button', { name: 'Save changes' })).toBeVisible();
  });
});
