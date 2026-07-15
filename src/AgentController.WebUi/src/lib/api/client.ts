import type {
  ProblemDetails,
  RepositoryProfile,
  RuntimeEnvironmentProfile,
  WorkSourceEnvironmentProfile,
} from './types';

type FetchImplementation = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

export interface ResourceClient<T> {
  list(signal?: AbortSignal): Promise<T[]>;
  get(key: string, signal?: AbortSignal): Promise<T>;
  create(profile: T, signal?: AbortSignal): Promise<T>;
  update(key: string, profile: T, signal?: AbortSignal): Promise<T>;
  delete(key: string, signal?: AbortSignal): Promise<void>;
}

export interface WorkSourceEnvironmentResourceClient extends ResourceClient<WorkSourceEnvironmentProfile> {
  getBoardStates(key: string, signal?: AbortSignal): Promise<Record<string, string[]>>;
}

export interface WebUiApiClient {
  repositories: ResourceClient<RepositoryProfile>;
  workSourceEnvironments: WorkSourceEnvironmentResourceClient;
  runtimeEnvironments: ResourceClient<RuntimeEnvironmentProfile>;
}

export interface ApiClientOptions {
  /** Defaults to the same-origin Web UI API. */
  baseUrl?: string;
  fetch?: FetchImplementation;
}

export class ApiError extends Error {
  readonly problem: ProblemDetails;

  constructor(problem: ProblemDetails) {
    super(problem.detail || problem.title);
    this.name = 'ApiError';
    this.problem = problem;
  }

  get status(): number {
    return this.problem.status;
  }

  get fieldErrors(): Readonly<Record<string, string[]>> {
    return this.problem.errors ?? {};
  }
}

export function getErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return error.problem.detail || error.problem.title;
  }

  if (error instanceof Error && error.message) {
    return error.message;
  }

  return 'An unexpected error occurred. Try again.';
}

export function getFieldErrors(error: unknown): Readonly<Record<string, string[]>> {
  return error instanceof ApiError ? error.fieldErrors : {};
}

export function createWebUiApiClient(options: ApiClientOptions = {}): WebUiApiClient {
  const baseUrl = (options.baseUrl ?? '/api/webui').replace(/\/$/, '');
  const fetchImplementation: FetchImplementation =
    options.fetch ?? ((input, init) => globalThis.fetch(input, init));

  async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const headers = new Headers(init.headers);
    headers.set('Accept', 'application/json, application/problem+json');

    let response: Response;
    try {
      response = await fetchImplementation(`${baseUrl}${path}`, { ...init, headers });
    } catch (error) {
      if (isAbortError(error)) {
        throw error;
      }

      throw new ApiError({
        title: 'Unable to reach Agent Controller.',
        status: 0,
        detail: 'Check the API connection and try again.',
      });
    }

    if (response.status === 204) {
      return undefined as T;
    }

    const body = await readResponseBody(response);
    if (!response.ok) {
      throw new ApiError(toProblemDetails(body, response));
    }

    return body as T;
  }

  function resource<T>(path: string): ResourceClient<T> {
    const profilePath = (key: string) => `${path}/${encodeURIComponent(key)}`;
    const write = (method: 'POST' | 'PUT', profile: T, signal?: AbortSignal): RequestInit => ({
      method,
      signal,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(profile),
    });

    return {
      list: (signal) => request<T[]>(path, { signal }),
      get: (key, signal) => request<T>(profilePath(key), { signal }),
      create: (profile, signal) => request<T>(path, write('POST', profile, signal)),
      update: (key, profile, signal) =>
        request<T>(profilePath(key), write('PUT', profile, signal)),
      delete: (key, signal) => request<void>(profilePath(key), { method: 'DELETE', signal }),
    };
  }

  return {
    repositories: resource<RepositoryProfile>('/repositories'),
    workSourceEnvironments: {
      ...resource<WorkSourceEnvironmentProfile>('/work-source-environments'),
      getBoardStates: (key, signal) =>
        request<Record<string, string[]>>(
          `/work-source-environments/${encodeURIComponent(key)}/board-states`,
          { signal },
        ),
    },
    runtimeEnvironments: resource<RuntimeEnvironmentProfile>('/runtime-environments'),
  };
}

async function readResponseBody(response: Response): Promise<unknown> {
  const contentType = response.headers.get('content-type') ?? '';
  if (contentType.includes('json')) {
    try {
      return await response.json();
    } catch {
      return undefined;
    }
  }

  const text = await response.text();
  return text || undefined;
}

function toProblemDetails(body: unknown, response: Response): ProblemDetails {
  if (!isRecord(body)) {
    return {
      title: response.statusText || 'Request failed.',
      status: response.status,
      detail: typeof body === 'string' ? body : undefined,
    };
  }

  return {
    type: stringValue(body.type),
    title: stringValue(body.title) ?? response.statusText ?? 'Request failed.',
    status: numberValue(body.status) ?? response.status,
    detail: stringValue(body.detail),
    instance: stringValue(body.instance),
    errors: validationErrors(body.errors),
  };
}

function validationErrors(value: unknown): Record<string, string[]> | undefined {
  if (!isRecord(value)) {
    return undefined;
  }

  const entries = Object.entries(value)
    .map(([key, messages]) => [
      key,
      Array.isArray(messages)
        ? messages.filter((message): message is string => typeof message === 'string')
        : [],
    ] as const)
    .filter(([, messages]) => messages.length > 0);

  return entries.length > 0 ? Object.fromEntries(entries) : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function stringValue(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}

function numberValue(value: unknown): number | undefined {
  return typeof value === 'number' ? value : undefined;
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

export const webUiApi = createWebUiApiClient();
