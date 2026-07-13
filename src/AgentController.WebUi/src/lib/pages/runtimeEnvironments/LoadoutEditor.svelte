<script lang="ts">
  import Button from '../../components/ui/Button.svelte';
  import type { ExecutionKind, LoadoutRow } from './runtimeEnvironmentForm';

  let {
    rows,
    disabled = false,
    error,
    onadd,
    onremove,
    onrowchange,
  }: {
    rows: LoadoutRow[];
    disabled?: boolean;
    error?: string;
    onadd: () => void;
    onremove: (id: string) => void;
    onrowchange: (id: string, patch: Partial<LoadoutRow>) => void;
  } = $props();

  const inputClasses =
    'min-h-11 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400';

  function updateKind(event: Event, id: string): void {
    onrowchange(id, { executionKind: (event.currentTarget as HTMLSelectElement).value as ExecutionKind });
  }

  function updateLoadout(event: Event, id: string): void {
    onrowchange(id, { loadout: (event.currentTarget as HTMLInputElement).value });
  }
</script>

<div class="space-y-4">
  <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h3 class="text-sm font-medium text-slate-200">Loadout mappings</h3>
      <p id="runtime-loadouts-hint" class="mt-1 text-sm leading-6 text-slate-400">
        Choose the pi-materia loadout used for each execution kind. New work is required.
      </p>
    </div>
    <Button variant="secondary" onclick={onadd} {disabled}>Add loadout</Button>
  </div>

  {#if rows.length === 0}
    <p class="rounded-lg border border-dashed border-slate-700 p-4 text-sm text-slate-400">
      No loadouts configured. Add the required New work mapping.
    </p>
  {/if}

  <div class="space-y-3">
    {#each rows as row, index (row.id)}
      <fieldset class="rounded-xl border border-slate-800 bg-slate-950/40 p-4">
        <legend class="sr-only">Loadout mapping {index + 1}</legend>
        <div class="grid gap-4 md:grid-cols-[minmax(0,0.75fr)_minmax(0,1.25fr)_auto] md:items-end">
          <div class="space-y-2">
            <label class="block text-sm font-medium text-slate-200" for={`runtime-loadout-kind-${row.id}`}>
              Execution kind
            </label>
            <select
              id={`runtime-loadout-kind-${row.id}`}
              class={inputClasses}
              value={row.executionKind}
              {disabled}
              aria-invalid={error ? 'true' : undefined}
              aria-describedby={`runtime-loadouts-hint${error ? ' runtime-loadouts-error' : ''}`}
              onchange={(event) => updateKind(event, row.id)}
            >
              <option value="">Choose an execution kind</option>
              <option value="newWork">New work</option>
              <option value="rework">Rework</option>
            </select>
          </div>
          <div class="space-y-2">
            <label class="block text-sm font-medium text-slate-200" for={`runtime-loadout-name-${row.id}`}>
              Loadout name
            </label>
            <input
              id={`runtime-loadout-name-${row.id}`}
              class={inputClasses}
              value={row.loadout}
              {disabled}
              autocomplete="off"
              placeholder="ADO-Build-NewWork"
              aria-invalid={error ? 'true' : undefined}
              aria-describedby={`runtime-loadouts-hint${error ? ' runtime-loadouts-error' : ''}`}
              oninput={(event) => updateLoadout(event, row.id)}
            />
          </div>
          <Button
            variant="ghost"
            ariaLabel={`Remove loadout mapping ${index + 1}`}
            onclick={() => onremove(row.id)}
            {disabled}
          >Remove</Button>
        </div>
      </fieldset>
    {/each}
  </div>

  {#if error}
    <p id="runtime-loadouts-error" class="text-sm font-medium text-rose-300">{error}</p>
  {/if}
</div>
