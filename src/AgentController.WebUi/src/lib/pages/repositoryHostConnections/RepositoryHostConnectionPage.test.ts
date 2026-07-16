import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../../App.svelte';
import { ApiError, type ResourceClient, type WebUiApiClient } from '../../api/client';
import type {
  HostRepository,
  RepositoryHostConnectionProfile,
  RepositoryHostConnectivityResult,
  RepositoryProfile,
  RuntimeEnvironmentProfile,
  WorkSourceEnvironmentProfile,
} from '../../api/types';

const connection: RepositoryHostConnectionProfile = {
  key: 'ado-repos-main',
  displayName: 'Primary Azure DevOps Repos',
  enabled: true,
  provider: 'AzureDevOpsRepos',
  organizationUrl: 'https://dev.azure.com/example',
  project: 'Agent Controller',
  personalAccessTokenReference: { kind: 'EnvVar', id: 'ADO_PAT' },
  createdAt: '2026-07-16T00:00:00Z',
  updatedAt: '2026-07-16T00:00:00Z',
};

function createApi(
  initialConnections: RepositoryHostConnectionProfile[] = [connection],
  verifyResult?: RepositoryHostConnectivityResult,
  repos?: HostRepository[],
) {
  let profiles = [...initialConnections];

  const defaultVerifyResult: RepositoryHostConnectivityResult = {
    success: true,
    authMechanism: 'PersonalAccessToken',
    errors: [],
    payload: { repositories: [] },
  };

  const repositoryHostConnections = {
    list: vi.fn(async () => [...profiles]),
    get: vi.fn(async (key: string) => {
      const profile = profiles.find((candidate) => candidate.key === key);
      if (!profile) throw new Error(`Missing repository host connection ${key}.`);
      return profile;
    }),
    create: vi.fn(async (profile: RepositoryHostConnectionProfile) => {
      profiles.push(profile);
      return profile;
    }),
    update: vi.fn(async (_key: string, profile: RepositoryHostConnectionProfile) => {
      profiles = profiles.map((candidate) => (candidate.key === profile.key ? profile : candidate));
      return profile;
    }),
    delete: vi.fn(async (key: string) => {
      profiles = profiles.filter((candidate) => candidate.key !== key);
    }),
    verifyConnection: vi.fn(
      async (): Promise<RepositoryHostConnectivityResult> => verifyResult ?? defaultVerifyResult,
    ),
    listRepositories: vi.fn(async (): Promise<HostRepository[]> => repos ?? []),
    onboardRepository: vi.fn(async (_key: string, _repoId: string): Promise<RepositoryProfile> => ({
      key: 'onboarded-repo',
      cloneUrl: 'https://dev.azure.com/example/project/_git/repo',
      defaultBranch: 'main',
      transport: 'httpsPat',
      environmentProfile: '',
      runtimeProfile: '',
      repositoryHostConnectionKey: 'ado-repos-main',
      remoteIdentity: 'repo-guid',
      runtimeEnvironmentKey: null,
      allowedPaths: [],
    })),
  };

  const client: WebUiApiClient = {
    repositories: staticResource<RepositoryProfile>([]),
    workSourceEnvironments: {
      ...staticResource<WorkSourceEnvironmentProfile>([]),
      verifyConnection: async () => ({
        success: true,
        authMechanism: 'PersonalAccessToken',
        errors: [],
      }),
    },
    repositoryHostConnections,
    runtimeEnvironments: staticResource<RuntimeEnvironmentProfile>([]),
  };

  return { client, connections: repositoryHostConnections };
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
  await fireEvent.input(await screen.findByLabelText(/Connection name/), {
    target: { value: 'ado-repos-secondary' },
  });
  await fireEvent.input(screen.getByLabelText(/Display name/), {
    target: { value: 'Secondary Repos' },
  });
  await fireEvent.input(screen.getByLabelText(/Organization URL/), {
    target: { value: 'https://dev.azure.com/example/' },
  });
  await fireEvent.input(screen.getByLabelText(/^Project/), {
    target: { value: 'Secondary Project' },
  });
  await fireEvent.input(screen.getByLabelText(/Environment variable name/), {
    target: { value: 'SECONDARY_ADO_PAT' },
  });
}

