<script lang="ts">
  import type { SecretInfo, SecretType, SecretVersionInfo } from '../../api/types';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import DataTable from '../../components/ui/DataTable.svelte';
  import { secretNewVersionPath } from './secretRoutes';

  let {
    name,
    versions,
    secrets,
    ondelete,
  }: {
    name: string;
    versions: SecretVersionInfo[];
    secrets: SecretInfo[];
    ondelete: (secret: SecretInfo) => void;
  } = $props();

  const secretInfo = $derived(secrets.find((secret) => secret.name === name));
  let copiedVersion = $state<number>();
  let expandedVersion = $state<number>();

  function formatTimestamp(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? value : date.toLocaleString();
  }

  function typeBadgeClass(secretType: SecretType): string {
    if (secretType === 'ssh-key') return 'bg-emerald-900/50 text-emerald-300';
    return 'bg-cyan-900/50 text-cyan-300';
  }

  function typeLabel(secretType: SecretType): string {
    if (secretType === 'ssh-key') return 'SSH Key';
    return 'PAT';
  }

  async function copyPublicKey(version: SecretVersionInfo): Promise<void> {
    if (!version.publicKey) return;

    try {
      await navigator.clipboard.writeText(version.publicKey);
      copiedVersion = version.version;
      setTimeout(() => { if (copiedVersion === version.version) copiedVersion = undefined; }, 2000);
    } catch {
      // Clipboard not available; fall back to nothing
    }
  }
</script>

<div class="space-y-6">
  {#if secretInfo}
    <Card title="Secret details" description="Named secret metadata.">
      <dl class="grid gap-x-8 gap-y-6 sm:grid-cols-2">
        <div>
          <dt class="text-sm font-medium text-slate-400">Secret name</dt>
          <dd class="mt-1 break-all text-slate-100">{secretInfo.name}</dd>
        </div>
        <div>
          <dt class="text-sm font-medium text-slate-400">Type</dt>
          <dd class="mt-1">
            <span class="inline-flex rounded-full px-2.5 py-0.5 text-xs font-semibold {typeBadgeClass(secretInfo.secretType)}">
              {typeLabel(secretInfo.secretType)}
            </span>
          </dd>
        </div>
        <div>
          <dt class="text-sm font-medium text-slate-400">Latest version</dt>
          <dd class="mt-1 text-slate-100">v{secretInfo.latestVersion}</dd>
        </div>
        <div>
          <dt class="text-sm font-medium text-slate-400">Created</dt>
          <dd class="mt-1 text-slate-100">{formatTimestamp(secretInfo.createdAt)}</dd>
        </div>
        <div>
          <dt class="text-sm font-medium text-slate-400">Last updated</dt>
          <dd class="mt-1 text-slate-100">{formatTimestamp(secretInfo.updatedAt)}</dd>
        </div>
      </dl>
    </Card>
  {/if}

  <Card title="Version history" description="All versions of this secret. Private keys and passphrases are never displayed. Public keys are shown for SSH-key secrets.">
    {#if versions.length > 0}
      <DataTable caption={`Version history for "${name}"`}>
        <thead class="bg-slate-950/60 text-xs tracking-wide text-slate-400 uppercase">
          <tr>
            <th class="px-4 py-3 font-medium" scope="col">Version</th>
            <th class="px-4 py-3 font-medium" scope="col">Type</th>
            <th class="px-4 py-3 font-medium" scope="col">Public key</th>
            <th class="px-4 py-3 font-medium" scope="col">Created</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-800">
          {#each versions as version (version.version)}
            <tr class="align-top">
              <td class="px-4 py-4">
                <span class="inline-flex rounded-full bg-slate-800 px-2.5 py-1 text-xs font-semibold text-slate-300">
                  v{version.version}
                </span>
              </td>
              <td class="px-4 py-4">
                <span class="inline-flex rounded-full px-2.5 py-0.5 text-xs font-semibold {typeBadgeClass(version.secretType)}">
                  {typeLabel(version.secretType)}
                </span>
              </td>
              <td class="px-4 py-4">
                {#if version.publicKey}
                  <div class="space-y-2">
                    <div class="flex items-center gap-2">
                      <code class="inline-block max-w-xs truncate align-middle text-sm text-slate-300" title={version.publicKey}>
                        {version.publicKey}
                      </code>
                      <button
                        type="button"
                        class="inline-flex shrink-0 items-center rounded px-2 py-1 text-xs font-medium text-slate-400 hover:bg-slate-800 hover:text-slate-200"
                        aria-expanded={expandedVersion === version.version}
                        aria-controls={`public-key-${version.version}`}
                        onclick={() => {
                          expandedVersion = expandedVersion === version.version
                            ? undefined
                            : version.version;
                        }}
                      >
                        {expandedVersion === version.version ? 'Hide' : 'View'}
                      </button>
                      <button
                        type="button"
                        class="inline-flex shrink-0 items-center gap-1 rounded px-2 py-1 text-xs font-medium text-slate-400 hover:bg-slate-800 hover:text-slate-200"
                        onclick={() => void copyPublicKey(version)}
                        title="Copy public key"
                      >
                        {#if copiedVersion === version.version}
                          <svg viewBox="0 0 24 24" class="size-3.5" fill="none" stroke="currentColor" aria-hidden="true">
                            <path d="M20 6L9 17l-5-5" stroke-linecap="round" stroke-linejoin="round" stroke-width="2"/>
                          </svg>
                          Copied
                        {:else}
                          <svg viewBox="0 0 24 24" class="size-3.5" fill="none" stroke="currentColor" aria-hidden="true">
                            <path d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" stroke-linecap="round" stroke-linejoin="round" stroke-width="2"/>
                          </svg>
                          Copy
                        {/if}
                      </button>
                    </div>
                    {#if expandedVersion === version.version}
                      <code
                        id={`public-key-${version.version}`}
                        class="block max-w-lg overflow-x-auto whitespace-pre-wrap break-all rounded-lg border border-slate-700 bg-slate-950 p-3 text-xs text-slate-200"
                      >{version.publicKey}</code>
                    {/if}
                  </div>
                {:else}
                  <span class="text-sm text-slate-500">—</span>
                {/if}
              </td>
              <td class="px-4 py-4 text-sm text-slate-400">
                {formatTimestamp(version.createdAt)}
              </td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
    {:else}
      <p class="text-sm text-slate-400">No version history available.</p>
    {/if}
  </Card>

  <div class="flex flex-wrap gap-3">
    <a
      href={secretNewVersionPath(name)}
      class="inline-flex min-h-10 items-center rounded-lg border border-slate-700 bg-slate-900 px-4 py-2 text-sm font-semibold text-slate-100 hover:bg-slate-800"
    >
      New version
    </a>
    <a
      href="/secrets"
      class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-cyan-300 hover:bg-slate-800 hover:text-cyan-200"
    >
      Back to secrets
    </a>
    {#if secretInfo}
      <Button variant="danger" ariaLabel={`Delete ${secretInfo.name}`} onclick={() => ondelete(secretInfo)}>
        Delete secret
      </Button>
    {/if}
  </div>
</div>
