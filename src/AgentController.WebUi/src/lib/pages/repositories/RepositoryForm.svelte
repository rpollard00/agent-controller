<script lang="ts">
  import { untrack } from 'svelte';
  import type {
    RepositoryHostConnectionProfile,
    RepositoryProfile,
    RuntimeEnvironmentProfile,
  } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Field from '../../components/ui/Field.svelte';
  import {
    createRepositoryFormValues,
    toRepositoryProfile,
    validateRepositoryForm,
    type RepositoryFormErrors,
  } from './repositoryForm';

  let {
    mode,
    profile,
    repositoryHostConnections,
    runtimeEnvironments,
    submitting = false,
    serverErrors = {},
    onsave,
    oncancel,
  }: {
    mode: 'create' | 'edit';
    profile?: RepositoryProfile;
    repositoryHostConnections: RepositoryHostConnectionProfile[];
    runtimeEnvironments: RuntimeEnvironmentProfile[];
    submitting?: boolean;
    serverErrors?: Readonly<Record<string, string[]>>;
    onsave: (profile: RepositoryProfile) => void;
    oncancel: () => void;
  } = $props();

  let values = $state(untrack(() => createRepositoryFormValues(profile)));
  let clientErrors = $state<RepositoryFormErrors>({});

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

  function environmentLabel(profile: { key: string; displayName: string; enabled: boolean }): string {
    return `${profile.displayName} — ${profile.key}${profile.enabled ? '' : ' (disabled)'}`;
  }

  function hasRuntimeEnvironment(key: string): boolean {
    return runtimeEnvironments.some((environment) => environment.key === key);
  }

  function hasRepositoryHostConnection(key: string): boolean {
    return repositoryHostConnections.some((connection) => connection.key === key);
  }

  function connectionLabel(profile: { key: string; displayName: string; enabled: boolean }): string {
    return `${profile.displayName} — ${profile.key}${profile.enabled ? '' : ' (disabled)'}`;
  }
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
          {#if values.repositoryHostConnectionKey && !hasRepositoryHostConnection(values.repositoryHostConnectionKey)}
            <option value={values.repositoryHostConnectionKey}>
              {values.repositoryHostConnectionKey} (unavailable)
            </option>
          {/if}
          {#each repositoryHostConnections as conn (conn.key)}
            <option value={conn.key}>{connectionLabel(conn)}</option>
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
