<script lang="ts">
  import { onDestroy } from 'svelte';
  import type { WebUiApiClient } from '../../api/client';
  import type { RepositoryHostConnectivityResult, RepositoryHostConnectionProfile } from '../../api/types';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import DataTable from '../../components/ui/DataTable.svelte';
  import {
    repositoryHostConnectionDetailPath,
    repositoryHostConnectionEditPath,
    repositoryHostConnectionRepoPickerPath,
  } from './repositoryHostConnectionRoutes';

  let {
    connections,
    empty,
    client,
    updatingKey,
    onrefresh,
    ontoggle,
    ondelete,
  }: {
    connections: RepositoryHostConnectionProfile[];
    empty: boolean;
    client: WebUiApiClient;
    updatingKey?: string;
    onrefresh: () => void;
    ontoggle: (profile: RepositoryHostConnectionProfile) => void;
    ondelete: (profile: RepositoryHostConnectionProfile) => void;
  } = $props();

  let verifyLoading = $state<Record<string, boolean>>({});
  let verifyResults = $state<Record<string, RepositoryHostConnectivityResult | null>>({});
  let verifyControllers = new Map<string, AbortController>();

  function getVerifyState(key: string): {
    loading: boolean;
    result: RepositoryHostConnectivityResult | null;
  } {
    return {
      loading: verifyLoading[key] ?? false,
      result: verifyResults[key] ?? null,
    };
  }

  function setVerifyLoading(key: string, loading: boolean): void {
    verifyLoading[key] = loading;
  }

  function setVerifyResult(key: string, result: RepositoryHostConnectivityResult | null): void {
    verifyResults[key] = result;
  }

  async function testConnection(profile: RepositoryHostConnectionProfile): Promise<void> {
    const key = profile.key;

    verifyControllers.get(key)?.abort();
    const controller = new AbortController();
    verifyControllers.set(key, controller);
    setVerifyLoading(key, true);
    setVerifyResult(key, null);

    try {
      const result = await client.repositoryHostConnections.verifyConnection(
        key,
        controller.signal,
      );
      if (!controller.signal.aborted) {
        setVerifyResult(key, result);
      }
    } catch (error) {
      if (!controller.signal.aborted) {
        setVerifyResult(key, {
          success: false,
          authMechanism: '',
          errors: [error instanceof Error ? error.message : 'Connection test failed.'],
        });
      }
    } finally {
      if (verifyControllers.get(key) === controller) {
        setVerifyLoading(key, false);
        verifyControllers.delete(key);
      }
    }
  }

  function getRepoCount(result: RepositoryHostConnectivityResult | null): number | undefined {
    if (!result?.payload) return undefined;
    const repos = result.payload.repositories;
    if (Array.isArray(repos)) return repos.length;
    return undefined;
  }


  onDestroy(() => {
    for (const controller of verifyControllers.values()) {
      controller.abort();
    }
  });
</script>

<Card
  title="Repository host connections"
  description="Connected APIs used as sources of repositories, decoupled from work sources."
