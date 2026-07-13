<script lang="ts">
  import type { RepositoryProfile } from '../../api/types';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import DataTable from '../../components/ui/DataTable.svelte';
  import { repositoryDetailPath, repositoryEditPath } from './repositoryRoutes';

  let {
    repositories,
    empty,
    onrefresh,
    ondelete,
  }: {
    repositories: RepositoryProfile[];
    empty: boolean;
    onrefresh: () => void;
    ondelete: (profile: RepositoryProfile) => void;
  } = $props();
</script>

<Card
  title="Managed repositories"
  description="Repository profiles configured through Agent Controller."
>
  {#snippet actions()}
    <Button variant="secondary" onclick={onrefresh}>Refresh</Button>
  {/snippet}

  {#if empty}
    <div class="rounded-xl border border-dashed border-slate-700 px-5 py-12 text-center">
      <h2 class="font-semibold text-white">No repositories yet</h2>
      <p class="mx-auto mt-2 max-w-lg text-sm leading-6 text-slate-400">
        Onboard a repository to connect source control with managed board and runtime environments.
      </p>
      <a
        href="/repositories/new"
        class="mt-5 inline-flex min-h-11 items-center justify-center rounded-lg bg-cyan-400 px-4 py-2 text-sm font-semibold text-slate-950 hover:bg-cyan-300"
      >
        Onboard your first repository
      </a>
    </div>
  {:else}
    <DataTable caption="Managed repositories">
      <thead class="bg-slate-950/60 text-xs tracking-wide text-slate-400 uppercase">
        <tr>
          <th class="px-4 py-3 font-medium" scope="col">Repository</th>
          <th class="px-4 py-3 font-medium" scope="col">Default branch</th>
          <th class="px-4 py-3 font-medium" scope="col">Managed environments</th>
          <th class="px-4 py-3 text-right font-medium" scope="col">Actions</th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-800">
        {#each repositories as profile (profile.key)}
          <tr class="align-top">
            <th class="px-4 py-4 font-medium" scope="row">
              <a
                class="break-all text-cyan-300 hover:text-cyan-200 hover:underline"
                href={repositoryDetailPath(profile.key)}
              >
                {profile.key}
              </a>
              <span class="mt-1 block max-w-sm break-all text-xs font-normal text-slate-500">
                {profile.cloneUrl}
              </span>
            </th>
            <td class="px-4 py-4 text-slate-300">{profile.defaultBranch}</td>
            <td class="px-4 py-4 text-slate-300">
              <span class="block">ADO: {profile.azureDevOpsEnvironmentKey ?? 'None'}</span>
              <span class="mt-1 block">Runtime: {profile.runtimeEnvironmentKey ?? 'None'}</span>
            </td>
            <td class="px-4 py-3">
              <div class="flex justify-end gap-1">
                <a
                  href={repositoryEditPath(profile.key)}
                  class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-slate-300 hover:bg-slate-800 hover:text-white"
                >
                  Edit
                </a>
                <Button
                  variant="ghost"
                  ariaLabel={`Delete ${profile.key}`}
                  onclick={() => ondelete(profile)}
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
