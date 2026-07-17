<script lang="ts">
  import { onDestroy } from 'svelte';
  import { getErrorMessage, getFieldErrors, type WebUiApiClient } from '../../api/client';
  import type {
    ConnectionProfile,
    ConnectionProject,
    HostRepository,
    RepositoryProfile,
  } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import DataTable from '../../components/ui/DataTable.svelte';
  import Dialog from '../../components/ui/Dialog.svelte';
  import ConnectionDetails from './ConnectionDetails.svelte';
  import ConnectionForm from './ConnectionForm.svelte';
  import ConnectionList from './ConnectionList.svelte';
  import {
    parseConnectionRouteWithSearch,
    connectionDetailPath,
    type ConnectionRoute,
  } from './connectionRoutes';

  let {
    pathname,
    client,
    navigate,
  }: {
    pathname: string;
    client: WebUiApiClient;
    navigate: (path: string) => void;
  } = $props();

  let status = $state<'loading' | 'empty' | 'ready' | 'error'>('loading');
  let connections = $state<ConnectionProfile[]>([]);
  let connection = $state<ConnectionProfile>();
  let projects = $state<ConnectionProject[]>([]);
  let repositories = $state<HostRepository[]>([]);
  let requestError = $state<unknown>();
  let mutationError = $state<unknown>();
  let submitting = $state(false);
  let updatingKey = $state<string>();
  let deleteTarget = $state<ConnectionProfile>();
  let deleteError = $state<unknown>();
  let deleting = $state(false);
  let loadController: AbortController | undefined;
  let mutationController: AbortController | undefined;
  let successNotice = $state<{ path: string; title: string; message: string }>();
  let onboardingRepo = $state<string>();
  let onboardingError = $state<unknown>();
  let searchParams = $state('');

  const route = $derived(parseConnectionRouteWithSearch(pathname, searchParams));
  const title = $derived(pageTitle(route));
  const description = $derived(pageDescription(route));
  const visibleSuccessNotice = $derived(
    successNotice?.path === pathname ? successNotice : undefined,
  );
  const mutationMessages = $derived(validationMessages(mutationError));
  const loadMessages = $derived(validationMessages(requestError));
  const deleteMessages = $derived(validationMessages(deleteError));
  const onboardingMessages = $derived(validationMessages(onboardingError));

  $effect(() => {
    // Sync search params from URL on mount and navigation
    if (typeof window !== 'undefined') {
      searchParams = window.location.search;
    }
  });

  $effect(() => {
    const currentRoute = route;
    if (!currentRoute) return;

    startLoad(currentRoute);
    return () => loadController?.abort();
  });

  onDestroy(() => {
    loadController?.abort();
    mutationController?.abort();
  });

  function startLoad(currentRoute: ConnectionRoute): void {
    loadController?.abort();
    loadController = new AbortController();
    void loadRoute(currentRoute, loadController.signal);
  }

  async function loadRoute(
    currentRoute: ConnectionRoute,
    signal: AbortSignal,
  ): Promise<void> {
    status = 'loading';
    requestError = undefined;
    mutationError = undefined;
    connection = undefined;
    projects = [];
    repositories = [];

    try {
      if (currentRoute.view === 'list') {
        connections = await client.connections.list(signal);
        status = connections.length > 0 ? 'ready' : 'empty';
        return;
      }

      if (currentRoute.view === 'create') {
        status = 'ready';
        return;
      }

      if (currentRoute.view === 'repoPicker') {
        connection = await client.connections.get(currentRoute.connectionKey, signal);
        projects = await client.connections.listProjects(
          currentRoute.connectionKey,
          signal,
        );
        if (signal.aborted) return;
        // If a project is specified in the route, load repos for it
        if (currentRoute.project) {
          repositories = await client.connections.listRepositories(
            currentRoute.connectionKey,
            currentRoute.project,
            signal,
          );
        }
        status = 'ready';
        return;
      }

      connection = await client.connections.get(currentRoute.key, signal);
      status = 'ready';
    } catch (error) {
      if (signal.aborted) return;
      requestError = error;
      status = 'error';
    }
  }

  async function saveConnection(profile: ConnectionProfile): Promise<void> {
    if (!route || (route.view !== 'create' && route.view !== 'edit')) return;

    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    submitting = true;
    mutationError = undefined;

    try {
      if (route.view === 'create') {
        const created = await client.connections.create(profile, controller.signal);
        const nextPath = connectionDetailPath(created.key);
        successNotice = {
          path: nextPath,
          title: 'Connection created',
          message: `Connection "${created.displayName}" was created.`,
        };
        navigate(nextPath);
      } else {
        const updated = await client.connections.update(
          route.key,
          profile,
          controller.signal,
        );
        connection = updated;
        successNotice = {
          path: pathname,
          title: 'Connection updated',
          message: `Connection "${updated.displayName}" was updated.`,
        };
      }
    } catch (error) {
      if (controller.signal.aborted) return;
      mutationError = error;
    } finally {
      if (mutationController === controller) submitting = false;
    }
  }

  async function toggleConnection(profile: ConnectionProfile): Promise<void> {
    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    updatingKey = profile.key;
    mutationError = undefined;

    try {
      const updated = await client.connections.update(
        profile.key,
        { ...profile, enabled: !profile.enabled },
        controller.signal,
      );
      connections = connections.map((candidate) =>
        candidate.key === updated.key ? updated : candidate,
      );
      if (connection?.key === updated.key) connection = updated;
      successNotice = {
        path: pathname,
        title: updated.enabled ? 'Connection enabled' : 'Connection disabled',
        message: `Connection "${updated.displayName}" is now ${
          updated.enabled ? 'enabled' : 'disabled'
        }.`,
      };
    } catch (error) {
      if (controller.signal.aborted) return;
      mutationError = error;
    } finally {
      if (mutationController === controller) updatingKey = undefined;
    }
  }

  function askToDelete(profile: ConnectionProfile): void {
    deleteTarget = profile;
    deleteError = undefined;
  }

  function closeDeleteDialog(): void {
    if (deleting) return;
    deleteTarget = undefined;
    deleteError = undefined;
  }

  async function confirmDelete(): Promise<void> {
    const target = deleteTarget;
    if (!target) return;

    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    deleting = true;
    deleteError = undefined;

    try {
      await client.connections.delete(target.key, controller.signal);
      deleteTarget = undefined;

      if (route?.view === 'detail' || route?.view === 'edit' || route?.view === 'repoPicker') {
        successNotice = {
          path: '/connections',
          title: 'Connection deleted',
          message: `Connection "${target.displayName}" was deleted.`,
        };
        navigate('/connections');
      } else {
        connections = connections.filter((profile) => profile.key !== target.key);
        status = connections.length > 0 ? 'ready' : 'empty';
        successNotice = {
          path: '/connections',
          title: 'Connection deleted',
          message: `Connection "${target.displayName}" was deleted.`,
        };
      }
    } catch (error) {
      if (controller.signal.aborted) return;
      deleteError = error;
    } finally {
      if (mutationController === controller) deleting = false;
    }
  }

  async function loadRepositoriesForProject(projectId: string): Promise<void> {
    if (!route || route.view !== 'repoPicker') return;

    loadController?.abort();
    const controller = new AbortController();
    loadController = controller;
    status = 'loading';

    try {
      repositories = await client.connections.listRepositories(
        route.connectionKey,
        projectId,
        controller.signal,
      );
      status = 'ready';
    } catch (error) {
      if (controller.signal.aborted) return;
      requestError = error;
      status = 'error';
    }
  }

  async function onboardRepository(repo: HostRepository): Promise<void> {
    if (!route || route.view !== 'repoPicker' || !route.project) return;

    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    onboardingRepo = repo.id;
    onboardingError = undefined;

    try {
      const created = await client.connections.onboardRepository(
        route.connectionKey,
        route.project,
        repo.id,
        undefined,
        controller.signal,
      );
      successNotice = {
        path: `/repositories/${created.key}`,
        title: 'Repository onboarded',
        message: `Repository "${created.key}" was onboarded from host discovery.`,
      };
      navigate(`/repositories/${created.key}`);
    } catch (error) {
      if (controller.signal.aborted) return;
      onboardingError = error;
    } finally {
      if (mutationController === controller) onboardingRepo = undefined;
    }
  }

  function cancelForm(): void {
    if (route?.view === 'edit') navigate(connectionDetailPath(route.key));
    else if (route?.view === 'repoPicker') navigate('/connections');
    else navigate('/connections');
  }

  function validationMessages(error: unknown): string[] {
    return Object.entries(getFieldErrors(error)).flatMap(([field, messages]) =>
      messages.map((message) => `${field}: ${message}`),
    );
  }

  function pageTitle(currentRoute: ConnectionRoute | undefined): string {
    if (!currentRoute || currentRoute.view === 'list') return 'Connections';
    if (currentRoute.view === 'create') return 'Add connection';
    if (currentRoute.view === 'edit') return `Edit ${currentRoute.key}`;
    if (currentRoute.view === 'repoPicker') return `Browse repositories`;
    return currentRoute.key;
  }

  function pageDescription(currentRoute: ConnectionRoute | undefined): string {
    if (!currentRoute || currentRoute.view === 'list') {
      return 'Manage unified provider connections for repositories, work tracking, and runtime environments.';
    }
    if (currentRoute.view === 'create') {
      return 'Connect a provider by referencing a named secret or environment variable for credentials.';
    }
    if (currentRoute.view === 'edit') {
      return 'Update connection settings and credential references.';
    }
    if (currentRoute.view === 'repoPicker') {
      return 'Browse available repositories and onboard one with a single click.';
    }
    return 'Review the connection and credential reference for this provider.';
  }

  function transportLabel(hint: string): string {
    switch (hint) {
      case 'ssh': return 'SSH';
      case 'httpsPat': return 'HTTPS + PAT';
      default: return 'Automatic';
    }
  }
