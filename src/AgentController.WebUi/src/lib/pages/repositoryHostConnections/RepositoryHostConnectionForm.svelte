<script lang="ts">
  import { untrack } from 'svelte';
  import type { RepositoryHostConnectionProfile } from '../../api/types';
  import { type WebUiApiClient } from '../../api/client';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Field from '../../components/ui/Field.svelte';
  import SecretPicker from '../../components/ui/SecretPicker.svelte';
  import {
    createRepositoryHostConnectionFormValues,
    toRepositoryHostConnectionProfile,
    validateRepositoryHostConnectionForm,
    type RepositoryHostConnectionFormErrors,
  } from './repositoryHostConnectionForm';

  let {
    mode,
    profile,
    submitting = false,
    serverErrors = {},
    onsave,
    client,
    oncancel,
  }: {
    mode: 'create' | 'edit';
    profile?: RepositoryHostConnectionProfile;
    submitting?: boolean;
    serverErrors?: Readonly<Record<string, string[]>>;
    onsave: (profile: RepositoryHostConnectionProfile) => void;
    client: WebUiApiClient;
    oncancel: () => void;
  } = $props();

  let values = $state(untrack(() => createRepositoryHostConnectionFormValues(profile)));
  let clientErrors = $state<RepositoryHostConnectionFormErrors>({});
  const secretsClient = client.secrets;

  const inputClasses =
    'min-h-11 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 placeholder:text-slate-600 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400';

  function fieldError(field: string): string | undefined {
    return clientErrors[field]?.[0] ?? serverErrors[field]?.[0];
  }

  function describedBy(id: string, field: string, hasHint = false): string | undefined {
    const ids: string[] = [];
    if (hasHint) ids.push(`${id}-hint`);
    if (fieldError(field)) ids.push(`rhc-${field}-error`);
    return ids.length > 0 ? ids.join(' ') : undefined;
  }

  function handleSubmit(event: SubmitEvent): void {
    event.preventDefault();
    clientErrors = validateRepositoryHostConnectionForm(values);
    if (Object.keys(clientErrors).length > 0) return;

    onsave(toRepositoryHostConnectionProfile(values, profile));
  }

  function clearClientError(field: string): void {
    if (!clientErrors[field]) return;
    const nextErrors = { ...clientErrors };
    delete nextErrors[field];
    clientErrors = nextErrors;
  }

  $effect(() => {
    // Clear client-side errors when secret id changes via the picker
    void values.secretId;
    if (values.secretId && clientErrors.secretId) clearClientError('secretId');
  });
</script>