>
  {#snippet actions()}
    <Button variant="secondary" onclick={onrefresh}>Refresh</Button>
  {/snippet}

  {#if empty}
    <div class="rounded-xl border border-dashed border-slate-700 px-5 py-12 text-center">
      <h2 class="font-semibold text-white">No repository host connections yet</h2>
      <p class="mx-auto mt-2 max-w-lg text-sm leading-6 text-slate-400">
        Add a connection to browse and onboard repositories from a remote host like Azure DevOps Repos.
      </p>
      <a
        href="/repository-host-connections/new"
        class="mt-5 inline-flex min-h-11 items-center justify-center rounded-lg bg-cyan-400 px-4 py-2 text-sm font-semibold text-slate-950 hover:bg-cyan-300"
      >
        Add your first connection
      </a>
    </div>
  {:else}
    <DataTable caption="Repository host connections">
      <thead class="bg-slate-950/60 text-xs tracking-wide text-slate-400 uppercase">
        <tr>
          <th class="px-4 py-3 font-medium" scope="col">Connection</th>
          <th class="px-4 py-3 font-medium" scope="col">Project</th>
          <th class="px-4 py-3 font-medium" scope="col">Provider</th>
          <th class="px-4 py-3 font-medium" scope="col">Status</th>
          <th class="px-4 py-3 text-right font-medium" scope="col">Actions</th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-800">
        {#each connections as profile (profile.key)}
          <tr class="align-top">
            <th class="px-4 py-4 font-medium" scope="row">
              <a
                class="text-cyan-300 hover:text-cyan-200 hover:underline"
                href={repositoryHostConnectionDetailPath(profile.key)}
              >
                {profile.displayName}
              </a>
              <span class="mt-1 block break-all text-xs font-normal text-slate-500">
                {profile.key}
              </span>
            </th>
            <td class="px-4 py-4 text-slate-300">
              <span class="block">{profile.project}</span>
              <span class="mt-1 block max-w-xs break-all text-xs text-slate-500">
                {profile.organizationUrl}
              </span>
            </td>
            <td class="px-4 py-4 text-slate-300">{profile.provider}</td>
            <td class="px-4 py-4">
              <span
                class={`inline-flex rounded-full px-2.5 py-1 text-xs font-semibold ${
                  profile.enabled
                    ? 'bg-emerald-950 text-emerald-300'
                    : 'bg-slate-800 text-slate-300'
                }`}
              >
                {profile.enabled ? 'Enabled' : 'Disabled'}
              </span>
            </td>
            <td class="px-4 py-3">
              <div class="flex min-w-max flex-wrap items-center justify-end gap-1">
                <Button
                  variant="ghost"
                  disabled={getVerifyState(profile.key).loading}
                  ariaLabel={`Test connection for ${profile.displayName}`}
                  onclick={() => void testConnection(profile)}
                >
                  {getVerifyState(profile.key).loading
                    ? 'Testing…'
                    : 'Test connection'}
                </Button>
                <Button
                  variant="ghost"
                  disabled={Boolean(updatingKey)}
                  ariaLabel={`${profile.enabled ? 'Disable' : 'Enable'} ${profile.displayName}`}
                  onclick={() => ontoggle(profile)}
                >
                  {updatingKey === profile.key
                    ? 'Saving…'
                    : profile.enabled
                      ? 'Disable'
                      : 'Enable'}
                </Button>
                <a
                  href={repositoryHostConnectionRepoPickerPath(profile.key)}
                  class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-slate-300 hover:bg-slate-800 hover:text-white"
                >
                  Browse repos
                </a>
                <a
                  href={repositoryHostConnectionEditPath(profile.key)}
                  class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-slate-300 hover:bg-slate-800 hover:text-white"
                >
                  Edit
                </a>
                <Button
                  variant="ghost"
                  disabled={Boolean(updatingKey)}
                  ariaLabel={`Delete ${profile.displayName}`}
                  onclick={() => ondelete(profile)}
                >
                  Delete
                </Button>
              </div>
              {#if getVerifyState(profile.key).loading}
                <div class="mt-2 flex items-center gap-2 text-xs text-slate-400" role="status">
                  <span
                    class="size-3 animate-spin rounded-full border border-slate-600 border-t-cyan-300"
                    aria-hidden="true"
                  ></span>
                  Testing connection…
                </div>
              {:else}
                {@const result = getVerifyState(profile.key).result}
                {#if result?.success}
                  <div class="mt-2">
                    <span class="inline-flex items-center gap-1 rounded-full bg-emerald-950 px-2.5 py-1 text-xs font-semibold text-emerald-300">
                      Connected
                      {#if getRepoCount(result) !== undefined}
                        <span class="text-emerald-400">({getRepoCount(result)} repos)</span>
                      {/if}
                    </span>
                  </div>
                {:else if result && !result.success}
                  <div class="mt-2 space-y-1">
                    <span class="inline-flex rounded-full bg-red-950 px-2.5 py-1 text-xs font-semibold text-red-300">
                      Failed
                    </span>
                    {#each result.errors as error}
                      <p class="text-xs text-red-400">{error}</p>
                    {/each}
                  </div>
                {/if}
              {/if}
            </td>
          </tr>
        {/each}
      </tbody>
    </DataTable>
  {/if}
</Card>
