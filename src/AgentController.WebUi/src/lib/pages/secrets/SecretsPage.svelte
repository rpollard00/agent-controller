<script lang="ts">
  import { onDestroy } from 'svelte';
  import { getErrorMessage, getFieldErrors, type WebUiApiClient } from '../../api/client';
  import type { SecretInfo, SecretVersionInfo, CreateSecretRequest, CreateSecretVersionRequest } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Card from '../../components/ui/Card.svelte';
  import Dialog from '../../components/ui/Dialog.svelte';
  import SecretList from './SecretList.svelte';
  import SecretVersions from './SecretVersions.svelte';
  import SecretForm from './SecretForm.svelte';
  import {
    parseSecretRoute,
    secretDetailPath,
    secretVersionsPath,
    type SecretRoute,
  } from './secretRoutes';

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
  let secrets = $state<SecretInfo[]>([]);
  let versions = $state<SecretVersionInfo[]>([]);
  let versionTargetSecret = $state<SecretInfo>();
  let requestError = $state<unknown>();
  let mutationError = $state<unknown>();
  let submitting = $state(false);
  let deleteTarget = $state<SecretInfo>();
  let deleteError = $state<unknown>();
  let deleting = $state(false);
  let loadController: AbortController | undefined;
  let mutationController: AbortController | undefined;
  let successNotice = $state<{ path: string; title: string; message: string }>();

  const route = $derived(parseSecretRoute(pathname));
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

  function startLoad(currentRoute: SecretRoute): void {
    loadController?.abort();
    loadController = new AbortController();
    void loadRoute(currentRoute, loadController.signal);
  }

  async function loadRoute(
    currentRoute: SecretRoute,
    signal: AbortSignal,
  ): Promise<void> {
    status = 'loading';
    requestError = undefined;
    mutationError = undefined;
    versions = [];
    versionTargetSecret = undefined;

    try {
      if (currentRoute.view === 'list') {
        secrets = await client.secrets.list(signal);
        status = secrets.length > 0 ? 'ready' : 'empty';
        return;
      }

      if (currentRoute.view === 'create') {
        status = 'ready';
        return;
      }

      if (currentRoute.view === 'newVersion') {
        secrets = await client.secrets.list(signal);
        if (signal.aborted) return;

        versionTargetSecret = secrets.find((secret) => secret.name === currentRoute.name);
        if (!versionTargetSecret) {
          requestError = new Error('Secret not found.');
          status = 'error';
          return;
        }

        status = 'ready';
        return;
      }

      // Both detail routes show the secret metadata and complete version history.
      secrets = await client.secrets.list(signal);
      if (signal.aborted) return;

      const targetSecret = secrets.find((secret) => secret.name === currentRoute.name);
      if (!targetSecret) {
        requestError = new Error('Secret not found.');
        status = 'error';
        return;
      }

      versions = await client.secrets.listVersions(currentRoute.name, signal);
      if (signal.aborted) return;
      status = 'ready';
    } catch (error) {
      if (signal.aborted) return;
      requestError = error;
      status = 'error';
    }
  }

  async function createSecret(request: CreateSecretRequest): Promise<void> {
    if (!route || route.view !== 'create') return;

    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    submitting = true;
    mutationError = undefined;

    try {
      const created = await client.secrets.create(request, controller.signal);
      const nextPath = secretDetailPath(created.name);
      successNotice = {
        path: nextPath,
        title: 'Secret created',
        message: `Secret "${created.name}" was created with version 1.`,
      };
      navigate(nextPath);
    } catch (error) {
      if (controller.signal.aborted) return;
      mutationError = error;
    } finally {
      if (mutationController === controller) submitting = false;
    }
  }

  async function createVersion(name: string, request: CreateSecretVersionRequest): Promise<void> {
    if (!route || route.view !== 'newVersion') return;

    mutationController?.abort();
    const controller = new AbortController();
    mutationController = controller;
    submitting = true;
    mutationError = undefined;

    try {
      const created = await client.secrets.createVersion(name, request, controller.signal);
      const nextPath = secretVersionsPath(created.name);
      successNotice = {
        path: nextPath,
        title: 'Version created',
        message: `Version ${created.version} of "${created.name}" was created.`,
      };
      navigate(nextPath);
    } catch (error) {
      if (controller.signal.aborted) return;
      mutationError = error;
    } finally {
      if (mutationController === controller) submitting = false;
    }
  }

  function askToDelete(secret: SecretInfo): void {
    deleteTarget = secret;
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
      await client.secrets.delete(target.name, controller.signal);
      deleteTarget = undefined;

      successNotice = {
        path: '/secrets',
        title: 'Secret deleted',
        message: `Secret "${target.name}" was deleted.`,
      };

      if (route?.view === 'detail' || route?.view === 'versions') {
        navigate('/secrets');
      } else {
        secrets = secrets.filter((secret) => secret.name !== target.name);
        status = secrets.length > 0 ? 'ready' : 'empty';
      }
    } catch (error) {
      if (controller.signal.aborted) return;
      deleteError = error;
    } finally {
      if (mutationController === controller) deleting = false;
    }
  }

  function cancelForm(): void {
    if (route?.view === 'newVersion') navigate(secretVersionsPath(route.name));
    else if (route?.view === 'detail') navigate(secretDetailPath(route.name));
    else navigate('/secrets');
  }

  function validationMessages(error: unknown): string[] {
    return Object.entries(getFieldErrors(error)).flatMap(([field, messages]) =>
      messages.map((message) => `${field}: ${message}`),
    );
  }

  function pageTitle(currentRoute: SecretRoute | undefined): string {
    if (!currentRoute || currentRoute.view === 'list') return 'Secrets';
    if (currentRoute.view === 'create') return 'Add secret';
    if (currentRoute.view === 'newVersion') return `New version — ${currentRoute.name}`;
    if (currentRoute.view === 'versions') return `Versions — ${currentRoute.name}`;
    return currentRoute.name;
  }

  function pageDescription(currentRoute: SecretRoute | undefined): string {
    if (!currentRoute || currentRoute.view === 'list') {
      return 'Manage named, versioned, encrypted-at-rest secrets for integrated platforms.';
    }
    if (currentRoute.view === 'create') {
      return 'Create a new secret. The value is encrypted and stored securely.';
    }
    if (currentRoute.view === 'newVersion') {
      return versionTargetSecret?.secretType === 'ssh-key'
        ? 'Enter a new SSH key pair to create the next version. Re-enter the complete private key, public key, and passphrase. Stored values are never displayed.'
        : 'Enter a new token value to create the next version. Stored values are never displayed.';
    }
    if (currentRoute.view === 'versions') {
      return 'Version history for this secret. Values are encrypted at rest and never displayed.';
    }
    return 'Secret details and version history.';
  }
