<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    title,
    description,
    children,
    actions,
  }: {
    title?: string;
    description?: string;
    children: Snippet;
    actions?: Snippet;
  } = $props();
</script>

<section class="rounded-2xl border border-slate-800 bg-slate-900/70 p-5 shadow-lg shadow-black/10 sm:p-6">
  {#if title || description || actions}
    <header class="mb-5 flex flex-wrap items-start justify-between gap-4">
      <div>
        {#if title}<h2 class="text-lg font-semibold text-white">{title}</h2>{/if}
        {#if description}<p class="mt-1 text-sm leading-6 text-slate-400">{description}</p>{/if}
      </div>
      {#if actions}<div class="shrink-0">{@render actions()}</div>{/if}
    </header>
  {/if}
  {@render children()}
</section>
