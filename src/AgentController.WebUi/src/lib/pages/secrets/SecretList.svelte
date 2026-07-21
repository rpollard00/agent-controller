<script lang="ts">
  import type { SecretInfo, SecretType } from '../../api/types';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import DataTable from '../../components/ui/DataTable.svelte';
  import { secretDetailPath, secretNewVersionPath } from './secretRoutes';

  let {
    secrets,
    empty,
    onrefresh,
    ondelete,
  }: {
    secrets: SecretInfo[];
    empty: boolean;
    onrefresh: () => void;
    ondelete: (secret: SecretInfo) => void;
  } = $props();

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
</script>

<Card
  title="Managed secrets"
  description="Named, versioned, encrypted-at-rest secrets for integrated platforms."
>
  {#snippet actions()}
    <Button variant="secondary" onclick={onrefresh}>Refresh</Button>
  {/snippet}

  {#if empty}
    <div class="rounded-xl border border-dashed border-slate-700 px-5 py-12 text-center">
      <h2 class="font-semibold text-white">No secrets yet</h2>
      <p class="mx-auto mt-2 max-w-lg text-sm leading-6 text-slate-400">
        Add a secret to store encrypted credentials for your integrated platforms.
        Values are encrypted at rest and never displayed after storage.
      </p>
      <a
        href="/secrets/new"
        class="mt-5 inline-flex min-h-11 items-center justify-center rounded-lg bg-cyan-400 px-4 py-2 text-sm font-semibold text-slate-950 hover:bg-cyan-300"
      >
        Add your first secret
      </a>
    </div>
  {:else}
    <DataTable caption="Managed secrets">
      <thead class="bg-slate-950/60 text-xs tracking-wide text-slate-400 uppercase">
        <tr>
          <th class="px-4 py-3 font-medium" scope="col">Name</th>
          <th class="px-4 py-3 font-medium" scope="col">Type</th>
          <th class="px-4 py-3 font-medium" scope="col">Latest version</th>
          <th class="px-4 py-3 font-medium" scope="col">Created</th>
          <th class="px-4 py-3 font-medium" scope="col">Updated</th>
          <th class="px-4 py-3 text-right font-medium" scope="col">Actions</th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-800">
        {#each secrets as secret (secret.name)}
          <tr class="align-top">
            <th class="px-4 py-4 font-medium" scope="row">
              <a
                class="text-cyan-300 hover:text-cyan-200 hover:underline"
                href={secretDetailPath(secret.name)}
              >
                {secret.name}
              </a>
            </th>
            <td class="px-4 py-4">
              <span class="inline-flex rounded-full px-2.5 py-0.5 text-xs font-semibold {typeBadgeClass(secret.secretType)}">
                {typeLabel(secret.secretType)}
              </span>
            </td>
            <td class="px-4 py-4 text-slate-300">
              <span class="inline-flex rounded-full bg-slate-800 px-2.5 py-1 text-xs font-semibold text-slate-300">
                v{secret.latestVersion}
              </span>
            </td>
            <td class="px-4 py-4 text-sm text-slate-400">
              {formatTimestamp(secret.createdAt)}
            </td>
            <td class="px-4 py-4 text-sm text-slate-400">
              {formatTimestamp(secret.updatedAt)}
            </td>
            <td class="px-4 py-3">
              <div class="flex min-w-max justify-end gap-1">
                <a
                  href={secretNewVersionPath(secret.name)}
                  class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-slate-300 hover:bg-slate-800 hover:text-white"
                >
                  New version
                </a>
                <a
                  href={secretDetailPath(secret.name)}
                  class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-slate-300 hover:bg-slate-800 hover:text-white"
                >
                  Details
                </a>
                <Button
                  variant="ghost"
                  ariaLabel={`Delete ${secret.name}`}
                  onclick={() => ondelete(secret)}
                >
                  Delete
                </Button>
              </div>
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
  {/if}
</Card>
