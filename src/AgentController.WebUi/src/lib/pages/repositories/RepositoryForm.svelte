<script lang="ts">
  import { untrack } from 'svelte';
  import type {
    ConnectionProfile,
    ConnectionProject,
    RepositoryProfile,
    RuntimeEnvironmentProfile,
  } from '../../api/types';
  import { type WebUiApiClient } from '../../api/client';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Field from '../../components/ui/Field.svelte';
  import SecretPicker from '../../components/ui/SecretPicker.svelte';
  import {
    createRepositoryFormValues,
    inferCloneTransport,
    requiresSshKey,
    resolveRepositoryFormTransport,
    toRepositoryProfile,
    validateRepositoryForm,
    type RepositoryFormErrors,
  } from './repositoryForm';

  let {
    mode,
    profile,
    connections,
    runtimeEnvironments,
    submitting = false,
    serverErrors = {},
    onsave,
    client,
    oncancel,
  }: {
    mode: 'create' | 'edit';
    profile?: RepositoryProfile;
    connections: ConnectionProfile[];
    runtimeEnvironments: RuntimeEnvironmentProfile[];
    submitting?: boolean;
    serverErrors?: Readonly<Record<string, string[]>>;
    onsave: (profile: RepositoryProfile) => void;
    client: WebUiApiClient;
    oncancel: () => void;
  } = $props();

  let values = $state(untrack(() => createRepositoryFormValues(profile)));
  let clientErrors = $state<RepositoryFormErrors>({});
  let projects = $state<ConnectionProject[]>([]);
  let projectsLoading = $state(false);

  const inferredTransport = $derived(inferCloneTransport(values.cloneUrl));
  const effectiveTransport = $derived(resolveRepositoryFormTransport(values));
  const sshKeyRequired = $derived(requiresSshKey(values));
  const showSshKeyPicker = $derived(sshKeyRequired || Boolean(values.sshKeyName));

  const inputClasses =
    'min-h-11 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 placeholder:text-slate-600 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400';

  function fieldError(field: string): string | undefined {
    return clientErrors[field]?.[0] ?? serverErrors[field]?.[0];
  }

  function describedBy(field: string, hasHint = false): string | undefined {
    const ids: string[] = [];
    if (hasHint) ids.push(`repository-${field}-hint`);
    if (fieldError(field)) ids.push(`repository-${field}-error`);
    return ids.length > 0 ? ids.join(' ') : undefined;
  }

  function handleSubmit(event: SubmitEvent): void {
    event.preventDefault();
    clientErrors = validateRepositoryForm(values);
    if (Object.keys(clientErrors).length > 0) return;

    onsave(toRepositoryProfile(values, profile));
  }

  function clearClientError(field: string): void {
    if (!clientErrors[field]) return;
    const nextErrors = { ...clientErrors };
    delete nextErrors[field];
    clientErrors = nextErrors;
  }

  async function loadProjects(connectionKey: string): Promise<void> {
    if (!connectionKey) {
      projects = [];
      values.project = '';
      projectsLoading = false;
      return;
    }
    projectsLoading = true;
    try {
      projects = await client.connections.listProjects(connectionKey);
    } catch {
      projects = [];
    } finally {
      projectsLoading = false;
    }
  }

  function environmentLabel(profile: { key: string; displayName: string; enabled: boolean }): string {
    return `${profile.displayName} — ${profile.key}${profile.enabled ? '' : ' (disabled)'}`;
  }

  function hasRuntimeEnvironment(key: string): boolean {
    return runtimeEnvironments.some((environment) => environment.key === key);
  }

  function hasConnection(key: string): boolean {
    return connections.some((connection) => connection.key === key);
  }

  function connectionLabel(profile: { key: string; displayName: string; enabled: boolean }): string {
    return `${profile.displayName} — ${profile.key}${profile.enabled ? '' : ' (disabled)'}`;
  }

  function transportLabel(transport: RepositoryProfile['transport']): string {
    const labels: Record<RepositoryProfile['transport'], string> = {
      unspecified: 'Not resolved',
      ssh: 'SSH',
      httpsPat: 'HTTPS + PAT',
      local: 'Local',
    };
    return labels[transport];
  }

  function clearSshKeyReference(): void {
    values.sshKeyName = '';
    values.sshKeyVersion = null;
    clearClientError('sshKeyReference');
  }

  $effect(() => {
    const hasSshKey = Boolean(values.sshKeyName);
    const keyIsRequired = sshKeyRequired;
    if ((hasSshKey || !keyIsRequired) && clientErrors.sshKeyReference) {
      clearClientError('sshKeyReference');
    }
  });

  $effect(() => {
    void loadProjects(values.repositoryHostConnectionKey);
  });
</script>

