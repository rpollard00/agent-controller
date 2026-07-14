<script lang="ts">
  import { untrack } from 'svelte';
  import type { RuntimeEnvironmentProfile } from '../../api/types';
  import Alert from '../../components/ui/Alert.svelte';
  import Button from '../../components/ui/Button.svelte';
  import Field from '../../components/ui/Field.svelte';
  import EnvironmentVariableEditor from './EnvironmentVariableEditor.svelte';
  import LoadoutEditor from './LoadoutEditor.svelte';
  import {
    createRuntimeEnvironmentFormValues,
    toRuntimeEnvironmentProfile,
    validateRuntimeEnvironmentForm,
    type EnvironmentVariableRow,
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
  let nextRowId = values.loadouts.length + values.forwardEnvironmentVariables.length;

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

  function addVariableMapping(): void {
    values.forwardEnvironmentVariables = [
      ...values.forwardEnvironmentVariables,
      { id: `variable-new-${nextRowId++}`, target: '', source: '' },
    ];
    clearClientError('runtimeSettings.forwardEnvironmentVariables');
  }

  function updateVariableMapping(id: string, patch: Partial<EnvironmentVariableRow>): void {
    values.forwardEnvironmentVariables = values.forwardEnvironmentVariables.map((row) =>
      row.id === id ? { ...row, ...patch } : row,
    );
    clearClientError('runtimeSettings.forwardEnvironmentVariables');
  }

  function removeVariableMapping(id: string): void {
    values.forwardEnvironmentVariables = values.forwardEnvironmentVariables.filter(
      (row) => row.id !== id,
    );
    clearClientError('runtimeSettings.forwardEnvironmentVariables');
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
        hint={mode === 'edit'
          ? 'Environment names are immutable. Create a new environment to use a different name.'
          : 'Use 1 to 32 characters starting with an ASCII letter, followed by ASCII letters, numbers, hyphens, or underscores. It cannot be changed after creation.'}
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
          aria-invalid={fieldError('key') ? 'true' : undefined}
          aria-describedby={describedBy('runtime-key', 'key', true)}
          oninput={() => clearClientError('key')}
        />
      </Field>

      <Field
        id="runtime-displayName"
        label="Display name"
        hint="A recognizable name for operators choosing a runtime environment."
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
          aria-invalid={fieldError('displayName') ? 'true' : undefined}
          aria-describedby={describedBy('runtime-displayName', 'displayName', true)}
          oninput={() => clearClientError('displayName')}
        />
      </Field>
    </div>

    <div class="rounded-xl border border-slate-800 bg-slate-950/40 p-4">
      <label class="flex min-h-8 cursor-pointer items-start gap-3" for="runtime-enabled">
        <input
          id="runtime-enabled"
          name="enabled"
          type="checkbox"
          class="mt-0.5 size-5 rounded border-slate-600 bg-slate-950 text-cyan-400"
          bind:checked={values.enabled}
          disabled={submitting}
          aria-describedby="runtime-enabled-hint"
        />
        <span class="block text-sm font-medium text-slate-100">Enabled</span>
      </label>
      <p id="runtime-enabled-hint" class="mt-1 ml-8 text-sm leading-6 text-slate-400">
        Disabled runtime environments remain configured but cannot be selected for execution.
      </p>
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
      <div class="grid gap-6 lg:grid-cols-2">
        <Field
          id="runtime-piExecutablePath"
          label="Pi executable"
          hint="An executable name resolved through PATH or an absolute path."
          error={fieldError('runtimeSettings.piExecutablePath')}
          required
        >
          <input
            id="runtime-piExecutablePath"
            name="piExecutablePath"
            class={inputClasses}
            bind:value={values.piExecutablePath}
            disabled={submitting}
            required
            spellcheck="false"
            autocomplete="off"
            placeholder="pi"
            aria-invalid={fieldError('runtimeSettings.piExecutablePath') ? 'true' : undefined}
            aria-describedby={describedBy(
              'runtime-piExecutablePath',
              'runtimeSettings.piExecutablePath',
              true,
            )}
            oninput={() => clearClientError('runtimeSettings.piExecutablePath')}
          />
        </Field>

        <Field
          id="runtime-controllerBaseUrl"
          label="Controller base URL"
          hint="The public Agent Controller origin used for runtime event callbacks."
          error={fieldError('runtimeSettings.controllerBaseUrl')}
          required
        >
          <input
            id="runtime-controllerBaseUrl"
            name="controllerBaseUrl"
            type="url"
            class={inputClasses}
            bind:value={values.controllerBaseUrl}
            disabled={submitting}
            required
            spellcheck="false"
            autocomplete="url"
            placeholder="http://localhost:5103"
            aria-invalid={fieldError('runtimeSettings.controllerBaseUrl') ? 'true' : undefined}
            aria-describedby={describedBy(
              'runtime-controllerBaseUrl',
              'runtimeSettings.controllerBaseUrl',
              true,
            )}
            oninput={() => clearClientError('runtimeSettings.controllerBaseUrl')}
          />
        </Field>

        <Field
          id="runtime-ptyWrapperPath"
          label="PTY wrapper executable"
          hint="Optional executable used to allocate a pseudo-terminal. Leave empty to launch pi directly."
          error={fieldError('runtimeSettings.ptyWrapperPath')}
        >
          <input
            id="runtime-ptyWrapperPath"
            name="ptyWrapperPath"
            class={inputClasses}
            bind:value={values.ptyWrapperPath}
            disabled={submitting}
            spellcheck="false"
            autocomplete="off"
            placeholder="script"
            aria-invalid={fieldError('runtimeSettings.ptyWrapperPath') ? 'true' : undefined}
            aria-describedby={describedBy(
              'runtime-ptyWrapperPath',
              'runtimeSettings.ptyWrapperPath',
              true,
            )}
            oninput={() => clearClientError('runtimeSettings.ptyWrapperPath')}
          />
        </Field>

        <Field
          id="runtime-ptyWrapperArgs"
          label="PTY wrapper arguments"
          hint="Arguments passed before the pi command; ignored when no wrapper is configured."
          error={fieldError('runtimeSettings.ptyWrapperArgs')}
        >
          <input
            id="runtime-ptyWrapperArgs"
            name="ptyWrapperArgs"
            class={inputClasses}
            bind:value={values.ptyWrapperArgs}
            disabled={submitting}
            spellcheck="false"
            autocomplete="off"
            placeholder="-qfc"
            aria-invalid={fieldError('runtimeSettings.ptyWrapperArgs') ? 'true' : undefined}
            aria-describedby={describedBy(
              'runtime-ptyWrapperArgs',
              'runtimeSettings.ptyWrapperArgs',
              true,
            )}
            oninput={() => clearClientError('runtimeSettings.ptyWrapperArgs')}
          />
        </Field>
      </div>

      <div class="border-t border-slate-800 pt-6">
        <LoadoutEditor
          rows={values.loadouts}
          disabled={submitting}
          error={fieldError('runtimeSettings.loadouts')}
          onadd={addLoadout}
          onremove={removeLoadout}
          onrowchange={updateLoadout}
        />
      </div>

      <div class="border-t border-slate-800 pt-6">
        <EnvironmentVariableEditor
          rows={values.forwardEnvironmentVariables}
          disabled={submitting}
          error={fieldError('runtimeSettings.forwardEnvironmentVariables')}
          onadd={addVariableMapping}
          onremove={removeVariableMapping}
          onrowchange={updateVariableMapping}
        />
      </div>
    {:else}
      <Alert
        variant="info"
        title="No process settings required"
        message="Mock Pi Materia simulates execution and does not use Pi executable, controller callback, PTY, loadout, or environment-variable forwarding settings."
      />
    {/if}
  </fieldset>

  <div class="flex flex-col-reverse gap-3 border-t border-slate-800 pt-6 sm:flex-row sm:justify-end">
    <Button variant="secondary" onclick={oncancel} disabled={submitting}>Cancel</Button>
    <Button type="submit" disabled={submitting}>
      {submitting ? 'Saving…' : mode === 'create' ? 'Create environment' : 'Save changes'}
    </Button>
  </div>
</form>
