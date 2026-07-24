<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { getErrorMessage, type WebUiApiClient, webUiApi } from '../../api/client';
  import type { RunCardItem } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import RunCard from './RunCard.svelte';
  import { isRunCardVisible } from './runCardModel';

  let { client = webUiApi }: { client?: WebUiApiClient } = $props();

  let status = $state<'loading' | 'empty' | 'ready' | 'error'>('loading');
  let cards = $state<RunCardItem[]>([]);
  let showCompleted = $state(false);
  let requestError = $state<unknown>();
  let controller: AbortController | undefined;
  let pollInterval: ReturnType<typeof setInterval> | undefined;

  const visibleCards = $derived(
    cards.filter((card) => isRunCardVisible(card, showCompleted)),
  );

  async function refresh(): Promise<void> {
    controller?.abort();
    const requestController = new AbortController();
    controller = requestController;
    status = 'loading';
    requestError = undefined;

    try {
      const loadedCards = await client.runs.list(requestController.signal);
      if (requestController.signal.aborted) return;

      cards = loadedCards;
      status = loadedCards.length === 0 ? 'empty' : 'ready';
    } catch (error) {
      if (requestController.signal.aborted) return;

      requestError = error;
      status = 'error';
    }
  }

  onMount(() => {
    void refresh();
    pollInterval = setInterval(() => void refresh(), 10_000);
  });

  onDestroy(() => {
    if (pollInterval !== undefined) clearInterval(pollInterval);
    controller?.abort();
  });
</script>

<div class="space-y-8">
  <div class="max-w-3xl">
    <p class="text-sm font-semibold tracking-widest text-cyan-300 uppercase">Execution</p>
    <h1 class="mt-2 text-3xl font-semibold tracking-tight text-white sm:text-4xl">Runs</h1>
    <p class="mt-3 text-base leading-7 text-slate-300">
      Monitor executing, pending, and completed controller work in one place.
    </p>
  </div>

  <Card title="Run activity" description="Latest controller activity, refreshed every 10 seconds.">
    {#snippet actions()}
      <div class="flex flex-wrap items-center justify-end gap-4">
        <label class="inline-flex min-h-10 cursor-pointer items-center gap-2 text-sm font-medium text-slate-300">
          <input
            type="checkbox"
            bind:checked={showCompleted}
            class="size-4 rounded border-slate-600 bg-slate-950 text-cyan-400 accent-cyan-400"
          />
          Show completed
        </label>
        <Button variant="secondary" onclick={() => void refresh()}>Refresh</Button>
      </div>
    {/snippet}

    {#if status === 'loading'}
      <div class="flex min-h-40 items-center justify-center gap-3 text-sm text-slate-300" role="status">
        <span
          class="size-4 animate-spin rounded-full border-2 border-slate-700 border-t-cyan-300"
          aria-hidden="true"
        ></span>
        Loading runs…
      </div>
    {:else if status === 'error'}
      <div class="space-y-4">
        <Alert
          variant="error"
          title="Could not load runs"
          message={getErrorMessage(requestError)}
        />
        <Button variant="secondary" onclick={() => void refresh()}>Try again</Button>
      </div>
    {:else if status === 'empty'}
      <div class="rounded-xl border border-dashed border-slate-700 px-5 py-12 text-center">
        <h2 class="font-semibold text-white">No runs yet</h2>
        <p class="mx-auto mt-2 max-w-lg text-sm leading-6 text-slate-400">
          Run activity will appear here when Agent Controller begins processing work.
        </p>
      </div>
    {:else if visibleCards.length === 0}
      <div class="rounded-xl border border-dashed border-slate-700 px-5 py-12 text-center">
        <h2 class="font-semibold text-white">No runs match this filter</h2>
        <p class="mx-auto mt-2 max-w-lg text-sm leading-6 text-slate-400">
          Completed work is hidden. Turn on Show completed to include it.
        </p>
      </div>
    {:else}
      <div class="space-y-3">
        {#each visibleCards as card (`${card.kind}:${card.id}`)}
          <RunCard {card} />
        {/each}
      </div>
    {/if}
  </Card>
</div>
