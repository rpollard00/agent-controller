import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import type { WebUiApiClient } from '../../api/client';
import type { BoardItemsDebugPageResponse, PullRequestsDebugPageResponse } from '../../api/types';
import DebugPage from './DebugPage.svelte';

const observedAt = '2026-07-30T03:40:43Z';

function boardResponse(total = 1): BoardItemsDebugPageResponse {
  return {
    sourceOptions: [{ key: 'boards-east', displayName: 'Boards East' }],
    items: [], failures: [], page: 1, pageSize: 50, total, observedAt,
  };
}

function pullRequestResponse(total = 2): PullRequestsDebugPageResponse {
  return {
    sourceOptions: [{ key: 'repos-west', name: 'Repos West' }],
    items: [], failures: [], page: 1, pageSize: 50, total, observedAt,
  };
}

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

function client(
  listBoardItems = vi.fn(async () => boardResponse()),
  listPullRequests = vi.fn(async () => pullRequestResponse()),
): WebUiApiClient {
  return {
    debug: {
      boardItems: { list: listBoardItems, get: vi.fn() },
      pullRequests: { list: listPullRequests, get: vi.fn() },
    },
  } as unknown as WebUiApiClient;
}

describe('DebugPage', () => {
  it('renders the minimal heading and loads each tab only on its first activation', async () => {
    const listBoardItems = vi.fn(async () => boardResponse());
    const listPullRequests = vi.fn(async () => pullRequestResponse());
    render(DebugPage, { client: client(listBoardItems, listPullRequests) });

    expect(screen.getByRole('heading', { level: 1, name: 'Debug' })).toBeVisible();
    expect(screen.getByText('Inspect work items and pull requests visible to the controller.')).toBeVisible();
    expect(await screen.findByText('1 board item found.')).toBeVisible();
    expect(listBoardItems).toHaveBeenCalledTimes(1);
    expect(listPullRequests).not.toHaveBeenCalled();

    await fireEvent.click(screen.getByRole('tab', { name: 'Pull requests' }));
    expect(await screen.findByText('2 pull requests found.')).toBeVisible();
    expect(listPullRequests).toHaveBeenCalledTimes(1);

    await fireEvent.click(screen.getByRole('tab', { name: 'Board items' }));
    await fireEvent.click(screen.getByRole('tab', { name: 'Pull requests' }));
    expect(listBoardItems).toHaveBeenCalledTimes(1);
    expect(listPullRequests).toHaveBeenCalledTimes(1);
  });

  it('shows accessible loading states when each tab is first activated', async () => {
    const pendingBoardItems = deferred<BoardItemsDebugPageResponse>();
    const pendingPullRequests = deferred<PullRequestsDebugPageResponse>();
    const listBoardItems = vi.fn(() => pendingBoardItems.promise);
    const listPullRequests = vi.fn(() => pendingPullRequests.promise);
    render(DebugPage, { client: client(listBoardItems, listPullRequests) });

    expect(await screen.findByRole('status')).toHaveTextContent('Loading board items…');
    expect(listPullRequests).not.toHaveBeenCalled();

    pendingBoardItems.resolve(boardResponse());
    expect(await screen.findByText('1 board item found.')).toBeVisible();

    await fireEvent.click(screen.getByRole('tab', { name: 'Pull requests' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Loading pull requests…');

    pendingPullRequests.resolve(pullRequestResponse());
    expect(await screen.findByText('2 pull requests found.')).toBeVisible();
  });

  it('supports keyboard tab navigation', async () => {
    render(DebugPage, { client: client() });
    const boardTab = screen.getByRole('tab', { name: 'Board items' });
    boardTab.focus();

    await fireEvent.keyDown(boardTab, { key: 'ArrowRight' });
    const pullRequestTab = screen.getByRole('tab', { name: 'Pull requests' });
    await waitFor(() => expect(pullRequestTab).toHaveFocus());
    expect(pullRequestTab).toHaveAttribute('aria-selected', 'true');

    await fireEvent.keyDown(pullRequestTab, { key: 'Home' });
    await waitFor(() => expect(boardTab).toHaveFocus());
    expect(boardTab).toHaveAttribute('aria-selected', 'true');
  });

  it('retains each tab filter and results when switching', async () => {
    const listBoardItems = vi.fn(async () => boardResponse(3));
    const listPullRequests = vi.fn(async () => pullRequestResponse(4));
    render(DebugPage, { client: client(listBoardItems, listPullRequests) });

    const boardPanel = await screen.findByRole('tabpanel', { name: 'Board items' });
    await screen.findByText('3 board items found.');
    await fireEvent.change(within(boardPanel).getByLabelText('Work source environment'), {
      target: { value: 'boards-east' },
    });
    await waitFor(() => expect(listBoardItems).toHaveBeenLastCalledWith(
      expect.objectContaining({ workSourceEnvironmentKey: 'boards-east', page: 1 }),
      expect.any(AbortSignal),
    ));

    await fireEvent.click(screen.getByRole('tab', { name: 'Pull requests' }));
    await screen.findByText('4 pull requests found.');
    await fireEvent.click(screen.getByRole('tab', { name: 'Board items' }));

    expect(within(boardPanel).getByLabelText('Work source environment')).toHaveValue('boards-east');
    expect(within(boardPanel).getByText('3 board items found.')).toBeVisible();
    expect(within(boardPanel).getByText(/Last refreshed/)).toBeVisible();
  });

  it('shows a useful failure and can retry', async () => {
    const listBoardItems = vi
      .fn()
      .mockRejectedValueOnce(new Error('Boards East could not be reached.'))
      .mockResolvedValueOnce(boardResponse(0));
    render(DebugPage, { client: client(listBoardItems) });

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Could not load board items');
    expect(alert).toHaveTextContent('Boards East could not be reached.');
    await fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('0 board items found.')).toBeVisible();
    expect(listBoardItems).toHaveBeenCalledTimes(2);
  });
});
