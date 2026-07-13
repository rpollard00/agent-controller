import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { beforeEach, describe, expect, it } from 'vitest';
import App from './App.svelte';
import { ApiError, type ResourceClient, type WebUiApiClient } from './lib/api/client';
import type {
  AzureDevOpsEnvironmentProfile,
  RepositoryProfile,
  RuntimeEnvironmentProfile,
} from './lib/api/types';

function resourceClient<T>(list: (signal?: AbortSignal) => Promise<T[]>): ResourceClient<T> {
  const notImplemented = async (): Promise<never> => {
    throw new Error('Not implemented in this component test.');
  };

  return {
    list,
    get: notImplemented,
    create: notImplemented,
    update: notImplemented,
    delete: async () => undefined,
  };
}

function createClient(
  repositoryList: (signal?: AbortSignal) => Promise<RepositoryProfile[]> = async () => [],
): WebUiApiClient {
  return {
    repositories: resourceClient(repositoryList),
    azureDevOpsEnvironments: resourceClient<AzureDevOpsEnvironmentProfile>(async () => []),
    runtimeEnvironments: resourceClient<RuntimeEnvironmentProfile>(async () => []),
  };
}

describe('App shell', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
  });

  it('renders semantic landmarks and responsive navigation', async () => {
    render(App, { client: createClient() });

    expect(screen.getByRole('banner')).toBeInTheDocument();
    expect(screen.getByRole('main')).toBeInTheDocument();
    expect(screen.getByRole('contentinfo')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1, name: 'Agent Controller' })).toBeVisible();

    const primaryNavigation = screen.getByRole('navigation', { name: 'Primary navigation' });
    expect(within(primaryNavigation).getByRole('link', { name: 'Overview' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(
      within(primaryNavigation).getByRole('link', { name: 'Azure DevOps Environments' }),
    ).toBeInTheDocument();
    expect(
      within(primaryNavigation).getByRole('link', { name: 'Runtime Environments' }),
    ).toBeInTheDocument();

    await fireEvent.click(screen.getByRole('button', { name: 'Open navigation' }));
    const mobileNavigation = screen.getByRole('navigation', { name: 'Mobile navigation' });
    expect(within(mobileNavigation).getByRole('link', { name: 'Repositories' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Close navigation' })).toHaveAttribute(
      'aria-expanded',
      'true',
    );
  });

  it('routes same-origin navigation without a document reload', async () => {
    render(App, { client: createClient() });

    const primaryNavigation = screen.getByRole('navigation', { name: 'Primary navigation' });
    await fireEvent.click(within(primaryNavigation).getByRole('link', { name: 'Repositories' }));

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Repositories' }),
    ).toBeVisible();
    expect(await screen.findByRole('heading', { name: 'No repositories yet' })).toBeVisible();
    expect(window.location.pathname).toBe('/repositories');
    expect(
      within(primaryNavigation).getByRole('link', { name: 'Repositories' }),
    ).toHaveAttribute('aria-current', 'page');
  });

  it('renders ProblemDetails and field validation messages from API failures', async () => {
    window.history.replaceState({}, '', '/repositories');
    const apiError = new ApiError({
      title: 'Validation failed.',
      status: 400,
      detail: 'The repository query could not be completed.',
      errors: {
        cloneUrl: ['Clone URL must be an absolute URL.'],
      },
    });
    const client = createClient(async () => Promise.reject(apiError));

    render(App, { client });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Could not load repositories');
    expect(alert).toHaveTextContent('The repository query could not be completed.');
    expect(alert).toHaveTextContent('cloneUrl: Clone URL must be an absolute URL.');
    expect(screen.getByRole('button', { name: 'Try again' })).toBeVisible();
  });

  it('shows a not-found route with a way back to the overview', () => {
    window.history.replaceState({}, '', '/not-a-route');

    render(App, { client: createClient() });

    expect(screen.getByRole('heading', { level: 1, name: 'Page not found' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Return to overview' })).toHaveAttribute('href', '/');
  });
});