<form class="space-y-8" novalidate onsubmit={handleSubmit}>
  {#if Object.keys(clientErrors).length > 0}
    <Alert
      variant="error"
      title="Correct the highlighted fields"
      message="Review the repository host connection configuration before saving."
    />
  {/if}

  <fieldset class="space-y-6">
    <legend class="text-base font-semibold text-white">Connection identity</legend>

    <div class="grid gap-6 lg:grid-cols-2">
      <Field
        id="rhc-key"
        label="Connection name"
        error={fieldError('key')}
        required
      >
        <input
          id="rhc-key"
          name="key"
          class={inputClasses}
          bind:value={values.key}
          readonly={mode === 'edit'}
          disabled={submitting}
          required
          autocomplete="off"
          placeholder="ado-repos-main"
          aria-invalid={fieldError('key') ? 'true' : undefined}
          aria-describedby={describedBy('rhc-key', 'key')}
          oninput={() => clearClientError('key')}
        />
      </Field>

      <Field
        id="rhc-displayName"
        label="Display name"
        error={fieldError('displayName')}
        required
      >
        <input
          id="rhc-displayName"
          name="displayName"
          class={inputClasses}
          bind:value={values.displayName}
          disabled={submitting}
          required
          autocomplete="off"
          placeholder="Primary Azure DevOps Repos"
          aria-invalid={fieldError('displayName') ? 'true' : undefined}
          aria-describedby={describedBy('rhc-displayName', 'displayName')}
          oninput={() => clearClientError('displayName')}
        />
      </Field>
    </div>
  </fieldset>

  <fieldset class="space-y-6 border-t border-slate-800 pt-7">
    <legend class="text-base font-semibold text-white">Provider</legend>

    <Field
      id="rhc-provider"
      label="Repository host provider"
      error={fieldError('provider')}
      required
    >
      <select
        id="rhc-provider"
        name="provider"
        class={inputClasses}
        bind:value={values.provider}
        disabled={submitting}
        required
        aria-invalid={fieldError('provider') ? 'true' : undefined}
        aria-describedby={describedBy('rhc-provider', 'provider')}
        onchange={() => clearClientError('provider')}
      >
        <option value="AzureDevOpsRepos">Azure DevOps Repos</option>
      </select>
    </Field>

    {#if values.provider === 'AzureDevOpsRepos'}
      <fieldset class="space-y-6">
        <legend class="text-sm font-semibold text-slate-300">Azure DevOps connection</legend>

        <div class="grid gap-6 lg:grid-cols-2">
          <Field
            id="rhc-organizationUrl"
            label="Organization URL"
            error={fieldError('organizationUrl')}
            required
          >
            <input
              id="rhc-organizationUrl"
              name="organizationUrl"
              type="url"
              class={inputClasses}
              bind:value={values.organizationUrl}
              disabled={submitting}
              required
              spellcheck="false"
              autocomplete="url"
              placeholder="https://dev.azure.com/example"
              aria-invalid={fieldError('organizationUrl') ? 'true' : undefined}
              aria-describedby={describedBy('rhc-organizationUrl', 'organizationUrl')}
              oninput={() => clearClientError('organizationUrl')}
            />
          </Field>

          <Field
            id="rhc-project"
            label="Project"
            error={fieldError('project')}
            required
          >
            <input
              id="rhc-project"
              name="project"
              class={inputClasses}
              bind:value={values.project}
              disabled={submitting}
              required
              autocomplete="off"
              aria-invalid={fieldError('project') ? 'true' : undefined}
              aria-describedby={describedBy('rhc-project', 'project')}
              oninput={() => clearClientError('project')}
            />
          </Field>
        </div>

        <fieldset class="space-y-4">
          <legend class="text-sm font-semibold text-slate-300">Secret reference</legend>
          <p class="text-sm leading-6 text-slate-400">
            Reference the secret holding the personal access token. Only the reference is stored; the PAT value is resolved at runtime.
          </p>

          <div class="grid gap-6 lg:grid-cols-2">
            <Field
              id="rhc-secretKind"
              label="Secret kind"
              error={fieldError('secretKind')}
            >
              <select
                id="rhc-secretKind"
                name="secretKind"
                class={inputClasses}
                bind:value={values.secretKind}
                disabled={submitting}
                aria-invalid={fieldError('secretKind') ? 'true' : undefined}
                aria-describedby={describedBy('rhc-secretKind', 'secretKind')}
                onchange={() => clearClientError('secretKind')}
              >
                <option value="EnvVar">Environment variable</option>
                <option value="Db">Database</option>
              </select>
            </Field>

            {#if values.secretKind === 'Db'}
              <div class="space-y-2">
                <label class="block text-sm font-medium text-slate-200" for="rhc-secretId-input">
                  Secret name
                  <span class="text-rose-300" aria-hidden="true"> *</span>
                  <span class="sr-only"> (required)</span>
                </label>
                <SecretPicker
                  id="rhc-secretId"
                  client={secretsClient}
                  bind:secretName={values.secretId}
                  secretVersion={null}
                  disabled={submitting}
                  error={fieldError('secretId')}
                />
              </div>
            {:else}
              <Field
                id="rhc-secretId"
                label="Environment variable name"
                hint="Name of the environment variable holding the PAT (e.g. ADO_PAT)."
                error={fieldError('secretId')}
                required
              >
                <input
                  id="rhc-secretId"
                  name="secretId"
                  class={inputClasses}
                  bind:value={values.secretId}
                  disabled={submitting}
                  required
                  spellcheck="false"
                  autocomplete="off"
                  placeholder="ADO_PAT"
                  aria-invalid={fieldError('secretId') ? 'true' : undefined}
                  aria-describedby={describedBy('rhc-secretId', 'secretId', true)}
                  oninput={() => clearClientError('secretId')}
                />
              </Field>
            {/if}
          </div>
        </fieldset>

        <Alert
          variant="info"
          title="Secrets are stored encrypted"
          message="Named secrets are stored encrypted at rest in the database. Select a secret by name, or reference an environment variable for runtime resolution."
        />
      </fieldset>
    {:else}
      <Alert
        variant="info"
        title="No provider-specific configuration"
        message="Provider-specific settings will appear here when a supported repository host provider is selected."
      />
    {/if}
  </fieldset>

  <div class="rounded-xl border border-slate-800 bg-slate-950/40 p-4">
    <label class="flex min-h-8 cursor-pointer items-start gap-3" for="rhc-enabled">
      <input
        id="rhc-enabled"
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
      {submitting ? 'Saving…' : mode === 'create' ? 'Create connection' : 'Save changes'}
    </Button>
  </div>
</form>