<form class="space-y-7" novalidate onsubmit={handleSubmit}>
  {#if Object.keys(clientErrors).length > 0}
    <Alert
      variant="error"
      title="Complete the required fields"
      message="Correct the highlighted fields before saving the repository."
    />
  {/if}

  <div class="grid gap-6 lg:grid-cols-2">
    <Field
      id="repository-key"
      label="Repository key"
      hint={mode === 'edit'
        ? 'Keys are immutable. Create a new repository profile to use a different key.'
        : 'Use a stable key. It cannot be changed after the repository is created.'}
      error={fieldError('key')}
      required
    >
      <input
        id="repository-key"
        name="key"
        class={inputClasses}
        bind:value={values.key}
        readonly={mode === 'edit'}
        disabled={submitting}
        required
        autocomplete="off"
        aria-invalid={fieldError('key') ? 'true' : undefined}
        aria-describedby={describedBy('key', true)}
        oninput={() => clearClientError('key')}
      />
    </Field>

    <Field
      id="repository-defaultBranch"
      label="Default branch"
      hint="The branch checked out when work starts."
      error={fieldError('defaultBranch')}
      required
    >
      <input
        id="repository-defaultBranch"
        name="defaultBranch"
        class={inputClasses}
        bind:value={values.defaultBranch}
        disabled={submitting}
        required
        autocomplete="off"
        aria-invalid={fieldError('defaultBranch') ? 'true' : undefined}
        aria-describedby={describedBy('defaultBranch', true)}
        oninput={() => clearClientError('defaultBranch')}
      />
    </Field>
  </div>

  <div class="grid gap-6 lg:grid-cols-[2fr_1fr]">
    <Field
      id="repository-cloneUrl"
      label="Clone URL or local path"
      hint="Enter an HTTPS, SSH, or file URL, or an absolute local repository path."
      error={fieldError('cloneUrl')}
      required
    >
      <input
        id="repository-cloneUrl"
        name="cloneUrl"
        class={inputClasses}
        bind:value={values.cloneUrl}
        disabled={submitting}
        required
        spellcheck="false"
        autocomplete="off"
        placeholder="https://dev.azure.com/example/project/_git/repository"
        aria-invalid={fieldError('cloneUrl') ? 'true' : undefined}
        aria-describedby={describedBy('cloneUrl', true)}
        oninput={() => clearClientError('cloneUrl')}
      />
    </Field>

    <Field
      id="repository-transport"
      label="Clone transport"
      hint="Automatic infers the transport from the clone location."
      error={fieldError('transport')}
      required
    >
      <select
        id="repository-transport"
        name="transport"
        class={inputClasses}
        bind:value={values.transport}
        disabled={submitting}
        required
        aria-invalid={fieldError('transport') ? 'true' : undefined}
        aria-describedby={describedBy('transport', true)}
      >
        <option value="unspecified">Automatic</option>
        <option value="httpsPat">HTTPS + PAT</option>
        <option value="ssh">SSH</option>
        <option value="local">Local</option>
      </select>
    </Field>
  </div>

  <div
    class="rounded-xl border border-slate-800 bg-slate-950/40 p-4"
    aria-live="polite"
  >
    <p class="text-xs font-semibold tracking-wider text-slate-500 uppercase">Effective clone transport</p>
    <p class="mt-1 text-base font-semibold text-white">{transportLabel(effectiveTransport)}</p>
    {#if inferredTransport === 'unspecified'}
      <p class="mt-1 text-sm text-slate-400">
        Enter a supported clone URL or local path to resolve its transport.
      </p>
    {:else if values.transport === 'unspecified'}
      <p class="mt-1 text-sm text-slate-400">
        Automatic uses {transportLabel(inferredTransport)} based on the clone URL.
      </p>
    {:else if values.transport === inferredTransport}
      <p class="mt-1 text-sm text-slate-400">
        The configured transport matches the clone URL.
      </p>
    {:else}
      <p class="mt-1 text-sm font-medium text-amber-300">
        The clone URL resolves to {transportLabel(inferredTransport)}, which does not match the configured
        {transportLabel(values.transport)} transport.
      </p>
    {/if}
  </div>

  {#if showSshKeyPicker}
    <div class="space-y-2 rounded-xl border border-slate-800 p-4 sm:p-5">
      <label class="block text-sm font-medium text-slate-200" for="repository-sshKeyReference-input">
        SSH key secret
        {#if sshKeyRequired}<span class="text-rose-300" aria-hidden="true"> *</span>{/if}
        {#if sshKeyRequired}<span class="sr-only"> (required)</span>{/if}
      </label>
      <SecretPicker
        id="repository-sshKeyReference"
        client={client.secrets}
        secretType="ssh-key"
        bind:secretName={values.sshKeyName}
        bind:secretVersion={values.sshKeyVersion}
        disabled={submitting}
        error={fieldError('sshKeyReference')}
      />
      <p class="text-sm text-slate-400">
        Use an externally created private key. Choose Latest to follow rotations or pin a specific version.
      </p>
      {#if values.sshKeyName}
        <button
          type="button"
          class="text-sm font-semibold text-cyan-300 hover:text-cyan-200 disabled:cursor-not-allowed disabled:opacity-50"
          disabled={submitting}
          onclick={clearSshKeyReference}
        >
          Remove SSH key reference
        </button>
      {/if}
    </div>
  {/if}

  <Field
    id="repository-allowedPaths"
    label="Allowed paths"
    hint="Enter one repository-relative path per line. Leave empty to allow all paths."
    error={fieldError('allowedPaths')}
  >
    <textarea
      id="repository-allowedPaths"
      name="allowedPaths"
      class={`${inputClasses} min-h-28 resize-y`}
      bind:value={values.allowedPaths}
      disabled={submitting}
      spellcheck="false"
      placeholder={'src/\ntests/'}
      aria-invalid={fieldError('allowedPaths') ? 'true' : undefined}
      aria-describedby={describedBy('allowedPaths', true)}
    ></textarea>
  </Field>

  <fieldset class="space-y-5 rounded-xl border border-slate-800 p-4 sm:p-5">
    <legend class="px-2 text-sm font-semibold text-white">Managed environment associations</legend>
    <p class="text-sm leading-6 text-slate-400">
      Associations are optional. They determine which board configuration, repository host, and runtime are used for
      this repository.
    </p>

    <div class="grid gap-6 lg:grid-cols-2">
      <Field
        id="repository-repositoryHostConnectionKey"
        label="Repository host connection"
        hint="Choose the repository host this repository is sourced from. Decoupled from work source."
        error={fieldError('repositoryHostConnectionKey')}
      >
        <select
          id="repository-repositoryHostConnectionKey"
          name="repositoryHostConnectionKey"
          class={inputClasses}
          bind:value={values.repositoryHostConnectionKey}
          disabled={submitting}
          aria-invalid={fieldError('repositoryHostConnectionKey') ? 'true' : undefined}
          aria-describedby={describedBy('repositoryHostConnectionKey', true)}
        >
          <option value="">No managed repository host connection</option>
          {#if values.repositoryHostConnectionKey && !hasConnection(values.repositoryHostConnectionKey)}
            <option value={values.repositoryHostConnectionKey}>
              {values.repositoryHostConnectionKey} (unavailable)
            </option>
          {/if}
          {#each connections as conn (conn.key)}
            <option value={conn.key}>{connectionLabel(conn)}</option>
          {/each}
        </select>
      </Field>

      <Field
        id="repository-project"
        label="Project"
        hint="The project within the selected connection that contains this repository."
        error={fieldError('project')}
      >
        <select
          id="repository-project"
          name="project"
          class={inputClasses}
          bind:value={values.project}
          disabled={submitting || projectsLoading}
          aria-invalid={fieldError('project') ? 'true' : undefined}
          aria-describedby={describedBy('project', true)}
        >
          <option value="">
            {projectsLoading ? 'Loading projects…' : 'Select a project…'}
          </option>
          {#if values.project && !projects.some((p) => p.name === values.project)}
            <option value={values.project}>{values.project} (unavailable)</option>
          {/if}
          {#each projects as project (project.id)}
            <option value={project.name}>{project.name}</option>
          {/each}
        </select>
      </Field>
    </div>

    <Field
      id="repository-runtimeEnvironmentKey"
      label="Runtime environment"
      error={fieldError('runtimeEnvironmentKey')}
    >
      <select
        id="repository-runtimeEnvironmentKey"
        name="runtimeEnvironmentKey"
        class={inputClasses}
        bind:value={values.runtimeEnvironmentKey}
        disabled={submitting}
        aria-invalid={fieldError('runtimeEnvironmentKey') ? 'true' : undefined}
        aria-describedby={describedBy('runtimeEnvironmentKey')}
      >
        <option value="">No managed runtime environment</option>
        {#if values.runtimeEnvironmentKey && !hasRuntimeEnvironment(values.runtimeEnvironmentKey)}
          <option value={values.runtimeEnvironmentKey}>
            {values.runtimeEnvironmentKey} (unavailable)
          </option>
        {/if}
        {#each runtimeEnvironments as environment (environment.key)}
          <option value={environment.key}>{environmentLabel(environment)}</option>
        {/each}
      </select>
    </Field>
  </fieldset>

  <div class="flex flex-col-reverse gap-3 border-t border-slate-800 pt-6 sm:flex-row sm:justify-end">
    <Button variant="secondary" onclick={oncancel} disabled={submitting}>Cancel</Button>
    <Button type="submit" disabled={submitting}>
      {submitting ? 'Saving…' : mode === 'create' ? 'Onboard repository' : 'Save changes'}
    </Button>
  </div>
</form>
