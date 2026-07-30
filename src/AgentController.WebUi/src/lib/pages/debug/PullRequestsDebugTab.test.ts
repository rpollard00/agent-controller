import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import type { PullRequestsDebugClient, WebUiApiClient } from '../../api/client';
import type { PullRequestDiagnosticDetail, PullRequestDiagnosticSummary, PullRequestsDebugPageResponse } from '../../api/types';
import PullRequestsDebugTab from './PullRequestsDebugTab.svelte';

const observedAt = '2026-07-30T03:40:43Z';
const pullRequest: PullRequestDiagnosticSummary = {
  pullRequestId: '73', title: 'Repair feedback pickup', url: 'https://repos.test/pullrequest/73',
  sourceControlEnvironmentKey: 'ado-east', repositoryKey: 'agent-controller', status: 'active', request: 'both',
};
const detail: PullRequestDiagnosticDetail = {
  ...pullRequest,
  sourceBranch: 'refs/heads/fix', targetBranch: 'refs/heads/main',
  labels: ['agent-rework-requested', 'agent-assistance-requested'],
  linkedWorkItems: [{ workItemId: '42', workItemUrl: 'https://boards.test/42' }],
  outcome: 'assistanceTakesPrecedence', eligible: true,
  checks: [
    { code: 'active', label: 'Active pull request', passed: true, reason: 'The pull request is active.' },
    { code: 'lineage', label: 'Originating run', passed: false, reason: 'No originating run was found.' },
  ],
  recognizedRevivalLabel: 'agent-rework-requested', recognizedAssistanceLabel: 'agent-assistance-requested',
  feedbackTrace: {
    pullRequestId: '73', markerStatus: 'present', reviewerAllowlistConfigured: true,
    totalThreadCount: 4, activeThreadCount: 3, allowlistedReviewerThreadCount: 2,
    nonEmptyContentThreadCount: 2, qualifyingThreadCount: 1, isAccepted: true,
  },
  tracking: null,
};

function response(overrides: Partial<PullRequestsDebugPageResponse> = {}): PullRequestsDebugPageResponse {
  return { sourceOptions: [{ key: 'ado-east', name: 'Azure East' }], items: [pullRequest], failures: [], page: 1, pageSize: 50, total: 1, observedAt, ...overrides };
}
function apiClient(
  list: PullRequestsDebugClient['list'] = vi.fn(async () => response()),
  get: PullRequestsDebugClient['get'] = vi.fn(async () => detail),
): WebUiApiClient {
  return { debug: { boardItems: { list: vi.fn(), get: vi.fn() }, pullRequests: { list, get } } } as unknown as WebUiApiClient;
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((pass, fail) => { resolve = pass; reject = fail; });
  return { promise, resolve, reject };
}

