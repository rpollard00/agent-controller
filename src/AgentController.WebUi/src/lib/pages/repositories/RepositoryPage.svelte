<script lang="ts">
  import { onDestroy } from 'svelte';
  import { getErrorMessage, getFieldErrors, type WebUiApiClient } from '../../api/client';
  import type {
    ConnectionProfile,
    RepositoryProfile,
    RuntimeEnvironmentProfile,
  } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import Dialog from '../../components/ui/Dialog.svelte';
  import RepositoryDetails from './RepositoryDetails.svelte';
  import RepositoryForm from './RepositoryForm.svelte';
  import RepositoryList from './RepositoryList.svelte';
  import {
    parseRepositoryRoute,
    repositoryDetailPath,
    type RepositoryRoute,
  } from './repositoryRoutes';

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
  let repositories = $state<RepositoryProfile[]>([]);
  let repository = $state<RepositoryProfile>();
  let connections = $state<ConnectionProfile[]>([]);
  let runtimeEnvironments = $state<RuntimeEnvironmentProfile[]>([]);
  let requestError = $state<unknown>();
  let mutationError = $state<unknown>();
  let submitting = $state(false);
  let deleteTarget = $state<RepositoryProfile>();
  let deleteError = $state<unknown>();
  let deleting = $state(false);
  let loadController: AbortController | undefined;
  let mutationController: AbortController | undefined;
  let successNotice = $state<{ path: string; title: string; message: string }>();

  const route = $derived(parseRepositoryRoute(pathname));
  const title = $derived(pageTitle(route));
  const description = $derived(pageDescription(route));
  const visibleSuccessNotice = $derived(
    successNotice?.path === pathname ? successNotice : undefined,
  );
  const mutationMessages = $derived(validationMessages(mutationError));
  const loadMessages = $derived(validationMessages(requestError));
  const deleteMessages = $derived(validationMessages(deleteError));

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

  function startLoad(currentRoute: RepositoryRoute): void {
    loadController?.abort();
    loadController = new AbortController();
    void loadRoute(currentRoute, loadController.signal);
  }

  async function loadRoute(currentRoute: RepositoryRoute, signal: AbortSignal): Promise<void> {
    status = 'loading';
    requestError = undefined;
    mutationError = undefined;
    repository = undefined;

    try {
      if (currentRoute.view === 'list') {
        repositories = await client.repositories.list(signal);
        status = repositories.length > 0 ? 'ready' : 'empty';
        return;
      }

      if (currentRoute.view === 'create') {
        [connections, runtimeEnvironments] = await Promise.all([
          client.connections.list(signal),
          client.runtimeEnvironments.list(signal),
        ]);
        status = 'ready';
        return;
      }

      const profileRequest = client.repositories.get(currentRoute.key, signal);
      if (currentRoute.view === 'edit') {
        [repository, connections, runtimeEnvironments] = await Promise.all([
          profileRequest,
          client.connections.list(signal),
          client.runtimeEnvironments.list(signal),
        ]);
      } else {
        repository = await profileRequest;
      }
      status = 'ready';
    } catch (error) {
      if (signal.aborted) return;
      requestError = error;
      status = 'error';
    }
  }

  async function saveRepository(profile: RepositoryProfile): Promise<void> {
    if (!route || (route.view !== 'create' && route.view !== 'edit')) return;

    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    submitting = true;
    mutationError = undefined;

    try {
      if (route.view === 'create') {
        const created = await client.repositories.create(profile, controller.signal);
        const nextPath = repositoryDetailPath(created.key);
        successNotice = {
          path: nextPath,
          title: 'Repository onboarded',
          message: `Repository “${created.key}” was onboarded.`,
        };
        navigate(nextPath);
      } else {
        const updated = await client.repositories.update(
          route.key,
          profile,
          controller.signal,
        );
        repository = updated;
        successNotice = {
          path: pathname,
          title: 'Repository updated',
          message: `Repository “${updated.key}” was updated.`,
        };
      }
    } catch (error) {
      if (controller.signal.aborted) return;
      mutationError = error;
    } finally {
      if (mutationController === controller) submitting = false;
    }
  }

  function askToDelete(profile: RepositoryProfile): void {
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
      await client.repositories.delete(target.key, controller.signal);
      deleteTarget = undefined;

      if (route?.view === 'detail' || route?.view === 'edit') {
        successNotice = {
          path: '/repositories',
          title: 'Repository deleted',
          message: `Repository “${target.key}” was deleted.`,
        };
        navigate('/repositories');
      } else {
        repositories = repositories.filter((profile) => profile.key !== target.key);
        status = repositories.length > 0 ? 'ready' : 'empty';
        successNotice = {
          path: '/repositories',
          title: 'Repository deleted',
          message: `Repository “${target.key}” was deleted.`,
        };
      }
    } catch (error) {
      if (controller.signal.aborted) return;
      deleteError = error;
    } finally {
      if (mutationController === controller) deleting = false;
    }
  }

  function cancelForm(): void {
    if (route?.view === 'edit') navigate(repositoryDetailPath(route.key));
    else navigate('/repositories');
  }

  function validationMessages(error: unknown): string[] {
    return Object.entries(getFieldErrors(error)).flatMap(([field, messages]) =>
      messages.map((message) => `${field}: ${message}`),
    );
  }

  function pageTitle(currentRoute: RepositoryRoute | undefined): string {
    if (!currentRoute || currentRoute.view === 'list') return 'Repositories';
    if (currentRoute.view === 'create') return 'Onboard repository';
    if (currentRoute.view === 'edit') return `Edit ${currentRoute.key}`;
    return currentRoute.key;
  }

  function pageDescription(currentRoute: RepositoryRoute | undefined): string {
    if (!currentRoute || currentRoute.view === 'list') {
      return 'Onboard and configure source repositories for agent work.';
    }
    if (currentRoute.view === 'create') {
      return 'Connect a source repository and choose the managed environments it will use.';
    }
    if (currentRoute.view === 'edit') {
      return 'Update clone settings, path restrictions, and managed environment associations.';
    }
    return 'Review the source and environment configuration used for this repository.';
  }
