<script lang="ts">
  import type { RuntimeEnvironmentProfile } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import { runtimeEnvironmentEditPath } from './runtimeEnvironmentRoutes';

  let {
    environment,
    updating = false,
    ontoggle,
    ondelete,
  }: {
    environment: RuntimeEnvironmentProfile;
    updating?: boolean;
    ontoggle: (profile: RuntimeEnvironmentProfile) => void;
    ondelete: (profile: RuntimeEnvironmentProfile) => void;
  } = $props();

  const loadouts = $derived(Object.entries(environment.runtimeSettings.loadouts));

  function formatTimestamp(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? value : date.toLocaleString();
  }

  function executionKindLabel(value: string): string {
    if (value === 'newWork') return 'New work';
    if (value === 'rework') return 'Rework';
    return value;
  }
</script>

<div class="space-y-6">
  <Card title="Environment details" description="Managed workspace and runtime provider selection.">
    {#snippet actions()}
      <div class="flex flex-wrap gap-2">
        <Button variant="secondary" disabled={updating} onclick={() => ontoggle(environment)}>
          {updating ? 'Saving…' : environment.enabled ? 'Disable' : 'Enable'}
        </Button>
        <a
          href={runtimeEnvironmentEditPath(environment.key)}
          class="inline-flex min-h-10 items-center rounded-lg border border-slate-700 bg-slate-900 px-4 py-2 text-sm font-semibold text-slate-100 hover:bg-slate-800"
        >
          Edit
        </a>
        <Button variant="danger" disabled={updating} onclick={() => ondelete(environment)}>
          Delete
        </Button>
      </div>
    {/snippet}

    <dl class="grid gap-x-8 gap-y-6 sm:grid-cols-2">
      <div>
        <dt class="text-sm font-medium text-slate-400">Environment name</dt>
        <dd class="mt-1 break-all text-slate-100">{environment.key}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Status</dt>
        <dd class="mt-1 text-slate-100">{environment.enabled ? 'Enabled' : 'Disabled'}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Display name</dt>
        <dd class="mt-1 text-slate-100">{environment.displayName}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Workspace provider</dt>
        <dd class="mt-1 text-slate-100">{environment.environmentProvider}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Workspace root</dt>
        <dd class="mt-1 break-all text-slate-100">
          {environment.environmentSettings.workspaceRoot ?? 'Service default'}
        </dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Runtime provider</dt>
        <dd class="mt-1 text-slate-100">{environment.runtimeProvider}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Created</dt>
        <dd class="mt-1 text-slate-100">{formatTimestamp(environment.createdAt)}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Last updated</dt>
        <dd class="mt-1 text-slate-100">{formatTimestamp(environment.updatedAt)}</dd>
      </div>
    </dl>
  </Card>

  {#if environment.runtimeProvider === 'PiMateria'}
    <Card title="Loadout mappings" description="Pi-materia loadout selected by execution kind.">
      {#if loadouts.length > 0}
        <dl class="grid gap-4 sm:grid-cols-2">
          {#each loadouts as [executionKind, loadout]}
            <div class="rounded-lg border border-slate-800 bg-slate-950/40 p-4">
              <dt class="text-sm font-medium text-slate-400">
                {executionKindLabel(executionKind)}
              </dt>
              <dd class="mt-1 break-all text-slate-100">{loadout}</dd>
            </div>
          {/each}
        </dl>
      {:else}
        <p class="text-sm text-slate-400">No loadouts configured.</p>
      {/if}
    </Card>
  {:else}
    <Alert
      variant="info"
      title="Simulated runtime"
      message="Mock Pi Materia simulates execution without launching a Pi process, so no loadout mapping applies."
    />
  {/if}

  <a
    href="/runtime-environments"
    class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-cyan-300 hover:bg-slate-800 hover:text-cyan-200"
  >
    Back to runtime environments
  </a>
</div>
