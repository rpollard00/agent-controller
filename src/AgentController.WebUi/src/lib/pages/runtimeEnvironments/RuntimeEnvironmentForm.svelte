<script lang="ts">
  import { untrack } from 'svelte';
  import type { RuntimeEnvironmentProfile } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Field from '../../components/ui/Field.svelte';
  import LoadoutEditor from './LoadoutEditor.svelte';
  import {
    createRuntimeEnvironmentFormValues,
    toRuntimeEnvironmentProfile,
    validateRuntimeEnvironmentForm,
    type LoadoutRow,
    type RuntimeEnvironmentFormErrors,
  } from './runtimeEnvironmentForm';

  let {
    mode,
    profile,
    submitting = false,
    serverErrors = {},
    onsave,
    oncancel,
  }: {
    mode: 'create' | 'edit';
    profile?: RuntimeEnvironmentProfile;
    submitting?: boolean;
    serverErrors?: Readonly<Record<string, string[]>>;
    onsave: (profile: RuntimeEnvironmentProfile) => void;
    oncancel: () => void;
  } = $props();

  let values = $state(untrack(() => createRuntimeEnvironmentFormValues(profile)));
  let clientErrors = $state<RuntimeEnvironmentFormErrors>({});
  let nextRowId = values.loadouts.length;

  const inputClasses =
    'min-h-11 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-slate-100 placeholder:text-slate-600 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400';

  function fieldError(field: string): string | undefined {
    return clientErrors[field]?.[0] ?? serverErrors[field]?.[0];
  }

  function describedBy(id: string, field: string, hasHint = false): string | undefined {
    const ids: string[] = [];
    if (hasHint) ids.push(`${id}-hint`);
    if (fieldError(field)) ids.push(`${id}-error`);
    return ids.length > 0 ? ids.join(' ') : undefined;
  }

  function handleSubmit(event: SubmitEvent): void {
    event.preventDefault();
    clientErrors = validateRuntimeEnvironmentForm(values);
    if (Object.keys(clientErrors).length > 0) return;

    onsave(toRuntimeEnvironmentProfile(values, profile));
  }

  function clearClientError(field: string): void {
    if (!clientErrors[field]) return;
    const nextErrors = { ...clientErrors };
    delete nextErrors[field];
    clientErrors = nextErrors;
  }

  function addLoadout(): void {
    const mappedKinds = new Set(values.loadouts.map((row) => row.executionKind));
    const executionKind = !mappedKinds.has('newWork')
      ? 'newWork'
      : !mappedKinds.has('rework')
        ? 'rework'
        : '';
    values.loadouts = [
      ...values.loadouts,
      { id: `loadout-new-${nextRowId++}`, executionKind, loadout: '' },
    ];
    clearClientError('runtimeSettings.loadouts');
  }

  function updateLoadout(id: string, patch: Partial<LoadoutRow>): void {
    values.loadouts = values.loadouts.map((row) => (row.id === id ? { ...row, ...patch } : row));
    clearClientError('runtimeSettings.loadouts');
  }

  function removeLoadout(id: string): void {
    values.loadouts = values.loadouts.filter((row) => row.id !== id);
    clearClientError('runtimeSettings.loadouts');
  }
</script>

