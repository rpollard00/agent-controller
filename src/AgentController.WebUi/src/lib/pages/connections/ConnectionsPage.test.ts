import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../../App.svelte';
import { ApiError, type ResourceClient, type SecretsResourceClient, type WebUiApiClient } from '../../api/client';
import type {
  ConnectionConnectivityResult,
  ConnectionProfile,
  ConnectionProject,
  HostRepository,
  RepositoryProfile,
  RuntimeEnvironmentProfile,
  SecretInfo,
  SecretVersionInfo,
  WorkSourceEnvironmentProfile,
} from '../../api/types';

const connection: ConnectionProfile = {
  key: 'ado-main',
  displayName: 'Primary Azure DevOps',
  enabled: true,
  provider: 'AzureDevOps',
  capabilities: ['Repositories', 'WorkTracking'],
  providerSettings: {
    provider: 'AzureDevOps',
    organizationUrl: 'https://dev.azure.com/example',
    personalAccessTokenReference: { name: 'ADO_PAT', version: null },
  },
  createdAt: '2026-07-16T00:00:00Z',
  updatedAt: '2026-07-16T00:00:00Z',
};

const projects: ConnectionProject[] = [
  { id: 'proj-1', name: 'Agent Controller' },
  { id: 'proj-2', name: 'Test Project' },
];

const repos: HostRepository[] = [
  {
    id: 'repo-1',
    name: 'web-app',
    defaultBranch: 'main',
    remoteUrl: 'https://dev.azure.com/example/project/_git/web-app',
    cloneTransportHint: 'httpsPat',
  },
];

