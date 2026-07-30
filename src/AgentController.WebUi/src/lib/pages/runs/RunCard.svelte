<script lang="ts">
  import type { RunCardItem } from '../../api/types';
  import {
    formatLocalDateTime,
    formatRelativeTime,
    formatSoakRemaining,
    getRunCardStateLabel,
    getRunCardStoplight,
    getRunStatusLabel,
    getSoakEligibilityDeadline,
  } from './runCardModel';

  let { card }: { card: RunCardItem } = $props();

  const stoplight = $derived(getRunCardStoplight(card.category));
  const stateLabel = $derived(getRunCardStateLabel(card));
  const isAssistance = $derived(card.requestMode === 'assistance');
  const lastEvent = $derived(
    card.lastEventMessage ?? card.lastEventType ?? 'No lifecycle events recorded',
  );
  const relativeEventTime = $derived(
    card.lastEventAt === null ? '' : formatRelativeTime(card.lastEventAt),
  );
  let clock = $state(Date.now());
  const soakDeadline = $derived(getSoakEligibilityDeadline(card));
  const soakAbsoluteTime = $derived(
    soakDeadline === null ? '' : formatLocalDateTime(soakDeadline),
  );
  const soakRemaining = $derived(
    soakDeadline === null ? '' : formatSoakRemaining(soakDeadline, clock),
  );

  $effect(() => {
    if (soakDeadline === null) return;

    clock = Date.now();
    const timer = setInterval(() => {
      clock = Date.now();
    }, 1_000);

    return () => clearInterval(timer);
  });
  const repositoryLabel = $derived(card.repoKey ?? 'Unknown repository');
  const workItemLabel = $derived(card.workItemTitle ?? 'Unknown work item');
  const runtimeLabel = $derived(card.runtimeType ?? 'Unknown runtime');
  const environmentLabel = $derived(card.runtimeProfileName ?? 'Unknown environment');
  const pullRequestLabel = $derived(
    card.pullRequest?.pullRequestId
      ? `PR #${card.pullRequest.pullRequestId}`
      : 'Pull request',
  );
  const assistanceStoryReference = $derived(
    card.assistanceStoryExternalId ?? card.assistanceStoryWorkItemId,
  );
  const assistanceStoryLabel = $derived(
    assistanceStoryReference ? `Story #${assistanceStoryReference}` : 'Assistance story',
  );
  const assistanceStoryUrl = $derived(
    card.assistanceStoryUrl ??
      (card.assistanceStoryWorkItemId !== null ? card.workItemUrl : null),
  );
</script>

<article
  class="w-full rounded-2xl border border-slate-800 bg-slate-900/70 px-5 py-4 shadow-lg shadow-black/10"
