import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import App from '../../../App.svelte';
import { ApiError, type ResourceClient, type SecretsResourceClient, type WebUiApiClient } from '../../api/client';
import type { CreatedSecretResponse, SecretInfo, SecretVersionInfo } from '../../api/types';

const secretName = 'test-secret';
const secret: SecretInfo = {
  name: secretName,
  latestVersion: 1,
  createdAt: '2026-07-16T00:00:00Z',
  updatedAt: '2026-07-16T00:00:00Z',
  secretType: 'personal-access-token',
};

const sshSecretName = 'deploy-key';
const sshSecret: SecretInfo = {
  name: sshSecretName,
  latestVersion: 1,
  createdAt: '2026-07-16T00:00:00Z',
  updatedAt: '2026-07-16T00:00:00Z',
  secretType: 'ssh-key',
};

function createApi(initialSecrets: SecretInfo[] = [secret]) {
  let secretsList = [...initialSecrets];

  const secretsClient: SecretsResourceClient = {
    list: vi.fn(async () => [...secretsList]),
    listVersions: vi.fn(async (name: string): Promise<SecretVersionInfo[]> => {
      if (name === sshSecretName) {
        return [{
          version: 1,
          createdAt: '2026-07-16T00:00:00Z',
          secretType: 'ssh-key',
          publicKey: 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample user@host',
        }];
      }
      return [{ version: 1, createdAt: '2026-07-16T00:00:00Z', secretType: 'personal-access-token', publicKey: null }];
    }),
    create: vi.fn(async (req): Promise<CreatedSecretResponse> => {
      const created = { name: req.name, latestVersion: 1 };
      secretsList.push({
        name: req.name,
        latestVersion: 1,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        secretType: req.payload.type,
      });
      return created;
    }),
    createVersion: vi.fn(async (name: string) => ({ name, version: 2 })),
    delete: vi.fn(async (name: string) => {
      secretsList = secretsList.filter((candidate) => candidate.name !== name);
    }),
  };

  const client = {
    secrets: secretsClient,
    connections: {
      list: vi.fn(async () => []),
      get: vi.fn(async () => ({})),
      create: vi.fn(async () => ({})),
      update: vi.fn(async () => ({})),
      delete: vi.fn(async () => undefined),
      verifyConnection: vi.fn(async () => ({ success: true, errors: [] })),
      listProjects: vi.fn(async () => []),
      listRepositories: vi.fn(async () => []),
      listBranches: vi.fn(async () => []),
      onboardRepository: vi.fn(async () => ({})),
    },
    repositories: staticResource([]),
    runtimeEnvironments: staticResource([]),
    workSourceEnvironments: {
      ...staticResource([]),
      verifyConnection: vi.fn(async () => ({ success: true, errors: [] })),
    },
  } as unknown as WebUiApiClient;

  return { client, secrets: secretsClient };
}

function staticResource<T>(items: T[]): ResourceClient<T> {
  return {
    list: vi.fn(async () => items),
    get: vi.fn(async () => items[0]),
    create: vi.fn(async (item: T) => item),
    update: vi.fn(async (_key: string, item: T) => item),
    delete: vi.fn(async () => undefined),
  };
}

