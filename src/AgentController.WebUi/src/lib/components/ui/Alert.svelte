<script lang="ts">
  type AlertVariant = 'info' | 'success' | 'warning' | 'error';

  let {
    title,
    message,
    variant = 'info',
    errors = [],
  }: {
    title: string;
    message?: string;
    variant?: AlertVariant;
    errors?: string[];
  } = $props();

  const styles: Record<AlertVariant, string> = {
    info: 'border-cyan-900/80 bg-cyan-950/40 text-cyan-100',
    success: 'border-emerald-900/80 bg-emerald-950/40 text-emerald-100',
    warning: 'border-amber-900/80 bg-amber-950/40 text-amber-100',
    error: 'border-rose-900/80 bg-rose-950/40 text-rose-100',
  };
</script>

<div
  class={`rounded-xl border p-4 ${styles[variant]}`}
  role={variant === 'error' ? 'alert' : 'status'}
>
  <p class="font-semibold">{title}</p>
  {#if message}<p class="mt-1 text-sm leading-6 opacity-85">{message}</p>{/if}
  {#if errors.length > 0}
    <ul class="mt-2 list-disc space-y-1 pl-5 text-sm">
      {#each errors as error}<li>{error}</li>{/each}
    </ul>
  {/if}
</div>
