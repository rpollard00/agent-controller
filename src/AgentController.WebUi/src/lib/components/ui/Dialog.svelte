<script lang="ts">
  import type { Snippet } from 'svelte';

  let {
    open,
    title,
    description,
    children,
    actions,
    onclose,
  }: {
    open: boolean;
    title: string;
    description?: string;
    children: Snippet;
    actions?: Snippet;
    onclose: () => void;
  } = $props();

  let dialog: HTMLDialogElement;

  $effect(() => {
    if (!dialog) return;

    if (open && !dialog.open) {
      if (typeof dialog.showModal === 'function') dialog.showModal();
      else dialog.setAttribute('open', '');
    } else if (!open && dialog.open) {
      dialog.close();
    }
  });
</script>

<dialog
  bind:this={dialog}
  aria-labelledby="dialog-title"
  aria-describedby={description ? 'dialog-description' : undefined}
  class="m-auto w-[min(32rem,calc(100%-2rem))] rounded-2xl border border-slate-700 bg-slate-900 p-0 text-slate-100 shadow-2xl backdrop:bg-slate-950/80"
  onclose={() => onclose()}
  oncancel={(event) => {
    event.preventDefault();
    onclose();
  }}
>
  <div class="p-6">
    <h2 id="dialog-title" class="text-xl font-semibold text-white">{title}</h2>
    {#if description}
      <p id="dialog-description" class="mt-2 leading-6 text-slate-400">{description}</p>
    {/if}
    <div class="mt-5">{@render children()}</div>
    {#if actions}
      <div class="mt-6 flex flex-wrap justify-end gap-3">{@render actions()}</div>
    {/if}
  </div>
</dialog>
