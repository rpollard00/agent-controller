<script lang="ts">
  import type { Snippet } from 'svelte';

  let { caption, children }: { caption: string; children: Snippet } = $props();
</script>

<div class="overflow-x-auto rounded-xl border border-slate-800">
  <table class="min-w-full divide-y divide-slate-800 text-left text-sm">
    <caption class="sr-only">{caption}</caption>
    {@render children()}
  </table>
</div>
