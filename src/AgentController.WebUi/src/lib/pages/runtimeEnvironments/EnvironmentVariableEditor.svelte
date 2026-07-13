<script lang="ts">
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import type { EnvironmentVariableRow } from './runtimeEnvironmentForm';

  let {
    rows,
    disabled = false,
    error,
    onadd,
    onremove,
    onrowchange,
  }: {
    rows: EnvironmentVariableRow[];
    disabled?: boolean;
    error?: string;
    onadd: () => void;
    onremove: (id: string) => void;
    onrowchange: (id: string, patch: Partial<EnvironmentVariableRow>) => void;
  } = $props();

  const inputClasses =
    'min-h-11 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 font-mono text-sm text-slate-100 placeholder:text-slate-600 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400';

  function updateTarget(event: Event, id: string): void {
    onrowchange(id, { target: (event.currentTarget as HTMLInputElement).value });
  }

  function updateSource(event: Event, id: string): void {
    onrowchange(id, { source: (event.currentTarget as HTMLInputElement).value });
  }
</script>

<div class="space-y-4">
  <div class="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
    <div>
      <h3 class="text-sm font-medium text-slate-200">Environment-variable forwarding</h3>
      <p id="runtime-variables-hint" class="mt-1 text-sm leading-6 text-slate-400">
        Map each variable created for the child process to a variable read from Agent Controller.
      </p>
    </div>
    <Button variant="secondary" onclick={onadd} {disabled}>Add variable mapping</Button>
  </div>

  <Alert
    variant="info"
    title="Variable references only"
    message="Enter environment-variable names in both columns. Secret values are never accepted or stored here."
  />

  {#if rows.length === 0}
    <p class="rounded-lg border border-dashed border-slate-700 p-4 text-sm text-slate-400">
      No environment variables will be forwarded to the runtime.
    </p>
  {/if}

  <div class="space-y-3">
    {#each rows as row, index (row.id)}
      <fieldset class="rounded-xl border border-slate-800 bg-slate-950/40 p-4">
        <legend class="sr-only">Environment-variable mapping {index + 1}</legend>
        <div class="grid gap-4 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] md:items-end">
          <div class="space-y-2">
            <label class="block text-sm font-medium text-slate-200" for={`runtime-variable-target-${row.id}`}>
              Target variable name
            </label>
            <input
              id={`runtime-variable-target-${row.id}`}
              class={inputClasses}
              value={row.target}
              {disabled}
              spellcheck="false"
              autocomplete="off"
              placeholder="AZURE_DEVOPS_EXT_PAT"
              pattern="[A-Za-z_][A-Za-z0-9_]*"
              aria-invalid={error ? 'true' : undefined}
              aria-describedby={`runtime-variables-hint${error ? ' runtime-variables-error' : ''}`}
              oninput={(event) => updateTarget(event, row.id)}
            />
          </div>
          <div class="space-y-2">
            <label class="block text-sm font-medium text-slate-200" for={`runtime-variable-source-${row.id}`}>
              Source variable reference
            </label>
            <input
              id={`runtime-variable-source-${row.id}`}
              class={inputClasses}
              value={row.source}
              {disabled}
              spellcheck="false"
              autocomplete="off"
              placeholder="AZURE_DEVOPS_PAT"
              pattern="[A-Za-z_][A-Za-z0-9_]*"
              aria-invalid={error ? 'true' : undefined}
              aria-describedby={`runtime-variables-hint${error ? ' runtime-variables-error' : ''}`}
              oninput={(event) => updateSource(event, row.id)}
            />
          </div>
          <Button
            variant="ghost"
            ariaLabel={`Remove environment-variable mapping ${index + 1}`}
            onclick={() => onremove(row.id)}
            {disabled}
          >Remove</Button>
        </div>
      </fieldset>
    {/each}
  </div>

  {#if error}
    <p id="runtime-variables-error" class="text-sm font-medium text-rose-300">{error}</p>
  {/if}
</div>
