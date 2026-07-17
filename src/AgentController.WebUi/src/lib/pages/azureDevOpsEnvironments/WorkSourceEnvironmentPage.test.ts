import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../../App.svelte';
import { ApiError, type ResourceClient, type WebUiApiClient } from '../../api/client';
import type {
  ConnectionProfile,
  RepositoryProfile,
  RuntimeEnvironmentProfile,
  SecretInfo,
  SecretsResourceClient,
  WorkSourceConnectivityResult,
  WorkSourceEnvironmentProfile,
} from '../../api/types';

const environment: WorkSourceEnvironmentProfile = {
  key: 'ado-main',
  displayName: 'Primary boards',
  enabled: true,
  provider: 'AzureDevOpsBoards',
  organizationUrl: 'https://dev.azure.com/example',
  project: 'Agent Controller',
  tagPrefix: 'agent',
  activeState: 'Active',
  completedState: 'Resolved',
  personalAccessTokenReference: { name: 'ADO_PAT', version: null },
  createdAt: '2026-07-13T00:00:00Z',
  updatedAt: '2026-07-13T00:00:00Z',
};

function createApi(
  initialEnvironments: WorkSourceEnvironmentProfile[] = [environment],
  verifyResult?: WorkSourceConnectivityResult,
  initialSecrets: SecretInfo[] = [{ name: 'ADO_PAT', latestVersion: 1, createdAt: '2026-07-13T00:00:00Z', updatedAt: '2026-07-13T00:00:00Z' }],
) {
  let profiles = [...initialEnvironments];
  let secrets = [...initialSecrets];

  const defaultVerifyResult: WorkSourceConnectivityResult = {
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
      async (): Promise<WorkSourceConnectivityResult> => verifyResult ?? defaultVerifyResult,
    ),
  };

  const secretsClient: SecretsResourceClient = {
    list: vi.fn(async () => [...secrets]),
    listVersions: vi.fn(async () => []),
    create: vi.fn(async (req) => { secrets.push({ name: req.name, latestVersion: 1, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString() }); return { name: req.name }; }),
    createVersion: vi.fn(async (name) => ({ name, version: 2 })),
  };

  const client: WebUiApiClient = {
    repositories: staticResource<RepositoryProfile>([]),
    workSourceEnvironments,
    connections: {
      ...staticResource<ConnectionProfile>([]),
      verifyConnection: async () => ({
        success: true,
        authMechanism: 'PersonalAccessToken',
        errors: [],
      }),
      listProjects: async () => [],
      listRepositories: async () => [],
      onboardRepository: async () => ({} as RepositoryProfile),
    },
    runtimeEnvironments: staticResource<RuntimeEnvironmentProfile>([]),
    secrets: secretsClient,
  };

  return { client, environments: workSourceEnvironments, secrets: secretsClient };
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
  await fireEvent.input(screen.getByLabelText(/Organization URL/), {
    target: { value: 'https://dev.azure.com/example/' },
  });
  await fireEvent.input(screen.getByLabelText(/^Project/), {
    target: { value: 'Secondary Project' },
  });
  // Select a secret from the combobox picker
  await fireEvent.click(screen.getByRole('button', { name: 'PAT secret (required)' }));
  await waitFor(() => expect(screen.getByText('ADO_PAT')).toBeVisible());
  fireEvent.click(screen.getByRole('option', { name: /ADO_PAT/ }));
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
    // Verify provider selector defaults to Azure DevOps
    expect(screen.getByLabelText(/Work source provider/)).toHaveValue('AzureDevOpsBoards');

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
        organizationUrl: 'https://dev.azure.com/example',
        project: 'Secondary Project',
        tagPrefix: 'agent',
        personalAccessTokenReference: { name: 'ADO_PAT', version: null },
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

    await fireEvent.click(await screen.findByRole('button', { name: 'Create environment' }));

    expect(await screen.findByText('Correct the highlighted fields')).toBeVisible();
    expect(screen.getByText('A key is required.')).toBeVisible();
    expect(screen.getByText('An Azure DevOps organization URL is required.')).toBeVisible();
    expect(screen.getByText('A secret reference for the PAT is required.')).toBeVisible();
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
          organizationUrl: ['The organization URL must not include a collection path.'],
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
      screen.getByText('The organization URL must not include a collection path.'),
    ).toBeVisible();
    expect(screen.getByLabelText(/Organization URL/)).toHaveAttribute('aria-invalid', 'true');
  });

  it('renders only the secret name reference, never a returned secret value', async () => {
    window.history.replaceState({}, '', '/work-source-environments/ado-main');
    const responseWithUnexpectedSecret = {
      ...environment,
      personalAccessToken: 'super-secret-value',
    } as unknown as WorkSourceEnvironmentProfile;
    const api = createApi([responseWithUnexpectedSecret]);
    render(App, { client: api.client });

    expect(await screen.findByText('ADO_PAT')).toBeVisible();
    expect(screen.getByText('Secret value redacted')).toBeVisible();
    expect(screen.queryByText('super-secret-value')).not.toBeInTheDocument();
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

    // Provider select is present and defaults to Azure DevOps
    const providerSelect = screen.getByLabelText(/Work source provider/);
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

    // verifyConnection should have been called with the environment key
    await waitFor(() =>
      expect(api.environments.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
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
      expect(api.environments.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
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
      expect(api.environments.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
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
    api.environments.verifyConnection.mockImplementationOnce(
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
      expect(api.environments.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
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

    // verifyConnection called with the correct key
    await waitFor(() =>
      expect(api.environments.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
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
      expect(api.environments.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
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
    api.environments.verifyConnection.mockImplementationOnce(
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
      expect(api.environments.verifyConnection).toHaveBeenCalledWith(
        'ado-main',
        expect.anything(),
      ),
    );

    // After the delayed resolution, result card appears
    await waitFor(() => expect(screen.getByText('Connected')).toBeVisible());
    expect(screen.getByText('(1 repos)')).toBeVisible();
  });
});
