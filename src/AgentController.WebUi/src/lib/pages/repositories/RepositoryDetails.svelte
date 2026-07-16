<script lang="ts">
  import type { CloneTransport, RepositoryProfile } from '../../api/types';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import { repositoryEditPath } from './repositoryRoutes';

  let {
    repository,
    ondelete,
  }: {
    repository: RepositoryProfile;
    ondelete: (profile: RepositoryProfile) => void;
  } = $props();

  function transportLabel(transport: CloneTransport): string {
    const labels: Record<CloneTransport, string> = {
      unspecified: 'Automatic',
      ssh: 'SSH',
      httpsPat: 'HTTPS + PAT',
      local: 'Local',
    };
    return labels[transport];
  }
</script>

<Card title="Repository details" description="Managed onboarding configuration.">
  {#snippet actions()}
    <div class="flex flex-wrap gap-2">
      <a
        href={repositoryEditPath(repository.key)}
        class="inline-flex min-h-10 items-center rounded-lg border border-slate-700 bg-slate-900 px-4 py-2 text-sm font-semibold text-slate-100 hover:bg-slate-800"
      >
        Edit
      </a>
      <Button variant="danger" onclick={() => ondelete(repository)}>Delete</Button>
    </div>
  {/snippet}

  <dl class="grid gap-x-8 gap-y-6 sm:grid-cols-2">
    <div>
      <dt class="text-sm font-medium text-slate-400">Repository key</dt>
      <dd class="mt-1 break-all text-slate-100">{repository.key}</dd>
    </div>
    <div>
      <dt class="text-sm font-medium text-slate-400">Default branch</dt>
      <dd class="mt-1 text-slate-100">{repository.defaultBranch}</dd>
    </div>
    <div class="sm:col-span-2">
      <dt class="text-sm font-medium text-slate-400">Clone location</dt>
      <dd class="mt-1 break-all text-slate-100">{repository.cloneUrl}</dd>
    </div>
    <div>
      <dt class="text-sm font-medium text-slate-400">Clone transport</dt>
      <dd class="mt-1 text-slate-100">{transportLabel(repository.transport)}</dd>
    </div>
    <div>
      <dt class="text-sm font-medium text-slate-400">Allowed paths</dt>
      <dd class="mt-1 text-slate-100">
        {repository.allowedPaths.length > 0 ? repository.allowedPaths.join(', ') : 'All paths'}
      </dd>
    </div>
    <div>
      <dt class="text-sm font-medium text-slate-400">Repository host connection</dt>
      <dd class="mt-1 text-slate-100">{repository.repositoryHostConnectionKey ?? 'None'}</dd>
    </div>
    <div>
      <dt class="text-sm font-medium text-slate-400">Runtime environment</dt>
      <dd class="mt-1 text-slate-100">{repository.runtimeEnvironmentKey ?? 'None'}</dd>
    </div>
  </dl>

  <div class="mt-7 border-t border-slate-800 pt-5">
    <a
      href="/repositories"
      class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-cyan-300 hover:bg-slate-800 hover:text-cyan-200"
    >
      Back to repositories
    </a>
  </div>
</Card>