>
  <div class="flex min-w-0 items-start gap-4">
    <span
      class="mt-1.5 size-3 shrink-0 rounded-full {stoplight.className}"
      role="img"
      aria-label={stoplight.ariaLabel}
    ></span>

    <div class="grid min-w-0 flex-1 gap-4 sm:grid-cols-[minmax(0,1.6fr)_minmax(8rem,0.7fr)_minmax(0,1fr)_minmax(0,1fr)_minmax(0,1.4fr)] sm:items-center">
      <div class="min-w-0">
        <div class="flex flex-wrap items-center gap-2">
          <h2 class="font-semibold text-white">{stateLabel}</h2>
          {#if card.requestMode}
            <span
              class="inline-flex rounded-full bg-cyan-950 px-2.5 py-0.5 text-xs font-semibold text-cyan-200"
            >
              {isAssistance ? 'PR assistance' : 'Revival rework'}
            </span>
          {/if}
          {#if isAssistance && card.kind === 'run'}
            <span
              class="inline-flex rounded-full bg-slate-800 px-2.5 py-0.5 text-xs font-semibold text-slate-300"
            >
              {getRunStatusLabel(card.status)}
            </span>
          {/if}
          {#if isAssistance && card.cycleNumber !== null}
            <span
              class="inline-flex rounded-full bg-slate-800 px-2.5 py-0.5 text-xs font-semibold text-slate-300"
            >
              Cycle {card.cycleNumber}
            </span>
          {/if}
          {#if card.runAttempt > 1}
            <span
              class="inline-flex rounded-full bg-slate-800 px-2.5 py-0.5 text-xs font-semibold text-slate-300"
            >
              Attempt {card.runAttempt}
            </span>
          {/if}
        </div>
        <div class="mt-1 flex min-w-0 items-baseline gap-2 text-sm text-slate-400">
          <p class="truncate" title={lastEvent}>{lastEvent}</p>
          {#if card.lastEventAt && relativeEventTime}
            <time class="shrink-0 text-xs text-slate-500" datetime={card.lastEventAt}>
              {relativeEventTime}
            </time>
          {/if}
        </div>
        {#if soakDeadline !== null && soakRemaining}
          <p class="mt-1 text-xs font-medium text-amber-200" aria-live="polite">
            {#if soakRemaining === 'Eligible now'}
              Eligible now
            {:else}
              Eligible after <time datetime={card.soakEligibleAt ?? undefined}>{soakAbsoluteTime}</time>
              <span class="text-amber-300/80">· {soakRemaining}</span>
            {/if}
          </p>
        {/if}
      </div>

      <div class="min-w-0">
        <p class="text-xs font-medium tracking-wide text-slate-500 uppercase">Runtime</p>
        <p class="mt-1 truncate text-sm text-slate-300" title={runtimeLabel}>{runtimeLabel}</p>
      </div>

      <div class="min-w-0">
        <p class="text-xs font-medium tracking-wide text-slate-500 uppercase">Environment</p>
        <p class="mt-1 truncate text-sm text-slate-300" title={environmentLabel}>
          {environmentLabel}
        </p>
        {#if card.environmentProviderType}
          <p class="truncate text-xs text-slate-500" title={card.environmentProviderType}>
            {card.environmentProviderType}
          </p>
        {/if}
      </div>

      <div class="min-w-0">
        <p class="text-xs font-medium tracking-wide text-slate-500 uppercase">Repository</p>
        {#if card.repositoryUrl}
          <a
            class="mt-1 block truncate text-sm font-medium text-cyan-300 hover:text-cyan-200 hover:underline"
            href={card.repositoryUrl}
            target="_blank"
            rel="noopener noreferrer"
            title={repositoryLabel}
          >
            {repositoryLabel}
          </a>
        {:else}
          <p class="mt-1 truncate text-sm text-slate-300" title={repositoryLabel}>
            {repositoryLabel}
          </p>
        {/if}
      </div>

      <div class="min-w-0">
        {#if isAssistance}
          <p class="text-xs font-medium tracking-wide text-slate-500 uppercase">PR &amp; story</p>
          <div class="mt-1 flex min-w-0 flex-wrap gap-x-3 gap-y-1">
            {#if card.pullRequest?.pullRequestUrl}
              <a
                class="truncate text-sm font-medium text-cyan-300 hover:text-cyan-200 hover:underline"
                href={card.pullRequest.pullRequestUrl}
                target="_blank"
                rel="noopener noreferrer"
              >
                {pullRequestLabel}
              </a>
            {:else}
              <span class="truncate text-sm text-slate-300">{pullRequestLabel}</span>
            {/if}

            {#if assistanceStoryUrl}
              <a
                class="truncate text-sm font-medium text-cyan-300 hover:text-cyan-200 hover:underline"
                href={assistanceStoryUrl}
                target="_blank"
                rel="noopener noreferrer"
                title={workItemLabel}
              >
                {assistanceStoryLabel}
              </a>
            {:else if assistanceStoryReference}
              <span class="truncate text-sm text-slate-300">{assistanceStoryLabel}</span>
            {:else}
              <span class="truncate text-sm text-slate-500">Story not created yet</span>
            {/if}
          </div>
        {:else}
          <p class="text-xs font-medium tracking-wide text-slate-500 uppercase">Work item</p>
          {#if card.workItemUrl}
            <a
              class="mt-1 block truncate text-sm font-medium text-cyan-300 hover:text-cyan-200 hover:underline"
              href={card.workItemUrl}
              target="_blank"
              rel="noopener noreferrer"
              title={workItemLabel}
            >
              {workItemLabel}
            </a>
          {:else}
            <p class="mt-1 truncate text-sm text-slate-300" title={workItemLabel}>
              {workItemLabel}
            </p>
          {/if}
        {/if}
      </div>
    </div>
  </div>
</article>