<form class="space-y-8" novalidate onsubmit={handleSubmit}>
  {#if Object.keys(clientErrors).length > 0}
    <Alert
      variant="error"
      title="Correct the highlighted fields"
      message="Review the runtime environment configuration before saving."
    />
  {/if}

  <fieldset class="space-y-6">
    <legend class="text-base font-semibold text-white">Environment identity</legend>

    <div class="grid gap-6 lg:grid-cols-2">
      <Field
        id="runtime-key"
        label="Environment name"
        error={fieldError('key')}
        required
      >
        <input
          id="runtime-key"
          name="key"
          class={inputClasses}
          bind:value={values.key}
          readonly={mode === 'edit'}
          disabled={submitting}
          required
          autocomplete="off"
          placeholder="contoso-dev"
          aria-invalid={fieldError('key') ? 'true' : undefined}
          aria-describedby={describedBy('runtime-key', 'key')}
          oninput={() => clearClientError('key')}
        />
      </Field>

      <Field
        id="runtime-displayName"
        label="Display name"
        error={fieldError('displayName')}
        required
      >
        <input
          id="runtime-displayName"
          name="displayName"
          class={inputClasses}
          bind:value={values.displayName}
          disabled={submitting}
          required
          autocomplete="off"
          placeholder="Contoso Software Development"
          aria-invalid={fieldError('displayName') ? 'true' : undefined}
          aria-describedby={describedBy('runtime-displayName', 'displayName')}
          oninput={() => clearClientError('displayName')}
        />
      </Field>
    </div>

  </fieldset>

  <fieldset class="space-y-6 border-t border-slate-800 pt-7">
    <legend class="text-base font-semibold text-white">Workspace provider</legend>

    <div class="grid gap-6 lg:grid-cols-2">
      <Field
        id="runtime-environmentProvider"
        label="Environment provider"
        hint="The provider that provisions an isolated workspace for each run."
        error={fieldError('environmentProvider')}
        required
      >
        <select
          id="runtime-environmentProvider"
          name="environmentProvider"
          class={inputClasses}
          bind:value={values.environmentProvider}
          disabled={submitting}
          required
          aria-invalid={fieldError('environmentProvider') ? 'true' : undefined}
          aria-describedby={describedBy(
            'runtime-environmentProvider',
            'environmentProvider',
            true,
          )}
          onchange={() => clearClientError('environmentProvider')}
        >
          <option value="LocalWorkspace">Local workspace</option>
        </select>
      </Field>

      <Field
        id="runtime-workspaceRoot"
        label="Workspace root"
        hint="Optional parent directory for provisioned workspaces. The service default is used when empty."
        error={fieldError('environmentSettings.workspaceRoot')}
      >
        <input
          id="runtime-workspaceRoot"
          name="workspaceRoot"
          class={inputClasses}
          bind:value={values.workspaceRoot}
          disabled={submitting}
          spellcheck="false"
          autocomplete="off"
          placeholder="/var/lib/agent-controller/workspaces"
          aria-invalid={fieldError('environmentSettings.workspaceRoot') ? 'true' : undefined}
          aria-describedby={describedBy(
            'runtime-workspaceRoot',
            'environmentSettings.workspaceRoot',
            true,
          )}
          oninput={() => clearClientError('environmentSettings.workspaceRoot')}
        />
      </Field>
    </div>
  </fieldset>

  <fieldset class="space-y-6 border-t border-slate-800 pt-7">
    <legend class="text-base font-semibold text-white">Agent runtime</legend>

    <Field
      id="runtime-runtimeProvider"
      label="Runtime provider"
      hint="Pi Materia launches a real pi process; Mock Pi Materia is intended for simulated runs."
      error={fieldError('runtimeProvider')}
      required
    >
      <select
        id="runtime-runtimeProvider"
        name="runtimeProvider"
        class={inputClasses}
        bind:value={values.runtimeProvider}
        disabled={submitting}
        required
        aria-invalid={fieldError('runtimeProvider') ? 'true' : undefined}
        aria-describedby={describedBy('runtime-runtimeProvider', 'runtimeProvider', true)}
        onchange={() => clearClientError('runtimeProvider')}
      >
        <option value="PiMateria">Pi Materia</option>
        <option value="MockPiMateria">Mock Pi Materia</option>
      </select>
    </Field>

    {#if values.runtimeProvider === 'PiMateria'}
      <LoadoutEditor
        rows={values.loadouts}
        disabled={submitting}
        error={fieldError('runtimeSettings.loadouts')}
        onadd={addLoadout}
        onremove={removeLoadout}
        onrowchange={updateLoadout}
      />
    {:else}
      <Alert
        variant="info"
        title="No loadout mapping required"
        message="Mock Pi Materia simulates execution without launching a Pi process, so no loadout mapping is needed."
      />
    {/if}
  </fieldset>

  <div class="rounded-xl border border-slate-800 bg-slate-950/40 p-4">
    <label class="flex min-h-8 cursor-pointer items-start gap-3" for="runtime-enabled">
      <input
        id="runtime-enabled"
        name="enabled"
        type="checkbox"
        class="mt-0.5 size-5 rounded border-slate-600 bg-slate-950 text-cyan-400"
        bind:checked={values.enabled}
        disabled={submitting}
      />
      <span class="block text-sm font-medium text-slate-100">Enabled</span>
    </label>
  </div>

  <div class="flex flex-col-reverse gap-3 border-t border-slate-800 pt-6 sm:flex-row sm:justify-end">
    <Button variant="secondary" onclick={oncancel} disabled={submitting}>Cancel</Button>
    <Button type="submit" disabled={submitting}>
      {submitting ? 'Saving…' : mode === 'create' ? 'Create environment' : 'Save changes'}
    </Button>
  </div>
</form>
