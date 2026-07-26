import { render, screen, within } from '@testing-library/svelte';
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
  runtimeProfileName: 'ReeseProjecto LocalWorkspace',
  environmentProviderType: 'LocalWorkspace',
  runAttempt: 1,
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
  lastEventMessage: 'Implementing the run card',
  lastEventAt: new Date().toISOString(),
  createdAt: '2026-07-24T00:00:00Z',
  updatedAt: '2026-07-24T01:00:00Z',
};

function card(overrides: Partial<RunCardItem> = {}): RunCardItem {
  return { ...baseCard, ...overrides };
}

const assistancePullRequest = {
  environmentKey: 'ado-prod',
  repositoryKey: 'agent-controller',
  pullRequestId: '42',
  pullRequestUrl: 'https://dev.azure.test/agent-controller/pullrequest/42',
  sourceBranch: 'refs/heads/contributor/change',
  targetBranch: 'refs/heads/main',
  sourceCommitSha: 'abc123',
  canonicalKey: 'ado-prod:agent-controller:42',
  hasCanonicalIdentity: true,
} as const;

function assistanceCard(overrides: Partial<RunCardItem> = {}): RunCardItem {
  return card({
    requestMode: 'assistance',
    pullRequest: assistancePullRequest,
    feedbackStatus: 'watching',
    ...overrides,
  });
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

  it('stacks the environment provider below the runtime profile name', () => {
    render(RunCard, { card: card() });

    const environmentColumn = screen.getByText('Environment').parentElement;
    expect(environmentColumn).not.toBeNull();

    const profileName = within(environmentColumn!).getByText('ReeseProjecto LocalWorkspace');
    const providerType = within(environmentColumn!).getByText('LocalWorkspace');
    expect(profileName).toBeVisible();
    expect(providerType).toBeVisible();
    expect(providerType).toHaveClass('text-xs', 'text-slate-500');
    expect(profileName.compareDocumentPosition(providerType)).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING,
    );
  });

  it('falls back when the runtime environment snapshot is unavailable', () => {
    render(RunCard, {
      card: card({ runtimeProfileName: null, environmentProviderType: null }),
    });

    expect(screen.getByText('Unknown environment')).toBeVisible();
    expect(screen.queryByText('LocalWorkspace')).not.toBeInTheDocument();
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

  it('preserves the revival rework soak presentation', () => {
    render(RunCard, {
      card: card({
        kind: 'rework-soak',
        status: 'Rework feedback soaking',
        category: 'pending',
        requestMode: 'revival',
        pullRequest: assistancePullRequest,
        feedbackStatus: 'watching',
        lastEventMessage: '3 feedback threads awaiting soak',
      }),
    });

    expect(screen.getByRole('heading', { name: 'Rework soak' })).toBeVisible();
    expect(screen.getByText('Revival rework')).toBeVisible();
    expect(screen.getByText('3 feedback threads awaiting soak')).toBeVisible();
    expect(screen.getByText('Work item')).toBeVisible();
    expect(screen.queryByText('PR & story')).not.toBeInTheDocument();
  });

  it.each<[
    RunCardItem['feedbackStatus'],
    RunCardItem['cycleStatus'],
    string,
  ]>([
    ['watching', null, 'Assistance soaking'],
    ['soaked', null, 'Assistance ready'],
    ['materialized', 'pending', 'Assistance story queued'],
  ])(
    'labels assistance tracking state %s/%s as %s',
    (feedbackStatus, cycleStatus, expectedLabel) => {
      render(RunCard, {
        card: assistanceCard({
          kind: 'rework-soak',
          status: expectedLabel,
          category: 'pending',
          feedbackStatus,
          cycleStatus,
        }),
      });

      expect(screen.getByRole('heading', { name: expectedLabel })).toBeVisible();
      expect(screen.getByText('PR assistance')).toBeVisible();
    },
  );

  it('links an assistance soak directly to its pull request before a story exists', () => {
    render(RunCard, {
      card: assistanceCard({
        kind: 'rework-soak',
        status: 'Assistance feedback soaking',
        category: 'pending',
      }),
    });

    const pullRequestLink = screen.getByRole('link', { name: 'PR #42' });
    expect(pullRequestLink).toHaveAttribute(
      'href',
      'https://dev.azure.test/agent-controller/pullrequest/42',
    );
    expect(pullRequestLink).toHaveAttribute('target', '_blank');
    expect(pullRequestLink).toHaveAttribute('rel', 'noopener noreferrer');
    expect(screen.getByText('Story not created yet')).toBeVisible();
  });

  it('links a queued assistance cycle to both the pull request and generated story', () => {
    render(RunCard, {
      card: assistanceCard({
        kind: 'rework-soak',
        status: 'Assistance story queued',
        category: 'pending',
        feedbackStatus: 'materialized',
        cycleStatus: 'pending',
        cycleNumber: 2,
        assistanceStoryWorkItemId: 'story-local-8042',
        assistanceStoryExternalId: '8042',
        assistanceStoryUrl: 'https://dev.azure.test/workitems/8042',
        workItemTitle: 'Assist PR 42',
        workItemUrl: 'https://dev.azure.test/workitems/8042',
      }),
    });

    expect(screen.getByRole('heading', { name: 'Assistance story queued' })).toBeVisible();
    expect(screen.getByText('Cycle 2')).toBeVisible();
    expect(screen.getByRole('link', { name: 'PR #42' })).toHaveAttribute(
      'href',
      assistancePullRequest.pullRequestUrl,
    );
    const storyLink = screen.getByRole('link', { name: 'Story #8042' });
    expect(storyLink).toHaveAttribute('href', 'https://dev.azure.test/workitems/8042');
    expect(storyLink).toHaveAttribute('target', '_blank');
    expect(storyLink).toHaveAttribute('rel', 'noopener noreferrer');
  });

  it('labels a consuming assistance run as in progress', () => {
    render(RunCard, {
      card: assistanceCard({
        status: 'AgentRunning',
        category: 'executing',
        feedbackStatus: 'materialized',
        cycleStatus: 'consumed',
        cycleNumber: 2,
        assistanceStoryWorkItemId: 'story-local-8042',
        assistanceStoryExternalId: '8042',
        assistanceStoryUrl: 'https://dev.azure.test/workitems/8042',
      }),
    });

    expect(screen.getByRole('heading', { name: 'Assistance in progress' })).toBeVisible();
    expect(screen.getByText('Agent running')).toBeVisible();
    expect(screen.getByText('PR assistance')).toBeVisible();
    expect(screen.getByRole('link', { name: 'PR #42' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Story #8042' })).toBeVisible();
  });

  it.each<[
    string,
    RunCardCategory,
    string,
  ]>([
    ['BranchPushed', 'completed', 'Assistance completed'],
    ['Completed', 'completed', 'Assistance completed'],
    ['Failed', 'attention', 'Assistance failed'],
    ['NeedsHuman', 'attention', 'Assistance needs human'],
    ['Cancelled', 'attention', 'Assistance cancelled'],
  ])('labels the %s assistance outcome in %s as %s', (status, category, expectedLabel) => {
    render(RunCard, {
      card: assistanceCard({
        status,
        category,
        feedbackStatus: 'materialized',
        cycleStatus: 'consumed',
      }),
    });

    expect(screen.getByRole('heading', { name: expectedLabel })).toBeVisible();
    expect(screen.getByText('PR assistance')).toBeVisible();
  });
});
