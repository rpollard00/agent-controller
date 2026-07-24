<script lang="ts">
  import type { RunCardItem } from '../../api/types';
  import { formatRelativeTime, getRunCardStoplight, getRunStatusLabel } from './runCardModel';

  let { card }: { card: RunCardItem } = $props();

  const stoplight = $derived(getRunCardStoplight(card.category));
  const stateLabel = $derived(
    card.kind === 'rework-soak' ? 'Rework soak' : getRunStatusLabel(card.status),
  );
  const lastEvent = $derived(
    card.lastEventMessage ?? card.lastEventType ?? 'No lifecycle events recorded',
  );
  const relativeEventTime = $derived(
    card.lastEventAt === null ? '' : formatRelativeTime(card.lastEventAt),
  );
  const repositoryLabel = $derived(card.repoKey ?? 'Unknown repository');
  const workItemLabel = $derived(card.workItemTitle ?? 'Unknown work item');
  const runtimeLabel = $derived(card.runtimeType ?? 'Unknown runtime');
  const environmentLabel = $derived(card.runtimeProfileName ?? 'Unknown environment');
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
      </div>
    </div>
  </div>
</article>