</script>

<div class="space-y-8">
  <div class="flex flex-col gap-5 sm:flex-row sm:items-end sm:justify-between">
    <div class="max-w-3xl">
      <p class="text-sm font-semibold tracking-widest text-cyan-300 uppercase">Connections</p>
      <h1 class="mt-2 break-words text-3xl font-semibold tracking-tight text-white sm:text-4xl">
        {title}
      </h1>
      <p class="mt-3 text-base leading-7 text-slate-300">{description}</p>
    </div>
    {#if route?.view === 'list'}
      <a
        href="/connections/new"
        class="inline-flex min-h-11 shrink-0 items-center justify-center rounded-lg bg-cyan-400 px-4 py-2 text-sm font-semibold text-slate-950 transition-colors hover:bg-cyan-300"
      >
        Add connection
      </a>
    {/if}
  </div>

  {#if visibleSuccessNotice}
    <Alert
      variant="success"
      title={visibleSuccessNotice.title}
      message={visibleSuccessNotice.message}
    />
  {/if}

  {#if mutationError && (route?.view === 'list' || route?.view === 'detail')}
    <Alert
      variant="error"
      title="Could not update connection"
      message={getErrorMessage(mutationError)}
      errors={mutationMessages}
    />
  {/if}

  {#if status === 'loading'}
    <Card>
      <div class="flex min-h-40 items-center justify-center gap-3 text-sm text-slate-300" role="status">
        <span
          class="size-4 animate-spin rounded-full border-2 border-slate-700 border-t-cyan-300"
          aria-hidden="true"
        ></span>
        Loading {route?.view === 'list' ? 'connections' : 'connection'}…
      </div>
    </Card>
  {:else if status === 'error'}
    <Card>
      <div class="space-y-4">
        <Alert
          variant="error"
          title={route?.view === 'list' ? 'Could not load connections' : 'Could not load connection'}
          message={getErrorMessage(requestError)}
          errors={loadMessages}
        />
        <div class="flex flex-wrap gap-3">
          <Button variant="secondary" onclick={() => route && startLoad(route)}>Try again</Button>
          {#if route?.view !== 'list'}
            <a
              href="/connections"
              class="inline-flex min-h-10 items-center rounded-lg px-4 py-2 text-sm font-semibold text-slate-300 hover:bg-slate-800 hover:text-white"
            >
              Back to Connections
            </a>
          {/if}
        </div>
      </div>
    </Card>
  {:else if route?.view === 'list'}
    <ConnectionList
      {connections}
      empty={status === 'empty'}
      {client}
      {updatingKey}
      onrefresh={() => startLoad(route)}
      ontoggle={(profile) => void toggleConnection(profile)}
      ondelete={askToDelete}
    />
  {:else if route?.view === 'create'}
    <Card
      title="Connection configuration"
      description="Required fields are marked with an asterisk."
    >
      {#if mutationError}
        <div class="mb-6">
          <Alert
            variant="error"
            title="Could not create connection"
            message={getErrorMessage(mutationError)}
            errors={mutationMessages}
          />
        </div>
      {/if}
      <ConnectionForm
        mode="create"
        {submitting}
        {client}
        serverErrors={getFieldErrors(mutationError)}
        onsave={(profile) => void saveConnection(profile)}
        oncancel={cancelForm}
      />
    </Card>
  {:else if route?.view === 'edit' && connection}
    <Card
      title="Connection configuration"
      description="The connection name is fixed after creation."
    >
      {#if mutationError}
        <div class="mb-6">
          <Alert
            variant="error"
            title="Could not update connection"
            message={getErrorMessage(mutationError)}
            errors={mutationMessages}
          />
        </div>
      {/if}
      {#key connection.key}
        <ConnectionForm
          mode="edit"
          profile={connection}
          {submitting}
          {client}
          serverErrors={getFieldErrors(mutationError)}
          onsave={(profile) => void saveConnection(profile)}
          oncancel={cancelForm}
        />
      {/key}
    </Card>
  {:else if route?.view === 'detail' && connection}
    <ConnectionDetails
      connection={connection}
      {client}
      updating={updatingKey === connection.key}
      ontoggle={(profile) => void toggleConnection(profile)}
      ondelete={askToDelete}
    />
  {:else if route?.view === 'repoPicker' && connection}
    <div class="space-y-6">
      {#if onboardingError}
        <Alert
          variant="error"
          title="Could not onboard repository"
          message={getErrorMessage(onboardingError)}
          errors={onboardingMessages}
        />
      {/if}

      <Card
        title="Available repositories"
        description={`Repositories discovered from ${connection.displayName} (${connection.provider}).`}
      >
        {#snippet actions()}
          <Button variant="secondary" onclick={() => startLoad(route)}>Refresh</Button>
        {/snippet}

        {#if projects.length > 0}
          <div class="mb-4 flex items-center gap-3">
            <label for="repo-picker-project" class="text-sm font-medium text-slate-300">Project:</label>
            <select
              id="repo-picker-project"
              class="min-h-9 rounded-lg border border-slate-700 bg-slate-950 px-3 py-1.5 text-sm text-slate-100"
              value={route.project ?? ''}
              onchange={(e) => {
                const selected = (e.target as HTMLSelectElement).value;
                if (selected) loadRepositoriesForProject(selected);
              }}
            >
              <option value="">Select a project…</option>
              {#each projects as project (project.id)}
                <option value={project.id}>{project.name}</option>
              {/each}
            </select>
          </div>
        {/if}

        {#if repositories.length === 0 && !route.project}
          <div class="rounded-xl border border-dashed border-slate-700 px-5 py-12 text-center">
            <h2 class="font-semibold text-white">Select a project</h2>
            <p class="mx-auto mt-2 max-w-lg text-sm leading-6 text-slate-400">
              Choose a project above to browse its repositories.
            </p>
          </div>
        {:else if repositories.length === 0}
          <div class="rounded-xl border border-dashed border-slate-700 px-5 py-12 text-center">
            <h2 class="font-semibold text-white">No repositories found</h2>
            <p class="mx-auto mt-2 max-w-lg text-sm leading-6 text-slate-400">
              The selected project returned no repositories. Verify the connection and try again.
            </p>
          </div>
        {:else}
          <DataTable caption="Discovered repositories">
            <thead class="bg-slate-950/60 text-xs tracking-wide text-slate-400 uppercase">
              <tr>
                <th class="px-4 py-3 font-medium" scope="col">Repository</th>
                <th class="px-4 py-3 font-medium" scope="col">Default branch</th>
                <th class="px-4 py-3 font-medium" scope="col">Transport</th>
                <th class="px-4 py-3 text-right font-medium" scope="col">Actions</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-800">
              {#each repositories as repo (repo.id)}
                <tr class="align-top">
                  <th class="px-4 py-4 font-medium" scope="row">
                    <span class="block text-slate-100">{repo.name}</span>
                    <span class="mt-1 block max-w-xs break-all text-xs text-slate-500">
                      {repo.remoteUrl}
                    </span>
                  </th>
                  <td class="px-4 py-4 text-slate-300">{repo.defaultBranch}</td>
                  <td class="px-4 py-4 text-slate-300">{transportLabel(repo.cloneTransportHint)}</td>
                  <td class="px-4 py-3 text-right">
                    <Button
                      variant="secondary"
                      disabled={Boolean(onboardingRepo)}
                      ariaLabel={`Onboard ${repo.name}`}
                      onclick={() => void onboardRepository(repo)}
                    >
                      {onboardingRepo === repo.id
                        ? 'Onboarding…'
                        : 'Onboard'}
                    </Button>
                  </td>
                </tr>
              {/each}
            </tbody>
          </DataTable>
        {/if}
      </Card>

      <a
        href={connectionDetailPath(connection.key)}
        class="inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-semibold text-cyan-300 hover:bg-slate-800 hover:text-cyan-200"
      >
        Back to {connection.displayName}
      </a>
    </div>
  {/if}
</div>

<Dialog
  open={Boolean(deleteTarget)}
  title="Delete connection?"
  description="A connection referenced by a work source or repository cannot be deleted."
  onclose={closeDeleteDialog}
>
  <p class="text-sm leading-6 text-slate-300">
    You are about to delete <strong class="font-semibold text-white">
      {deleteTarget?.displayName}
    </strong>. This action cannot be undone.
  </p>
  {#if deleteError}
    <div class="mt-4">
      <Alert
        variant="error"
        title="Could not delete connection"
        message={getErrorMessage(deleteError)}
        errors={deleteMessages}
      />
    </div>
  {/if}
  {#snippet actions()}
    <Button variant="secondary" onclick={closeDeleteDialog} disabled={deleting}>Cancel</Button>
    <Button variant="danger" onclick={() => void confirmDelete()} disabled={deleting}>
      {deleting ? 'Deleting…' : 'Delete connection'}
    </Button>
  {/snippet}
</Dialog>
