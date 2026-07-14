<script lang="ts">
  import type { AzureDevOpsEnvironmentProfile } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import { workSourceEnvironmentEditPath } from './workSourceEnvironmentRoutes';

  let {
    environment,
    updating = false,
    ontoggle,
    ondelete,
  }: {
    environment: AzureDevOpsEnvironmentProfile;
    updating?: boolean;
    ontoggle: (profile: AzureDevOpsEnvironmentProfile) => void;
    ondelete: (profile: AzureDevOpsEnvironmentProfile) => void;
  } = $props();

  function listLabel(values: string[], emptyLabel: string): string {
    return values.length > 0 ? values.join(', ') : emptyLabel;
  }

  function formatTimestamp(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? value : date.toLocaleString();
  }
</script>

<div class="space-y-6">
  <Card title="Environment details" description="Managed Azure DevOps connection and board policy.">
    {#snippet actions()}
      <div class="flex flex-wrap gap-2">
        <Button
          variant="secondary"
          disabled={updating}
          onclick={() => ontoggle(environment)}
        >
          {updating ? 'Saving…' : environment.enabled ? 'Disable' : 'Enable'}
        </Button>
        <a
          href={workSourceEnvironmentEditPath(environment.key)}
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
        <dt class="text-sm font-medium text-slate-400">Environment key</dt>
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
        <dt class="text-sm font-medium text-slate-400">Project</dt>
        <dd class="mt-1 text-slate-100">{environment.project}</dd>
      </div>
      <div class="sm:col-span-2">
        <dt class="text-sm font-medium text-slate-400">Organization URL</dt>
        <dd class="mt-1 break-all text-slate-100">{environment.organizationUrl}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Work-item type</dt>
        <dd class="mt-1 text-slate-100">{environment.workItemType}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Eligible tags</dt>
        <dd class="mt-1 text-slate-100">
          {listLabel(environment.eligibleTags, 'Any tag')}
        </dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Excluded tags</dt>
        <dd class="mt-1 text-slate-100">
          {listLabel(environment.excludedTags, 'None')}
        </dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Eligible states</dt>
        <dd class="mt-1 text-slate-100">
          {listLabel(environment.eligibleStates, 'Any state')}
        </dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Excluded states</dt>
        <dd class="mt-1 text-slate-100">
          {listLabel(environment.excludedStates, 'None')}
        </dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Active state</dt>
        <dd class="mt-1 text-slate-100">{environment.activeState ?? 'Not configured'}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Completed state</dt>
        <dd class="mt-1 text-slate-100">{environment.completedState ?? 'Not configured'}</dd>
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

  <Card title="Credential reference" description="The Azure DevOps PAT itself is never stored in this profile.">
    <dl>
      <div>
        <dt class="text-sm font-medium text-slate-400">PAT environment variable</dt>
        <dd class="mt-2 break-all font-mono text-sm text-cyan-200">
          {environment.patEnvironmentVariable}
        </dd>
      </div>
    </dl>
    <div class="mt-5">
      <Alert
        variant="info"
        title="Secret value redacted"
        message="Only the environment-variable name is shown. Configure its secret value outside Agent Controller."
      />
    </div>
  </Card>

  <a
    href="/work-source-environments"
    class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-cyan-300 hover:bg-slate-800 hover:text-cyan-200"
  >
    Back to Azure DevOps environments
  </a>
</div>
