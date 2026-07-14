<script lang="ts">
  let {
    selected,
    suggestions = [],
    disabled = false,
    error = false,
  }: {
    selected: string[];
    suggestions?: string[];
    disabled?: boolean;
    error?: boolean;
  } = $props();

  let input = $state('');
  let nextId = selected.length;

  const inputClasses =
    'min-h-11 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 placeholder:text-slate-600 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400';

  const available = $derived(
    suggestions.filter((s) => !selected.includes(s)),
  );

  function handleKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && input.trim()) {
      event.preventDefault();
      addState(input.trim());
    }
  }

  function addState(value: string): void {
    const trimmed = value.trim();
    if (!trimmed || selected.includes(trimmed)) return;
    selected.push(trimmed);
    input = '';
  }

  function removeState(index: number): void {
    selected.splice(index, 1);
  }

  function addFromSuggestion(value: string): void {
    if (selected.includes(value)) return;
    selected.push(value);
  }
</script>

<div class="space-y-3">
  {#if selected.length > 0}
    <div class="flex flex-wrap gap-2">
      {#each selected as state, index (state)}
        <span class="inline-flex items-center gap-1 rounded-md bg-slate-800 px-2.5 py-1 text-sm text-slate-200">
          {state}
          <button
            type="button"
            class="ml-0.5 flex size-4 cursor-pointer items-center justify-center rounded text-slate-400 hover:text-slate-200"
            class:cursor-not-allowed={disabled}
            disabled={disabled}
            aria-label={`Remove {state}`}
            onclick={() => removeState(index)}
          >
            <span aria-hidden="true">&times;</span>
          </button>
        </span>
      {/each}
    </div>
  {/if}

  <div class="flex gap-2">
    <input
      class={inputClasses}
      value={input}
      disabled={disabled}
      autocomplete="off"
      placeholder="e.g. Resolved"
      aria-label="Add completed state"
      aria-invalid={error ? 'true' : undefined}
      oninput={(e) => input = (e.target as HTMLInputElement).value}
      onkeydown={handleKeydown}
    />
    <button
      type="button"
      class="min-h-11 shrink-0 rounded-lg border border-slate-700 bg-slate-800 px-3 py-2 text-sm font-medium text-slate-200 hover:bg-slate-700 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-500"
      disabled={disabled || !input.trim() || selected.includes(input.trim())}
      onclick={() => addState(input)}
    >
      Add
    </button>
  </div>

  {#if available.length > 0}
    <div class="space-y-1">
      <p class="text-xs font-medium text-slate-500">Suggested from board</p>
      <div class="flex flex-wrap gap-1.5">
        {#each available as state (state)}
          <button
            type="button"
            class="rounded-md border border-slate-700 bg-slate-900 px-2 py-0.5 text-xs text-slate-300 hover:border-slate-500 hover:text-slate-100 disabled:cursor-not-allowed disabled:text-slate-600"
            disabled={disabled}
            onclick={() => addFromSuggestion(state)}
          >
            + {state}
          </button>
        {/each}
      </div>
    </div>
  {/if}
</div>