describe('secret screens', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/secrets');
  });

  it('shows loading, empty, and retryable error states for the secrets list', async () => {
    const loadingApi = createApi();
    let resolveList!: (value: SecretInfo[]) => void;
    vi.mocked(loadingApi.secrets.list).mockImplementationOnce(
      () => new Promise((resolve) => { resolveList = resolve; }),
    );
    const loadingView = render(App, { client: loadingApi.client });

    expect(screen.getByRole('status')).toHaveTextContent('Loading secrets…');
    resolveList([secret]);
    expect(await screen.findByRole('link', { name: secretName })).toBeVisible();
    loadingView.unmount();

    window.history.replaceState({}, '', '/secrets');
    const emptyApi = createApi([]);
    const emptyView = render(App, { client: emptyApi.client });
    expect(await screen.findByRole('heading', { name: 'No secrets yet' })).toBeVisible();
    emptyView.unmount();

    window.history.replaceState({}, '', '/secrets');
    const errorApi = createApi();
    vi.mocked(errorApi.secrets.list).mockRejectedValueOnce(new Error('Secrets are unavailable.'));
    render(App, { client: errorApi.client });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Could not load secrets');
    expect(alert).toHaveTextContent('Secrets are unavailable.');
    await fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('link', { name: secretName })).toBeVisible();
  });

  it('shows a load error instead of a mistyped version form for an unknown secret', async () => {
    window.history.replaceState({}, '', '/secrets/missing-secret/new-version');
    const api = createApi();
    render(App, { client: api.client });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Could not load secret');
    expect(alert).toHaveTextContent('Secret not found.');
    expect(screen.queryByRole('button', { name: 'Create version' })).toBeNull();
    expect(api.secrets.createVersion).not.toHaveBeenCalled();
  });

  it('creates a new version and navigates to versions view with success notice', async () => {
    window.history.replaceState({}, '', `/secrets/${secretName}/new-version`);
    const api = createApi();
    render(App, { client: api.client });

    // Wait for the page to load and show the form
    expect(
      await screen.findByText('New secret version'),
    ).toBeVisible();

    // Enter a new value (use placeholder because SecretForm omits id on Field)
    await fireEvent.input(screen.getByPlaceholderText('Enter new token value'), {
      target: { value: 'new-secret-value-123' },
    });

    // Submit the form
    await fireEvent.click(screen.getByRole('button', { name: 'Create version' }));

    // Verify createVersion was called with a PAT payload
    await waitFor(() => expect(api.secrets.createVersion).toHaveBeenCalledOnce());
    expect(api.secrets.createVersion).toHaveBeenCalledWith(
      secretName,
      expect.objectContaining({
        payload: { type: 'personal-access-token', value: 'new-secret-value-123' },
      }),
      expect.any(AbortSignal),
    );

    // Navigates to the versions view
    await waitFor(() =>
      expect(window.location.pathname).toBe(`/secrets/${secretName}/versions`),
    );
    expect(await screen.findByText('Version created')).toBeVisible();
    expect(screen.queryByText('Could not create version')).toBeNull();
  });

  it('cancels the new-version form and navigates to versions view without calling the API', async () => {
    window.history.replaceState({}, '', `/secrets/${secretName}/new-version`);
    const api = createApi();
    render(App, { client: api.client });

    expect(
      await screen.findByText('New secret version'),
    ).toBeVisible();

    await fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() =>
      expect(window.location.pathname).toBe(`/secrets/${secretName}/versions`),
    );

    expect(screen.queryByText('Could not create version')).toBeNull();
    expect(api.secrets.createVersion).not.toHaveBeenCalled();
  });

  it('blocks new-version submission with empty value and shows validation error', async () => {
    window.history.replaceState({}, '', `/secrets/${secretName}/new-version`);
    const api = createApi();
    render(App, { client: api.client });

    expect(
      await screen.findByText('New secret version'),
    ).toBeVisible();

    await fireEvent.click(screen.getByRole('button', { name: 'Create version' }));

    expect(await screen.findByText('Secret value is required.')).toBeVisible();
    expect(api.secrets.createVersion).not.toHaveBeenCalled();
  });

  it('blocks create-mode submission without a name', async () => {
    window.history.replaceState({}, '', '/secrets/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Add secret' }),
    ).toBeVisible();

    // Enter a value but leave the name empty
    await fireEvent.input(screen.getByPlaceholderText('Enter token value'), {
      target: { value: 'some-value' },
    });

    await fireEvent.click(screen.getByRole('button', { name: 'Create secret' }));

    expect(await screen.findByText('Secret name is required.')).toBeVisible();
    expect(api.secrets.create).not.toHaveBeenCalled();
  });

  it('opens the delete confirmation dialog and canceling does not call the API', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: `Delete ${secretName}` }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete secret?' });
    expect(dialog).toHaveTextContent('This action cannot be undone.');

    await fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    expect(api.secrets.delete).not.toHaveBeenCalled();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.getByRole('link', { name: secretName })).toBeVisible();
  });

  it('deletes a secret from the list only after confirmation', async () => {
    const api = createApi();
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: `Delete ${secretName}` }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete secret?' });
    expect(api.secrets.delete).not.toHaveBeenCalled();
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete secret' }));

    await waitFor(() =>
      expect(api.secrets.delete).toHaveBeenCalledWith(secretName, expect.any(AbortSignal)),
    );
    expect(await screen.findByText('Secret deleted')).toBeVisible();
    expect(screen.queryByRole('link', { name: secretName })).toBeNull();
    expect(screen.getByRole('heading', { name: 'No secrets yet' })).toBeVisible();
  });

  it('navigates back to the secrets list after deleting from the detail view', async () => {
    window.history.replaceState({}, '', `/secrets/${secretName}`);
    const api = createApi();
    render(App, { client: api.client });

    expect(await screen.findByText('Secret details')).toBeVisible();
    await fireEvent.click(screen.getByRole('button', { name: `Delete ${secretName}` }));

    const dialog = await screen.findByRole('dialog', { name: 'Delete secret?' });
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete secret' }));

    await waitFor(() =>
      expect(api.secrets.delete).toHaveBeenCalledWith(secretName, expect.any(AbortSignal)),
    );
    await waitFor(() => expect(window.location.pathname).toBe('/secrets'));
    expect(await screen.findByText('Secret deleted')).toBeVisible();
  });

  it('keeps the secret and shows the conflict detail when the delete is rejected as in use', async () => {
    const api = createApi();
    vi.mocked(api.secrets.delete).mockRejectedValueOnce(
      new ApiError({
        status: 409,
        title: 'Resource conflict.',
        detail: `Secret '${secretName}' is referenced by connection 'ado-main'.`,
      }),
    );
    render(App, { client: api.client });

    await fireEvent.click(await screen.findByRole('button', { name: `Delete ${secretName}` }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete secret?' });
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Delete secret' }));

    expect(
      await within(dialog).findByText(
        `Secret '${secretName}' is referenced by connection 'ado-main'.`,
      ),
    ).toBeVisible();
    expect(screen.getByRole('dialog', { name: 'Delete secret?' })).toBeVisible();
    expect(api.secrets.delete).toHaveBeenCalledWith(secretName, expect.any(AbortSignal));
    expect(screen.getByRole('link', { name: secretName })).toBeVisible();
    expect(screen.queryByText('Secret deleted')).toBeNull();
  });

  it('creates a PAT secret with type selector', async () => {
    window.history.replaceState({}, '', '/secrets/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Add secret' }),
    ).toBeVisible();

    // Default type is PAT — no need to click the selector
    await fireEvent.input(screen.getByPlaceholderText('e.g. azure-devops-pat'), {
      target: { value: 'my-pat' },
    });

    await fireEvent.input(screen.getByPlaceholderText('Enter token value'), {
      target: { value: 'pat-value-123' },
    });

    await fireEvent.click(screen.getByRole('button', { name: 'Create secret' }));

    await waitFor(() => expect(api.secrets.create).toHaveBeenCalledOnce());
    expect(api.secrets.create).toHaveBeenCalledWith(
      expect.objectContaining({
        name: 'my-pat',
        payload: { type: 'personal-access-token', value: 'pat-value-123' },
      }),
      expect.any(AbortSignal),
    );
  });

  it('creates an SSH key secret via the type selector', async () => {
    window.history.replaceState({}, '', '/secrets/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Add secret' }),
    ).toBeVisible();

    // Switch type to SSH Key
    const sshOption = screen.getByRole('radio', { name: /SSH Key/ });
    await fireEvent.click(sshOption);

    await fireEvent.input(screen.getByPlaceholderText('e.g. azure-devops-pat'), {
      target: { value: 'my-ssh-key' },
    });

    await fireEvent.input(screen.getByPlaceholderText(/BEGIN OPENSSH/), {
      target: { value: '-----BEGIN OPENSSH PRIVATE KEY-----\nabc123\n-----END OPENSSH PRIVATE KEY-----' },
    });

    await fireEvent.input(screen.getByPlaceholderText('ssh-ed25519 AAAAC3... user@host'), {
      target: { value: 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample user@host' },
    });

    await fireEvent.click(screen.getByRole('button', { name: 'Create secret' }));

    await waitFor(() => expect(api.secrets.create).toHaveBeenCalledOnce());
    expect(api.secrets.create).toHaveBeenCalledWith(
      expect.objectContaining({
        name: 'my-ssh-key',
        payload: {
          type: 'ssh-key',
          privateKey: '-----BEGIN OPENSSH PRIVATE KEY-----\nabc123\n-----END OPENSSH PRIVATE KEY-----',
          publicKey: 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample user@host',
          passphrase: null,
        },
      }),
      expect.any(AbortSignal),
    );
  });

  it('creates an SSH key secret with a passphrase', async () => {
    window.history.replaceState({}, '', '/secrets/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Add secret' }),
    ).toBeVisible();

    // Switch to SSH Key
    const sshOption = screen.getByRole('radio', { name: /SSH Key/ });
    await fireEvent.click(sshOption);

    await fireEvent.input(screen.getByPlaceholderText('e.g. azure-devops-pat'), {
      target: { value: 'encrypted-ssh-key' },
    });

    await fireEvent.input(screen.getByPlaceholderText(/BEGIN OPENSSH/), {
      target: { value: '-----BEGIN OPENSSH PRIVATE KEY-----\nencrypted\n-----END OPENSSH PRIVATE KEY-----' },
    });

    await fireEvent.input(screen.getByPlaceholderText('ssh-ed25519 AAAAC3... user@host'), {
      target: { value: 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample user@host' },
    });

    // Enable passphrase toggle
    const passphraseSwitch = screen.getByRole('switch', { name: /passphrase/i });
    await fireEvent.click(passphraseSwitch);

    await fireEvent.input(screen.getByPlaceholderText('Enter passphrase'), {
      target: { value: 'my-passphrase' },
    });

    await fireEvent.click(screen.getByRole('button', { name: 'Create secret' }));

    await waitFor(() => expect(api.secrets.create).toHaveBeenCalledOnce());
    expect(api.secrets.create).toHaveBeenCalledWith(
      expect.objectContaining({
        name: 'encrypted-ssh-key',
        payload: {
          type: 'ssh-key',
          privateKey: expect.stringContaining('BEGIN OPENSSH PRIVATE KEY'),
          publicKey: expect.stringContaining('ssh-ed25519'),
          passphrase: 'my-passphrase',
        },
      }),
      expect.any(AbortSignal),
    );
  });

  it('requires a passphrase value when passphrase storage is enabled', async () => {
    window.history.replaceState({}, '', '/secrets/new');
    const api = createApi([]);
    render(App, { client: api.client });

    expect(await screen.findByRole('heading', { level: 1, name: 'Add secret' })).toBeVisible();
    await fireEvent.click(screen.getByRole('radio', { name: /SSH Key/ }));
    await fireEvent.input(screen.getByPlaceholderText('e.g. azure-devops-pat'), {
      target: { value: 'encrypted-key' },
    });
    await fireEvent.input(screen.getByPlaceholderText(/BEGIN OPENSSH/), {
      target: { value: 'private-key' },
    });
    await fireEvent.input(screen.getByPlaceholderText('ssh-ed25519 AAAAC3... user@host'), {
      target: { value: 'public-key' },
    });
    await fireEvent.click(screen.getByRole('switch', { name: /passphrase/i }));
    await fireEvent.click(screen.getByRole('button', { name: 'Create secret' }));

    expect(await screen.findByText('Passphrase is required when enabled.')).toBeVisible();
    expect(api.secrets.create).not.toHaveBeenCalled();
  });

  it('shows SSH version type badge and public key on the versions page', async () => {
    window.history.replaceState({}, '', `/secrets/${sshSecretName}`);
    const api = createApi([secret, sshSecret]);
    render(App, { client: api.client });

    expect((await screen.findAllByText('SSH Key')).length).toBeGreaterThanOrEqual(2);

    // The public key column should show for SSH versions
    expect(await screen.findByText(/ssh-ed25519 AAAAC3/)).toBeVisible();

    // Full-key view and copy controls are available for public material only.
    const viewButton = screen.getByRole('button', { name: 'View' });
    expect(viewButton).toHaveAttribute('aria-expanded', 'false');
    await fireEvent.click(viewButton);
    expect(viewButton).toHaveAttribute('aria-expanded', 'true');
    expect(document.getElementById('public-key-1')).toHaveTextContent('ssh-ed25519 AAAAC3');
    expect(screen.getByTitle('Copy public key')).toBeVisible();

    // Private key and passphrase text should never appear in the UI
    expect(screen.queryByText(/BEGIN OPENSSH PRIVATE KEY/)).toBeNull();
  });

  it('shows PAT version badge and no public key on the versions page', async () => {
    window.history.replaceState({}, '', `/secrets/${secretName}`);
    const api = createApi();
    render(App, { client: api.client });

    expect((await screen.findAllByText('PAT')).length).toBeGreaterThanOrEqual(2);
    // No public key for PAT secrets
    expect(screen.getByText('—')).toBeVisible();
  });

  it('keeps the SSH type immutable and submits a complete replacement version', async () => {
    window.history.replaceState({}, '', `/secrets/${sshSecretName}/new-version`);
    const api = createApi([secret, sshSecret]);
    render(App, { client: api.client });

    expect(
      await screen.findByText('New secret version'),
    ).toBeVisible();

    // The SSH Key type should be shown as immutable
    expect(await screen.findByText('SSH Key')).toBeVisible();
    expect(screen.getByText('Type is immutable once set.')).toBeVisible();

    // The form requires a complete SSH payload rather than redisplaying stored values.
    const privateKeyInput = screen.getByPlaceholderText(/BEGIN OPENSSH/);
    const publicKeyInput = screen.getByPlaceholderText('ssh-ed25519 AAAAC3... user@host');
    expect(privateKeyInput).toBeVisible();
    expect(privateKeyInput).toHaveValue('');
    expect(publicKeyInput).toHaveValue('');

    await fireEvent.input(privateKeyInput, {
      target: { value: '-----BEGIN OPENSSH PRIVATE KEY-----\nreplacement\n-----END OPENSSH PRIVATE KEY-----' },
    });
    await fireEvent.input(publicKeyInput, {
      target: { value: 'ssh-ed25519 replacement-key user@host' },
    });
    await fireEvent.click(screen.getByRole('button', { name: 'Create version' }));

    await waitFor(() => expect(api.secrets.createVersion).toHaveBeenCalledOnce());
    expect(api.secrets.createVersion).toHaveBeenCalledWith(
      sshSecretName,
      {
        payload: {
          type: 'ssh-key',
          privateKey: '-----BEGIN OPENSSH PRIVATE KEY-----\nreplacement\n-----END OPENSSH PRIVATE KEY-----',
          publicKey: 'ssh-ed25519 replacement-key user@host',
          passphrase: null,
        },
      },
      expect.any(AbortSignal),
    );
  });

  it('displays type badges for each secret in the list', async () => {
    window.history.replaceState({}, '', '/secrets');
    const api = createApi([secret, sshSecret]);
    render(App, { client: api.client });

    expect(await screen.findByText('PAT')).toBeVisible();
    expect(await screen.findByText('SSH Key')).toBeVisible();
  });
});
