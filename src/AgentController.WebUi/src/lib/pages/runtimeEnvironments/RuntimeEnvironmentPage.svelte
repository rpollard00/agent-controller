<script lang="ts">
  import { onDestroy } from 'svelte';
  import { getErrorMessage, getFieldErrors, type WebUiApiClient } from '../../api/client';
  import type { RuntimeEnvironmentProfile } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import Dialog from '../../components/ui/Dialog.svelte';
  import RuntimeEnvironmentDetails from './RuntimeEnvironmentDetails.svelte';
  import RuntimeEnvironmentForm from './RuntimeEnvironmentForm.svelte';
  import RuntimeEnvironmentList from './RuntimeEnvironmentList.svelte';
  import {
    parseRuntimeEnvironmentRoute,
    runtimeEnvironmentDetailPath,
    type RuntimeEnvironmentRoute,
  } from './runtimeEnvironmentRoutes';

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
  let environments = $state<RuntimeEnvironmentProfile[]>([]);
  let environment = $state<RuntimeEnvironmentProfile>();
  let requestError = $state<unknown>();
  let mutationError = $state<unknown>();
  let submitting = $state(false);
  let updatingKey = $state<string>();
  let deleteTarget = $state<RuntimeEnvironmentProfile>();
  let deleteError = $state<unknown>();
  let deleting = $state(false);
  let loadController: AbortController | undefined;
  let mutationController: AbortController | undefined;
  let successNotice = $state<{ path: string; title: string; message: string }>();

  const route = $derived(parseRuntimeEnvironmentRoute(pathname));
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

  function startLoad(currentRoute: RuntimeEnvironmentRoute): void {
    loadController?.abort();
    loadController = new AbortController();
    void loadRoute(currentRoute, loadController.signal);
  }

  async function loadRoute(
    currentRoute: RuntimeEnvironmentRoute,
    signal: AbortSignal,
  ): Promise<void> {
    status = 'loading';
    requestError = undefined;
    mutationError = undefined;
    environment = undefined;

    try {
      if (currentRoute.view === 'list') {
        environments = await client.runtimeEnvironments.list(signal);
        status = environments.length > 0 ? 'ready' : 'empty';
        return;
      }

      if (currentRoute.view === 'create') {
        status = 'ready';
        return;
      }

      environment = await client.runtimeEnvironments.get(currentRoute.key, signal);
      status = 'ready';
    } catch (error) {
      if (signal.aborted) return;
      requestError = error;
      status = 'error';
    }
  }

  async function saveEnvironment(profile: RuntimeEnvironmentProfile): Promise<void> {
    if (!route || (route.view !== 'create' && route.view !== 'edit')) return;

    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    submitting = true;
    mutationError = undefined;

    try {
      if (route.view === 'create') {
        const created = await client.runtimeEnvironments.create(profile, controller.signal);
        const nextPath = runtimeEnvironmentDetailPath(created.key);
        successNotice = {
          path: nextPath,
          title: 'Environment created',
          message: `Runtime environment “${created.displayName}” was created.`,
        };
        navigate(nextPath);
      } else {
        const updated = await client.runtimeEnvironments.update(
          route.key,
          profile,
          controller.signal,
        );
        environment = updated;
        successNotice = {
          path: pathname,
          title: 'Environment updated',
          message: `Runtime environment “${updated.displayName}” was updated.`,
        };
      }
    } catch (error) {
      if (controller.signal.aborted) return;
      mutationError = error;
    } finally {
      if (mutationController === controller) submitting = false;
    }
  }

  async function toggleEnvironment(profile: RuntimeEnvironmentProfile): Promise<void> {
    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    updatingKey = profile.key;
    mutationError = undefined;

    try {
      const updated = await client.runtimeEnvironments.update(
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
        message: `Runtime environment “${updated.displayName}” is now ${
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

  function askToDelete(profile: RuntimeEnvironmentProfile): void {
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
      await client.runtimeEnvironments.delete(target.key, controller.signal);
      deleteTarget = undefined;

      if (route?.view === 'detail' || route?.view === 'edit') {
        successNotice = {
          path: '/runtime-environments',
          title: 'Environment deleted',
          message: `Runtime environment “${target.displayName}” was deleted.`,
        };
        navigate('/runtime-environments');
      } else {
        environments = environments.filter((profile) => profile.key !== target.key);
        status = environments.length > 0 ? 'ready' : 'empty';
        successNotice = {
          path: '/runtime-environments',
          title: 'Environment deleted',
          message: `Runtime environment “${target.displayName}” was deleted.`,
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
    if (route?.view === 'edit') navigate(runtimeEnvironmentDetailPath(route.key));
    else navigate('/runtime-environments');
  }

  function validationMessages(error: unknown): string[] {
    return Object.entries(getFieldErrors(error)).flatMap(([field, messages]) =>
      messages.map((message) => `${field}: ${message}`),
    );
  }

  function pageTitle(currentRoute: RuntimeEnvironmentRoute | undefined): string {
    if (!currentRoute || currentRoute.view === 'list') return 'Runtime Environments';
    if (currentRoute.view === 'create') return 'Add runtime environment';
    if (currentRoute.view === 'edit') return `Edit ${currentRoute.key}`;
    return currentRoute.key;
  }

  function pageDescription(currentRoute: RuntimeEnvironmentRoute | undefined): string {
    if (!currentRoute || currentRoute.view === 'list') {
      return 'Manage workspace providers and the agent runtimes that execute work.';
    }
    if (currentRoute.view === 'create') {
      return 'Configure an isolated workspace and select how agent work will run.';
    }
    if (currentRoute.view === 'edit') {
      return 'Update workspace, runtime provider, and loadout mapping settings.';
    }
    return 'Review workspace configuration, the selected runtime, and loadout mappings.';
  }
</script>

<div class="space-y-8">
  <div class="flex flex-col gap-5 sm:flex-row sm:items-end sm:justify-between">
    <div class="max-w-3xl">
      <p class="text-sm font-semibold tracking-widest text-cyan-300 uppercase">Execution</p>
      <h1 class="mt-2 break-words text-3xl font-semibold tracking-tight text-white sm:text-4xl">
        {title}
      </h1>
      <p class="mt-3 text-base leading-7 text-slate-300">{description}</p>
    </div>
    {#if route?.view === 'list'}
      <a
        href="/runtime-environments/new"
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
        Loading {route?.view === 'list' ? 'runtime environments' : 'environment'}…
      </div>
    </Card>
  {:else if status === 'error'}
    <Card>
      <div class="space-y-4">
        <Alert
          variant="error"
          title={route?.view === 'list'
            ? 'Could not load runtime environments'
            : 'Could not load runtime environment'}
          message={getErrorMessage(requestError)}
          errors={loadMessages}
        />
        <div class="flex flex-wrap gap-3">
          <Button variant="secondary" onclick={() => route && startLoad(route)}>Try again</Button>
          {#if route?.view !== 'list'}
            <a
              href="/runtime-environments"
              class="inline-flex min-h-10 items-center rounded-lg px-4 py-2 text-sm font-semibold text-slate-300 hover:bg-slate-800 hover:text-white"
            >
              Back to runtime environments
            </a>
          {/if}
        </div>
      </div>
    </Card>
  {:else if route?.view === 'list'}
    <RuntimeEnvironmentList
      {environments}
      empty={status === 'empty'}
      {updatingKey}
      onrefresh={() => startLoad(route)}
      ontoggle={(profile) => void toggleEnvironment(profile)}
      ondelete={askToDelete}
    />
  {:else if route?.view === 'create'}
    <Card
      title="Runtime environment configuration"
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
      <RuntimeEnvironmentForm
        mode="create"
        {submitting}
        serverErrors={getFieldErrors(mutationError)}
        onsave={(profile) => void saveEnvironment(profile)}
        oncancel={cancelForm}
      />
    </Card>
  {:else if route?.view === 'edit' && environment}
    <Card
      title="Runtime environment configuration"
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
        <RuntimeEnvironmentForm
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
    <RuntimeEnvironmentDetails
      {environment}
      updating={updatingKey === environment.key}
      ontoggle={(profile) => void toggleEnvironment(profile)}
      ondelete={askToDelete}
    />
  {/if}
</div>

<Dialog
  open={Boolean(deleteTarget)}
  title="Delete runtime environment?"
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
