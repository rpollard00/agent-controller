<script lang="ts">
  import type { BoardItemDiagnosticDetail, BoardItemDiagnosticSummary } from '../../api/types';
  import Dialog from '../../components/ui/Dialog.svelte';

  let {
    item,
    detail,
    loading,
    error,
    onclose,
    onretry,
  }: {
    item: BoardItemDiagnosticSummary | undefined;
    detail: BoardItemDiagnosticDetail | undefined;
    loading: boolean;
    error?: string;
    onclose: () => void;
    onretry: () => void;
  } = $props();

  const chipClass = 'inline-flex max-w-full break-all rounded-full bg-slate-800 px-2.5 py-1 text-xs font-medium text-slate-200';
</script>

<Dialog
  open={item !== undefined}
  title={item?.title ?? 'Work item diagnostics'}
  contextLine={item ? `Work item #${item.id} · ${item.project}` : undefined}
  variant="wide"
  {loading}
  loadingLabel="Loading work item diagnostics…"
  {error}
  {onretry}
  externalLink={item?.url ? { href: item.url, label: 'Open work item' } : undefined}
  {onclose}
>
  {#if detail}
    <div class="space-y-8">
      <div>
        <span
          class={`inline-flex rounded-full px-3 py-1 text-sm font-semibold ${
            detail.eligible
              ? 'bg-emerald-950 text-emerald-300'
              : 'bg-rose-950 text-rose-300'
          }`}
        >{detail.eligible ? 'Eligible' : 'Not eligible'}</span>
      </div>

      <section aria-labelledby="board-diagnostic-checks">
        <h3 id="board-diagnostic-checks" class="text-base font-semibold text-white">Checks</h3>
        <ul class="mt-3 divide-y divide-slate-800 rounded-xl border border-slate-800">
          {#each detail.checks as check (check.code)}
            <li class="grid gap-2 px-4 py-3 sm:grid-cols-[auto_1fr] sm:gap-3">
              <span
                class={`inline-flex h-fit w-fit rounded-full px-2 py-0.5 text-xs font-semibold ${
                  check.passed
                    ? 'bg-emerald-950 text-emerald-300'
                    : 'bg-rose-950 text-rose-300'
                }`}
              >{check.passed ? 'Pass' : 'Fail'}</span>
              <div>
                <p class="font-medium text-slate-100">{check.label}</p>
                <p class="mt-0.5 text-sm leading-6 text-slate-400">{check.reason}</p>
              </div>
            </li>
          {/each}
        </ul>
      </section>

      <section aria-labelledby="board-current-values">
        <h3 id="board-current-values" class="text-base font-semibold text-white">Current values</h3>
        <dl class="mt-3 grid gap-4 rounded-xl border border-slate-800 bg-slate-950/30 p-4 sm:grid-cols-2">
          <div>
            <dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">State</dt>
            <dd class="mt-1 text-sm text-slate-100">{detail.state || 'None'}</dd>
          </div>
          <div>
            <dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Environment</dt>
            <dd class="mt-1 text-sm text-slate-100">{detail.workSourceEnvironmentKey}</dd>
          </div>
          <div>
            <dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Project</dt>
            <dd class="mt-1 text-sm text-slate-100">{detail.project}</dd>
          </div>
          <div>
            <dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Repository</dt>
            <dd class="mt-1 text-sm text-slate-100">{detail.repositoryKey || 'None'}</dd>
          </div>
          <div class="sm:col-span-2">
            <dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Tags</dt>
            <dd class="mt-2 flex flex-wrap gap-2">
              {#if detail.tags.length === 0}
                <span class="text-sm text-slate-400">None</span>
              {:else}
                {#each detail.tags as tag (tag)}
                  <span class={chipClass}>{tag}</span>
                {/each}
              {/if}
            </dd>
          </div>
        </dl>
      </section>

      <section aria-labelledby="board-recognized-tags">
        <h3 id="board-recognized-tags" class="text-base font-semibold text-white">Recognized tags</h3>
        <div class="mt-3 grid gap-4 sm:grid-cols-3">
          <div class="rounded-xl border border-slate-800 p-4">
            <h4 class="text-sm font-semibold text-slate-200">Repository</h4>
            <div class="mt-3 flex flex-wrap gap-2">
              {#if detail.recognizedRepositoryTags.length === 0}
                <span class="text-sm text-slate-400">None configured</span>
              {:else}
                {#each detail.recognizedRepositoryTags as tag (tag)}
                  <span class={chipClass}>{tag}</span>
                {/each}
              {/if}
            </div>
          </div>
          <div class="rounded-xl border border-slate-800 p-4">
            <h4 class="text-sm font-semibold text-slate-200">New work</h4>
            <div class="mt-3"><span class={chipClass}>{detail.recognizedReadyTag}</span></div>
          </div>
          <div class="rounded-xl border border-slate-800 p-4">
            <h4 class="text-sm font-semibold text-slate-200">Assistance</h4>
            <div class="mt-3"><span class={chipClass}>{detail.recognizedReadyReworkTag}</span></div>
            <p class="mt-3 text-xs leading-5 text-slate-400">
              The ready-rework tag also requires a pending Assistance cycle.
            </p>
          </div>
        </div>
      </section>
    </div>
  {/if}
</Dialog>
