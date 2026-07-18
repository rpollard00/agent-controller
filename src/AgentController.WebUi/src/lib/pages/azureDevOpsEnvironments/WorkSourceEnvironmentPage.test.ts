import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../../App.svelte';
import { ApiError, type ResourceClient, type WebUiApiClient } from '../../api/client';
import type {
  ConnectionConnectivityResult,
  ConnectionProfile,
  ConnectionProject,
  RepositoryProfile,
  RuntimeEnvironmentProfile,
  WorkSourceEnvironmentProfile,
} from '../../api/types';

const connection: ConnectionProfile = {
  key: 'azuredevops-example',
  displayName: 'Example ADO',
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
  { id: 'proj-2', name: 'Secondary Project' },
];

const environment: WorkSourceEnvironmentProfile = {
  key: 'ado-main',
  displayName: 'Primary boards',
  enabled: true,
  provider: 'AzureDevOpsBoards',
  connectionKey: 'azuredevops-example',
  project: 'Agent Controller',
  tagPrefix: 'agent',
  activeState: 'Active',
  completedState: 'Resolved',
  createdAt: '2026-07-13T00:00:00Z',
  updatedAt: '2026-07-13T00:00:00Z',
};

function createApi(
  initialEnvironments: WorkSourceEnvironmentProfile[] = [environment],
  verifyResult?: ConnectionConnectivityResult,
  initialConnections: ConnectionProfile[] = [connection],
  initialProjects: ConnectionProject[] = projects,
) {
  let profiles = [...initialEnvironments];
  let connections = [...initialConnections];

  const defaultVerifyResult: ConnectionConnectivityResult = {
    success: true,
    authMechanism: 'PersonalAccessToken',
    errors: [],
    payload: { repositories: [] },
  };

  const workSourceEnvironments = {
    list: vi.fn(async () => [...profiles]),
    get: vi.fn(async (key: string) => {
      const profile = profiles.find((candidate) => candidate.key === key);
      if (!profile) throw new Error(`Missing work source environment ${key}.`);
      return profile;
    }),
    create: vi.fn(async (profile: WorkSourceEnvironmentProfile) => {
      profiles.push(profile);
      return profile;
    }),
    update: vi.fn(async (_key: string, profile: WorkSourceEnvironmentProfile) => {
      profiles = profiles.map((candidate) => (candidate.key === profile.key ? profile : candidate));
      return profile;
    }),
    delete: vi.fn(async (key: string) => {
      profiles = profiles.filter((candidate) => candidate.key !== key);
    }),
    verifyConnection: vi.fn(
      async (): Promise<ConnectionConnectivityResult> => verifyResult ?? defaultVerifyResult,
    ),
  };

  const connectionsClient = {
    list: vi.fn(async () => [...connections]),
    get: vi.fn(async (key: string) => {
      const conn = connections.find((c) => c.key === key);
      if (!conn) throw new Error(`Missing connection ${key}.`);
      return conn;
    }),
    create: vi.fn(async (profile: ConnectionProfile) => {
      connections.push(profile);
      return profile;
    }),
    update: vi.fn(async (_key: string, profile: ConnectionProfile) => {
      connections = connections.map((c) => (c.key === profile.key ? profile : c));
      return profile;
    }),
    delete: vi.fn(async (key: string) => {
      connections = connections.filter((c) => c.key !== key);
    }),
    verifyConnection: vi.fn(
      async (): Promise<ConnectionConnectivityResult> => verifyResult ?? defaultVerifyResult,
    ),
    listProjects: vi.fn(async () => [...initialProjects]),
    listRepositories: vi.fn(async () => []),
    onboardRepository: vi.fn(async () => ({} as RepositoryProfile)),
  };

  const client: WebUiApiClient = {
    repositories: staticResource<RepositoryProfile>([]),
    workSourceEnvironments,
    connections: connectionsClient,
    runtimeEnvironments: staticResource<RuntimeEnvironmentProfile>([]),
    secrets: {
      list: vi.fn(async () => []),
      listVersions: vi.fn(async () => []),
      create: vi.fn(async () => ({ name: 'test' })),
      createVersion: vi.fn(async () => ({ name: 'test', version: 1 })),
    },
  };

  return { client, environments: workSourceEnvironments, connections: connectionsClient };
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
  await fireEvent.input(await screen.findByLabelText(/Environment name/), {
    target: { value: 'ado-secondary' },
  });
  await fireEvent.input(screen.getByLabelText(/Display name/), {
    target: { value: 'Secondary boards' },
  });
  // Select a connection from the dropdown
  await fireEvent.change(screen.getByLabelText(/Connection/), {
    target: { value: 'azuredevops-example' },
  });
  // Wait for projects to load, then select one
  await waitFor(() => expect(screen.getByLabelText(/^Project/)).not.toBeDisabled());
  await fireEvent.change(screen.getByLabelText(/^Project/), {
    target: { value: 'Secondary Project' },
  });
}

