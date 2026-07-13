<script lang="ts">
  import { untrack } from 'svelte';
  import type { AzureDevOpsEnvironmentProfile } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Field from '../../components/ui/Field.svelte';
  import {
    createAzureDevOpsEnvironmentFormValues,
    toAzureDevOpsEnvironmentProfile,
    validateAzureDevOpsEnvironmentForm,
    type AzureDevOpsEnvironmentFormErrors,
  } from './azureDevOpsEnvironmentForm';

  let {
    mode,
    profile,
    submitting = false,
    serverErrors = {},
    onsave,
    oncancel,
  }: {
    mode: 'create' | 'edit';
    profile?: AzureDevOpsEnvironmentProfile;
    submitting?: boolean;
    serverErrors?: Readonly<Record<string, string[]>>;
    onsave: (profile: AzureDevOpsEnvironmentProfile) => void;
    oncancel: () => void;
  } = $props();

  let values = $state(untrack(() => createAzureDevOpsEnvironmentFormValues(profile)));
  let clientErrors = $state<AzureDevOpsEnvironmentFormErrors>({});

  const inputClasses =
    'min-h-11 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 placeholder:text-slate-600 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400';

  function fieldError(field: string): string | undefined {
    return clientErrors[field]?.[0] ?? serverErrors[field]?.[0];
  }

  function describedBy(field: string, hasHint = false): string | undefined {
    const ids: string[] = [];
    if (hasHint) ids.push(`ado-${field}-hint`);
    if (fieldError(field)) ids.push(`ado-${field}-error`);
    return ids.length > 0 ? ids.join(' ') : undefined;
  }

  function handleSubmit(event: SubmitEvent): void {
    event.preventDefault();
    clientErrors = validateAzureDevOpsEnvironmentForm(values);
    if (Object.keys(clientErrors).length > 0) return;

    onsave(toAzureDevOpsEnvironmentProfile(values, profile));
  }

  function clearClientError(field: string): void {
    if (!clientErrors[field]) return;
    const nextErrors = { ...clientErrors };
    delete nextErrors[field];
    clientErrors = nextErrors;
  }
</script>

