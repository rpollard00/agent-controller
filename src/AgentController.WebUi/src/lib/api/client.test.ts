import { describe, expect, it, vi } from 'vitest';
import { ApiError, createWebUiApiClient, getFieldErrors } from './client';
import type {
  BoardItemsDebugPageResponse,
  ConnectionProfile,
  ConnectionProject,
  PullRequestsDebugPageResponse,
  RepositoryProfile,
  ReviewerIdentityPolicyMetadata,
  RunCardItem,
} from './types';

const repository: RepositoryProfile = {
  key: 'web.repo',
  cloneUrl: 'https://example.test/repo.git',
  webUrl: null,
  defaultBranch: 'main',
  transport: 'httpsPat',
  environmentProfile: '',
  runtimeProfile: '',
  repositoryHostConnectionKey: null,
  remoteIdentity: null,
  runtimeEnvironmentKey: 'runtime-main',
  reviewerIdentities: [],
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

const runCard: RunCardItem = {
  id: 'run-1',
  kind: 'run',
  status: 'AgentRunning',
  category: 'executing',
  workItemTitle: 'Add runs dashboard',
  workItemUrl: 'https://work.example.test/items/42',
  workItemSource: 'AzureDevOpsBoards',
  repoKey: 'agent-controller',
  repositoryUrl: 'https://git.example.test/agent-controller',
  runtimeType: 'PiMateria',
  runtimeProfileName: 'ReeseProjecto LocalWorkspace',
  environmentProviderType: 'LocalWorkspace',
  runAttempt: 2,
  requestMode: null,
  pullRequest: null,
  cycleNumber: null,
  assistanceStoryWorkItemId: null,
  assistanceStoryExternalId: null,
  assistanceStoryUrl: null,
  feedbackStatus: null,
  cycleStatus: null,
  consumingRunId: null,
  lastEventType: 'runtime.progress',
  lastEventMessage: 'Implementing API client',
  lastEventAt: '2026-07-24T01:00:00Z',
  soakEligibleAt: null,
  createdAt: '2026-07-24T00:00:00Z',
  updatedAt: '2026-07-24T01:00:00Z',
};

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

  it('gets credential-free reviewer policy metadata for an encoded connection key', async () => {
    const metadata: ReviewerIdentityPolicyMetadata = {
      provider: 'AzureDevOps',
      isSupported: true,
      supportedIdentityKinds: [
        {
          kind: 'email',
          label: 'Email / uniqueName',
          hint: 'The Azure DevOps uniqueName, usually an email address.',
          placeholder: 'reviewer@example.com',
          validationCategory: 'email',
        },
      ],
    };
    const fetchMock = vi.fn(async () => Response.json(metadata));
    const client = createWebUiApiClient({ fetch: fetchMock });
    const controller = new AbortController();

    await expect(
      client.connections.getReviewerIdentityPolicy('connection key/one', controller.signal),
    ).resolves.toEqual(metadata);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/connections/connection%20key%2Fone/reviewer-identity-policy',
      expect.objectContaining({ signal: controller.signal }),
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

  it('lists run cards from the runs endpoint and passes through the response', async () => {
    const fetchMock = vi.fn(async () => Response.json([runCard]));
    const client = createWebUiApiClient({ fetch: fetchMock });
    const controller = new AbortController();

    await expect(client.runs.list(controller.signal)).resolves.toEqual([runCard]);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/runs',
      expect.objectContaining({ signal: controller.signal }),
    );
  });

  it('uses endpoint defaults when debug list options are omitted', async () => {
    const boardPage: BoardItemsDebugPageResponse = {
      sourceOptions: [{ key: 'boards-main', displayName: 'Main boards' }],
      items: [],
      failures: [],
      page: 1,
      pageSize: 50,
      total: 0,
      observedAt: '2026-07-30T00:00:00Z',
    };
    const fetchMock = vi.fn(async () => Response.json(boardPage));
    const client = createWebUiApiClient({ fetch: fetchMock });

    const result: BoardItemsDebugPageResponse = await client.debug.boardItems.list();

    expect(result).toEqual(boardPage);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/debug/board-items',
      expect.objectContaining({}),
    );
  });

  it('encodes board debug filters independently and forwards abort signals', async () => {
    const fetchMock = vi.fn(async () => Response.json({ items: [] }));
    const client = createWebUiApiClient({ fetch: fetchMock });
    const controller = new AbortController();

    await client.debug.boardItems.list(
      {
        workSourceEnvironmentKey: 'boards/main & west',
        includeTerminal: true,
        page: 3,
        pageSize: 25,
      },
      controller.signal,
    );

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/debug/board-items?workSourceEnvironmentKey=boards%2Fmain%20%26%20west&includeTerminal=true&page=3&pageSize=25',
      expect.objectContaining({ signal: controller.signal }),
    );
  });

  it('encodes pull request debug filters without adding unrelated parameters', async () => {
    const pullRequestPage: PullRequestsDebugPageResponse = {
      sourceOptions: [{ key: 'ado/main', name: 'Main Azure DevOps' }],
      items: [],
      failures: [],
      page: 2,
      pageSize: 10,
      total: 12,
      observedAt: '2026-07-30T00:00:00Z',
    };
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) =>
      Response.json(pullRequestPage),
    );
    const client = createWebUiApiClient({ fetch: fetchMock });

    const result: PullRequestsDebugPageResponse = await client.debug.pullRequests.list({
      includeInactive: false,
      page: 2,
      pageSize: 10,
    });

    expect(result.total).toBe(12);
    expect(fetchMock.mock.calls[0][0]).toBe(
      '/api/webui/debug/pull-requests?includeInactive=false&page=2&pageSize=10',
    );
  });

  it('escapes every segment of composite debug detail identities', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) =>
      Response.json({ eligible: false }),
    );
    const client = createWebUiApiClient({ fetch: fetchMock });

    await client.debug.boardItems.get('boards/main', 'item #42');
    await client.debug.pullRequests.get('ado/main', 'repo one/two', 'PR #7?');

    expect(fetchMock.mock.calls[0][0]).toBe(
      '/api/webui/debug/board-items/boards%2Fmain/item%20%2342',
    );
    expect(fetchMock.mock.calls[1][0]).toBe(
      '/api/webui/debug/pull-requests/ado%2Fmain/repo%20one%2Ftwo/PR%20%237%3F',
    );
  });

  it('does not convert aborted debug requests into API errors', async () => {
    const abortError = new DOMException('The operation was aborted.', 'AbortError');
    const fetchMock = vi.fn(async () => {
      throw abortError;
    });
    const client = createWebUiApiClient({ fetch: fetchMock });
    const controller = new AbortController();
    controller.abort();

    await expect(
      client.debug.boardItems.list({}, controller.signal),
    ).rejects.toBe(abortError);
  });

  it('preserves problem details from debug requests', async () => {
    const fetchMock = vi.fn(async () =>
      Response.json(
        { title: 'Pull request not found.', status: 404, detail: 'It is no longer visible.' },
        { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    );
    const client = createWebUiApiClient({ fetch: fetchMock });

    await expect(client.debug.pullRequests.get('ado', 'repo', '99')).rejects.toMatchObject({
      status: 404,
      problem: { title: 'Pull request not found.', detail: 'It is no longer visible.' },
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

  it('connections client: lists branches with project and repositoryId parameters', async () => {
    const branches = ['main', 'develop', 'feature/new-work'];
    const fetchMock = vi.fn(async () => Response.json(branches));
    const client = createWebUiApiClient({ fetch: fetchMock });

    const result = await client.connections.listBranches('ado-main', 'Agent Controller', 'repo-1');
    expect(result).toEqual(branches);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/webui/connections/ado-main/repositories/repo-1/branches?project=Agent%20Controller',
      expect.objectContaining({}),
    );
  });

  it('connections client: onboards repository with project parameter', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) =>
      Response.json(repository, { status: 201, headers: { Location: '/repositories/onboarded' } }),
    );
    const client = createWebUiApiClient({ fetch: fetchMock });

    await client.connections.onboardRepository(
      'ado-main',
      'Agent Controller',
      'repo-1',
      'runtime-main',
    );
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/webui/connections/ado-main/repositories/onboard');
    expect(JSON.parse(init?.body as string)).toEqual({
      project: 'Agent Controller',
      repositoryId: 'repo-1',
      runtimeEnvironmentKey: 'runtime-main',
    });
  });
});