function createApi(
  initialConnections: ConnectionProfile[] = [connection],
  verifyResult?: ConnectionConnectivityResult,
  initialProjects: ConnectionProject[] = projects,
  initialRepos: HostRepository[] = repos,
  initialSecrets: SecretInfo[] = [{
    name: 'ADO_PAT',
    latestVersion: 1,
    createdAt: '2026-07-16T00:00:00Z',
    updatedAt: '2026-07-16T00:00:00Z',
    secretType: 'personal-access-token',
  }],
) {
  let profiles = [...initialConnections];
  let secrets = [...initialSecrets];

  const defaultVerifyResult: ConnectionConnectivityResult = {
    success: true,
    authMechanism: 'PersonalAccessToken',
    errors: [],
    payload: {
      scope: 'organization',
      organizationUrl: 'https://dev.azure.com/example',
    },
  };

  const connections = {
    list: vi.fn(async () => [...profiles]),
    get: vi.fn(async (key: string) => {
      const profile = profiles.find((candidate) => candidate.key === key);
      if (!profile) throw new Error(`Missing connection ${key}.`);
      return profile;
    }),
    create: vi.fn(async (profile: ConnectionProfile) => {
      profiles.push(profile);
      return profile;
    }),
    update: vi.fn(async (_key: string, profile: ConnectionProfile) => {
      profiles = profiles.map((candidate) => (candidate.key === profile.key ? profile : candidate));
      return profile;
    }),
    delete: vi.fn(async (key: string) => {
      profiles = profiles.filter((candidate) => candidate.key !== key);
    }),
    verifyConnection: vi.fn(
      async (): Promise<ConnectionConnectivityResult> => verifyResult ?? defaultVerifyResult,
    ),
    listProjects: vi.fn(async (): Promise<ConnectionProject[]> => initialProjects),
    listRepositories: vi.fn(async (): Promise<HostRepository[]> => initialRepos),
    onboardRepository: vi.fn(async (_key: string, _project: string, _repoId: string): Promise<RepositoryProfile> => ({
      key: 'onboarded-repo',
      cloneUrl: 'https://dev.azure.com/example/project/_git/repo',
      defaultBranch: 'main',
      transport: 'httpsPat',
      environmentProfile: '',
      runtimeProfile: '',
      repositoryHostConnectionKey: 'ado-main',
      remoteIdentity: 'repo-guid',
      runtimeEnvironmentKey: null,
      sshKeyReference: null,
      allowedPaths: [],
      project: null,
    })),
  };

  const secretsClient: SecretsResourceClient = {
    list: vi.fn(async () => [...secrets]),
    listVersions: vi.fn(async (name: string): Promise<SecretVersionInfo[]> => {
      const secret = secrets.find((candidate) => candidate.name === name);
      if (!secret) return [];

      return Array.from({ length: secret.latestVersion }, (_, index) => ({
        version: index + 1,
        createdAt: `2026-07-${String(16 + index).padStart(2, '0')}T00:00:00Z`,
        secretType: secret.secretType,
        publicKey: secret.secretType === 'ssh-key' ? 'ssh-ed25519 public-key-material' : null,
      }));
    }),
    create: vi.fn(async (req) => {
      const now = new Date().toISOString();
      secrets.push({
        name: req.name,
        latestVersion: 1,
        createdAt: now,
        updatedAt: now,
        secretType: req.payload.type,
      });
      return { name: req.name };
    }),
    createVersion: vi.fn(async (name) => ({ name, version: 2 })),
    delete: vi.fn(async () => undefined),
  };

  const client: WebUiApiClient = {
    repositories: {
      ...staticResource<RepositoryProfile>([]),
      getCloneTransport: vi.fn(async () => {
        throw new Error('Not implemented in this component test.');
      }),
    },
    workSourceEnvironments: {
      ...staticResource<WorkSourceEnvironmentProfile>([]),
      verifyConnection: async () => ({
        success: true,
        authMechanism: 'PersonalAccessToken',
        errors: [],
      }),
    },
    connections,
    runtimeEnvironments: staticResource<RuntimeEnvironmentProfile>([]),
    secrets: secretsClient,
  };

  return { client, connections, secrets: secretsClient };
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
    target: { value: 'ado-secondary' },
  });
  await fireEvent.input(screen.getByLabelText(/Display name/), {
    target: { value: 'Secondary ADO' },
  });
  await fireEvent.input(screen.getByLabelText(/Organization URL/), {
    target: { value: 'https://dev.azure.com/example/' },
  });
  // Select a secret from the combobox picker
  await fireEvent.click(screen.getByRole('button', { name: 'PAT secret (required)' }));
  await waitFor(() => expect(screen.getByText('ADO_PAT')).toBeVisible());
  fireEvent.click(screen.getByRole('option', { name: /ADO_PAT/ }));
}

