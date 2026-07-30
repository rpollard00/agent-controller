<script lang="ts">
  import { onDestroy } from 'svelte';
  import { getErrorMessage, type WebUiApiClient } from '../../api/client';
  import type { PullRequestsDebugPageResponse } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';

  let { client, active = false }: { client: WebUiApiClient; active?: boolean } = $props();

  let environmentKey = $state('');
  let includeInactive = $state(false);
  let page = $state(1);
  let result = $state<PullRequestsDebugPageResponse>();
  let status = $state<'idle' | 'loading' | 'ready' | 'error'>('idle');
  let requestError = $state<unknown>();
  let activated = $state(false);
  let controller: AbortController | undefined;

  async function refresh(): Promise<void> {
    controller?.abort();
    const requestController = new AbortController();
    controller = requestController;
    status = 'loading';
    requestError = undefined;

    try {
      const loaded = await client.debug.pullRequests.list(
        {
          sourceControlEnvironmentKey: environmentKey || undefined,
          includeInactive,
          page,
          pageSize: result?.pageSize ?? 50,
        },
        requestController.signal,
      );
      if (requestController.signal.aborted) return;
      result = loaded;
      page = loaded.page;
      status = 'ready';
    } catch (error) {
      if (requestController.signal.aborted) return;
      requestError = error;
      status = 'error';
    }
  }

  function filtersChanged(): void {
    page = 1;
    if (activated) void refresh();
  }

  $effect(() => {
    if (active && !activated) {
      activated = true;
      void refresh();
    }
  });

  onDestroy(() => controller?.abort());
</script>

<div class="space-y-5">
  <div class="flex flex-wrap items-end gap-4 rounded-xl border border-slate-800 bg-slate-900/40 p-4">
    <label class="grid min-w-64 gap-1.5 text-sm font-medium text-slate-200">
      Source control environment
      <select
        bind:value={environmentKey}
        onchange={filtersChanged}
        class="min-h-10 rounded-lg border border-slate-700 bg-slate-950 px-3 text-sm text-white"
      >
        <option value="">All source control environments</option>
        {#each result?.sourceOptions ?? [] as option (option.key)}
          <option value={option.key}>{option.name} ({option.key})</option>
        {/each}
      </select>
    </label>

    <label class="inline-flex min-h-10 cursor-pointer items-center gap-2 text-sm font-medium text-slate-300">
      <input
        type="checkbox"
        bind:checked={includeInactive}
        onchange={filtersChanged}
        class="size-4 rounded border-slate-600 bg-slate-950 accent-cyan-400"
      />
      Show inactive PRs
    </label>

    <Button variant="secondary" onclick={() => void refresh()}>Refresh</Button>
    {#if result?.observedAt}
      <span class="pb-2 text-xs text-slate-400">Last refreshed {new Date(result.observedAt).toLocaleString()}</span>
    {/if}
  </div>

  {#if status === 'loading'}
    <div class="flex min-h-32 items-center justify-center gap-3 text-sm text-slate-300" role="status">
      <span class="size-4 animate-spin rounded-full border-2 border-slate-700 border-t-cyan-300" aria-hidden="true"></span>
      Loading pull requests…
    </div>
  {:else if status === 'error'}
    <div class="space-y-4">
      <Alert variant="error" title="Could not load pull requests" message={getErrorMessage(requestError)} />
      <Button variant="secondary" onclick={() => void refresh()}>Try again</Button>
    </div>
  {:else if status === 'ready'}
    <p class="text-sm text-slate-300">{result?.total ?? 0} pull {(result?.total ?? 0) === 1 ? 'request' : 'requests'} found.</p>
  {/if}
</div>