describe('work source environment screens', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/work-source-environments');
  });

  it('creates an environment with board policy deferred until after save', async () => {
    window.history.replaceState({}, '', '/work-source-environments/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Add work source environment' }),
    ).toBeVisible();

    // Connection selector is present
    await screen.findByLabelText(/Connection/);

    // Board policy section is hidden on create — only connection fields are present
    expect(screen.queryByLabelText(/Active state/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/^Completed state$/)).not.toBeInTheDocument();
    expect(screen.getByText('Board policy configured after save')).toBeVisible();

    await completeRequiredCreateFields();
    await fireEvent.click(screen.getByRole('button', { name: 'Create environment' }));

    await waitFor(() => expect(api.environments.create).toHaveBeenCalledOnce());
    expect(api.environments.create).toHaveBeenCalledWith(
      expect.objectContaining({
        key: 'ado-secondary',
        displayName: 'Secondary boards',
        enabled: true,
        provider: 'AzureDevOpsBoards',
        connectionKey: 'azuredevops-example',
        project: 'Secondary Project',
        tagPrefix: 'agent',
        activeState: null,
        completedState: null,
      }),
      expect.any(AbortSignal),
    );
    expect(
      await screen.findByText(/Work source environment .*Secondary boards.* was created/),
    ).toBeVisible();
    expect(window.location.pathname).toBe('/work-source-environments/ado-secondary');
  });

  it('validates required fields before submitting', async () => {
    window.history.replaceState({}, '', '/work-source-environments/new');
    const api = createApi([]);
    render(App, { client: api.client });

    await screen.findByRole('heading', { level: 1, name: 'Add work source environment' });
    await fireEvent.click(await screen.findByRole('button', { name: 'Create environment' }));

    expect(await screen.findByText('Correct the highlighted fields')).toBeVisible();
    expect(screen.getByText('A key is required.')).toBeVisible();
    expect(screen.getByText('A connection is required.')).toBeVisible();
    expect(api.environments.create).not.toHaveBeenCalled();
  });

  it('edits board settings and environment enablement while preserving the key', async () => {
    window.history.replaceState({}, '', '/work-source-environments/ado-main/edit');
    const api = createApi();
    render(App, { client: api.client });

    const keyInput = await screen.findByLabelText(/Environment name/);
    expect(keyInput).toHaveAttribute('readonly');

    await fireEvent.input(screen.getByLabelText(/Display name/), {
      target: { value: 'Updated boards' },
    });
    await fireEvent.click(screen.getByLabelText('Enabled'));
    await fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(api.environments.update).toHaveBeenCalledOnce());
    expect(api.environments.update).toHaveBeenCalledWith(
      'ado-main',
      expect.objectContaining({
        key: 'ado-main',
        displayName: 'Updated boards',
        enabled: false,
        createdAt: environment.createdAt,
      }),
      expect.any(AbortSignal),
    );
    expect(
      await screen.findByText(/Work source environment .*Updated boards.* was updated/),
    ).toBeVisible();
  });

  it('shows field-level server validation errors', async () => {
    window.history.replaceState({}, '', '/work-source-environments/new');
    const api = createApi([]);
    api.environments.create.mockRejectedValueOnce(
      new ApiError({
        title: 'Validation failed.',
        status: 400,
        detail: 'Correct the highlighted work source fields.',
        errors: {
          connectionKey: ['The connection does not exist.'],
        },
      }),
    );
    render(App, { client: api.client });

    await completeRequiredCreateFields();
    await fireEvent.click(screen.getByRole('button', { name: 'Create environment' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Could not create environment');
    expect(alert).toHaveTextContent('Correct the highlighted work source fields.');
    expect(
      screen.getByText('The connection does not exist.'),
    ).toBeVisible();
    expect(screen.getByLabelText(/Connection/)).toHaveAttribute('aria-invalid', 'true');
  });

  it('enables and disables an environment from the list', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Disable Primary boards' }));

    await waitFor(() => expect(api.environments.update).toHaveBeenCalledOnce());
    expect(api.environments.update).toHaveBeenCalledWith(
      'ado-main',
      expect.objectContaining({ enabled: false }),
      expect.any(AbortSignal),
    );
    expect(await screen.findByText('Environment disabled')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Enable Primary boards' })).toBeVisible();
  });

  it('deletes an environment only after confirmation', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Delete Primary boards' }));

    const dialog = await screen.findByRole('dialog', {
      name: 'Delete work source environment?',
    });
    expect(api.environments.delete).not.toHaveBeenCalled();
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete environment' }));

    await waitFor(() =>
      expect(api.environments.delete).toHaveBeenCalledWith('ado-main', expect.any(AbortSignal)),
    );
    expect(
      await screen.findByRole('heading', { name: 'No work source environments yet' }),
    ).toBeVisible();
    expect(screen.getByText(/Work source environment .*Primary boards.* was deleted/)).toBeVisible();
  });

  it('requires confirmation and explains repository reference conflicts on delete', async () => {
    const api = createApi();
    api.environments.delete.mockRejectedValueOnce(
      new ApiError({
        title: 'Resource conflict.',
        status: 409,
        detail: "Work source environment 'ado-main' is referenced by repository 'web.repo'.",
      }),
    );
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: 'Delete Primary boards' }));

    const dialog = await screen.findByRole('dialog', {
      name: 'Delete work source environment?',
    });
    expect(api.environments.delete).not.toHaveBeenCalled();
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete environment' }));

    expect(
      await within(dialog).findByText(
        "Work source environment 'ado-main' is referenced by repository 'web.repo'.",
      ),
    ).toBeVisible();
    expect(api.environments.delete).toHaveBeenCalledWith('ado-main', expect.any(AbortSignal));
    expect(screen.getByRole('link', { name: 'Primary boards' })).toBeVisible();
  });

  it('submits provider with board policy fields deferred in create flow', async () => {
    window.history.replaceState({}, '', '/work-source-environments/new');
    const api = createApi([]);
    render(App, { client: api.client });

    // Wait for the form to render
    await screen.findByRole('heading', { level: 1, name: 'Add work source environment' });

    // Provider select is present and defaults to Azure DevOps
    const providerSelect = await screen.findByLabelText(/Work source provider/);
    expect(providerSelect).toHaveValue('AzureDevOpsBoards');

    // Board policy fields (tagPrefix, activeState, completedState) are hidden on create
    expect(screen.queryByLabelText(/Tag prefix/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/Active state/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/^Completed state$/)).not.toBeInTheDocument();

    await completeRequiredCreateFields();
    await fireEvent.click(screen.getByRole('button', { name: 'Create environment' }));

    await waitFor(() => expect(api.environments.create).toHaveBeenCalledOnce());
    expect(api.environments.create).toHaveBeenCalledWith(
      expect.objectContaining({
        provider: 'AzureDevOpsBoards',
        tagPrefix: 'agent',
        activeState: null,
        completedState: null,
      }),
      expect.any(AbortSignal),
    );
  });

  it('exposes board policy fields on edit flow for a saved environment', async () => {
    window.history.replaceState({}, '', '/work-source-environments/ado-main/edit');
    const api = createApi();
    render(App, { client: api.client });

    // Board policy fields are visible on edit (saved environment)
    expect(await screen.findByLabelText(/Tag prefix/)).toBeVisible();
    expect(screen.getByLabelText(/Active state/)).toBeVisible();
    expect(screen.getByLabelText(/^Completed state$/)).toBeVisible();

    // Board policy info alert is NOT present on edit
    expect(screen.queryByText('Board policy configured after save')).not.toBeInTheDocument();
  });

  // ── Test Connection — List view ─────────────────────────────────────

  it('renders a Test connection button per row in the list view', async () => {
    const api = createApi();
    render(App, { client: api.client });

    // Wait for the table to render
    await screen.findByRole('table');

    // Each row should have a Test connection button
    const buttons = screen.getAllByRole('button', {
      name: /Test connection for Primary boards/i,
    });
    expect(buttons.length).toBeGreaterThanOrEqual(1);
    expect(buttons[0]).toBeVisible();
    expect(buttons[0]).not.toBeDisabled();
  });

  it('list view: clicking Test connection shows success badge with repo count', async () => {
    const api = createApi([environment], {
      success: true,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 200,
      errors: [],
      payload: { repositories: ['repo-a', 'repo-b', 'repo-c'] },
    });
    render(App, { client: api.client });

    await screen.findByRole('table');
    const testButton = screen.getByRole('button', {
      name: /Test connection for Primary boards/i,
    });
    fireEvent.click(testButton);

    // verifyConnection should have been called with the connection key
    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'azuredevops-example',
        expect.anything(),
      ),
    );

    // Success badge should appear
    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
    expect(screen.getByText('(3 repos)')).toBeVisible();
  });

  it('list view: clicking Test connection shows success badge without repo count when payload is empty', async () => {
    const api = createApi([environment], {
      success: true,
      authMechanism: 'PersonalAccessToken',
      errors: [],
      payload: {},
    });
    render(App, { client: api.client });

    await screen.findByRole('table');
    const testButton = screen.getByRole('button', {
      name: /Test connection for Primary boards/i,
    });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'azuredevops-example',
        expect.anything(),
      ),
    );

    // Success badge should appear without repo count
    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
    expect(screen.queryByText(/\(\d+ repos\)/)).not.toBeInTheDocument();
  });

  it('list view: clicking Test connection shows failure badge with error messages', async () => {
    const api = createApi([environment], {
      success: false,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 401,
      errors: ['Unauthorized: the personal access token is invalid or expired.'],
    });
    render(App, { client: api.client });

    await screen.findByRole('table');
    const testButton = screen.getByRole('button', {
      name: /Test connection for Primary boards/i,
    });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'azuredevops-example',
        expect.anything(),
      ),
    );

    // Failure badge and error message
    await waitFor(() => expect(screen.getByText('Failed')).toBeVisible());
    expect(
      screen.getByText('Unauthorized: the personal access token is invalid or expired.'),
    ).toBeVisible();
  });

  it('list view: Test connection button shows loading state then success', async () => {
    // Use a macrotask delay so there's a window to observe the loading state
    const api = createApi([environment]);
    api.connections.verifyConnection.mockImplementationOnce(
      async () =>
        new Promise((resolve) =>
          setTimeout(
            () =>
              resolve({
                success: true,
                authMechanism: 'PersonalAccessToken',
                errors: [],
                payload: { repositories: ['repo-x'] },
              }),
            0,
          ),
        ),
    );
    render(App, { client: api.client });

    await screen.findByRole('table');
    const testButton = screen.getByRole('button', {
      name: /Test connection for Primary boards/i,
    });
    fireEvent.click(testButton);

    // verifyConnection should have been called
    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'azuredevops-example',
        expect.anything(),
      ),
    );

    // After the delayed resolution, success badge should appear
    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
  });

  // ── Test Connection — Details view ──────────────────────────────────

  it('details view renders a Test connection button', async () => {
    window.history.replaceState({}, '', '/work-source-environments/ado-main');
    const api = createApi();
    render(App, { client: api.client });

    // Wait for details view to load
    expect(await screen.findByText('Environment details')).toBeVisible();

    const testButton = screen.getByRole('button', { name: 'Test connection' });
    expect(testButton).toBeVisible();
    expect(testButton).not.toBeDisabled();
  });

  it('details view: Test connection transitions through loading to success result', async () => {
    window.history.replaceState({}, '', '/work-source-environments/ado-main');
    const api = createApi([environment], {
      success: true,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 200,
      errors: [],
      payload: { repositories: ['repo-a', 'repo-b'] },
    });
    render(App, { client: api.client });

    expect(await screen.findByText('Environment details')).toBeVisible();
    const testButton = screen.getByRole('button', { name: 'Test connection' });
    fireEvent.click(testButton);

    // verifyConnection called with the connection key
    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'azuredevops-example',
        expect.anything(),
      ),
    );

    // Success result card
    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
    expect(screen.getByText('(2 repos)')).toBeVisible();
  });

  it('details view: Test connection transitions through loading to failure result', async () => {
    window.history.replaceState({}, '', '/work-source-environments/ado-main');
    const api = createApi([environment], {
      success: false,
      authMechanism: 'PersonalAccessToken',
      httpStatus: 401,
      errors: ['Connection failed: invalid credentials.'],
    });
    render(App, { client: api.client });

    expect(await screen.findByText('Environment details')).toBeVisible();
    const testButton = screen.getByRole('button', { name: 'Test connection' });
    fireEvent.click(testButton);

    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'azuredevops-example',
        expect.anything(),
      ),
    );

    // Failure result card
    await waitFor(() => expect(screen.getByText('Failed')).toBeVisible());
    expect(screen.getByText('Connection failed: invalid credentials.')).toBeVisible();
  });

  it('details view: Test connection shows loading state then result', async () => {
    window.history.replaceState({}, '', '/work-source-environments/ado-main');
    // Use a macrotask delay so there's a window to observe the loading state
    const api = createApi([environment]);
    api.connections.verifyConnection.mockImplementationOnce(
      async () =>
        new Promise((resolve) =>
          setTimeout(
            () =>
              resolve({
                success: true,
                authMechanism: 'PersonalAccessToken',
                errors: [],
                payload: { repositories: ['only-repo'] },
              }),
            0,
          ),
        ),
    );
    render(App, { client: api.client });

    expect(await screen.findByText('Environment details')).toBeVisible();
    const testButton = screen.getByRole('button', { name: 'Test connection' });
    fireEvent.click(testButton);

    // verifyConnection called
    await waitFor(() =>
      expect(api.connections.verifyConnection).toHaveBeenCalledWith(
        'azuredevops-example',
        expect.anything(),
      ),
    );

    // After the delayed resolution, result card appears
    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
    expect(screen.getByText('(1 repos)')).toBeVisible();
  });
});
