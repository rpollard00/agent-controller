import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import type { BoardItemsDebugClient, WebUiApiClient } from '../../api/client';
import type {
  BoardItemDiagnosticDetail,
  BoardItemDiagnosticSummary,
  BoardItemsDebugPageResponse,
} from '../../api/types';
import BoardItemsDebugTab from './BoardItemsDebugTab.svelte';

const observedAt = '2026-07-30T03:40:43Z';

const item: BoardItemDiagnosticSummary = {
  id: '42',
  title: 'Repair pickup diagnostics',
  url: 'https://boards.example.test/items/42',
  project: 'Controller',
  workSourceEnvironmentKey: 'boards-east',
  repositoryKey: 'agent-controller',
  state: 'Active',
  match: 'eligible',
};

const detail: BoardItemDiagnosticDetail = {
  ...item,
  tags: ['repo:agent-controller', 'agent-ready-rework'],
  eligible: true,
  checks: [
    { code: 'source', label: 'Work source enabled', passed: true, reason: 'The source is enabled.' },
    { code: 'terminal', label: 'Nonterminal state', passed: false, reason: 'The current state is excluded.' },
  ],
  recognizedRepositoryTags: ['repo:agent-controller', 'repo:ui'],
  recognizedReadyTag: 'agent-ready',
  recognizedReadyReworkTag: 'agent-ready-rework',
};

function response(overrides: Partial<BoardItemsDebugPageResponse> = {}): BoardItemsDebugPageResponse {
  return {
    sourceOptions: [{ key: 'boards-east', displayName: 'Boards East' }],
    items: [item],
    failures: [],
    page: 1,
    pageSize: 50,
    total: 1,
    observedAt,
    ...overrides,
  };
}

function apiClient(
  list: BoardItemsDebugClient['list'] = vi.fn(async () => response()),
  get: BoardItemsDebugClient['get'] = vi.fn(async () => detail),
): WebUiApiClient {
  return {
    debug: {
      boardItems: { list, get },
      pullRequests: { list: vi.fn(), get: vi.fn() },
    },
  } as unknown as WebUiApiClient;
}

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void; reject: (error: unknown) => void } {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((pass, fail) => {
    resolve = pass;
    reject = fail;
  });
  return { promise, resolve, reject };
}

