import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../../App.svelte';
import { ApiError, type ResourceClient, type WebUiApiClient } from '../../api/client';
import type {
  RepositoryProfile,
  RuntimeEnvironmentProfile,
  WorkSourceEnvironmentProfile,
} from '../../api/types';

const environment: WorkSourceEnvironmentProfile = {
  key: 'ado-main',
  displayName: 'Primary boards',
  enabled: true,
  provider: 'AzureDevOpsBoards',
  organizationUrl: 'https://dev.azure.com/example',
  project: 'Agent Controller',
  completedStates: ['Resolved', 'Removed'],
  tagPrefix: 'agent',
  activeState: 'Active',
  completedState: 'Resolved',
  patEnvironmentVariable: 'ADO_PAT',
  createdAt: '2026-07-13T00:00:00Z',
  updatedAt: '2026-07-13T00:00:00Z',
};

function createApi(initialEnvironments: WorkSourceEnvironmentProfile[] = [environment]) {
  let profiles = [...initialEnvironments];

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
    getBoardStates: vi.fn(async () => ({
      Default: ['Active', 'New', 'Removed', 'Resolved'],
    })),
  };

  const client: WebUiApiClient = {
    repositories: staticResource<RepositoryProfile>([]),
    workSourceEnvironments,
    runtimeEnvironments: staticResource<RuntimeEnvironmentProfile>([]),
  };

  return { client, environments: workSourceEnvironments };
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
  await fireEvent.input(screen.getByLabelText(/PAT environment-variable reference/), {
    target: { value: 'SECONDARY_ADO_PAT' },
  });
}

describe('work source environment screens', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/work-source-environments');
  });

  it('creates an environment with board policy and a credential reference', async () => {
    window.history.replaceState({}, '', '/work-source-environments/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Add work source environment' }),
    ).toBeVisible();
    expect(screen.getByText('Secret values are not stored')).toBeVisible();

    // Verify provider selector defaults to Azure DevOps
    expect(screen.getByLabelText(/Work source provider/)).toHaveValue('AzureDevOpsBoards');

    await completeRequiredCreateFields();
    await fireEvent.input(screen.getByLabelText(/Active state/), {
      target: { value: 'Active' },
    });
    await fireEvent.input(screen.getByLabelText(/^Completed state$/), {
      target: { value: 'Resolved' },
    });
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
        completedStates: [],
        tagPrefix: 'agent',
        patEnvironmentVariable: 'SECONDARY_ADO_PAT',
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
    expect(screen.getByText('The PAT environment-variable name is required.')).toBeVisible();
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

  it('renders only the PAT environment-variable reference, never a returned secret value', async () => {
    window.history.replaceState({}, '', '/work-source-environments/ado-main');
    const responseWithUnexpectedSecret = {
      ...environment,
      personalAccessToken: 'super-secret-value',
    } as WorkSourceEnvironmentProfile;
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

  it('submits provider, completedStates and tagPrefix in create flow', async () => {
    window.history.replaceState({}, '', '/work-source-environments/new');
    const api = createApi([]);
    render(App, { client: api.client });

    // Provider select is present and defaults to Azure DevOps
    const providerSelect = screen.getByLabelText(/Work source provider/);
    expect(providerSelect).toHaveValue('AzureDevOpsBoards');

    // Tag prefix field is present with placeholder
    const tagPrefixInput = screen.getByLabelText(/Tag prefix/);
    expect(tagPrefixInput).toHaveAttribute('placeholder', 'agent');

    await completeRequiredCreateFields();
    await fireEvent.input(screen.getByLabelText(/Tag prefix/), {
      target: { value: 'ac' },
    });
    await fireEvent.input(screen.getByLabelText(/Active state/), {
      target: { value: 'Active' },
    });
    await fireEvent.input(screen.getByLabelText(/^Completed state$/), {
      target: { value: 'Resolved' },
    });
    await fireEvent.click(screen.getByRole('button', { name: 'Create environment' }));

    await waitFor(() => expect(api.environments.create).toHaveBeenCalledOnce());
    expect(api.environments.create).toHaveBeenCalledWith(
      expect.objectContaining({
        provider: 'AzureDevOpsBoards',
        tagPrefix: 'ac',
        completedStates: [],
        activeState: 'Active',
        completedState: 'Resolved',
      }),
      expect.any(AbortSignal),
    );
  });
});