</script>

<div class="space-y-8">
  <div class="flex flex-col gap-5 sm:flex-row sm:items-end sm:justify-between">
    <div class="max-w-3xl">
      <p class="text-sm font-semibold tracking-widest text-cyan-300 uppercase">Repositories</p>
      <h1 class="mt-2 break-words text-3xl font-semibold tracking-tight text-white sm:text-4xl">
        {title}
      </h1>
      <p class="mt-3 text-base leading-7 text-slate-300">{description}</p>
    </div>
    {#if route?.view === 'list'}
      <a
        href="/repositories/new"
        class="inline-flex min-h-11 shrink-0 items-center justify-center rounded-lg bg-cyan-400 px-4 py-2 text-sm font-semibold text-slate-950 transition-colors hover:bg-cyan-300"
      >
        Onboard repository
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

  {#if status === 'loading'}
    <Card>
      <div class="flex min-h-40 items-center justify-center gap-3 text-sm text-slate-300" role="status">
        <span
          class="size-4 animate-spin rounded-full border-2 border-slate-700 border-t-cyan-300"
          aria-hidden="true"
        ></span>
        Loading {route?.view === 'list' ? 'repositories' : 'repository'}…
      </div>
    </Card>
  {:else if status === 'error'}
    <Card>
      <div class="space-y-4">
        <Alert
          variant="error"
          title={route?.view === 'list' ? 'Could not load repositories' : 'Could not load repository'}
          message={getErrorMessage(requestError)}
          errors={loadMessages}
        />
        <div class="flex flex-wrap gap-3">
          <Button variant="secondary" onclick={() => route && startLoad(route)}>Try again</Button>
          {#if route?.view !== 'list'}
            <a
              href="/repositories"
              class="inline-flex min-h-10 items-center rounded-lg px-4 py-2 text-sm font-semibold text-slate-300 hover:bg-slate-800 hover:text-white"
            >
              Back to repositories
            </a>
          {/if}
        </div>
      </div>
    </Card>
  {:else if route?.view === 'list'}
    <RepositoryList
      {repositories}
      empty={status === 'empty'}
      onrefresh={() => startLoad(route)}
      ondelete={askToDelete}
    />
  {:else if route?.view === 'create'}
    <Card
      title="Repository configuration"
      description="Required fields are marked with an asterisk."
    >
      {#if mutationError}
        <div class="mb-6">
          <Alert
            variant="error"
            title="Could not onboard repository"
            message={getErrorMessage(mutationError)}
            errors={mutationMessages}
          />
        </div>
      {/if}
      <RepositoryForm
        mode="create"
        {connections}
        {runtimeEnvironments}
        {submitting}
        {client}
        serverErrors={getFieldErrors(mutationError)}
        onsave={(profile) => void saveRepository(profile)}
        oncancel={cancelForm}
      />
    </Card>
  {:else if route?.view === 'edit' && repository}
    <Card
      title="Repository configuration"
      description="The repository key is fixed after onboarding."
    >
      {#if mutationError}
        <div class="mb-6">
          <Alert
            variant="error"
            title="Could not update repository"
            message={getErrorMessage(mutationError)}
            errors={mutationMessages}
          />
        </div>
      {/if}
      {#key repository.key}
        <RepositoryForm
          mode="edit"
          profile={repository}
          {connections}
          {runtimeEnvironments}
          {submitting}
          {client}
          serverErrors={getFieldErrors(mutationError)}
          onsave={(profile) => void saveRepository(profile)}
          oncancel={cancelForm}
        />
      {/key}
    </Card>
  {:else if route?.view === 'detail' && repository}
    <RepositoryDetails {repository} ondelete={askToDelete} />
  {/if}
</div>

<Dialog
  open={Boolean(deleteTarget)}
  title="Delete repository?"
  description="This removes the managed repository profile. It does not delete the source repository."
  onclose={closeDeleteDialog}
>
  <p class="text-sm leading-6 text-slate-300">
    You are about to delete <strong class="font-semibold text-white">{deleteTarget?.key}</strong>.
    This action cannot be undone.
  </p>
  {#if deleteError}
    <div class="mt-4">
      <Alert
        variant="error"
        title="Could not delete repository"
        message={getErrorMessage(deleteError)}
        errors={deleteMessages}
      />
    </div>
  {/if}
  {#snippet actions()}
    <Button variant="secondary" onclick={closeDeleteDialog} disabled={deleting}>Cancel</Button>
    <Button variant="danger" onclick={() => void confirmDelete()} disabled={deleting}>
      {deleting ? 'Deleting…' : 'Delete repository'}
    </Button>
  {/snippet}
</Dialog>
