<script lang="ts">
  import { onDestroy } from 'svelte';
  import { getErrorMessage, type WebUiApiClient } from '../../api/client';
  import type {
    PullRequestDiagnosticDetail,
    PullRequestDiagnosticSummary,
    PullRequestRequestMatch,
    PullRequestsDebugPageResponse,
  } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import DataTable from '../../components/ui/DataTable.svelte';
  import Pagination from '../../components/ui/Pagination.svelte';
  import PullRequestDiagnosticsDialog from './PullRequestDiagnosticsDialog.svelte';

  let { client, active = false }: { client: WebUiApiClient; active?: boolean } = $props();

  let environmentKey = $state('');
  let includeInactive = $state(false);
  let page = $state(1);
  let result = $state<PullRequestsDebugPageResponse>();
  let status = $state<'idle' | 'loading' | 'ready' | 'error'>('idle');
  let requestError = $state<unknown>();
  let activated = $state(false);
  let listController: AbortController | undefined;

  let selectedPullRequest = $state<PullRequestDiagnosticSummary>();
  let detail = $state<PullRequestDiagnosticDetail>();
  let detailStatus = $state<'idle' | 'loading' | 'ready' | 'error'>('idle');
  let detailError = $state<unknown>();
  let detailController: AbortController | undefined;

  const requestPresentation: Record<PullRequestRequestMatch, { label: string; className: string }> = {
    revival: { label: 'Revival', className: 'bg-violet-950 text-violet-300' },
    assistance: { label: 'Assistance', className: 'bg-cyan-950 text-cyan-300' },
    both: { label: 'Both', className: 'bg-blue-950 text-blue-300' },
    none: { label: 'None', className: 'bg-slate-800 text-slate-300' },
  };

  async function refresh(): Promise<void> {
    listController?.abort();
    const requestController = new AbortController();
    listController = requestController;
    status = 'loading';
    requestError = undefined;

    try {
      const loaded = await client.debug.pullRequests.list({
        sourceControlEnvironmentKey: environmentKey || undefined,
        includeInactive,
        page,
        pageSize: result?.pageSize ?? 50,
      }, requestController.signal);
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

  function closeDetail(): void {
    detailController?.abort();
    detailController = undefined;
    selectedPullRequest = undefined;
    detail = undefined;
    detailError = undefined;
    detailStatus = 'idle';
  }

  async function loadDetail(): Promise<void> {
    if (!selectedPullRequest) return;
    detailController?.abort();
    const requestController = new AbortController();
    detailController = requestController;
    detail = undefined;
    detailError = undefined;
    detailStatus = 'loading';

    try {
      const loaded = await client.debug.pullRequests.get(
        selectedPullRequest.sourceControlEnvironmentKey,
        selectedPullRequest.repositoryKey,
        selectedPullRequest.pullRequestId,
        requestController.signal,
      );
      if (requestController.signal.aborted) return;
      detail = loaded;
      detailStatus = 'ready';
    } catch (error) {
      if (requestController.signal.aborted) return;
      detailError = error;
      detailStatus = 'error';
    }
  }

  function openDetail(pullRequest: PullRequestDiagnosticSummary): void {
    selectedPullRequest = pullRequest;
    void loadDetail();
  }

  function filtersChanged(): void {
    page = 1;
    closeDetail();
    if (activated) void refresh();
  }

  function goToPage(nextPage: number): void {
    page = nextPage;
    closeDetail();
    void refresh();
  }

  $effect(() => {
    if (active && !activated) {
      activated = true;
      void refresh();
    }
  });

  onDestroy(() => {
    listController?.abort();
    detailController?.abort();
  });
</script>

<div class="space-y-5">
  <div class="flex flex-wrap items-end gap-4 rounded-xl border border-slate-800 bg-slate-900/40 p-4">
    <label class="grid min-w-64 gap-1.5 text-sm font-medium text-slate-200">
      Source control environment
      <select bind:value={environmentKey} onchange={filtersChanged} class="min-h-10 rounded-lg border border-slate-700 bg-slate-950 px-3 text-sm text-white">
        <option value="">All source control environments</option>
        {#each result?.sourceOptions ?? [] as option (option.key)}<option value={option.key}>{option.name} ({option.key})</option>{/each}
      </select>
    </label>

    <label class="inline-flex min-h-10 cursor-pointer items-center gap-2 text-sm font-medium text-slate-300">
      <input type="checkbox" bind:checked={includeInactive} onchange={filtersChanged} class="size-4 rounded border-slate-600 bg-slate-950 accent-cyan-400" />
      Show inactive PRs
    </label>

    <Button variant="secondary" disabled={status === 'loading'} onclick={() => void refresh()}>Refresh</Button>
    {#if result?.observedAt}<span class="pb-2 text-xs text-slate-400">Last refreshed {new Date(result.observedAt).toLocaleString()}</span>{/if}
  </div>

  {#if status === 'loading' && !result}
    <div class="flex min-h-32 items-center justify-center gap-3 text-sm text-slate-300" role="status">
      <span class="size-4 animate-spin rounded-full border-2 border-slate-700 border-t-cyan-300" aria-hidden="true"></span>
      Loading pull requests…
    </div>
  {:else if status === 'error'}
    <div class="space-y-4">
      <Alert variant="error" title="Could not load pull requests" message={getErrorMessage(requestError)} />
      <Button variant="secondary" onclick={() => void refresh()}>Try again</Button>
    </div>
  {:else if result}
    {#if status === 'loading'}<p class="text-xs text-slate-400" role="status">Refreshing pull requests…</p>{/if}
    <p class="text-sm text-slate-300">{result.total} pull {result.total === 1 ? 'request' : 'requests'} found.</p>

    {#if result.failures.length > 0}
      <div class="rounded-lg border border-amber-700/40 bg-amber-950/30 px-4 py-3 text-sm text-amber-100" role="alert">
        <p class="font-semibold">Some repositories could not be inspected.</p>
        <ul class="mt-1 space-y-1">
          {#each result.failures as failure (`${failure.sourceControlEnvironmentKey}:${failure.repositoryKey}`)}
            <li><span class="font-medium">{failure.sourceControlEnvironmentKey} · {failure.repositoryKey}:</span> {failure.message}</li>
          {/each}
        </ul>
      </div>
    {/if}

    {#if result.items.length === 0}
      <p class="rounded-xl border border-slate-800 px-4 py-10 text-center text-sm text-slate-400">No pull requests match the current filters.</p>
    {:else}
      <DataTable caption="Pull request pickup diagnostics">
        <thead class="bg-slate-950/60 text-xs tracking-wide text-slate-400 uppercase"><tr>
          <th scope="col" class="px-4 py-3 font-semibold">Pull request</th>
          <th scope="col" class="px-4 py-3 font-semibold">Repository</th>
          <th scope="col" class="px-4 py-3 font-semibold">Status</th>
          <th scope="col" class="px-4 py-3 font-semibold">Request</th>
          <th scope="col" class="px-4 py-3 text-right font-semibold">Diagnostics</th>
        </tr></thead>
        <tbody class="divide-y divide-slate-800 bg-slate-900/30">
          {#each result.items as pullRequest (`${pullRequest.sourceControlEnvironmentKey}:${pullRequest.repositoryKey}:${pullRequest.pullRequestId}`)}
            <tr class="align-top">
              <td class="px-4 py-4"><p class="max-w-md font-medium text-white">{pullRequest.title}</p><p class="mt-1 text-xs text-slate-400">PR #{pullRequest.pullRequestId}</p></td>
              <td class="px-4 py-4 text-slate-300">{pullRequest.repositoryKey}</td>
              <td class="px-4 py-4 text-slate-300">{pullRequest.status || 'None'}</td>
              <td class="px-4 py-4"><span class={`inline-flex rounded-full px-2.5 py-1 text-xs font-semibold ${requestPresentation[pullRequest.request].className}`}>{requestPresentation[pullRequest.request].label}</span></td>
              <td class="px-4 py-4 text-right"><Button variant="ghost" ariaLabel={`View diagnostics for PR #${pullRequest.pullRequestId}`} onclick={() => openDetail(pullRequest)}>View</Button></td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
    {/if}

    <Pagination page={result.page} pageSize={result.pageSize} total={result.total} label="Pull requests pagination" onprevious={() => goToPage(Math.max(1, page - 1))} onnext={() => goToPage(page + 1)} />
  {/if}
</div>

<PullRequestDiagnosticsDialog
  pullRequest={selectedPullRequest}
  {detail}
  loading={detailStatus === 'loading'}
  error={detailStatus === 'error' ? getErrorMessage(detailError) : undefined}
  onclose={closeDetail}
  onretry={() => void loadDetail()}
/>
