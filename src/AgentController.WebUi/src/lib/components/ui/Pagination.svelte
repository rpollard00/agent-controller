<script lang="ts">
  let {
    page,
    pageSize,
    total,
    onprevious,
    onnext,
    label = 'Pagination',
  }: {
    page: number;
    pageSize: number;
    total: number;
    onprevious: () => void;
    onnext: () => void;
    label?: string;
  } = $props();

  const safePage = $derived(Math.max(1, Math.floor(page) || 1));
  const safePageSize = $derived(Math.max(1, Math.floor(pageSize) || 1));
  const safeTotal = $derived(Math.max(0, Math.floor(total) || 0));
  const start = $derived(safeTotal === 0 ? 0 : Math.min((safePage - 1) * safePageSize + 1, safeTotal));
  const end = $derived(Math.min(safePage * safePageSize, safeTotal));
  const hasPrevious = $derived(safePage > 1 && safeTotal > 0);
  const hasNext = $derived(safePage * safePageSize < safeTotal);
</script>

<nav aria-label={label} class="flex flex-wrap items-center justify-end gap-3 text-sm">
  <span aria-live="polite" class="tabular-nums text-slate-400">{start}–{end} of {safeTotal}</span>
  <div class="flex items-center gap-2">
    <button
      type="button"
      disabled={!hasPrevious}
      aria-disabled={!hasPrevious}
      class="inline-flex min-h-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900 px-3 font-semibold text-slate-200 hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
      onclick={onprevious}
    >Previous</button>
    <button
      type="button"
      disabled={!hasNext}
      aria-disabled={!hasNext}
      class="inline-flex min-h-9 items-center justify-center rounded-lg border border-slate-700 bg-slate-900 px-3 font-semibold text-slate-200 hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
      onclick={onnext}
    >Next</button>
  </div>
</nav>
