<script lang="ts">
  import { onDestroy } from 'svelte';
  import { getErrorMessage, getFieldErrors, type WebUiApiClient } from '../../api/client';
  import type { WorkSourceEnvironmentProfile } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import Dialog from '../../components/ui/Dialog.svelte';
  import WorkSourceEnvironmentDetails from './WorkSourceEnvironmentDetails.svelte';
  import WorkSourceEnvironmentForm from './WorkSourceEnvironmentForm.svelte';
  import WorkSourceEnvironmentList from './WorkSourceEnvironmentList.svelte';
  import {
    workSourceEnvironmentDetailPath,
    parseWorkSourceEnvironmentRoute,
    type WorkSourceEnvironmentRoute,
  } from './workSourceEnvironmentRoutes';

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
  let environments = $state<WorkSourceEnvironmentProfile[]>([]);
  let environment = $state<WorkSourceEnvironmentProfile>();
  let requestError = $state<unknown>();
  let mutationError = $state<unknown>();
  let submitting = $state(false);
  let updatingKey = $state<string>();
  let deleteTarget = $state<WorkSourceEnvironmentProfile>();
  let deleteError = $state<unknown>();
  let deleting = $state(false);
  let loadController: AbortController | undefined;
  let mutationController: AbortController | undefined;
  let successNotice = $state<{ path: string; title: string; message: string }>();

  const route = $derived(parseWorkSourceEnvironmentRoute(pathname));
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
    return () => {
      loadController?.abort();
    };
  });

  onDestroy(() => {
    loadController?.abort();
    mutationController?.abort();
  });

  function startLoad(currentRoute: WorkSourceEnvironmentRoute): void {
    loadController?.abort();
    loadController = new AbortController();
    void loadRoute(currentRoute, loadController.signal);
  }

  async function loadRoute(
    currentRoute: WorkSourceEnvironmentRoute,
    signal: AbortSignal,
  ): Promise<void> {
    status = 'loading';
    requestError = undefined;
    mutationError = undefined;
    environment = undefined;

    try {
      if (currentRoute.view === 'list') {
        environments = await client.workSourceEnvironments.list(signal);
        status = environments.length > 0 ? 'ready' : 'empty';
        return;
      }

      if (currentRoute.view === 'create') {
        status = 'ready';
        return;
      }

      environment = await client.workSourceEnvironments.get(currentRoute.key, signal);
      status = 'ready';
    } catch (error) {
      if (signal.aborted) return;
      requestError = error;
      status = 'error';
    }
  }

  async function saveEnvironment(profile: WorkSourceEnvironmentProfile): Promise<void> {
    if (!route || (route.view !== 'create' && route.view !== 'edit')) return;

    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    submitting = true;
    mutationError = undefined;

    try {
      if (route.view === 'create') {
        const created = await client.workSourceEnvironments.create(profile, controller.signal);
        const nextPath = workSourceEnvironmentDetailPath(created.key);
        successNotice = {
          path: nextPath,
          title: 'Environment created',
          message: `Work source environment "${created.displayName}" was created.`,
        };
        navigate(nextPath);
      } else {
        const updated = await client.workSourceEnvironments.update(
          route.key,
          profile,
          controller.signal,
        );
        environment = updated;
        successNotice = {
          path: pathname,
          title: 'Environment updated',
          message: `Work source environment "${updated.displayName}" was updated.`,
        };
      }
    } catch (error) {
      if (controller.signal.aborted) return;
      mutationError = error;
    } finally {
      if (mutationController === controller) submitting = false;
    }
  }

  async function toggleEnvironment(profile: WorkSourceEnvironmentProfile): Promise<void> {
    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    updatingKey = profile.key;
    mutationError = undefined;

    try {
      const updated = await client.workSourceEnvironments.update(
        profile.key,
        { ...profile, enabled: !profile.enabled },
        controller.signal,
      );
      environments = environments.map((candidate) =>
        candidate.key === updated.key ? updated : candidate,
      );
      if (environment?.key === updated.key) environment = updated;
      successNotice = {
        path: pathname,
        title: updated.enabled ? 'Environment enabled' : 'Environment disabled',
        message: `Work source environment "${updated.displayName}" is now ${
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

  function askToDelete(profile: WorkSourceEnvironmentProfile): void {
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
      await client.workSourceEnvironments.delete(target.key, controller.signal);
      deleteTarget = undefined;

      if (route?.view === 'detail' || route?.view === 'edit') {
        successNotice = {
          path: '/work-source-environments',
          title: 'Environment deleted',
          message: `Work source environment "${target.displayName}" was deleted.`,
        };
        navigate('/work-source-environments');
      } else {
        environments = environments.filter((profile) => profile.key !== target.key);
        status = environments.length > 0 ? 'ready' : 'empty';
        successNotice = {
          path: '/work-source-environments',
          title: 'Environment deleted',
          message: `Work source environment "${target.displayName}" was deleted.`,
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
    if (route?.view === 'edit') navigate(workSourceEnvironmentDetailPath(route.key));
    else navigate('/work-source-environments');
  }

  function validationMessages(error: unknown): string[] {
    return Object.entries(getFieldErrors(error)).flatMap(([field, messages]) =>
      messages.map((message) => `${field}: ${message}`),
    );
  }

  function pageTitle(currentRoute: WorkSourceEnvironmentRoute | undefined): string {
    if (!currentRoute || currentRoute.view === 'list') return 'Work source environments';
    if (currentRoute.view === 'create') return 'Add work source environment';
    if (currentRoute.view === 'edit') return `Edit ${currentRoute.key}`;
    return currentRoute.key;
  }

  function pageDescription(currentRoute: WorkSourceEnvironmentRoute | undefined): string {
    if (!currentRoute || currentRoute.view === 'list') {
      return 'Manage work source connections, credential references, and board policies.';
    }
    if (currentRoute.view === 'create') {
      return 'Connect a work source without storing its personal access token.';
    }
    if (currentRoute.view === 'edit') {
      return 'Update connection settings, work-selection rules, and environment availability.';
    }
    return 'Review the connection, board policy, and credential reference for this environment.';
  }
</script>

<div class="space-y-8">
  <div class="flex flex-col gap-5 sm:flex-row sm:items-end sm:justify-between">
    <div class="max-w-3xl">
      <p class="text-sm font-semibold tracking-widest text-cyan-300 uppercase">Work source environments</p>
      <h1 class="mt-2 break-words text-3xl font-semibold tracking-tight text-white sm:text-4xl">
        {title}
      </h1>
      <p class="mt-3 text-base leading-7 text-slate-300">{description}</p>
    </div>
    {#if route?.view === 'list'}
      <a
        href="/work-source-environments/new"
        class="inline-flex min-h-11 shrink-0 items-center justify-center rounded-lg bg-cyan-400 px-4 py-2 text-sm font-semibold text-slate-950 transition-colors hover:bg-cyan-300"
      >
        Add environment
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
      title="Could not update environment"
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
        Loading {route?.view === 'list' ? 'work source environments' : 'environment'}…
      </div>
    </Card>
  {:else if status === 'error'}
    <Card>
      <div class="space-y-4">
        <Alert
          variant="error"
          title={route?.view === 'list'
            ? 'Could not load work source environments'
            : 'Could not load work source environment'}
          message={getErrorMessage(requestError)}
          errors={loadMessages}
        />
        <div class="flex flex-wrap gap-3">
          <Button variant="secondary" onclick={() => route && startLoad(route)}>Try again</Button>
          {#if route?.view !== 'list'}
            <a
              href="/work-source-environments"
              class="inline-flex min-h-10 items-center rounded-lg px-4 py-2 text-sm font-semibold text-slate-300 hover:bg-slate-800 hover:text-white"
            >
              Back to Work source environments
            </a>
          {/if}
        </div>
      </div>
    </Card>
  {:else if route?.view === 'list'}
    <WorkSourceEnvironmentList
      {environments}
      empty={status === 'empty'}
      {client}
      {updatingKey}
      onrefresh={() => startLoad(route)}
      ontoggle={(profile) => void toggleEnvironment(profile)}
      ondelete={askToDelete}
    />
  {:else if route?.view === 'create'}
    <Card
      title="Work source environment configuration"
      description="Required fields are marked with an asterisk."
    >
      {#if mutationError}
        <div class="mb-6">
          <Alert
            variant="error"
            title="Could not create environment"
            message={getErrorMessage(mutationError)}
            errors={mutationMessages}
          />
        </div>
      {/if}
      <WorkSourceEnvironmentForm
        mode="create"
        {submitting}
        serverErrors={getFieldErrors(mutationError)}
        onsave={(profile) => void saveEnvironment(profile)}
        oncancel={cancelForm}
      />
    </Card>
  {:else if route?.view === 'edit' && environment}
    <Card
      title="Work source environment configuration"
      description="The environment name is fixed after creation."
    >
      {#if mutationError}
        <div class="mb-6">
          <Alert
            variant="error"
            title="Could not update environment"
            message={getErrorMessage(mutationError)}
            errors={mutationMessages}
          />
        </div>
      {/if}
      {#key environment.key}
        <WorkSourceEnvironmentForm
          mode="edit"
          profile={environment}
          {submitting}
          serverErrors={getFieldErrors(mutationError)}
          onsave={(profile) => void saveEnvironment(profile)}
          oncancel={cancelForm}
        />
      {/key}
    </Card>
  {:else if route?.view === 'detail' && environment}
    <WorkSourceEnvironmentDetails
      {environment}
      {client}
      updating={updatingKey === environment.key}
      ontoggle={(profile) => void toggleEnvironment(profile)}
      ondelete={askToDelete}
    />
  {/if}
</div>

<Dialog
  open={Boolean(deleteTarget)}
  title="Delete work source environment?"
  description="An environment referenced by a repository cannot be deleted."
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
        title="Could not delete environment"
        message={getErrorMessage(deleteError)}
        errors={deleteMessages}
      />
    </div>
  {/if}
  {#snippet actions()}
    <Button variant="secondary" onclick={closeDeleteDialog} disabled={deleting}>Cancel</Button>
    <Button variant="danger" onclick={() => void confirmDelete()} disabled={deleting}>
      {deleting ? 'Deleting…' : 'Delete environment'}
    </Button>
  {/snippet}
</Dialog>
