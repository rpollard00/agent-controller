import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { RunCardCategory, RunCardItem } from '../../api/types';
import RunCard from './RunCard.svelte';

const baseCard: RunCardItem = {
  id: 'run-1',
  kind: 'run',
  status: 'AgentRunning',
  category: 'executing',
  workItemTitle: 'Add the runs dashboard',
  workItemUrl: 'https://work.example.test/items/42',
  workItemSource: 'AzureDevOpsBoards',
  repoKey: 'agent-controller',
  repositoryUrl: 'https://git.example.test/agent-controller',
  runtimeType: 'PiMateria',
  runAttempt: 1,
  lastEventType: 'runtime.progress',
  lastEventMessage: 'Implementing the run card',
  lastEventAt: new Date().toISOString(),
  createdAt: '2026-07-24T00:00:00Z',
  updatedAt: '2026-07-24T01:00:00Z',
};

function card(overrides: Partial<RunCardItem> = {}): RunCardItem {
  return { ...baseCard, ...overrides };
}

describe('RunCard', () => {
  it.each<[
    RunCardCategory,
    string,
    string,
  ]>([
    ['executing', 'Executing', 'bg-green-500'],
    ['pending', 'Pending', 'bg-amber-400'],
    ['attention', 'Needs attention', 'bg-red-500'],
    ['completed', 'Completed', 'bg-gray-500'],
  ])('renders the %s stoplight', (category, ariaLabel, className) => {
    render(RunCard, { card: card({ category }) });

    expect(screen.getByRole('img', { name: ariaLabel })).toHaveClass(className, 'rounded-full');
  });

  it('renders external links for the repository and work item', () => {
    render(RunCard, { card: card() });

    const repositoryLink = screen.getByRole('link', { name: 'agent-controller' });
    expect(repositoryLink).toHaveAttribute(
      'href',
      'https://git.example.test/agent-controller',
    );
    expect(repositoryLink).toHaveAttribute('target', '_blank');
    expect(repositoryLink).toHaveAttribute('rel', 'noopener noreferrer');

    const workItemLink = screen.getByRole('link', { name: 'Add the runs dashboard' });
    expect(workItemLink).toHaveAttribute('href', 'https://work.example.test/items/42');
    expect(workItemLink).toHaveAttribute('target', '_blank');
    expect(workItemLink).toHaveAttribute('rel', 'noopener noreferrer');
  });

  it('renders repository and work item values as plain text when URLs are unavailable', () => {
    render(RunCard, {
      card: card({ repositoryUrl: null, workItemUrl: null }),
    });

    expect(screen.getByText('agent-controller')).not.toBeInstanceOf(HTMLAnchorElement);
    expect(screen.getByText('Add the runs dashboard')).not.toBeInstanceOf(HTMLAnchorElement);
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('falls back to the latest event type when the event message is null', () => {
    render(RunCard, {
      card: card({ lastEventMessage: null, lastEventType: 'run.status.changed' }),
    });

    expect(screen.getByText('run.status.changed')).toBeVisible();
  });

  it('shows the run attempt badge only for retries', () => {
    const { rerender } = render(RunCard, { card: card({ runAttempt: 1 }) });

    expect(screen.queryByText('Attempt 1')).not.toBeInTheDocument();

    rerender({ card: card({ runAttempt: 3 }) });
    expect(screen.getByText('Attempt 3')).toBeVisible();
  });

  it('uses the synthesized rework soak state and thread-count message', () => {
    render(RunCard, {
      card: card({
        kind: 'rework-soak',
        status: 'Rework feedback soaking',
        category: 'pending',
        lastEventMessage: '3 feedback threads awaiting soak',
      }),
    });

    expect(screen.getByRole('heading', { name: 'Rework soak' })).toBeVisible();
    expect(screen.getByText('3 feedback threads awaiting soak')).toBeVisible();
  });
});
