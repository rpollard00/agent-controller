<script lang="ts">
  import { onDestroy } from 'svelte';
  import type { WebUiApiClient } from '../../api/client';
  import type { ConnectionConnectivityResult, ConnectionProfile } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import { connectionEditPath, connectionRepoPickerPath } from './connectionRoutes';

  let {
    connection,
    client,
    updating = false,
    ontoggle,
    ondelete,
  }: {
    connection: ConnectionProfile;
    client: WebUiApiClient;
    updating?: boolean;
    ontoggle: (profile: ConnectionProfile) => void;
    ondelete: (profile: ConnectionProfile) => void;
  } = $props();

  function formatTimestamp(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? value : date.toLocaleString();
  }

  let verifyLoading = $state(false);
  let verifyResult = $state<ConnectionConnectivityResult | null>(null);
  let verifyController: AbortController | undefined;

  async function testConnection(): Promise<void> {
    verifyController?.abort();
    const controller = new AbortController();
    verifyController = controller;
    verifyLoading = true;
    verifyResult = null;

    try {
      const result = await client.connections.verifyConnection(
        connection.key,
        controller.signal,
      );
      if (!controller.signal.aborted) {
        verifyResult = result;
      }
    } catch (error) {
      if (!controller.signal.aborted) {
        verifyResult = {
          success: false,
          authMechanism: '',
          errors: [error instanceof Error ? error.message : 'Connection test failed.'],
        };
      }
    } finally {
      if (verifyController === controller) {
        verifyLoading = false;
        verifyController = undefined;
      }
    }
  }

  function getRepoCount(result: ConnectionConnectivityResult | null): number | undefined {
    if (!result?.payload) return undefined;
    const repos = result.payload.repositories;
    if (Array.isArray(repos)) return repos.length;
    return undefined;
  }

  function orgUrl(): string {
    return connection.providerSettings?.organizationUrl ?? '';
  }

  function patSecretName(): string {
    return connection.providerSettings?.personalAccessTokenReference?.name ?? '';
  }

  function patSecretVersion(): number | null {
    return connection.providerSettings?.personalAccessTokenReference?.version ?? null;
  }

  onDestroy(() => {
    verifyController?.abort();
  });
</script>

<div class="space-y-6">
  <Card title="Connection details" description="Unified provider connection and credential reference.">
    {#snippet actions()}
      <div class="flex flex-wrap gap-2">
        <Button
          variant="secondary"
          disabled={updating || verifyLoading}
          onclick={() => ontoggle(connection)}
        >
          {updating ? 'Saving…' : connection.enabled ? 'Disable' : 'Enable'}
        </Button>
        <Button
          variant="secondary"
          disabled={verifyLoading}
          ariaLabel="Test connection"
          onclick={() => void testConnection()}
        >
          {verifyLoading ? 'Testing…' : 'Test connection'}
        </Button>
        {#if connection.capabilities.includes('Repositories')}
          <a
            href={connectionRepoPickerPath(connection.key)}
            class="inline-flex min-h-10 items-center rounded-lg border border-slate-700 bg-slate-900 px-4 py-2 text-sm font-semibold text-slate-100 hover:bg-slate-800"
          >
            Browse repositories
          </a>
        {/if}
        <a
          href={connectionEditPath(connection.key)}
          class="inline-flex min-h-10 items-center rounded-lg border border-slate-700 bg-slate-900 px-4 py-2 text-sm font-semibold text-slate-100 hover:bg-slate-800"
        >
          Edit
        </a>
        <Button variant="danger" disabled={updating} onclick={() => ondelete(connection)}>
          Delete
        </Button>
      </div>
    {/snippet}

    <dl class="grid gap-x-8 gap-y-6 sm:grid-cols-2">
      <div>
        <dt class="text-sm font-medium text-slate-400">Connection name</dt>
        <dd class="mt-1 break-all text-slate-100">{connection.key}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Status</dt>
        <dd class="mt-1 text-slate-100">{connection.enabled ? 'Enabled' : 'Disabled'}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Display name</dt>
        <dd class="mt-1 text-slate-100">{connection.displayName}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Provider</dt>
        <dd class="mt-1 text-slate-100">{connection.provider}</dd>
      </div>
      <div class="sm:col-span-2">
        <dt class="text-sm font-medium text-slate-400">Capabilities</dt>
        <dd class="mt-1 flex flex-wrap gap-2">
          {#each connection.capabilities as cap}
            <span class="inline-flex rounded-full bg-cyan-950 px-2.5 py-1 text-xs font-semibold text-cyan-300">
              {cap}
            </span>
          {/each}
        </dd>
      </div>
      {#if orgUrl()}
        <div class="sm:col-span-2">
          <dt class="text-sm font-medium text-slate-400">Organization URL</dt>
          <dd class="mt-1 break-all text-slate-100">{orgUrl()}</dd>
        </div>
      {/if}
      <div>
        <dt class="text-sm font-medium text-slate-400">Created</dt>
        <dd class="mt-1 text-slate-100">{formatTimestamp(connection.createdAt)}</dd>
      </div>
      <div>
        <dt class="text-sm font-medium text-slate-400">Last updated</dt>
        <dd class="mt-1 text-slate-100">{formatTimestamp(connection.updatedAt)}</dd>
      </div>
    </dl>
  </Card>

  {#if connection.providerSettings}
    <Card title="Credential reference" description="The PAT is stored as a named, versioned secret encrypted at rest.">
      <dl class="grid gap-x-8 gap-y-6 sm:grid-cols-2">
        <div>
          <dt class="text-sm font-medium text-slate-400">Secret name</dt>
          <dd class="mt-1 break-all font-mono text-sm text-cyan-200">
            {patSecretName() || 'Not configured'}
          </dd>
        </div>
        <div>
          <dt class="text-sm font-medium text-slate-400">Pinned version</dt>
          <dd class="mt-1 text-slate-100">
            {patSecretVersion()
              ? `v${patSecretVersion()}`
              : 'Latest'}
          </dd>
        </div>
      </dl>
      <div class="mt-5">
        <Alert
          variant="info"
          title="Secret value redacted"
          message="Only the secret reference is shown. The raw credential is resolved at runtime and never displayed."
        />
      </div>
    </Card>
  {/if}

  {#if verifyLoading}
    <Card title="Testing connection">
      <div class="flex items-center gap-3 text-sm text-slate-300" role="status">
        <span
          class="size-4 animate-spin rounded-full border-2 border-slate-700 border-t-cyan-300"
          aria-hidden="true"
        ></span>
        Testing connection…
      </div>
    </Card>
  {:else if verifyResult}
    <Card title="Connection test result">
      {#if verifyResult.success}
        <div class="flex items-center gap-2">
          <span class="inline-flex items-center gap-1 rounded-full bg-emerald-950 px-2.5 py-1 text-xs font-semibold text-emerald-300">
            Connected
            {#if getRepoCount(verifyResult) !== undefined}
              <span class="text-emerald-400">({getRepoCount(verifyResult)} repos)</span>
            {/if}
          </span>
        </div>
      {:else}
        <div class="space-y-1">
          <span class="inline-flex rounded-full bg-red-950 px-2.5 py-1 text-xs font-semibold text-red-300">
            Failed
          </span>
          {#each verifyResult.errors as error}
            <p class="text-sm text-red-400">{error}</p>
          {/each}
        </div>
      {/if}
    </Card>
  {/if}

  <a
    href="/connections"
    class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-cyan-300 hover:bg-slate-800 hover:text-cyan-200"
  >
    Back to Connections
  </a>
</div>
