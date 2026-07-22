import { describe, expect, it, vi } from 'vitest';
import { ApiError, createWebUiApiClient, getFieldErrors } from './client';
import type { ConnectionProfile, ConnectionProject, RepositoryProfile } from './types';

const repository: RepositoryProfile = {
  key: 'web.repo',
  cloneUrl: 'https://example.test/repo.git',
  defaultBranch: 'main',
  transport: 'httpsPat',
  environmentProfile: '',
  runtimeProfile: '',
  repositoryHostConnectionKey: null,
  remoteIdentity: null,
  runtimeEnvironmentKey: null,
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
  createdAt: '2026-07-16T00:00:00Z',
  updatedAt: '2026-07-16T00:00:00Z',
};

const connectionProject: ConnectionProject = { id: 'proj-1', name: 'Agent Controller' };

describe('Web UI API client', () => {
  it('uses same-origin API paths and sends typed JSON requests', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) =>
      Response.json(repository, { status: 201, headers: { Location: '/repositories/web.repo' } }),
    );
    const client = createWebUiApiClient({ fetch: fetchMock });

    await expect(client.repositories.create(repository)).resolves.toEqual(repository);
    expect(fetchMock).toHaveBeenCalledOnce();

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/webui/repositories');
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe(JSON.stringify(repository));
    expect(new Headers(init?.headers).get('Content-Type')).toBe('application/json');
  });

  it('encodes profile keys and handles no-content responses', async () => {
    const fetchMock = vi.fn(
      async (_input: RequestInfo | URL, _init?: RequestInit) =>
        new Response(null, { status: 204 }),
    );
    const client = createWebUiApiClient({ fetch: fetchMock });

    await expect(client.repositories.delete('repo key/one')).resolves.toBeUndefined();
    expect(fetchMock.mock.calls[0][0]).toBe('/api/webui/repositories/repo%20key%2Fone');
    expect(fetchMock.mock.calls[0][1]?.method).toBe('DELETE');
  });

  it('gets the resolved clone transport for an encoded repository key', async () => {
    const resolution = {
      transport: 'ssh',
      credentialSource: 'sshKey',
      credentialReference: { name: 'deploy-key', version: 2 },
      blockingIssues: [],
      isReady: true,
    } as const;
    const fetchMock = vi.fn(async () => Response.json(resolution));
    const client = createWebUiApiClient({ fetch: fetchMock });

    await expect(client.repositories.getCloneTransport('repo key/one')).resolves.toEqual(
      resolution,
    );
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/repositories/repo%20key%2Fone/clone-transport',
      expect.objectContaining({}),
    );
  });

  it('posts a credential-aware clone preflight for an encoded repository key', async () => {
    const result = {
      success: false,
      reason: "PAT secret 'clone-pat' (version 2) was not found.",
      failureCode: 'credentialNotFound',
      transport: 'httpsPat',
      cloneUrl: 'https://example.test/repo.git',
      credentialSource: 'connectionPersonalAccessToken',
      credentialReference: { name: 'clone-pat', version: 2 },
    } as const;
    const fetchMock = vi.fn(async () => Response.json(result));
    const client = createWebUiApiClient({ fetch: fetchMock });

    await expect(client.repositories.checkClonePreflight('repo key/one')).resolves.toEqual(
      result,
    );
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/repositories/repo%20key%2Fone/clone-preflight',
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('preserves RFC problem details and field-level validation errors', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) =>
      Response.json(
        {
          title: 'Validation failed.',
          status: 400,
          detail: 'Correct the highlighted fields.',
          errors: { cloneUrl: ['Clone URL must be absolute.'] },
        },
        { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    );
    const client = createWebUiApiClient({ fetch: fetchMock });

    try {
      await client.repositories.get('web.repo');
      throw new Error('Expected the API request to fail.');
    } catch (error) {
      expect(error).toBeInstanceOf(ApiError);
      expect(error).toMatchObject({ status: 400 });
      expect(getFieldErrors(error)).toEqual({ cloneUrl: ['Clone URL must be absolute.'] });
    }
  });

  it('normalizes network failures into a consistent API error', async () => {
    const fetchMock = vi.fn(async () => {
      throw new TypeError('fetch failed');
    });
    const client = createWebUiApiClient({ fetch: fetchMock });

    await expect(client.runtimeEnvironments.list()).rejects.toMatchObject({
      status: 0,
      problem: { title: 'Unable to reach Agent Controller.' },
    });
  });

  it('uses encoded secrets endpoints and preserves typed secret payloads', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'DELETE') return new Response(null, { status: 204 });
      if (init?.method === 'POST' && String(_input).endsWith('/versions')) {
        return Response.json({ name: 'deploy/key', version: 2 });
      }
      if (init?.method === 'POST') {
        return Response.json({ name: 'deploy/key' }, { status: 201 });
      }
      return Response.json([]);
    });
    const client = createWebUiApiClient({ fetch: fetchMock });
    const sshPayload = {
      type: 'ssh-key' as const,
      privateKey: 'private-key-material',
      publicKey: 'ssh-ed25519 public-key-material',
      passphrase: null,
    };

    await client.secrets.list();
    await client.secrets.listVersions('deploy/key');
    await client.secrets.create({ name: 'deploy/key', payload: sshPayload });
    await client.secrets.createVersion('deploy/key', { payload: sshPayload });
    await client.secrets.delete('deploy/key');

    expect(fetchMock.mock.calls[0][0]).toBe('/api/webui/secrets');
    expect(fetchMock.mock.calls[1][0]).toBe('/api/webui/secrets/deploy%2Fkey/versions');
    expect(fetchMock.mock.calls[2]).toEqual([
      '/api/webui/secrets',
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ name: 'deploy/key', payload: sshPayload }) }),
    ]);
    expect(fetchMock.mock.calls[3]).toEqual([
      '/api/webui/secrets/deploy%2Fkey/versions',
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ payload: sshPayload }) }),
    ]);
    expect(fetchMock.mock.calls[4]).toEqual([
      '/api/webui/secrets/deploy%2Fkey',
      expect.objectContaining({ method: 'DELETE' }),
    ]);
  });

  it('connections client: lists connections', async () => {
    const fetchMock = vi.fn(async () => Response.json([connection]));
    const client = createWebUiApiClient({ fetch: fetchMock });

    const result = await client.connections.list();
    expect(result).toEqual([connection]);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/connections',
      expect.objectContaining({}),
    );
  });

  it('connections client: verifies connectivity via POST /connections/{key}/verify', async () => {
    const fetchMock = vi.fn(async () =>
      Response.json({ success: true, authMechanism: 'PersonalAccessToken', errors: [] }),
    );
    const client = createWebUiApiClient({ fetch: fetchMock });

    await client.connections.verifyConnection('ado-main');
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/connections/ado-main/verify',
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('connections client: lists projects via GET /connections/{key}/projects', async () => {
    const fetchMock = vi.fn(async () => Response.json([connectionProject]));
    const client = createWebUiApiClient({ fetch: fetchMock });

    const result = await client.connections.listProjects('ado-main');
    expect(result).toEqual([connectionProject]);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/connections/ado-main/projects',
      expect.objectContaining({}),
    );
  });

  it('connections client: lists repositories with project parameter', async () => {
    const fetchMock = vi.fn(async () => Response.json([]));
    const client = createWebUiApiClient({ fetch: fetchMock });

    await client.connections.listRepositories('ado-main', 'Agent Controller');
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/connections/ado-main/repositories?project=Agent%20Controller',
      expect.objectContaining({}),
    );
  });

  it('connections client: onboards repository with project parameter', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) =>
      Response.json(repository, { status: 201, headers: { Location: '/repositories/onboarded' } }),
    );
    const client = createWebUiApiClient({ fetch: fetchMock });

    await client.connections.onboardRepository('ado-main', 'Agent Controller', 'repo-1');
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/webui/connections/ado-main/repositories/onboard');
    expect(JSON.parse(init?.body as string)).toEqual({
      project: 'Agent Controller',
      repositoryId: 'repo-1',
    });
  });
});
