<script lang="ts">
  import { untrack } from 'svelte';
  import type {
    ConnectionProfile,
    ConnectionProject,
    HostRepository,
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
    isHostDriven,
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
  let projectsLoadError = $state<string>();
  let repositories = $state<HostRepository[]>([]);
  let repositoriesLoading = $state(false);
  let repositoriesLoadError = $state<string>();
  let branches = $state<string[]>([]);
  let branchesLoading = $state(false);
  let branchesLoadError = $state<string>();

  const hostDriven = $derived(isHostDriven(values));
  const inferredTransport = $derived(inferCloneTransport(values.cloneUrl));
  const effectiveTransport = $derived(resolveRepositoryFormTransport(values));
  const sshKeyRequired = $derived(requiresSshKey(values));
  const showSshKeyPicker = $derived(sshKeyRequired || Boolean(values.sshKeyName));
  const hasEnumerationError = $derived(Boolean(projectsLoadError || repositoriesLoadError || branchesLoadError));
  const enumerationErrorTitle = $derived(
    branchesLoadError
      ? 'Could not load branches'
      : repositoriesLoadError
        ? 'Could not load repositories'
        : projectsLoadError
          ? 'Could not load projects'
          : '',
  );
  const enumerationErrorMessage = $derived(
    branchesLoadError || repositoriesLoadError || projectsLoadError || '',
  );

  const inputClasses =
    'min-h-11 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 placeholder:text-slate-600 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400 readonly:cursor-default readonly:bg-slate-900/80 readonly:text-slate-300';

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
      values.selectedRepositoryId = '';
      repositories = [];
      projectsLoading = false;
      projectsLoadError = undefined;
      return;
    }
    projectsLoading = true;
    projectsLoadError = undefined;
    values.project = '';
    values.selectedRepositoryId = '';
    repositories = [];
    try {
      projects = await client.connections.listProjects(connectionKey);
      if (projects.length === 0) {
        projectsLoadError = 'No projects found for this connection. Verify the connection is configured correctly.';
      }
    } catch {
      projects = [];
      projectsLoadError = 'Could not load projects. Check the connection configuration and permissions, then try again.';
    } finally {
      projectsLoading = false;
    }
  }

  async function loadRepositories(connectionKey: string, project: string): Promise<void> {
    repositoriesLoading = true;
    repositoriesLoadError = undefined;
    values.selectedRepositoryId = '';
    try {
      const result = await client.connections.listRepositories(connectionKey, project);
      // Guard against stale responses when host or project changed during the request
      if (connectionKey === values.repositoryHostConnectionKey && project === values.project) {
        repositories = result;
        if (repositories.length === 0) {
          repositoriesLoadError = 'No repositories found in this project. It may be empty or the connection may not have access.';
        }
      }
    } catch {
      if (connectionKey === values.repositoryHostConnectionKey && project === values.project) {
        repositories = [];
        repositoriesLoadError = 'Could not load repositories. The project may no longer exist or the connection may have insufficient permissions.';
      }
    } finally {
      if (connectionKey === values.repositoryHostConnectionKey && project === values.project) {
        repositoriesLoading = false;
      }
    }
  }

  async function loadBranches(connectionKey: string, project: string, repositoryId: string): Promise<void> {
    branchesLoading = true;
    branchesLoadError = undefined;
    try {
      const result = await client.connections.listBranches(connectionKey, project, repositoryId);
      // Guard against stale responses when host, project, or repository changed during the request
      if (connectionKey === values.repositoryHostConnectionKey && project === values.project && repositoryId === values.selectedRepositoryId) {
        branches = result;
        if (branches.length === 0) {
          branchesLoadError = 'No branches found for this repository.';
        }
      }
    } catch {
      if (connectionKey === values.repositoryHostConnectionKey && project === values.project && repositoryId === values.selectedRepositoryId) {
        branches = [];
        branchesLoadError = 'Could not load branches. The repository may no longer exist or the connection may have insufficient permissions.';
      }
    } finally {
      if (connectionKey === values.repositoryHostConnectionKey && project === values.project && repositoryId === values.selectedRepositoryId) {
        branchesLoading = false;
      }
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

  function repositoryLabel(repo: HostRepository): string {
    return `${repo.name}${repo.defaultBranch ? ` (default: ${repo.defaultBranch})` : ''}`;
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

  // Load projects when the host connection changes
  $effect(() => {
    void loadProjects(values.repositoryHostConnectionKey);
  });

  // Load repositories when a project is selected (only in host-driven mode)
  $effect(() => {
    if (hostDriven && values.project) {
      void loadRepositories(values.repositoryHostConnectionKey, values.project);
    } else {
      repositories = [];
      values.selectedRepositoryId = '';
      repositoriesLoadError = undefined;
    }
  });

  // Prefill fields when a repository is selected from the host or transport changes.
  // In host-driven mode, the clone URL is derived from the selected transport:
  // HTTPS uses remoteUrl; SSH uses sshUrl (with a fallback to remoteUrl).
  $effect(() => {
    const selectedId = values.selectedRepositoryId;
    if (!selectedId || !hostDriven) return;
    const repo = repositories.find((r) => r.id === selectedId);
    if (!repo) return;
    values.key = repo.name;
    values.cloneUrl = values.transport === 'ssh' ? (repo.sshUrl ?? repo.remoteUrl) : repo.remoteUrl;
    values.defaultBranch = repo.defaultBranch || 'main';
  });

  // Load branches when a repository is selected (only in host-driven mode)
  $effect(() => {
    if (hostDriven && values.selectedRepositoryId && values.project) {
      void loadBranches(values.repositoryHostConnectionKey, values.project, values.selectedRepositoryId);
    } else {
      branches = [];
      branchesLoadError = undefined;
    }
  });

  // Clear SSH key reference when inherit from environment is checked
  $effect(() => {
    if (values.sshKeyInheritEnvironment && values.sshKeyName) {
      values.sshKeyName = '';
      values.sshKeyVersion = null;
      clearClientError('sshKeyReference');
    }
  });

  // Clear SSH key error when requirements are satisfied
  $effect(() => {
    const hasSshKey = Boolean(values.sshKeyName);
    const keyIsRequired = sshKeyRequired && !values.sshKeyInheritEnvironment;
    if ((hasSshKey || !keyIsRequired) && clientErrors.sshKeyReference) {
      clearClientError('sshKeyReference');
    }
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

  {#if hasEnumerationError}
    <Alert
      variant="warning"
      title={enumerationErrorTitle}
      message={enumerationErrorMessage}
    />
  {/if}

  <!-- Repository Host → Project → Repository selection -->
  <section class="space-y-6 rounded-xl border border-slate-800 p-4 sm:p-5">
    <h2 class="text-sm font-semibold text-white">Repository source</h2>

    <Field
      id="repository-repositoryHostConnectionKey"
      label="Repository Host"
      hint="Choose the host connection to browse projects and repositories."
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
        <option value="">None — manual entry</option>
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

    {#if hostDriven}
      <Field
        id="repository-project"
        label="Project"
        hint="The Azure DevOps project that contains the repository."
        error={fieldError('project')}
      >
        <select
          id="repository-project"
          name="project"
          class={inputClasses}
          bind:value={values.project}
          disabled={submitting || projectsLoading || Boolean(projectsLoadError)}
          aria-invalid={fieldError('project') ? 'true' : undefined}
          aria-describedby={describedBy('project', true)}
        >
          <option value="">
            {projectsLoading ? 'Loading projects…' : projectsLoadError ? 'Error loading projects' : 'Select a project…'}
          </option>
          {#if values.project && !projects.some((p) => p.name === values.project)}
            <option value={values.project}>{values.project} (unavailable)</option>
          {/if}
          {#each projects as project (project.id)}
            <option value={project.name}>{project.name}</option>
          {/each}
        </select>
      </Field>

      {#if values.project && !projectsLoadError}
        <Field
          id="repository-repository"
          label="Repository"
          hint="Select a repository from the project. Its name, URL, and default branch are filled in automatically."
          error={fieldError('repository')}
        >
          <select
            id="repository-repository"
            name="repository"
            class={inputClasses}
            bind:value={values.selectedRepositoryId}
            disabled={submitting || repositoriesLoading || Boolean(repositoriesLoadError)}
            aria-describedby={describedBy('repository', true)}
          >
            <option value="">
              {repositoriesLoading ? 'Loading repositories…' : repositoriesLoadError ? 'Error loading repositories' : 'Select a repository…'}
            </option>
            {#if values.selectedRepositoryId && !repositories.some((r) => r.id === values.selectedRepositoryId)}
              <option value={values.selectedRepositoryId}>
                {(repositories.find((r) => r.id === values.selectedRepositoryId)?.name ?? values.selectedRepositoryId)} (unavailable)
              </option>
            {/if}
            {#each repositories as repo (repo.id)}
              <option value={repo.id}>{repositoryLabel(repo)}</option>
            {/each}
          </select>
        </Field>
      {/if}
    {/if}
  </section>

  <!-- Repository configuration -->
  <div class="grid gap-6 lg:grid-cols-2">
    <Field
      id="repository-key"
      label="Repository Name"
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
      hint={hostDriven
        ? values.selectedRepositoryId
          ? 'Enumerated from the host repository. Select the branch for new work.'
          : 'Select a repository above to enumerate branches.'
        : 'The branch checked out when work starts.'}
      error={fieldError('defaultBranch')}
      required
    >
      {#if hostDriven && values.selectedRepositoryId && !branchesLoadError}
        <select
          id="repository-defaultBranch"
          name="defaultBranch"
          class={inputClasses}
          bind:value={values.defaultBranch}
          disabled={submitting || branchesLoading}
          required
          aria-invalid={fieldError('defaultBranch') ? 'true' : undefined}
          aria-describedby={describedBy('defaultBranch', true)}
        >
          <option value="">
            {branchesLoading ? 'Loading branches…' : branches.length === 0 && branchesLoadError ? 'Error loading branches' : branches.length === 0 ? 'No branches found' : 'Select a branch…'}
          </option>
          {#if values.defaultBranch && branches.length > 0 && !branches.includes(values.defaultBranch)}
            <option value={values.defaultBranch}>{values.defaultBranch} (unavailable)</option>
          {/if}
          {#each branches as branch (branch)}
            <option value={branch}>{branch}</option>
          {/each}
        </select>
      {:else}
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
      {/if}
    </Field>
  </div>

  <div class="grid gap-6 lg:grid-cols-[2fr_1fr]">
    <Field
      id="repository-cloneUrl"
      label="Clone URL or local path"
      hint={hostDriven
        ? 'Derived from the selected repository and transport. HTTPS uses the remote URL; SSH uses the SSH URL.'
        : 'Enter an HTTPS, SSH, or file URL, or an absolute local repository path.'}
      error={fieldError('cloneUrl')}
      required
    >
      <input
        id="repository-cloneUrl"
        name="cloneUrl"
        class={inputClasses}
        bind:value={values.cloneUrl}
        readonly={hostDriven}
        disabled={submitting}
        required
        spellcheck="false"
        autocomplete="off"
        placeholder={hostDriven
          ? 'https://dev.azure.com/example/project/_git/repository'
          : 'https://dev.azure.com/example/project/_git/repository'}
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
    <div class="space-y-4 rounded-xl border border-slate-800 p-4 sm:p-5">
      <h3 class="text-sm font-semibold text-white">SSH authentication</h3>

      <!-- Inherit from environment checkbox -->
      <label class="flex cursor-pointer items-start gap-3">
        <input
          type="checkbox"
          class="mt-0.5 h-4 w-4 rounded border-slate-700 bg-slate-950 text-cyan-400 focus:ring-cyan-400"
          bind:checked={values.sshKeyInheritEnvironment}
          disabled={submitting}
        />
        <div>
          <span class="text-sm font-medium text-slate-200">Inherit from environment</span>
          <p class="text-xs text-slate-400">
            Use the SSH key configured in the runner environment (e.g. ssh-agent or default key paths). No key reference is saved with this profile.
          </p>
        </div>
      </label>

      {#if values.sshKeyInheritEnvironment}
        <Alert
          variant="warning"
          title="Environment SSH key required"
          message="No SSH key reference is stored with this profile. The SSH key must be set up in the runner environment (ssh-agent, ~/.ssh/id_ed25519, or similar) for cloning to work. If no key is available, clone will fail."
        />
      {:else}
        <div class="space-y-2">
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
    </div>
  {/if}

  <fieldset class="space-y-5 rounded-xl border border-slate-800 p-4 sm:p-5">
    <legend class="px-2 text-sm font-semibold text-white">Managed environment associations</legend>

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