describe('BoardItemsDebugTab', () => {
  it('renders the operator toolbar, table, match badges, failures, and empty state', async () => {
    const list = vi
      .fn()
      .mockResolvedValueOnce(response({
        failures: [{ workSourceEnvironmentKey: 'boards-west', project: 'Web', message: 'Connection failed.' }],
      }))
      .mockResolvedValueOnce(response({ items: [], total: 0 }));
    render(BoardItemsDebugTab, { client: apiClient(list), active: true });

    expect(screen.getByLabelText('Work source environment')).toHaveValue('');
    expect(await screen.findByRole('option', { name: 'Boards East (boards-east)' })).toBeVisible();
    expect(screen.getByLabelText('Show terminal items')).not.toBeChecked();
    expect(screen.getByRole('columnheader', { name: 'Work item' })).toBeVisible();
    expect(screen.getByText('Repair pickup diagnostics')).toBeVisible();
    expect(screen.getByText('Work item #42')).toBeVisible();
    expect(screen.getByText('agent-controller')).toBeVisible();
    expect(screen.getByText('Eligible')).toBeVisible();
    expect(screen.queryByText('repo:agent-controller')).not.toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('boards-west · Web: Connection failed.');
    expect(screen.getByText(/Last refreshed/)).toBeVisible();

    await fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText('No board items match the current filters.')).toBeVisible();
    expect(screen.getByText('0 board items found.')).toBeVisible();
  });

  it('keeps results visible and reports progress while refreshing', async () => {
    const refreshedAt = '2026-07-30T04:40:43Z';
    const pending = deferred<BoardItemsDebugPageResponse>();
    const list = vi
      .fn()
      .mockResolvedValueOnce(response())
      .mockImplementationOnce(() => pending.promise);
    render(BoardItemsDebugTab, { client: apiClient(list), active: true });

    expect(await screen.findByText('Repair pickup diagnostics')).toBeVisible();
    expect(screen.getByText(`Last refreshed ${new Date(observedAt).toLocaleString()}`)).toBeVisible();

    await fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Refreshing board items…');
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeDisabled();
    expect(screen.getByText('Repair pickup diagnostics')).toBeVisible();
    expect(screen.getByText(`Last refreshed ${new Date(observedAt).toLocaleString()}`)).toBeVisible();

    pending.resolve(response({ observedAt: refreshedAt }));

    expect(await screen.findByText(`Last refreshed ${new Date(refreshedAt).toLocaleString()}`)).toBeVisible();
    expect(screen.queryByText('Refreshing board items…')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeEnabled();
  });

  it('applies filters, resets paging, and pages with accessible controls', async () => {
    const list = vi.fn(async (options: { page?: number }) => response({ page: options.page ?? 1, total: 120 }));
    render(BoardItemsDebugTab, { client: apiClient(list), active: true });
    expect(await screen.findByText('1–50 of 120')).toBeVisible();

    await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(list).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 2 }),
      expect.any(AbortSignal),
    ));

    await fireEvent.change(screen.getByLabelText('Work source environment'), {
      target: { value: 'boards-east' },
    });
    await waitFor(() => expect(list).toHaveBeenLastCalledWith(
      expect.objectContaining({ workSourceEnvironmentKey: 'boards-east', page: 1 }),
      expect.any(AbortSignal),
    ));

    await fireEvent.click(screen.getByLabelText('Show terminal items'));
    await waitFor(() => expect(list).toHaveBeenLastCalledWith(
      expect.objectContaining({ includeTerminal: true, page: 1 }),
      expect.any(AbortSignal),
    ));
    expect(screen.getByRole('navigation', { name: 'Board items pagination' })).toBeVisible();
  });

  it('lazy-loads and renders accessible detail diagnostics, then restores focus on close', async () => {
    const pending = deferred<BoardItemDiagnosticDetail>();
    const get = vi.fn(() => pending.promise);
    render(BoardItemsDebugTab, { client: apiClient(undefined, get), active: true });
    const trigger = await screen.findByRole('button', { name: 'View diagnostics for work item #42' });

    trigger.focus();
    await fireEvent.click(trigger);
    const dialog = screen.getByRole('dialog', { name: 'Repair pickup diagnostics' });
    expect(dialog).toHaveAccessibleDescription('Work item #42 · Controller');
    expect(within(dialog).getByRole('status')).toHaveTextContent('Loading work item diagnostics…');
    expect(get).toHaveBeenCalledWith('boards-east', '42', expect.any(AbortSignal));

    pending.resolve(detail);
    expect(await within(dialog).findByText('Current values')).toBeVisible();
    expect(within(dialog).getByText('Eligible')).toBeVisible();
    expect(within(dialog).getByText('Pass')).toBeVisible();
    expect(within(dialog).getByText('Fail')).toBeVisible();
    expect(within(dialog).getAllByText('repo:agent-controller')).toHaveLength(2);
    expect(within(dialog).getByText('agent-ready')).toBeVisible();
    expect(within(dialog).getAllByText('agent-ready-rework').length).toBeGreaterThan(0);
    expect(within(dialog).getByText(/requires a pending Assistance cycle/)).toBeVisible();
    expect(within(dialog).getByRole('link', { name: /Open work item/ })).toHaveAttribute('href', item.url);

    await fireEvent.click(within(dialog).getByRole('button', { name: 'Close dialog' }));
    expect(screen.queryByRole('dialog', { name: 'Repair pickup diagnostics' })).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });

  it('shows retryable detail errors and aborts detail when a filter changes', async () => {
    const never = deferred<BoardItemDiagnosticDetail>();
    const get = vi
      .fn()
      .mockRejectedValueOnce(new Error('Could not inspect this item.'))
      .mockImplementationOnce((_environment, _id, signal: AbortSignal) => {
        signal.addEventListener('abort', () => never.reject(new DOMException('Aborted', 'AbortError')));
        return never.promise;
      });
    render(BoardItemsDebugTab, { client: apiClient(undefined, get), active: true });

    await fireEvent.click(await screen.findByRole('button', { name: 'View diagnostics for work item #42' }));
    const dialog = await screen.findByRole('dialog', { name: 'Repair pickup diagnostics' });
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Could not inspect this item.');
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Retry' }));
    expect(within(dialog).getByRole('status')).toHaveTextContent('Loading work item diagnostics…');

    const detailSignal = get.mock.calls[1][2] as AbortSignal;
    await fireEvent.click(screen.getByLabelText('Show terminal items'));
    expect(detailSignal.aborted).toBe(true);
    expect(screen.queryByRole('dialog', { name: 'Repair pickup diagnostics' })).not.toBeInTheDocument();
  });
});
