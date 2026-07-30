<script lang="ts">
  import type { PullRequestDiagnosticDetail, PullRequestDiagnosticSummary } from '../../api/types';
  import Dialog from '../../components/ui/Dialog.svelte';

  let {
    pullRequest,
    detail,
    loading,
    error,
    onclose,
    onretry,
  }: {
    pullRequest: PullRequestDiagnosticSummary | undefined;
    detail: PullRequestDiagnosticDetail | undefined;
    loading: boolean;
    error?: string;
    onclose: () => void;
    onretry: () => void;
  } = $props();

  const chipClass = 'inline-flex max-w-full break-all rounded-full bg-slate-800 px-2.5 py-1 text-xs font-medium text-slate-200';

  function outcomeLabel(value: PullRequestDiagnosticDetail): string {
    if (value.outcome === 'assistanceTakesPrecedence') return 'Assistance takes precedence';
    return value.eligible ? 'Eligible' : 'Not eligible';
  }
</script>

<Dialog
  open={pullRequest !== undefined}
  title={pullRequest?.title ?? 'Pull request diagnostics'}
  contextLine={pullRequest ? `PR #${pullRequest.pullRequestId} · ${pullRequest.repositoryKey}` : undefined}
  variant="wide"
  {loading}
  loadingLabel="Loading pull request diagnostics…"
  {error}
  {onretry}
  externalLink={pullRequest?.url ? { href: pullRequest.url, label: 'Open pull request' } : undefined}
  {onclose}
>
  {#if detail}
    <div class="space-y-8">
      <div>
        <span class={`inline-flex rounded-full px-3 py-1 text-sm font-semibold ${
          detail.outcome === 'assistanceTakesPrecedence'
            ? 'bg-cyan-950 text-cyan-300'
            : detail.eligible
              ? 'bg-emerald-950 text-emerald-300'
              : 'bg-rose-950 text-rose-300'
        }`}>{outcomeLabel(detail)}</span>
      </div>

      <section aria-labelledby="pull-request-diagnostic-checks">
        <h3 id="pull-request-diagnostic-checks" class="text-base font-semibold text-white">Checks</h3>
        <ul class="mt-3 divide-y divide-slate-800 rounded-xl border border-slate-800">
          {#each detail.checks as check (check.code)}
            <li class="grid gap-2 px-4 py-3 sm:grid-cols-[auto_1fr] sm:gap-3">
              <span class={`inline-flex h-fit w-fit rounded-full px-2 py-0.5 text-xs font-semibold ${
                check.passed ? 'bg-emerald-950 text-emerald-300' : 'bg-rose-950 text-rose-300'
              }`}>{check.passed ? 'Pass' : 'Fail'}</span>
              <div>
                <p class="font-medium text-slate-100">{check.label}</p>
                <p class="mt-0.5 text-sm leading-6 text-slate-400">{check.reason}</p>
              </div>
            </li>
          {/each}
        </ul>
      </section>

      <section aria-labelledby="pull-request-current-values">
        <h3 id="pull-request-current-values" class="text-base font-semibold text-white">Current values</h3>
        <dl class="mt-3 grid gap-4 rounded-xl border border-slate-800 bg-slate-950/30 p-4 sm:grid-cols-2">
          <div><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Status</dt><dd class="mt-1 text-sm text-slate-100">{detail.status || 'None'}</dd></div>
          <div><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Environment</dt><dd class="mt-1 text-sm text-slate-100">{detail.sourceControlEnvironmentKey}</dd></div>
          <div><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Repository</dt><dd class="mt-1 text-sm text-slate-100">{detail.repositoryKey}</dd></div>
          <div><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Branches</dt><dd class="mt-1 break-all text-sm text-slate-100">{detail.sourceBranch} → {detail.targetBranch}</dd></div>
          <div class="sm:col-span-2">
            <dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Labels</dt>
            <dd class="mt-2 flex flex-wrap gap-2">
              {#if detail.labels.length === 0}<span class="text-sm text-slate-400">None</span>{:else}
                {#each detail.labels as label (label)}<span class={chipClass}>{label}</span>{/each}
              {/if}
            </dd>
          </div>
          <div class="sm:col-span-2">
            <dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Linked work items</dt>
            <dd class="mt-2 flex flex-wrap gap-2">
              {#if detail.linkedWorkItems.length === 0}<span class="text-sm text-slate-400">None</span>{:else}
                {#each detail.linkedWorkItems as item (item.workItemId)}
                  <a class={`${chipClass} hover:text-white`} href={item.workItemUrl} target="_blank" rel="noreferrer">Work item #{item.workItemId}</a>
                {/each}
              {/if}
            </dd>
          </div>
          {#if detail.feedbackTrace}
            <div><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Reviewers configured</dt><dd class="mt-1 text-sm text-slate-100">{detail.feedbackTrace.reviewerAllowlistConfigured ? 'Yes' : 'No'}</dd></div>
            <div><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Feedback marker</dt><dd class="mt-1 text-sm text-slate-100">{detail.feedbackTrace.markerStatus}</dd></div>
            <div><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Threads</dt><dd class="mt-1 text-sm text-slate-100">{detail.feedbackTrace.totalThreadCount} total · {detail.feedbackTrace.activeThreadCount} active</dd></div>
            <div><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Reviewer threads</dt><dd class="mt-1 text-sm text-slate-100">{detail.feedbackTrace.allowlistedReviewerThreadCount}</dd></div>
            <div><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Nonempty threads</dt><dd class="mt-1 text-sm text-slate-100">{detail.feedbackTrace.nonEmptyContentThreadCount}</dd></div>
            <div><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Qualifying threads</dt><dd class="mt-1 text-sm text-slate-100">{detail.feedbackTrace.qualifyingThreadCount}</dd></div>
          {:else}
            <div class="sm:col-span-2"><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Review feedback</dt><dd class="mt-1 text-sm text-slate-400">Not inspected</dd></div>
          {/if}
          {#if detail.tracking}
            <div class="sm:col-span-2"><dt class="text-xs font-semibold tracking-wide text-slate-400 uppercase">Tracking</dt><dd class="mt-1 text-sm text-slate-100">{detail.tracking.requestMode} · {detail.tracking.feedbackStatus}{detail.tracking.cycleStatus ? ` · ${detail.tracking.cycleStatus}` : ''}</dd></div>
          {/if}
        </dl>
      </section>

      <section aria-labelledby="pull-request-recognized-labels">
        <h3 id="pull-request-recognized-labels" class="text-base font-semibold text-white">Recognized labels</h3>
        <div class="mt-3 grid gap-4 sm:grid-cols-2">
          <div class="rounded-xl border border-slate-800 p-4">
            <h4 class="text-sm font-semibold text-slate-200">Revival</h4>
            <div class="mt-3"><span class={chipClass}>{detail.recognizedRevivalLabel}</span></div>
            <p class="mt-3 text-xs leading-5 text-slate-400">Requires originating run lineage and qualifying review feedback.</p>
          </div>
          <div class="rounded-xl border border-slate-800 p-4">
            <h4 class="text-sm font-semibold text-slate-200">Assistance</h4>
            <div class="mt-3"><span class={chipClass}>{detail.recognizedAssistanceLabel}</span></div>
            <p class="mt-3 text-xs leading-5 text-slate-400">Takes precedence when both labels are present and can proceed with zero comments.</p>
          </div>
        </div>
      </section>
    </div>
  {/if}
</Dialog>
