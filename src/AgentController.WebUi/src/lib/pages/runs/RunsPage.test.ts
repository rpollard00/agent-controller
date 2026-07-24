import { fireEvent, render, screen } from '@testing-library/svelte';
import { tick } from 'svelte';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { WebUiApiClient } from '../../api/client';
import type { RunCardItem } from '../../api/types';
import RunsPage from './RunsPage.svelte';

const baseCard: RunCardItem = {
  id: 'run-1',
  kind: 'run',
  status: 'AgentRunning',
  category: 'executing',
  workItemTitle: 'Active dashboard work',
  workItemUrl: null,
  workItemSource: 'AzureDevOpsBoards',
  repoKey: 'agent-controller',
  repositoryUrl: null,
  runtimeType: 'PiMateria',
  runAttempt: 1,
  lastEventType: 'runtime.progress',
  lastEventMessage: 'Building the dashboard',
  lastEventAt: '2026-07-24T01:00:00Z',
  createdAt: '2026-07-24T00:00:00Z',
  updatedAt: '2026-07-24T01:00:00Z',
};

function card(overrides: Partial<RunCardItem> = {}): RunCardItem {
  return { ...baseCard, ...overrides };
}

function createClient(
  list: (signal?: AbortSignal) => Promise<RunCardItem[]>,
): WebUiApiClient {
  return { runs: { list } } as WebUiApiClient;
}

afterEach(() => {
  vi.useRealTimers();
});

describe('RunsPage', () => {
  it('hides completed cards by default and reveals them with the toggle', async () => {
    const client = createClient(async () => [
      card(),
      card({
        id: 'run-2',
        status: 'Completed',
        category: 'completed',
        workItemTitle: 'Completed dashboard work',
      }),
    ]);

    render(RunsPage, { client });

    expect(await screen.findByText('Active dashboard work')).toBeVisible();
    expect(screen.queryByText('Completed dashboard work')).not.toBeInTheDocument();

    await fireEvent.click(screen.getByRole('checkbox', { name: 'Show completed' }));

    expect(await screen.findByText('Completed dashboard work')).toBeVisible();
  });

  it('polls for run cards every 10 seconds', async () => {
    vi.useFakeTimers();
    const list = vi.fn(async () => []);
    const view = render(RunsPage, { client: createClient(list) });
    await tick();

    expect(list).toHaveBeenCalledOnce();

    await vi.advanceTimersByTimeAsync(9_999);
    expect(list).toHaveBeenCalledOnce();

    await vi.advanceTimersByTimeAsync(1);
    expect(list).toHaveBeenCalledTimes(2);

    view.unmount();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('aborts the in-flight request when unmounted', async () => {
    let signal: AbortSignal | undefined;
    const list = vi.fn((requestSignal?: AbortSignal) => {
      signal = requestSignal;
      return new Promise<RunCardItem[]>(() => undefined);
    });
    const view = render(RunsPage, { client: createClient(list) });
    await tick();

    expect(signal).toBeDefined();
    expect(signal?.aborted).toBe(false);

    view.unmount();

    expect(signal?.aborted).toBe(true);
  });

  it('aborts an in-flight request before refreshing', async () => {
    const signals: AbortSignal[] = [];
    const list = vi.fn((signal?: AbortSignal) => {
      if (signal) signals.push(signal);
      return new Promise<RunCardItem[]>(() => undefined);
    });
    render(RunsPage, { client: createClient(list) });
    await tick();

    await fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(list).toHaveBeenCalledTimes(2);
    expect(signals[0].aborted).toBe(true);
    expect(signals[1].aborted).toBe(false);
  });

  it('shows a loading state while the initial request is pending', () => {
    const client = createClient(() => new Promise<RunCardItem[]>(() => undefined));

    render(RunsPage, { client });

    expect(screen.getByRole('status')).toHaveTextContent('Loading runs…');
  });

  it('shows an error with a try-again action when loading fails', async () => {
    const client = createClient(async () => {
      throw new Error('Runs are unavailable.');
    });

    render(RunsPage, { client });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Could not load runs');
    expect(alert).toHaveTextContent('Runs are unavailable.');
    expect(screen.getByRole('button', { name: 'Try again' })).toBeVisible();
  });

  it('shows an empty state when there is no run activity', async () => {
    render(RunsPage, { client: createClient(async () => []) });

    expect(await screen.findByRole('heading', { name: 'No runs yet' })).toBeVisible();
    expect(
      screen.getByText(/Run activity will appear here when Agent Controller begins processing work/),
    ).toBeVisible();
  });
});