describe('repository host connection screens', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/repository-host-connections');
  });

  it('creates a connection with secret reference', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Add repository host connection' }),
    ).toBeVisible();
    expect(screen.getByText('Secrets are stored encrypted')).toBeVisible();

    // Verify provider selector defaults to Azure DevOps Repos
    expect(screen.getByLabelText(/Repository host provider/)).toHaveValue('AzureDevOpsRepos');

    await completeRequiredCreateFields();
    await fireEvent.click(screen.getByRole('button', { name: 'Create connection' }));

    await waitFor(() => expect(api.connections.create).toHaveBeenCalledOnce());
    expect(api.connections.create).toHaveBeenCalledWith(
      expect.objectContaining({
        key: 'ado-repos-secondary',
        displayName: 'Secondary Repos',
        enabled: true,
        provider: 'AzureDevOpsRepos',
        organizationUrl: 'https://dev.azure.com/example',
        project: 'Secondary Project',
        personalAccessTokenReference: { kind: 'EnvVar', id: 'SECONDARY_ADO_PAT' },
      }),
      expect.any(AbortSignal),
    );
    expect(
      await screen.findByText(/Repository host connection .*Secondary Repos.* was created/),
    ).toBeVisible();
    expect(window.location.pathname).toBe('/repository-host-connections/ado-repos-secondary');
  });

  it('validates required fields before submitting', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Create connection' }));

    expect(await screen.findByText('Correct the highlighted fields')).toBeVisible();
    expect(screen.getByText('A key is required.')).toBeVisible();
    expect(screen.getByText('An Azure DevOps organization URL is required.')).toBeVisible();
    expect(screen.getByText('A secret reference identifier is required.')).toBeVisible();
    expect(api.connections.create).not.toHaveBeenCalled();
  });

  it('edits connection settings while preserving the key', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/ado-repos-main/edit');
    const api = createApi();
    render(App, { client: api.client });

    const keyInput = await screen.findByLabelText(/Connection name/);
    expect(keyInput).toHaveAttribute('readonly');

    await fireEvent.input(screen.getByLabelText(/Display name/), {
      target: { value: 'Updated Repos' },
    });
    await fireEvent.click(screen.getByLabelText('Enabled'));
    await fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.connections.update).toHaveBeenCalledOnce());
    expect(api.connections.update).toHaveBeenCalledWith(
      'ado-repos-main',
      expect.objectContaining({
        key: 'ado-repos-main',
        displayName: 'Updated Repos',
        enabled: false,
        createdAt: connection.createdAt,
      }),
      expect.any(AbortSignal),
    );
    expect(
      await screen.findByText(/Repository host connection .*Updated Repos.* was updated/),
    ).toBeVisible();
  });

  it('shows field-level server validation errors', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/new');
    const api = createApi([]);
    api.connections.create.mockRejectedValueOnce(
      new ApiError({
        title: 'Validation failed.',
        status: 400,
        detail: 'Correct the highlighted connection fields.',
        errors: {
          organizationUrl: ['The organization URL must not include a collection path.'],
        },
      }),
    );
    render(App, { client: api.client });

    await completeRequiredCreateFields();
    await fireEvent.click(screen.getByRole('button', { name: 'Create connection' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Could not create connection');
    expect(alert).toHaveTextContent('Correct the highlighted connection fields.');
    expect(
      screen.getByText('The organization URL must not include a collection path.'),
    ).toBeVisible();
    expect(screen.getByLabelText(/Organization URL/)).toHaveAttribute('aria-invalid', 'true');
  });

  it('renders only the secret reference, never a returned secret value', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/ado-repos-main');
    const api = createApi([connection]);
    render(App, { client: api.client });

    expect(await screen.findByText('ADO_PAT')).toBeVisible();
    expect(screen.getByText('Secret value redacted')).toBeVisible();
  });

  it('enables and disables a connection from the list', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Disable Primary Azure DevOps Repos' }));

    await waitFor(() => expect(api.connections.update).toHaveBeenCalledOnce());
    expect(api.connections.update).toHaveBeenCalledWith(
      'ado-repos-main',
      expect.objectContaining({ enabled: false }),
      expect.any(AbortSignal),
    );
    expect(await screen.findByText('Connection disabled')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Enable Primary Azure DevOps Repos' })).toBeVisible();
  });

  it('deletes a connection only after confirmation', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Delete Primary Azure DevOps Repos' }));

    const dialog = await screen.findByRole('dialog', {
      name: 'Delete repository host connection?',
    });
    expect(api.connections.delete).not.toHaveBeenCalled();
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete connection' }));

    await waitFor(() =>
      expect(api.connections.delete).toHaveBeenCalledWith('ado-repos-main', expect.any(AbortSignal)),
    );
    expect(
      await screen.findByRole('heading', { name: 'No repository host connections yet' }),
    ).toBeVisible();
    expect(screen.getByText(/Repository host connection .*Primary Azure DevOps Repos.* was deleted/)).toBeVisible();
  });

  // ── Test Connection — List view ─────────────────────────────────────

  it('renders a Test connection button per row in the list view', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await screen.findByRole('table');

    const buttons = screen.getAllByRole('button', {
      name: /Test connection for Primary Azure DevOps Repos/i,
    });
    expect(buttons.length).toBeGreaterThanOrEqual(1);
    expect(buttons[0]).toBeVisible();
  });

  it('list view: Test connection shows success badge with repo count', async () => {
    const api = createApi([connection], {
      success: true,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 200,
      errors: [],
      payload: { repositories: ['repo-a', 'repo-b'] },
    });
    render(App, { client: api.client });

    await screen.findByRole('table');
    const testButton = screen.getByRole('button', {
      name: /Test connection for Primary Azure DevOps Repos/i,
    });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'ado-repos-main',
        expect.anything(),
      ),
    );

    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
    expect(screen.getByText('(2 repos)')).toBeVisible();
  });

  it('list view: Test connection shows failure badge with error messages', async () => {
    const api = createApi([connection], {
      success: false,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 401,
      errors: ['Unauthorized: the personal access token is invalid or expired.'],
    });
    render(App, { client: api.client });

    await screen.findByRole('table');
    const testButton = screen.getByRole('button', {
      name: /Test connection for Primary Azure DevOps Repos/i,
    });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'ado-repos-main',
        expect.anything(),
      ),
    );

    await waitFor(() => expect(screen.getByText('Failed')).toBeVisible());
    expect(
      screen.getByText('Unauthorized: the personal access token is invalid or expired.'),
    ).toBeVisible();
  });

  // ── Test Connection — Details view ──────────────────────────────────

  it('details view renders a Test connection button', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/ado-repos-main');
    const api = createApi();
    render(App, { client: api.client });

    expect(await screen.findByText('Connection details')).toBeVisible();

    const testButton = screen.getByRole('button', { name: 'Test connection' });
    expect(testButton).toBeVisible();
    expect(testButton).not.toBeDisabled();
  });

  it('details view: Test connection transitions through loading to success', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/ado-repos-main');
    const api = createApi([connection], {
      success: true,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 200,
      errors: [],
      payload: { repositories: ['repo-a', 'repo-b'] },
    });
    render(App, { client: api.client });

    expect(await screen.findByText('Connection details')).toBeVisible();
    const testButton = screen.getByRole('button', { name: 'Test connection' });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'ado-repos-main',
        expect.anything(),
      ),
    );

    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
    expect(screen.getByText('(2 repos)')).toBeVisible();
  });

  it('details view: Test connection transitions through loading to failure', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/ado-repos-main');
    const api = createApi([connection], {
      success: false,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 401,
      errors: ['Connection failed: invalid credentials.'],
    });
    render(App, { client: api.client });

    expect(await screen.findByText('Connection details')).toBeVisible();
    const testButton = screen.getByRole('button', { name: 'Test connection' });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'ado-repos-main',
        expect.anything(),
      ),
    );

    await waitFor(() => expect(screen.getByText('Failed')).toBeVisible());
    expect(screen.getByText('Connection failed: invalid credentials.')).toBeVisible();
  });

  // ── Repo picker ─────────────────────────────────────────────────────

  it('repo picker renders discovered repositories with onboard buttons', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/ado-repos-main/repos');
    const api = createApi([connection], undefined, [
      {
        id: 'repo-1',
        name: 'web-app',
        defaultBranch: 'main',
        remoteUrl: 'https://dev.azure.com/example/project/_git/web-app',
        cloneTransportHint: 'httpsPat',
      },
      {
        id: 'repo-2',
        name: 'api-service',
        defaultBranch: 'develop',
        remoteUrl: 'https://dev.azure.com/example/project/_git/api-service',
        cloneTransportHint: 'ssh',
      },
    ]);
    render(App, { client: api.client });

    expect(await screen.findByText('Available repositories')).toBeVisible();
    expect(screen.getByText('web-app')).toBeVisible();
    expect(screen.getByText('api-service')).toBeVisible();
    expect(screen.getByText('HTTPS + PAT')).toBeVisible();
    expect(screen.getByText('SSH')).toBeVisible();
    expect(screen.getAllByRole('button', { name: /Onboard/i }).length).toBeGreaterThanOrEqual(2);
  });

  it('repo picker: clicking Onboard calls the onboard endpoint and navigates to the repository', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/ado-repos-main/repos');
    const api = createApi([connection], undefined, [
      {
        id: 'repo-1',
        name: 'web-app',
        defaultBranch: 'main',
        remoteUrl: 'https://dev.azure.com/example/project/_git/web-app',
        cloneTransportHint: 'httpsPat',
      },
    ]);
    render(App, { client: api.client });

    expect(await screen.findByText('Available repositories')).toBeVisible();
    const onboardButton = screen.getByRole('button', { name: 'Onboard web-app' });
    fireEvent.click(onboardButton);

    await waitFor(() =>
      expect(api.connections.onboardRepository).toHaveBeenCalledWith(
        'ado-repos-main',
        'repo-1',
        undefined,
        expect.any(AbortSignal),
      ),
    );

    // Wait for the navigation to complete
    await waitFor(() => {
      expect(window.location.pathname).toBe('/repositories/onboarded-repo');
    });
  });

  it('repo picker: shows empty state when no repositories found', async () => {
    window.history.replaceState({}, '', '/repository-host-connections/ado-repos-main/repos');
    const api = createApi([connection]);
    render(App, { client: api.client });

    expect(await screen.findByText('No repositories found')).toBeVisible();
  });

  // ── Browse repos link ───────────────────────────────────────────────

  it('list view renders Browse repos link per row', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await screen.findByRole('table');
    const browseLink = screen.getByRole('link', { name: 'Browse repos' });
    expect(browseLink).toBeVisible();
    expect(browseLink).toHaveAttribute('href', '/repository-host-connections/ado-repos-main/repos');
  });
});
