import { describe, expect, it, vi } from 'vitest';
import { ApiError, createWebUiApiClient, getFieldErrors } from './client';
import type { RepositoryProfile } from './types';

const repository: RepositoryProfile = {
  key: 'web.repo',
  cloneUrl: 'https://example.test/repo.git',
  defaultBranch: 'main',
  transport: 'httpsPat',
  environmentProfile: '',
  runtimeProfile: '',
  azureDevOpsEnvironmentKey: null,
  runtimeEnvironmentKey: null,
  allowedPaths: ['src'],
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
});