</script>

<div class="space-y-8">
  <div class="flex flex-col gap-5 sm:flex-row sm:items-end sm:justify-between">
    <div class="max-w-3xl">
      <p class="text-sm font-semibold tracking-widest text-cyan-300 uppercase">Configuration</p>
      <h1 class="mt-2 break-words text-3xl font-semibold tracking-tight text-white sm:text-4xl">
        {title}
      </h1>
      <p class="mt-3 text-base leading-7 text-slate-300">{description}</p>
    </div>
    {#if route?.view === 'list'}
      <a
        href="/secrets/new"
        class="inline-flex min-h-11 shrink-0 items-center justify-center rounded-lg bg-cyan-400 px-4 py-2 text-sm font-semibold text-slate-950 transition-colors hover:bg-cyan-300"
      >
        Add secret
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
        Loading {route?.view === 'list' ? 'secrets' : 'secret'}…
      </div>
    </Card>
  {:else if status === 'error'}
    <Card>
      <div class="space-y-4">
        <Alert
          variant="error"
          title={route?.view === 'list'
            ? 'Could not load secrets'
            : 'Could not load secret'}
          message={getErrorMessage(requestError)}
          errors={loadMessages}
        />
        <div class="flex flex-wrap gap-3">
          <Button variant="secondary" onclick={() => route && startLoad(route)}>Try again</Button>
          {#if route?.view !== 'list'}
            <a
              href="/secrets"
              class="inline-flex min-h-10 items-center rounded-lg px-4 py-2 text-sm font-semibold text-slate-300 hover:bg-slate-800 hover:text-white"
            >
              Back to secrets
            </a>
          {/if}
        </div>
      </div>
    </Card>
  {:else if route?.view === 'list'}
    <SecretList
      {secrets}
      empty={status === 'empty'}
      onrefresh={() => startLoad(route)}
      ondelete={askToDelete}
    />
  {:else if route?.view === 'create'}
    <Card
      title="Secret configuration"
      description="The value is encrypted at rest and never displayed after storage."
    >
      {#if mutationError}
        <div class="mb-6">
          <Alert
            variant="error"
            title="Could not create secret"
            message={getErrorMessage(mutationError)}
            errors={mutationMessages}
          />
        </div>
      {/if}
      <SecretForm
        mode="create"
        {submitting}
        serverErrors={getFieldErrors(mutationError)}
        onsave={(request) => {
          // Create mode always submits a CreateSecretRequest; narrow the form's
          // union type so the secret name is guaranteed present.
          if ('name' in request) void createSecret(request);
        }}
        oncancel={cancelForm}
      />
    </Card>
  {:else if route?.view === 'newVersion'}
    <Card
      title="New secret version"
      description="Stored values are never displayed. Enter the new value below."
    >
      {#if mutationError}
        <div class="mb-6">
          <Alert
            variant="error"
            title="Could not create version"
            message={getErrorMessage(mutationError)}
            errors={mutationMessages}
          />
        </div>
      {/if}
      <SecretForm
        mode="newVersion"
        secretType={versionTargetSecret?.secretType}
        {submitting}
        serverErrors={getFieldErrors(mutationError)}
        onsave={(request) => void createVersion(route.name, request)}
        oncancel={cancelForm}
      />
    </Card>
  {:else if (route?.view === 'detail' || route?.view === 'versions')}
    <SecretVersions
      name={route.name}
      {versions}
      {secrets}
      ondelete={askToDelete}
    />
  {/if}
</div>

<Dialog
  open={Boolean(deleteTarget)}
  title="Delete secret?"
  description="A secret referenced by a connection or repository cannot be deleted."
  onclose={closeDeleteDialog}
>
  <p class="text-sm leading-6 text-slate-300">
    You are about to delete <strong class="font-semibold text-white">
      {deleteTarget?.name}
    </strong>. This action cannot be undone.
  </p>
  {#if deleteError}
    <div class="mt-4">
      <Alert
        variant="error"
        title="Could not delete secret"
        message={getErrorMessage(deleteError)}
        errors={deleteMessages}
      />
    </div>
  {/if}
  {#snippet actions()}
    <Button variant="secondary" onclick={closeDeleteDialog} disabled={deleting}>Cancel</Button>
    <Button variant="danger" onclick={() => void confirmDelete()} disabled={deleting}>
      {deleting ? 'Deleting…' : 'Delete secret'}
    </Button>
  {/snippet}
</Dialog>
