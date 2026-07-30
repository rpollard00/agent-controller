<script lang="ts">
  import type { Snippet } from 'svelte';

  export type DialogExternalLink = {
    href: string;
    label?: string;
  };

  let {
    open,
    title,
    description,
    contextLine,
    children,
    actions,
    onclose,
    variant = 'default',
    loading = false,
    loadingLabel = 'Loading…',
    error,
    onretry,
    externalLink,
  }: {
    open: boolean;
    title: string;
    description?: string;
    contextLine?: string;
    children?: Snippet;
    actions?: Snippet;
    onclose: () => void;
    variant?: 'default' | 'wide';
    loading?: boolean;
    loadingLabel?: string;
    error?: string;
    onretry?: () => void;
    externalLink?: DialogExternalLink;
  } = $props();

  const componentId = $props.id();
  const titleId = `${componentId}-title`;
  const descriptionId = `${componentId}-description`;
  const contextId = `${componentId}-context`;

  let dialog!: HTMLDialogElement;
  let previouslyFocused: HTMLElement | null = null;
  let wasOpen = false;

  function restoreFocus() {
    const target = previouslyFocused;
    previouslyFocused = null;
    if (target?.isConnected) target.focus();
  }

  function focusFirstControl() {
    queueMicrotask(() => {
      if (!open || !dialog) return;
      const target = dialog.querySelector<HTMLElement>(
        '[autofocus], button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      );
      (target ?? dialog).focus();
    });
  }

  function getFocusableElements() {
    return Array.from(
      dialog.querySelectorAll<HTMLElement>(
        'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      ),
    ).filter((element) => !element.hasAttribute('hidden'));
  }

  function handleKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') {
      event.preventDefault();
      onclose();
      return;
    }

    if (event.key !== 'Tab') return;
    const focusable = getFocusableElements();
    if (focusable.length === 0) {
      event.preventDefault();
      dialog.focus();
      return;
    }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  $effect(() => {
    if (!dialog) return;

    if (open && !wasOpen) {
      previouslyFocused = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      if (typeof dialog.showModal === 'function') dialog.showModal();
      else dialog.setAttribute('open', '');
      focusFirstControl();
    } else if (!open && wasOpen) {
      if (dialog.open && typeof dialog.close === 'function') dialog.close();
      else dialog.removeAttribute('open');
      restoreFocus();
    }
    wasOpen = open;
  });

  $effect(() => () => restoreFocus());
</script>

<dialog
  bind:this={dialog}
  tabindex="-1"
  aria-labelledby={titleId}
  aria-describedby={contextLine ? contextId : description ? descriptionId : undefined}
  aria-busy={loading || undefined}
  class={`m-auto rounded-2xl border border-slate-700 bg-slate-900 p-0 text-slate-100 shadow-2xl backdrop:bg-slate-950/80 ${
    variant === 'wide'
      ? 'max-h-[calc(100dvh-2rem)] w-[min(64rem,calc(100%-2rem))] overflow-hidden'
      : 'w-[min(32rem,calc(100%-2rem))]'
  }`}
  onkeydown={handleKeydown}
  onclose={() => {
    restoreFocus();
    if (open) onclose();
  }}
  oncancel={(event) => {
    event.preventDefault();
    onclose();
  }}
>
  <div class={variant === 'wide' ? 'flex max-h-[calc(100dvh-2rem)] flex-col' : ''}>
    <div class={`flex items-start gap-4 ${variant === 'wide' ? 'border-b border-slate-800 px-6 py-5' : 'px-6 pt-6'}`}>
      <div class="min-w-0 flex-1">
        <h2 id={titleId} class="text-xl font-semibold text-white">{title}</h2>
        {#if contextLine}
          <p id={contextId} class="mt-1 text-sm leading-6 text-slate-400">{contextLine}</p>
        {:else if description}
          <p id={descriptionId} class="mt-2 leading-6 text-slate-400">{description}</p>
        {/if}
      </div>
      {#if externalLink}
        <a
          href={externalLink.href}
          target="_blank"
          rel="noreferrer"
          class="shrink-0 rounded-md px-2 py-1 text-sm font-semibold text-cyan-300 hover:text-cyan-200"
        >
          {externalLink.label ?? 'Open externally'}<span class="sr-only"> (opens in a new tab)</span>
        </a>
      {/if}
      {#if variant === 'wide'}
        <button
          type="button"
          aria-label="Close dialog"
          class="inline-flex min-h-10 min-w-10 shrink-0 items-center justify-center rounded-lg text-2xl leading-none text-slate-300 hover:bg-slate-800 hover:text-white"
          onclick={onclose}
        >
          <span aria-hidden="true">×</span>
        </button>
      {/if}
    </div>

    <div class={variant === 'wide' ? 'min-h-0 overflow-y-auto p-6' : 'px-6 pb-6'}>
      <div class={variant === 'wide' ? '' : 'mt-5'}>
        {#if loading}
          <div role="status" class="py-8 text-center text-sm text-slate-300">{loadingLabel}</div>
        {:else if error}
          <div role="alert" class="rounded-xl border border-rose-500/40 bg-rose-950/40 p-4 text-sm text-rose-100">
            <p>{error}</p>
            {#if onretry}
              <button
                type="button"
                class="mt-3 rounded-lg border border-rose-400/50 px-3 py-2 font-semibold hover:bg-rose-900/50"
                onclick={onretry}
              >Retry</button>
            {/if}
          </div>
        {:else if children}
          {@render children()}
        {/if}
      </div>
      {#if actions && !loading}
        <div class="mt-6 flex flex-wrap justify-end gap-3">{@render actions()}</div>
      {/if}
    </div>
  </div>
</dialog>
