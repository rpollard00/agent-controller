<script lang="ts">
  import { untrack } from 'svelte';
  import type { ConnectionProfile, ConnectionProject, WorkSourceEnvironmentProfile } from '../../api/types';
  import { type WebUiApiClient } from '../../api/client';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Field from '../../components/ui/Field.svelte';
  import {
    createWorkSourceEnvironmentFormValues,
    toWorkSourceEnvironmentProfile,
    validateWorkSourceEnvironmentForm,
    type WorkSourceEnvironmentFormErrors,
  } from './azureDevOpsEnvironmentForm';

  let {
    mode,
    profile,
    connections,
    submitting = false,
    serverErrors = {},
    onsave,
    client,
    oncancel,
  }: {
    mode: 'create' | 'edit';
    profile?: WorkSourceEnvironmentProfile;
    connections: ConnectionProfile[];
    submitting?: boolean;
    serverErrors?: Readonly<Record<string, string[]>>;
    onsave: (profile: WorkSourceEnvironmentProfile) => void;
    client: WebUiApiClient;
    oncancel: () => void;
  } = $props();

  let values = $state(untrack(() => createWorkSourceEnvironmentFormValues(profile)));
  let clientErrors = $state<WorkSourceEnvironmentFormErrors>({});
  let projects = $state<ConnectionProject[]>([]);
  let projectsLoading = $state(false);

  const tagPrefixHint =
    'Namespace for controller-owned tags like prefix-ready, prefix-active, prefix-failed, prefix-needs-human. Defaults to \'agent\' when blank.';

  const inputClasses =
    'min-h-11 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 placeholder:text-slate-600 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400';

  function fieldError(field: string): string | undefined {
    return clientErrors[field]?.[0] ?? serverErrors[field]?.[0];
  }

  function describedBy(id: string, field: string, hasHint = false): string | undefined {
    const ids: string[] = [];
    if (hasHint) ids.push(`${id}-hint`);
    if (fieldError(field)) ids.push(`wse-${field}-error`);
    return ids.length > 0 ? ids.join(' ') : undefined;
  }

  function handleSubmit(event: SubmitEvent): void {
    event.preventDefault();
    clientErrors = validateWorkSourceEnvironmentForm(values);
    if (Object.keys(clientErrors).length > 0) return;

    onsave(toWorkSourceEnvironmentProfile(values, profile));
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

  function hasConnection(key: string): boolean {
    return connections.some((conn) => conn.key === key);
  }

  function connectionLabel(conn: { key: string; displayName: string; enabled: boolean }): string {
    return `${conn.displayName} — ${conn.key}${conn.enabled ? '' : ' (disabled)'}`;
  }

  $effect(() => {
    void loadProjects(values.connectionKey);
  });
</script>

<form class="space-y-8" novalidate onsubmit={handleSubmit}>
  {#if Object.keys(clientErrors).length > 0}
    <Alert
      variant="error"
      title="Correct the highlighted fields"
      message="Review the work source environment configuration before saving."
    />
  {/if}

  <fieldset class="space-y-6">
    <legend class="text-base font-semibold text-white">Environment identity</legend>

    <div class="grid gap-6 lg:grid-cols-2">
      <Field
        id="wse-key"
        label="Environment name"
        error={fieldError('key')}
        required
      >
        <input
          id="wse-key"
          name="key"
          class={inputClasses}
          bind:value={values.key}
          readonly={mode === 'edit'}
          disabled={submitting}
          required
          autocomplete="off"
          placeholder="ado-dev"
          aria-invalid={fieldError('key') ? 'true' : undefined}
          aria-describedby={describedBy('wse-key', 'key')}
          oninput={() => clearClientError('key')}
        />
      </Field>

      <Field
        id="wse-displayName"
        label="Display name"
        error={fieldError('displayName')}
        required
      >
        <input
          id="wse-displayName"
          name="displayName"
          class={inputClasses}
          bind:value={values.displayName}
          disabled={submitting}
          required
          autocomplete="off"
          placeholder="Business Products Azure DevOps"
          aria-invalid={fieldError('displayName') ? 'true' : undefined}
          aria-describedby={describedBy('wse-displayName', 'displayName')}
          oninput={() => clearClientError('displayName')}
        />
      </Field>
    </div>

  </fieldset>

  <fieldset class="space-y-6 border-t border-slate-800 pt-7">
    <legend class="text-base font-semibold text-white">Provider</legend>

    <Field
      id="wse-provider"
      label="Work source provider"
      error={fieldError('provider')}
      required
    >
      <select
        id="wse-provider"
        name="provider"
        class={inputClasses}
        bind:value={values.provider}
        disabled={submitting}
        required
        aria-invalid={fieldError('provider') ? 'true' : undefined}
        aria-describedby={describedBy('wse-provider', 'provider')}
        onchange={() => clearClientError('provider')}
      >
        <option value="AzureDevOpsBoards">Azure DevOps</option>
      </select>
    </Field>

    {#if values.provider === 'AzureDevOpsBoards'}
      <fieldset class="space-y-6">
        <legend class="text-sm font-semibold text-slate-300">Azure DevOps connection</legend>

        <div class="grid gap-6 lg:grid-cols-2">
          <Field
            id="wse-connectionKey"
            label="Connection"
            hint="Select the Azure DevOps connection to use. PAT credentials are managed on the connection."
            error={fieldError('connectionKey')}
            required
          >
            <select
              id="wse-connectionKey"
              name="connectionKey"
              class={inputClasses}
              bind:value={values.connectionKey}
              disabled={submitting}
              required
              aria-invalid={fieldError('connectionKey') ? 'true' : undefined}
              aria-describedby={describedBy('wse-connectionKey', 'connectionKey', true)}
              onchange={() => clearClientError('connectionKey')}
            >
              <option value="">Select a connection…</option>
              {#if values.connectionKey && !hasConnection(values.connectionKey)}
                <option value={values.connectionKey}>
                  {values.connectionKey} (unavailable)
                </option>
              {/if}
              {#each connections as conn (conn.key)}
                <option value={conn.key}>{connectionLabel(conn)}</option>
              {/each}
            </select>
          </Field>

          <Field
            id="wse-project"
            label="Project"
            error={fieldError('project')}
            required
          >
            <select
              id="wse-project"
              name="project"
              class={inputClasses}
              bind:value={values.project}
              disabled={submitting || projectsLoading}
              required
              aria-invalid={fieldError('project') ? 'true' : undefined}
              aria-describedby={describedBy('wse-project', 'project')}
              onchange={() => clearClientError('project')}
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

      </fieldset>
    {:else}
      <Alert
        variant="info"
        title="No provider-specific configuration"
        message="Provider-specific settings will appear here when a supported work source provider is selected."
      />
    {/if}
  </fieldset>

  {#if mode === 'create'}
    <div class="rounded-xl border border-slate-800 bg-slate-950/40 p-4">
      <Alert
        variant="info"
        title="Board policy configured after save"
        message="Save the connection first, then edit the environment to configure board states, active/completed state, and tag prefix."
      />
    </div>
  {:else}
    <fieldset class="space-y-6 border-t border-slate-800 pt-7">
      <legend class="text-base font-semibold text-white">Board policy</legend>

      <Field
        id="wse-tagPrefix"
        label="Tag prefix"
        hint={tagPrefixHint}
        error={fieldError('tagPrefix')}
      >
        <input
          id="wse-tagPrefix"
          name="tagPrefix"
          class={inputClasses}
          bind:value={values.tagPrefix}
          disabled={submitting}
          autocomplete="off"
          placeholder="agent"
          aria-invalid={fieldError('tagPrefix') ? 'true' : undefined}
          aria-describedby={describedBy('wse-tagPrefix', 'tagPrefix', true)}
          oninput={() => clearClientError('tagPrefix')}
        />
      </Field>

      <div class="grid gap-6 lg:grid-cols-2">
        <Field
          id="wse-activeState"
          label="Active state"
          error={fieldError('activeState')}
        >
          <input
            id="wse-activeState"
            name="activeState"
            class={inputClasses}
            bind:value={values.activeState}
            disabled={submitting}
            autocomplete="off"
            placeholder="Active"
            aria-invalid={fieldError('activeState') ? 'true' : undefined}
            aria-describedby={describedBy('wse-activeState', 'activeState')}
            oninput={() => clearClientError('activeState')}
          />
        </Field>

        <Field
          id="wse-completedState"
          label="Completed state"
          error={fieldError('completedState')}
        >
          <input
            id="wse-completedState"
            name="completedState"
            class={inputClasses}
            bind:value={values.completedState}
            disabled={submitting}
            autocomplete="off"
            placeholder="Resolved"
            aria-invalid={fieldError('completedState') ? 'true' : undefined}
            aria-describedby={describedBy('wse-completedState', 'completedState')}
            oninput={() => clearClientError('completedState')}
          />
        </Field>
      </div>
    </fieldset>
  {/if}

  <div class="rounded-xl border border-slate-800 bg-slate-950/40 p-4">
    <label class="flex min-h-8 cursor-pointer items-start gap-3" for="wse-enabled">
      <input
        id="wse-enabled"
        name="enabled"
        type="checkbox"
        class="mt-0.5 size-5 rounded border-slate-600 bg-slate-950 text-cyan-400"
        bind:checked={values.enabled}
        disabled={submitting}
      />
      <span class="block text-sm font-medium text-slate-100">Enabled</span>
    </label>
    {#if fieldError('enabled')}
      <p class="mt-2 ml-8 text-sm font-medium text-rose-300">{fieldError('enabled')}</p>
    {/if}
  </div>

  <div class="flex flex-col-reverse gap-3 border-t border-slate-800 pt-6 sm:flex-row sm:justify-end">
    <Button variant="secondary" onclick={oncancel} disabled={submitting}>Cancel</Button>
    <Button type="submit" disabled={submitting}>
      {submitting ? 'Saving…' : mode === 'create' ? 'Create environment' : 'Save changes'}
    </Button>
  </div>
</form>