describe('connection screens', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/connections');
  });

  it('emits provider type discriminator in providerSettings payload (regression)', async () => {
    window.history.replaceState({}, '', '/connections/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await completeRequiredCreateFields();
    await fireEvent.click(screen.getByRole('button', { name: 'Create connection' }));

    await waitFor(() => expect(api.connections.create).toHaveBeenCalledOnce());
    const callArgs = api.connections.create.mock.calls[0];
    const payload = callArgs[0] as ConnectionProfile;
    // Regression: providerSettings must carry the "provider" discriminator
    // so the backend's JsonPolymorphic binder can resolve the concrete subtype.
    // Without this, ReadFromJsonAsync throws NotSupportedException -> 500.
    expect(payload.providerSettings).not.toBeNull();
    expect(payload.providerSettings!.provider).toBe('AzureDevOps');
    expect(window.location.pathname).toBe('/connections/ado-secondary');
  });

  it('creates a connection with secret reference', async () => {
    window.history.replaceState({}, '', '/connections/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Add connection' }),
    ).toBeVisible();

    // Verify provider selector defaults to Azure DevOps
    expect(screen.getByLabelText(/Provider/)).toHaveValue('AzureDevOps');

    await completeRequiredCreateFields();
    await fireEvent.click(screen.getByRole('button', { name: 'Create connection' }));

    await waitFor(() => expect(api.connections.create).toHaveBeenCalledOnce());
    expect(api.connections.create).toHaveBeenCalledWith(
      expect.objectContaining({
        key: 'ado-secondary',
        displayName: 'Secondary ADO',
        enabled: true,
        provider: 'AzureDevOps',
        providerSettings: expect.objectContaining({
          provider: 'AzureDevOps',
          organizationUrl: 'https://dev.azure.com/example',
          personalAccessTokenReference: { name: 'ADO_PAT', version: null },
        }),
      }),
      expect.any(AbortSignal),
    );
    expect(
      await screen.findByText(/Connection .*Secondary ADO.* was created/),
    ).toBeVisible();
    expect(window.location.pathname).toBe('/connections/ado-secondary');
  });

  it('filters credential choices to PAT secrets and allows pinning a listed version', async () => {
    window.history.replaceState({}, '', '/connections/new');
    const api = createApi([], undefined, projects, repos, [
      {
        name: 'ADO_PAT',
        latestVersion: 3,
        createdAt: '2026-07-16T00:00:00Z',
        updatedAt: '2026-07-18T00:00:00Z',
        secretType: 'personal-access-token',
      },
      {
        name: 'ADO_SSH_KEY',
        latestVersion: 2,
        createdAt: '2026-07-16T00:00:00Z',
        updatedAt: '2026-07-17T00:00:00Z',
        secretType: 'ssh-key',
      },
    ]);
    render(App, { client: api.client });

    await completeRequiredCreateFields();
    await waitFor(() => expect(api.secrets.listVersions).toHaveBeenCalledWith(
      'ADO_PAT',
      expect.any(AbortSignal),
    ));

    await fireEvent.click(screen.getByRole('button', { name: 'PAT secret (required)' }));
    expect(screen.getByRole('option', { name: /ADO_PAT PAT · Latest v3/ })).toBeVisible();
    expect(screen.queryByRole('option', { name: /ADO_SSH_KEY/ })).not.toBeInTheDocument();
    await fireEvent.click(screen.getByRole('option', { name: /ADO_PAT PAT · Latest v3/ }));

    const versionPicker = screen.getByLabelText('Secret version');
    expect(versionPicker).toHaveValue('');
    expect(within(versionPicker).getByRole('option', {
      name: 'Latest (currently v3)',
    })).toBeVisible();
    expect(within(versionPicker).getByRole('option', {
      name: 'v2',
    })).toBeVisible();

    await fireEvent.change(versionPicker, { target: { value: '2' } });
    await fireEvent.click(screen.getByRole('button', { name: 'Create connection' }));

    await waitFor(() => expect(api.connections.create).toHaveBeenCalledOnce());
    const created = api.connections.create.mock.calls[0][0] as ConnectionProfile;
    expect(created.providerSettings?.personalAccessTokenReference).toEqual({
      name: 'ADO_PAT',
      version: 2,
    });
  });

  it('validates required fields before submitting', async () => {
    window.history.replaceState({}, '', '/connections/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Create connection' }));

    expect(await screen.findByText('Correct the highlighted fields')).toBeVisible();
    expect(screen.getByText('A key is required.')).toBeVisible();
    expect(screen.getByText('An Azure DevOps organization URL is required.')).toBeVisible();
    expect(screen.getByText('A secret reference for the PAT is required.')).toBeVisible();
    expect(api.connections.create).not.toHaveBeenCalled();
  });

  it('edits connection settings while preserving the key', async () => {
    window.history.replaceState({}, '', '/connections/ado-main/edit');
    const api = createApi();
    render(App, { client: api.client });

    const keyInput = await screen.findByLabelText(/Connection name/);
    expect(keyInput).toHaveAttribute('readonly');

    await fireEvent.input(screen.getByLabelText(/Display name/), {
      target: { value: 'Updated ADO' },
    });
    await fireEvent.click(screen.getByLabelText('Enabled'));
    await fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.connections.update).toHaveBeenCalledOnce());
    expect(api.connections.update).toHaveBeenCalledWith(
      'ado-main',
      expect.objectContaining({
        key: 'ado-main',
        displayName: 'Updated ADO',
        enabled: false,
        createdAt: connection.createdAt,
      }),
      expect.any(AbortSignal),
    );
    expect(
      await screen.findByText(/Connection .*Updated ADO.* was updated/),
    ).toBeVisible();
  });

  it('shows field-level server validation errors', async () => {
    window.history.replaceState({}, '', '/connections/new');
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
    expect(
      screen.getByText('The organization URL must not include a collection path.'),
    ).toBeVisible();
  });

  it('shows server credential type errors on the PAT picker', async () => {
    window.history.replaceState({}, '', '/connections/new');
    const api = createApi([]);
    api.connections.create.mockRejectedValueOnce(
      new ApiError({
        title: 'Validation failed.',
        status: 400,
        errors: {
          'providerSettings.personalAccessTokenReference': [
            'Connection credentials must reference a personal access token secret.',
          ],
        },
      }),
    );
    render(App, { client: api.client });

    await completeRequiredCreateFields();
    await fireEvent.click(screen.getByRole('button', { name: 'Create connection' }));

    await waitFor(() => {
      expect(document.getElementById('conn-secretName-error')).toHaveTextContent(
        'Connection credentials must reference a personal access token secret.',
      );
    });
  });

  it('renders the secret reference, never a returned secret value', async () => {
    window.history.replaceState({}, '', '/connections/ado-main');
    const api = createApi([connection]);
    render(App, { client: api.client });

    expect(await screen.findByText('ADO_PAT')).toBeVisible();
  });

  it('enables and disables a connection from the list', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Disable Primary Azure DevOps' }));

    await waitFor(() => expect(api.connections.update).toHaveBeenCalledOnce());
    expect(api.connections.update).toHaveBeenCalledWith(
      'ado-main',
      expect.objectContaining({ enabled: false }),
      expect.any(AbortSignal),
    );
    expect(await screen.findByText('Connection disabled')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Enable Primary Azure DevOps' })).toBeVisible();
  });

  it('deletes a connection only after confirmation', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Delete Primary Azure DevOps' }));

    const dialog = await screen.findByRole('dialog', {
      name: 'Delete connection?',
    });
    expect(api.connections.delete).not.toHaveBeenCalled();
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete connection' }));

    await waitFor(() =>
      expect(api.connections.delete).toHaveBeenCalledWith('ado-main', expect.any(AbortSignal)),
    );
    expect(
      await screen.findByRole('heading', { name: 'No connections yet' }),
    ).toBeVisible();
    expect(screen.getByText(/Connection .*Primary Azure DevOps.* was deleted/)).toBeVisible();
  });

  // ── Test Connection — List view ─────────────────────────────────────

  it('renders a Test connection button per row in the list view', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await screen.findByRole('table');

    const buttons = screen.getAllByRole('button', {
      name: /Test connection for Primary Azure DevOps/i,
    });
    expect(buttons.length).toBeGreaterThanOrEqual(1);
    expect(buttons[0]).toBeVisible();
  });

  it('list view: Test connection shows success badge with organization scope', async () => {
    const api = createApi([connection], {
      success: true,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 200,
      errors: [],
      payload: {
        scope: 'organization',
        organizationUrl: 'https://dev.azure.com/example',
      },
    });
    render(App, { client: api.client });

    await screen.findByRole('table');
    const testButton = screen.getByRole('button', {
      name: /Test connection for Primary Azure DevOps/i,
    });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
        expect.anything(),
      ),
    );

    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
    expect(screen.getByText('(organization)')).toBeVisible();
    expect(screen.queryByText(/\d+ repos/)).not.toBeInTheDocument();
  });

  it('list view: Test connection shows plain Connected badge when no payload is returned', async () => {
    const api = createApi([connection], {
      success: true,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 200,
      errors: [],
    });
    render(App, { client: api.client });

    await screen.findByRole('table');
    const testButton = screen.getByRole('button', {
      name: /Test connection for Primary Azure DevOps/i,
    });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
        expect.anything(),
      ),
    );

    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
    expect(screen.queryByText('(organization)')).not.toBeInTheDocument();
    expect(screen.queryByText(/\d+ repos/)).not.toBeInTheDocument();
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
      name: /Test connection for Primary Azure DevOps/i,
    });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
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
    window.history.replaceState({}, '', '/connections/ado-main');
    const api = createApi();
    render(App, { client: api.client });

    expect(await screen.findByText('Connection details')).toBeVisible();

    const testButton = screen.getByRole('button', { name: 'Test connection' });
    expect(testButton).toBeVisible();
    expect(testButton).not.toBeDisabled();
  });

  it('details view: Test connection transitions through loading to success', async () => {
    window.history.replaceState({}, '', '/connections/ado-main');
    const api = createApi([connection], {
      success: true,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 200,
      errors: [],
      payload: {
        scope: 'organization',
        organizationUrl: 'https://dev.azure.com/example',
      },
    });
    render(App, { client: api.client });

    expect(await screen.findByText('Connection details')).toBeVisible();
    const testButton = screen.getByRole('button', { name: 'Test connection' });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
        expect.anything(),
      ),
    );

    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
    expect(screen.getByText('(organization)')).toBeVisible();
    expect(screen.queryByText(/\d+ repos/)).not.toBeInTheDocument();
  });

  it('details view: Test connection transitions through loading to failure', async () => {
    window.history.replaceState({}, '', '/connections/ado-main');
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
        'ado-main',
        expect.anything(),
      ),
    );

    await waitFor(() => expect(screen.getByText('Failed')).toBeVisible());
    expect(screen.getByText('Connection failed: invalid credentials.')).toBeVisible();
  });

  // ── Repo picker ─────────────────────────────────────────────────────

  it('repo picker renders project dropdown and discovered repositories', async () => {
    window.history.replaceState({}, '', '/connections/ado-main/repos?project=proj-1');
    const api = createApi([connection]);
    render(App, { client: api.client });

    expect(await screen.findByText('Available repositories')).toBeVisible();
    await waitFor(() => expect(screen.getByText('web-app')).toBeVisible());
    expect(screen.getByText('HTTPS + PAT')).toBeVisible();
    expect(screen.getAllByRole('button', { name: /Onboard/i }).length).toBeGreaterThanOrEqual(1);
  });

  it('repo picker: clicking Onboard calls the onboard endpoint and navigates', async () => {
    window.history.replaceState({}, '', '/connections/ado-main/repos?project=proj-1');
    const api = createApi([connection]);
    render(App, { client: api.client });

    expect(await screen.findByText('Available repositories')).toBeVisible();
    const onboardButton = await screen.findByRole('button', { name: 'Onboard web-app' });
    fireEvent.click(onboardButton);

    await waitFor(() =>
      expect(api.connections.onboardRepository).toHaveBeenCalledWith(
        'ado-main',
        'proj-1',
        'repo-1',
        undefined,
        expect.any(AbortSignal),
      ),
    );

    await waitFor(() => {
      expect(window.location.pathname).toBe('/repositories/onboarded-repo');
    });
  });

  it('repo picker: shows empty state when no project selected', async () => {
    window.history.replaceState({}, '', '/connections/ado-main/repos');
    const api = createApi([connection]);
    render(App, { client: api.client });

    expect(await screen.findByText('Select a project')).toBeVisible();
  });

  // ── Browse repos link ───────────────────────────────────────────────

  it('list view renders Browse repos link per row', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await screen.findByRole('table');
    const browseLink = screen.getByRole('link', { name: 'Browse repos' });
    expect(browseLink).toBeVisible();
    expect(browseLink).toHaveAttribute('href', '/connections/ado-main/repos');
  });
});
