<script lang="ts">
  import type { Snippet } from 'svelte';

  type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost';

  let {
    children,
    type = 'button',
    variant = 'primary',
    disabled = false,
    ariaLabel,
    onclick,
  }: {
    children: Snippet;
    type?: 'button' | 'submit' | 'reset';
    variant?: ButtonVariant;
    disabled?: boolean;
    ariaLabel?: string;
    onclick?: (event: MouseEvent) => void;
  } = $props();

  const variants: Record<ButtonVariant, string> = {
    primary: 'bg-cyan-400 text-slate-950 hover:bg-cyan-300',
    secondary: 'border border-slate-700 bg-slate-900 text-slate-100 hover:bg-slate-800',
    danger: 'bg-rose-500 text-white hover:bg-rose-400',
    ghost: 'text-slate-300 hover:bg-slate-800 hover:text-white',
  };
</script>

<button
  {type}
  {disabled}
  aria-label={ariaLabel}
  class={`inline-flex min-h-10 items-center justify-center gap-2 rounded-lg px-4 py-2 text-sm font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${variants[variant]}`}
  {onclick}
>
  {@render children()}
</button>
