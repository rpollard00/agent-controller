import type { RunCardCategory, RunCardItem } from '../../api/types';

export interface RunCardStoplight {
  readonly className: string;
  readonly ariaLabel: string;
}

const stoplights = {
  executing: { className: 'bg-green-500', ariaLabel: 'Executing' },
  pending: { className: 'bg-amber-400', ariaLabel: 'Pending' },
  attention: { className: 'bg-red-500', ariaLabel: 'Needs attention' },
  completed: { className: 'bg-gray-500', ariaLabel: 'Completed' },
} as const satisfies Record<RunCardCategory, RunCardStoplight>;

const runStatusLabels: Readonly<Record<string, string>> = {
  Queued: 'Queued',
  Claimed: 'Claimed',
  EnvironmentProvisioning: 'Environment provisioning',
  EnvironmentReady: 'Environment ready',
  RepositoryCloning: 'Repository cloning',
  RepositoryReady: 'Repository ready',
  ContextInjected: 'Context injected',
  AgentStarting: 'Agent starting',
  AgentRunning: 'Agent running',
  AwaitingResult: 'Awaiting result',
  ResultReceived: 'Result received',
  PrOpened: 'PR opened',
  BranchPushed: 'Branch pushed',
  NeedsHuman: 'Needs human',
  Completed: 'Completed',
  Failed: 'Failed',
  Cancelled: 'Cancelled',
  CleanupPending: 'Cleanup pending',
  CleanedUp: 'Cleaned up',
};

const minuteInMilliseconds = 60_000;
const hourInMilliseconds = 60 * minuteInMilliseconds;
const dayInMilliseconds = 24 * hourInMilliseconds;

export function getRunCardStoplight(category: RunCardCategory): RunCardStoplight {
  return stoplights[category];
}

export function getRunStatusLabel(status: string): string {
  const knownLabel = runStatusLabels[status];
  if (knownLabel !== undefined) return knownLabel;

  const words = status
    .replace(/([a-z\d])([A-Z])/g, '$1 $2')
    .replace(/([A-Z])([A-Z][a-z])/g, '$1 $2');

  return words.length === 0
    ? words
    : `${words[0].toUpperCase()}${words.slice(1).toLowerCase()}`;
}

/** User-facing lifecycle label that keeps assistance distinct from story revival. */
export function getRunCardStateLabel(
  card: Pick<
    RunCardItem,
    'kind' | 'status' | 'requestMode' | 'feedbackStatus' | 'cycleStatus'
  >,
): string {
  if (card.requestMode !== 'assistance') {
    return card.kind === 'rework-soak' ? 'Rework soak' : getRunStatusLabel(card.status);
  }

  if (card.kind === 'rework-soak') {
    switch (card.feedbackStatus) {
      case 'watching':
        return 'Assistance soaking';
      case 'soaked':
        return 'Assistance ready';
      case 'materialized':
        return card.cycleStatus === 'consumed'
          ? 'Assistance in progress'
          : 'Assistance story queued';
      case 'superseded':
        return 'Assistance superseded';
      default:
        return card.status;
    }
  }

  switch (card.status) {
    case 'NeedsHuman':
      return 'Assistance needs human';
    case 'Failed':
      return 'Assistance failed';
    case 'Cancelled':
      return 'Assistance cancelled';
    case 'BranchPushed':
    case 'Completed':
    case 'CleanupPending':
    case 'CleanedUp':
      return 'Assistance completed';
    default:
      return 'Assistance in progress';
  }
}

export function formatRelativeTime(
  isoTimestamp: string,
  nowInMilliseconds: number = Date.now(),
): string {
  const timestamp = Date.parse(isoTimestamp);
  if (!Number.isFinite(timestamp)) return '';

  const elapsed = Math.max(0, nowInMilliseconds - timestamp);
  if (elapsed < minuteInMilliseconds) return 'just now';
  if (elapsed < hourInMilliseconds) return `${Math.floor(elapsed / minuteInMilliseconds)}m ago`;
  if (elapsed < dayInMilliseconds) return `${Math.floor(elapsed / hourInMilliseconds)}h ago`;
  return `${Math.floor(elapsed / dayInMilliseconds)}d ago`;
}

export function isRunCardVisible(
  card: Pick<RunCardItem, 'category'>,
  showCompleted: boolean,
): boolean {
  return card.category !== 'completed' || showCompleted;
}
