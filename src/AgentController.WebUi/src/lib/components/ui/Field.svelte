<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    id,
    label,
    hint,
    error,
    required = false,
    children,
  }: {
    id: string;
    label: string;
    hint?: string;
    error?: string;
    required?: boolean;
    children: Snippet;
  } = $props();

  const hintId = $derived(hint ? `${id}-hint` : undefined);
  const errorId = $derived(error ? `${id}-error` : undefined);
</script>

<div class="space-y-2">
  <label class="block text-sm font-medium text-slate-200" for={id}>
    {label}
    {#if required}<span class="text-rose-300" aria-hidden="true"> *</span>{/if}
    {#if required}<span class="sr-only"> (required)</span>{/if}
  </label>
  {@render children()}
  {#if hint}<p id={hintId} class="text-sm text-slate-400">{hint}</p>{/if}
  {#if error}<p id={errorId} class="text-sm font-medium text-rose-300">{error}</p>{/if}
</div>