describe('PullRequestsDebugTab', () => {
  it('renders filters, rows, request state, repository failures, and an empty state', async () => {
    const list = vi.fn()
      .mockResolvedValueOnce(response({ failures: [{ sourceControlEnvironmentKey: 'ado-west', repositoryKey: 'web', message: 'Connection failed.' }] }))
      .mockResolvedValueOnce(response({ items: [], total: 0 }));
    render(PullRequestsDebugTab, { client: apiClient(list), active: true });

    expect(screen.getByLabelText('Source control environment')).toHaveValue('');
    expect(await screen.findByRole('option', { name: 'Azure East (ado-east)' })).toBeVisible();
    expect(screen.getByLabelText('Show inactive PRs')).not.toBeChecked();
    expect(screen.getByRole('columnheader', { name: 'Pull request' })).toBeVisible();
    expect(screen.getByText('Repair feedback pickup')).toBeVisible();
    expect(screen.getByText('PR #73')).toBeVisible();
    expect(screen.getByText('Both')).toBeVisible();
    expect(screen.queryByText('agent-rework-requested')).not.toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('ado-west · web: Connection failed.');

    await fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText('No pull requests match the current filters.')).toBeVisible();
  });

  it('filters inactive pull requests, resets and pages independently', async () => {
    const list = vi.fn(async (options: { page?: number; includeInactive?: boolean }) => response({
      page: options.page ?? 1,
      total: 120,
      items: options.includeInactive ? [{ ...pullRequest, status: 'completed' }] : [pullRequest],
    }));
    render(PullRequestsDebugTab, { client: apiClient(list), active: true });
    expect(await screen.findByText('1–50 of 120')).toBeVisible();
    await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 }), expect.any(AbortSignal)));
    await fireEvent.change(screen.getByLabelText('Source control environment'), { target: { value: 'ado-east' } });
    await waitFor(() => expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ sourceControlEnvironmentKey: 'ado-east', page: 1 }), expect.any(AbortSignal)));
    await fireEvent.click(screen.getByLabelText('Show inactive PRs'));
    await waitFor(() => expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ includeInactive: true, page: 1 }), expect.any(AbortSignal)));
    expect(await screen.findByText('completed')).toBeVisible();
    expect(screen.getByRole('navigation', { name: 'Pull requests pagination' })).toBeVisible();
  });

  it('lazy-loads accessible diagnostics with precedence, checks, values, labels, and links', async () => {
    const pending = deferred<PullRequestDiagnosticDetail>();
    const get = vi.fn(() => pending.promise);
    render(PullRequestsDebugTab, { client: apiClient(undefined, get), active: true });
    const trigger = await screen.findByRole('button', { name: 'View diagnostics for PR #73' });
    trigger.focus();
    await fireEvent.click(trigger);
    const dialog = screen.getByRole('dialog', { name: 'Repair feedback pickup' });
    expect(dialog).toHaveAccessibleDescription('PR #73 · agent-controller');
    expect(within(dialog).getByRole('status')).toHaveTextContent('Loading pull request diagnostics…');
    expect(get).toHaveBeenCalledWith('ado-east', 'agent-controller', '73', expect.any(AbortSignal));

    pending.resolve(detail);
    expect(await within(dialog).findByText('Assistance takes precedence')).toBeVisible();
    expect(within(dialog).getByText('Pass')).toBeVisible();
    expect(within(dialog).getByText('Fail')).toBeVisible();
    expect(within(dialog).getByText('4 total · 3 active')).toBeVisible();
    expect(within(dialog).getAllByText('agent-rework-requested')).toHaveLength(2);
    expect(within(dialog).getAllByText('agent-assistance-requested')).toHaveLength(2);
    expect(within(dialog).getByRole('link', { name: /Open pull request/ })).toHaveAttribute('href', pullRequest.url);
    expect(within(dialog).getByRole('link', { name: 'Work item #42' })).toHaveAttribute('href', 'https://boards.test/42');
    expect(within(dialog).getByText(/zero comments/)).toBeVisible();

    await fireEvent.click(within(dialog).getByRole('button', { name: 'Close dialog' }));
    expect(trigger).toHaveFocus();
  });

  it('retries detail errors and closes and aborts detail when filters change', async () => {
    const never = deferred<PullRequestDiagnosticDetail>();
    const get = vi.fn()
      .mockRejectedValueOnce(new Error('Could not inspect this pull request.'))
      .mockImplementationOnce((_environment, _repository, _id, signal: AbortSignal) => {
        signal.addEventListener('abort', () => never.reject(new DOMException('Aborted', 'AbortError')));
        return never.promise;
      });
    render(PullRequestsDebugTab, { client: apiClient(undefined, get), active: true });
    await fireEvent.click(await screen.findByRole('button', { name: 'View diagnostics for PR #73' }));
    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Could not inspect this pull request.');
    await fireEvent.click(within(dialog).getByRole('button', { name: 'Retry' }));
    const signal = get.mock.calls[1][3] as AbortSignal;
    await fireEvent.click(screen.getByLabelText('Show inactive PRs'));
    expect(signal.aborted).toBe(true);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