<form class="space-y-8" novalidate onsubmit={handleSubmit}>
  {#if Object.keys(clientErrors).length > 0}
    <Alert
      variant="error"
      title="Correct the highlighted fields"
      message="Review the Azure DevOps environment configuration before saving."
    />
  {/if}

  <fieldset class="space-y-6">
    <legend class="text-base font-semibold text-white">Environment identity</legend>

    <div class="grid gap-6 lg:grid-cols-2">
      <Field
        id="ado-key"
        label="Environment key"
        hint={mode === 'edit'
          ? 'Keys are immutable. Create a new environment to use a different key.'
          : 'Use a stable key. It cannot be changed after creation.'}
        error={fieldError('key')}
        required
      >
        <input
          id="ado-key"
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
        id="ado-displayName"
        label="Display name"
        hint="A recognizable name for operators choosing an environment."
        error={fieldError('displayName')}
        required
      >
        <input
          id="ado-displayName"
          name="displayName"
          class={inputClasses}
          bind:value={values.displayName}
          disabled={submitting}
          required
          autocomplete="off"
          aria-invalid={fieldError('displayName') ? 'true' : undefined}
          aria-describedby={describedBy('displayName', true)}
          oninput={() => clearClientError('displayName')}
        />
      </Field>
    </div>

    <div class="rounded-xl border border-slate-800 bg-slate-950/40 p-4">
      <label class="flex min-h-8 cursor-pointer items-start gap-3" for="ado-enabled">
        <input
          id="ado-enabled"
          name="enabled"
          type="checkbox"
          class="mt-0.5 size-5 rounded border-slate-600 bg-slate-950 text-cyan-400"
          bind:checked={values.enabled}
          disabled={submitting}
          aria-describedby="ado-enabled-hint"
        />
        <span class="block text-sm font-medium text-slate-100">Enabled for new work</span>
      </label>
      <p id="ado-enabled-hint" class="mt-1 ml-8 text-sm leading-6 text-slate-400">
        Disabled environments remain configured but cannot be selected for execution.
      </p>
      {#if fieldError('enabled')}
        <p class="mt-2 ml-8 text-sm font-medium text-rose-300">{fieldError('enabled')}</p>
      {/if}
    </div>
  </fieldset>

  <fieldset class="space-y-6 border-t border-slate-800 pt-7">
    <legend class="text-base font-semibold text-white">Azure DevOps connection</legend>

    <div class="grid gap-6 lg:grid-cols-2">
      <Field
        id="ado-organizationUrl"
        label="Organization URL"
        hint="The Azure DevOps organization root URL, without a query or fragment."
        error={fieldError('organizationUrl')}
        required
      >
        <input
          id="ado-organizationUrl"
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
          aria-describedby={describedBy('organizationUrl', true)}
          oninput={() => clearClientError('organizationUrl')}
        />
      </Field>

      <Field
        id="ado-project"
        label="Project"
        hint="The Azure DevOps project that contains the board."
        error={fieldError('project')}
        required
      >
        <input
          id="ado-project"
          name="project"
          class={inputClasses}
          bind:value={values.project}
          disabled={submitting}
          required
          autocomplete="off"
          aria-invalid={fieldError('project') ? 'true' : undefined}
          aria-describedby={describedBy('project', true)}
          oninput={() => clearClientError('project')}
        />
      </Field>
    </div>

    <Field
      id="ado-patEnvironmentVariable"
      label="PAT environment-variable reference"
      hint="Enter the name of an environment variable available to Agent Controller, for example ADO_PAT."
      error={fieldError('patEnvironmentVariable')}
      required
    >
      <input
        id="ado-patEnvironmentVariable"
        name="patEnvironmentVariable"
        class={inputClasses}
        bind:value={values.patEnvironmentVariable}
        disabled={submitting}
        required
        spellcheck="false"
        autocomplete="off"
        placeholder="ADO_PAT"
        aria-invalid={fieldError('patEnvironmentVariable') ? 'true' : undefined}
        aria-describedby={describedBy('patEnvironmentVariable', true)}
        oninput={() => clearClientError('patEnvironmentVariable')}
      />
    </Field>

    <Alert
      variant="info"
      title="Secret values are not stored"
      message="Agent Controller stores only the environment-variable name. Enter the PAT in the runtime environment, never in this form."
    />
  </fieldset>

  <fieldset class="space-y-6 border-t border-slate-800 pt-7">
    <legend class="text-base font-semibold text-white">Board policy</legend>
    <p class="text-sm leading-6 text-slate-400">
      Enter one tag or state per line. Leave an eligible list empty to avoid filtering on that value.
    </p>

    <Field
      id="ado-workItemType"
      label="Work-item type"
      hint="The board work-item type Agent Controller polls."
      error={fieldError('workItemType')}
      required
    >
      <input
        id="ado-workItemType"
        name="workItemType"
        class={inputClasses}
        bind:value={values.workItemType}
        disabled={submitting}
        required
        autocomplete="off"
        aria-invalid={fieldError('workItemType') ? 'true' : undefined}
        aria-describedby={describedBy('workItemType', true)}
        oninput={() => clearClientError('workItemType')}
      />
    </Field>

    <div class="grid gap-6 lg:grid-cols-2">
      <Field
        id="ado-eligibleTags"
        label="Eligible tags"
        hint="A work item must match the configured eligible tags."
        error={fieldError('eligibleTags')}
      >
        <textarea
          id="ado-eligibleTags"
          name="eligibleTags"
          class={`${inputClasses} min-h-28 resize-y`}
          bind:value={values.eligibleTags}
          disabled={submitting}
          placeholder={'agent-ready\nautonomous'}
          aria-invalid={fieldError('eligibleTags') ? 'true' : undefined}
          aria-describedby={describedBy('eligibleTags', true)}
          oninput={() => clearClientError('eligibleTags')}
        ></textarea>
      </Field>

      <Field
        id="ado-excludedTags"
        label="Excluded tags"
        hint="A matching excluded tag prevents execution."
        error={fieldError('excludedTags')}
      >
        <textarea
          id="ado-excludedTags"
          name="excludedTags"
          class={`${inputClasses} min-h-28 resize-y`}
          bind:value={values.excludedTags}
          disabled={submitting}
          placeholder={'agent-active\nmanual-only'}
          aria-invalid={fieldError('excludedTags') ? 'true' : undefined}
          aria-describedby={describedBy('excludedTags', true)}
          oninput={() => clearClientError('excludedTags')}
        ></textarea>
      </Field>

      <Field
        id="ado-eligibleStates"
        label="Eligible states"
        hint="Board states from which work may be selected."
        error={fieldError('eligibleStates')}
      >
        <textarea
          id="ado-eligibleStates"
          name="eligibleStates"
          class={`${inputClasses} min-h-28 resize-y`}
          bind:value={values.eligibleStates}
          disabled={submitting}
          placeholder={'New\nApproved'}
          aria-invalid={fieldError('eligibleStates') ? 'true' : undefined}
          aria-describedby={describedBy('eligibleStates', true)}
          oninput={() => clearClientError('eligibleStates')}
        ></textarea>
      </Field>

      <Field
        id="ado-excludedStates"
        label="Excluded states"
        hint="Board states that must never be selected."
        error={fieldError('excludedStates')}
      >
        <textarea
          id="ado-excludedStates"
          name="excludedStates"
          class={`${inputClasses} min-h-28 resize-y`}
          bind:value={values.excludedStates}
          disabled={submitting}
          placeholder={'Closed\nRemoved'}
          aria-invalid={fieldError('excludedStates') ? 'true' : undefined}
          aria-describedby={describedBy('excludedStates', true)}
          oninput={() => clearClientError('excludedStates')}
        ></textarea>
      </Field>
    </div>

    <div class="grid gap-6 lg:grid-cols-2">
      <Field
        id="ado-activeState"
        label="Active state"
        hint="Optional state applied when Agent Controller begins work."
        error={fieldError('activeState')}
      >
        <input
          id="ado-activeState"
          name="activeState"
          class={inputClasses}
          bind:value={values.activeState}
          disabled={submitting}
          autocomplete="off"
          placeholder="Active"
          aria-invalid={fieldError('activeState') ? 'true' : undefined}
          aria-describedby={describedBy('activeState', true)}
          oninput={() => clearClientError('activeState')}
        />
      </Field>

      <Field
        id="ado-completedState"
        label="Completed state"
        hint="Optional state applied when Agent Controller completes work."
        error={fieldError('completedState')}
      >
        <input
          id="ado-completedState"
          name="completedState"
          class={inputClasses}
          bind:value={values.completedState}
          disabled={submitting}
          autocomplete="off"
          placeholder="Resolved"
          aria-invalid={fieldError('completedState') ? 'true' : undefined}
          aria-describedby={describedBy('completedState', true)}
          oninput={() => clearClientError('completedState')}
        />
      </Field>
    </div>
  </fieldset>

  <div class="flex flex-col-reverse gap-3 border-t border-slate-800 pt-6 sm:flex-row sm:justify-end">
    <Button variant="secondary" onclick={oncancel} disabled={submitting}>Cancel</Button>
    <Button type="submit" disabled={submitting}>
      {submitting ? 'Saving…' : mode === 'create' ? 'Create environment' : 'Save changes'}
    </Button>
  </div>
</form>
