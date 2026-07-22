<script lang="ts">
  import type { ClonePreflightResult, CloneTransport, RepositoryProfile } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import { repositoryEditPath } from './repositoryRoutes';

  let {
    repository,
    preflightStatus,
    preflight,
    preflightError,
    onpreflight,
    ondelete,
  }: {
    repository: RepositoryProfile;
    preflightStatus: 'idle' | 'checking' | 'complete' | 'error';
    preflight?: ClonePreflightResult;
    preflightError?: string;
    onpreflight: () => void;
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

  function credentialLabel(result: ClonePreflightResult): string {
    const reference = result.credentialReference;
    if (!reference) return result.transport === 'local' ? 'Not required' : 'None';

    const type = result.credentialSource === 'sshKey' ? 'SSH key' : 'PAT';
    const version = reference.version === null ? 'latest version' : `version ${reference.version}`;
    return `${type} · ${reference.name} · ${version}`;
  }
</script>

<Card title="Repository details" description="Managed onboarding configuration.">
  {#snippet actions()}
    <div class="flex flex-wrap gap-2">
      <Button variant="secondary" onclick={onpreflight} disabled={preflightStatus === 'checking'}>
        {preflightStatus === 'checking' ? 'Checking…' : 'Run clone preflight'}
      </Button>
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
      <dt class="text-sm font-medium text-slate-400">Repository Name</dt>
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
      <dt class="text-sm font-medium text-slate-400">Repository Host</dt>
      <dd class="mt-1 text-slate-100">{repository.repositoryHostConnectionKey ?? 'None'}</dd>
    </div>
    <div>
      <dt class="text-sm font-medium text-slate-400">Runtime environment</dt>
      <dd class="mt-1 text-slate-100">{repository.runtimeEnvironmentKey ?? 'None'}</dd>
    </div>
  </dl>

  <section class="mt-7 space-y-4 border-t border-slate-800 pt-5" aria-labelledby="clone-preflight-heading">
    <div>
      <h3 id="clone-preflight-heading" class="text-base font-semibold text-white">Clone preflight</h3>
      <p class="mt-1 text-sm leading-6 text-slate-400">
        Checks credential resolution, remote reachability, and authentication without cloning.
      </p>
    </div>

    {#if preflightStatus === 'idle'}
      <p class="text-sm text-slate-300">Run a preflight to verify that this repository is ready to clone.</p>
    {:else if preflightStatus === 'checking'}
      <p class="text-sm text-slate-300" role="status">Contacting the repository with the configured credential…</p>
    {:else if preflightStatus === 'error'}
      <Alert
        variant="error"
        title="Could not run clone preflight"
        message={preflightError ?? 'The preflight request failed. Try again.'}
      />
    {:else if preflight}
      <Alert
        variant={preflight.success ? 'success' : 'error'}
        title={preflight.success ? 'Clone preflight passed' : 'Clone preflight failed'}
        message={preflight.success
          ? 'The repository is reachable and accepted the configured credential.'
          : preflight.reason}
      />
      <dl class="grid gap-3 rounded-lg border border-slate-800 bg-slate-950/40 p-4 text-sm sm:grid-cols-2">
        <div>
          <dt class="text-slate-400">Effective transport</dt>
          <dd class="mt-1 text-slate-100">{transportLabel(preflight.transport)}</dd>
        </div>
        <div>
          <dt class="text-slate-400">Credential checked</dt>
          <dd class="mt-1 break-all text-slate-100">{credentialLabel(preflight)}</dd>
        </div>
        {#if preflight.failureCode}
          <div class="sm:col-span-2">
            <dt class="text-slate-400">Failure code</dt>
            <dd class="mt-1 font-mono text-slate-100">{preflight.failureCode}</dd>
          </div>
        {/if}
      </dl>
    {/if}
  </section>

  <div class="mt-7 border-t border-slate-800 pt-5">
    <a
      href="/repositories"
      class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-cyan-300 hover:bg-slate-800 hover:text-cyan-200"
    >
      Back to repositories
    </a>
  </div>
</Card>
