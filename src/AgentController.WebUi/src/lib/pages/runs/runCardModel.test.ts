import { describe, expect, it } from 'vitest';
import type { RunCardCategory, RunCardItem } from '../../api/types';
import {
  formatLocalDateTime,
  formatRelativeTime,
  formatSoakRemaining,
  getRunCardStoplight,
  getRunStatusLabel,
  getSoakEligibilityDeadline,
  isRunCardVisible,
} from './runCardModel';

describe('getRunCardStoplight', () => {
  it.each<[
    RunCardCategory,
    { className: string; ariaLabel: string },
  ]>([
    ['executing', { className: 'bg-green-500', ariaLabel: 'Executing' }],
    ['pending', { className: 'bg-amber-400', ariaLabel: 'Pending' }],
    ['attention', { className: 'bg-red-500', ariaLabel: 'Needs attention' }],
    ['completed', { className: 'bg-gray-500', ariaLabel: 'Completed' }],
  ])('maps %s to its stoplight presentation', (category, expected) => {
    expect(getRunCardStoplight(category)).toEqual(expected);
  });
});

describe('getRunStatusLabel', () => {
  it.each([
    ['Queued', 'Queued'],
    ['Claimed', 'Claimed'],
    ['EnvironmentProvisioning', 'Environment provisioning'],
    ['EnvironmentReady', 'Environment ready'],
    ['RepositoryCloning', 'Repository cloning'],
    ['RepositoryReady', 'Repository ready'],
    ['ContextInjected', 'Context injected'],
    ['AgentStarting', 'Agent starting'],
    ['AgentRunning', 'Agent running'],
    ['AwaitingResult', 'Awaiting result'],
    ['ResultReceived', 'Result received'],
    ['PrOpened', 'PR opened'],
    ['BranchPushed', 'Branch pushed'],
    ['NeedsHuman', 'Needs human'],
    ['Completed', 'Completed'],
    ['Failed', 'Failed'],
    ['Cancelled', 'Cancelled'],
    ['CleanupPending', 'Cleanup pending'],
    ['CleanedUp', 'Cleaned up'],
  ])('formats %s as %s', (status, expected) => {
    expect(getRunStatusLabel(status)).toBe(expected);
  });

  it('provides a readable fallback for a future lifecycle status', () => {
    expect(getRunStatusLabel('WaitingForReviewer')).toBe('Waiting for reviewer');
  });
});

describe('formatRelativeTime', () => {
  const now = Date.parse('2026-07-24T12:00:00.000Z');

  it.each([
    ['2026-07-24T12:00:00.000Z', 'just now'],
    ['2026-07-24T11:59:01.000Z', 'just now'],
    ['2026-07-24T11:59:00.000Z', '1m ago'],
    ['2026-07-24T11:58:00.000Z', '2m ago'],
    ['2026-07-24T11:00:01.000Z', '59m ago'],
    ['2026-07-24T11:00:00.000Z', '1h ago'],
    ['2026-07-24T09:00:00.000Z', '3h ago'],
    ['2026-07-23T12:00:01.000Z', '23h ago'],
    ['2026-07-23T12:00:00.000Z', '1d ago'],
    ['2026-07-22T12:00:00.000Z', '2d ago'],
  ])('formats %s as %s', (timestamp, expected) => {
    expect(formatRelativeTime(timestamp, now)).toBe(expected);
  });

  it('treats a future timestamp as just now', () => {
    expect(formatRelativeTime('2026-07-24T12:00:01.000Z', now)).toBe('just now');
  });

  it('returns an empty label for an invalid timestamp', () => {
    expect(formatRelativeTime('not-a-timestamp', now)).toBe('');
  });
});

describe('soak eligibility timing', () => {
  const deadline = '2026-07-24T12:01:01.000Z';
  const watchingCard = {
    kind: 'rework-soak',
    requestMode: 'revival',
    feedbackStatus: 'watching',
    soakEligibleAt: deadline,
  } satisfies Pick<
    RunCardItem,
    'kind' | 'requestMode' | 'feedbackStatus' | 'soakEligibleAt'
  >;

  it('formats an absolute local time and deterministic live duration', () => {
    const deadlineMilliseconds = Date.parse(deadline);

    expect(formatLocalDateTime(deadlineMilliseconds)).toBe(
      new Date(deadlineMilliseconds).toLocaleString(),
    );
    expect(
      formatSoakRemaining(deadlineMilliseconds, Date.parse('2026-07-24T12:00:00.000Z')),
    ).toBe('1m 1s remaining');
    expect(formatSoakRemaining(deadlineMilliseconds, deadlineMilliseconds)).toBe(
      'Eligible now',
    );
  });

  it('rejects invalid formatter values', () => {
    expect(formatLocalDateTime(Number.NaN)).toBe('');
    expect(formatSoakRemaining(Number.NaN, 0)).toBe('');
  });

  it.each([
    [{ ...watchingCard, requestMode: 'assistance' as const }, true],
    [{ ...watchingCard, requestMode: null }, false],
    [{ ...watchingCard, kind: 'run' as const }, false],
    [{ ...watchingCard, feedbackStatus: 'soaked' as const }, false],
    [{ ...watchingCard, feedbackStatus: 'materialized' as const }, false],
    [{ ...watchingCard, feedbackStatus: 'superseded' as const }, false],
    [{ ...watchingCard, soakEligibleAt: null }, false],
    [{ ...watchingCard, soakEligibleAt: 'invalid' }, false],
  ])('selects only valid watching rework soak cards %#', (candidate, expected) => {
    expect(getSoakEligibilityDeadline(candidate) !== null).toBe(expected);
  });
});

describe('isRunCardVisible', () => {
  it.each<[
    RunCardCategory,
    boolean,
    boolean,
  ]>([
    ['executing', false, true],
    ['pending', false, true],
    ['attention', false, true],
    ['completed', false, false],
    ['completed', true, true],
  ])(
    'returns %s for a %s card when showCompleted is %s',
    (category, showCompleted, expected) => {
      expect(isRunCardVisible({ category }, showCompleted)).toBe(expected);
    },
  );
});
