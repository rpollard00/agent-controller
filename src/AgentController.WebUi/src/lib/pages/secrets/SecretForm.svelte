<script lang="ts">
  import type {
    CreateSecretPayload,
    CreateSecretRequest,
    CreateSecretVersionRequest,
    SecretType,
  } from '../../api/types';
  import Button from '../../components/ui/Button.svelte';
  import Field from '../../components/ui/Field.svelte';

  let {
    mode,
    secretType,
    submitting,
    serverErrors,
    onsave,
    oncancel,
  }: {
    mode: 'create' | 'newVersion';
    /** The immutable secret type when creating a new version; undefined for create mode. */
    secretType?: SecretType;
    submitting: boolean;
    serverErrors?: Readonly<Record<string, string[]>>;
    onsave: (request: CreateSecretRequest | CreateSecretVersionRequest) => void;
    oncancel: () => void;
  } = $props();

  let name = $state('');
  let selectedType = $state<SecretType>('personal-access-token');
  let patValue = $state('');
  let privateKey = $state('');
  let publicKey = $state('');
  let passphrase = $state('');
  let usePassphrase = $state(false);

  let nameTouched = $state(false);
  let patValueTouched = $state(false);
  let privateKeyTouched = $state(false);
  let publicKeyTouched = $state(false);
  let passphraseTouched = $state(false);

  // Determine whether we can change the type (only during creation)
  const typeLocked = $derived(mode !== 'create');
  const effectiveType = $derived(typeLocked ? (secretType ?? selectedType) : selectedType);

  const nameError = $derived(
    mode !== 'create'
      ? undefined
      : firstServerError('name')
        || (nameTouched && !name.trim() ? 'Secret name is required.' : undefined)
        || (nameTouched && name.trim().length > 256 ? 'Secret name must be 256 characters or fewer.' : undefined),
  );

  const patValueError = $derived(
    effectiveType !== 'personal-access-token'
      ? undefined
      : firstServerError('value')
        || (patValueTouched && !patValue ? 'Secret value is required.' : undefined),
  );

  const privateKeyError = $derived(
    effectiveType !== 'ssh-key'
      ? undefined
      : firstServerError('privateKey')
        || (privateKeyTouched && !privateKey ? 'Private key is required.' : undefined),
  );

  const publicKeyError = $derived(
    effectiveType !== 'ssh-key'
      ? undefined
      : firstServerError('publicKey')
        || (publicKeyTouched && !publicKey ? 'Public key is required.' : undefined),
  );

  const passphraseError = $derived(
    effectiveType !== 'ssh-key' || !usePassphrase
      ? undefined
      : firstServerError('passphrase')
        || (passphraseTouched && !passphrase ? 'Passphrase is required when enabled.' : undefined),
  );

  function firstServerError(field: string): string | undefined {
    return serverErrors?.[field]?.[0] ?? serverErrors?.[`payload.${field}`]?.[0];
  }

  function buildPayload(): CreateSecretPayload | null {
    if (effectiveType === 'personal-access-token') {
      if (!patValue) return null;
      return { type: 'personal-access-token', value: patValue };
    }

    if (effectiveType === 'ssh-key') {
      if (!privateKey || !publicKey || (usePassphrase && !passphrase)) return null;
      return {
        type: 'ssh-key',
        privateKey,
        publicKey,
        passphrase: usePassphrase ? passphrase : null,
      };
    }

    return null;
  }

  function handleSubmit(): void {
    if (mode === 'create') nameTouched = true;
    if (effectiveType === 'personal-access-token') {
      patValueTouched = true;
    } else {
      privateKeyTouched = true;
      publicKeyTouched = true;
      passphraseTouched = true;
    }

    if (mode === 'create' && nameError) return;
    if (patValueError || privateKeyError || publicKeyError || passphraseError) return;

    const payload = buildPayload();
    if (!payload) return;

    if (mode === 'create') {
      onsave({ name: name.trim(), payload });
    } else {
      onsave({ payload });
    }
  }

</script>

<form
  class="space-y-6"
  onsubmit={(event) => { event.preventDefault(); handleSubmit(); }}
>
  {#if mode === 'create'}
    <Field
      id="secret-name"
      label="Secret name"
      required
      error={nameError}
      hint="A unique identifier for this secret. Used to reference it from other configurations."
    >
      <input
        id="secret-name"
        type="text"
        class="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-sm text-white placeholder-slate-500 focus:border-cyan-400 focus:outline-none focus:ring-1 focus:ring-cyan-400"
        placeholder="e.g. azure-devops-pat"
        value={name}
        oninput={(e) => { name = e.currentTarget.value; if (!nameTouched && name.length > 0) nameTouched = true; }}
        onblur={() => (nameTouched = true)}
      />
    </Field>

    <fieldset class="space-y-2">
      <legend class="text-sm font-medium text-slate-200">
        Secret type <span class="text-rose-300" aria-hidden="true">*</span>
        <span class="sr-only"> (required)</span>
      </legend>
      <div class="flex gap-3" role="radiogroup" aria-label="Secret type">
        <button
          type="button"
          role="radio"
          aria-checked={selectedType === 'personal-access-token'}
          class="flex-1 rounded-lg border px-4 py-3 text-sm font-medium transition-colors aria-checked:border-cyan-400 aria-checked:bg-slate-800 aria-checked:text-cyan-300 border-slate-700 bg-slate-900 text-slate-300 hover:border-slate-500"
          onclick={() => { selectedType = 'personal-access-token'; patValueTouched = false; }}
        >
          <span class="block font-semibold">Personal Access Token</span>
          <span class="mt-1 block text-xs text-slate-500">A single password/token value</span>
        </button>
        <button
          type="button"
          role="radio"
          aria-checked={selectedType === 'ssh-key'}
          class="flex-1 rounded-lg border px-4 py-3 text-sm font-medium transition-colors aria-checked:border-cyan-400 aria-checked:bg-slate-800 aria-checked:text-cyan-300 border-slate-700 bg-slate-900 text-slate-300 hover:border-slate-500"
          onclick={() => { selectedType = 'ssh-key'; }}
        >
          <span class="block font-semibold">SSH Key</span>
          <span class="mt-1 block text-xs text-slate-500">Private + public key pair</span>
        </button>
      </div>
      <p class="text-sm text-slate-400">Select the type of credential to store.</p>
    </fieldset>
  {:else}
    <!-- type locked for version creation -->
    <Field
      id="secret-type-readonly"
      label="Secret type"
    >
      <div class="flex items-center gap-2 rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-sm text-slate-400">
        {#if secretType === 'ssh-key'}
          <span class="inline-flex rounded-full bg-emerald-900/50 px-2.5 py-0.5 text-xs font-semibold text-emerald-300">SSH Key</span>
        {:else}
          <span class="inline-flex rounded-full bg-cyan-900/50 px-2.5 py-0.5 text-xs font-semibold text-cyan-300">PAT</span>
        {/if}
        <span class="text-slate-500">Type is immutable once set.</span>
      </div>
    </Field>
  {/if}

  {#if effectiveType === 'personal-access-token'}
    <Field
      id="secret-value"
      label="Secret value"
      required
      error={patValueError}
      hint={mode === 'create'
        ? 'The plaintext token value. It will be encrypted at rest and never displayed again.'
        : 'The new plaintext token value. It will be encrypted at rest and never displayed again.'}
    >
      <input
        id="secret-value"
        type="password"
        class="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-sm text-white placeholder-slate-500 focus:border-cyan-400 focus:outline-none focus:ring-1 focus:ring-cyan-400"
        placeholder={mode === 'create' ? 'Enter token value' : 'Enter new token value'}
        value={patValue}
        oninput={(e) => { patValue = e.currentTarget.value; if (!patValueTouched && patValue.length > 0) patValueTouched = true; }}
        onblur={() => (patValueTouched = true)}
      />
    </Field>
  {:else}
    <!-- SSH key fields -->
    <Field
      id="secret-private-key"
      label="Private key"
      required
      error={privateKeyError}
      hint="The PEM-encoded RSA or Ed25519 private key, including -----BEGIN ...----- markers."
    >
      <textarea
        id="secret-private-key"
        class="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-sm font-mono text-white placeholder-slate-500 focus:border-cyan-400 focus:outline-none focus:ring-1 focus:ring-cyan-400"
        rows="6"
        placeholder="-----BEGIN OPENSSH PRIVATE KEY-----&#10;..."
        value={privateKey}
        oninput={(e) => { privateKey = e.currentTarget.value; if (!privateKeyTouched && privateKey.length > 0) privateKeyTouched = true; }}
        onblur={() => (privateKeyTouched = true)}
      ></textarea>
    </Field>

    <Field
      id="secret-public-key"
      label="Public key"
      required
      error={publicKeyError}
      hint="The corresponding public key, typically ending in a comment like user@host."
    >
      <input
        id="secret-public-key"
        type="text"
        class="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-sm text-white placeholder-slate-500 focus:border-cyan-400 focus:outline-none focus:ring-1 focus:ring-cyan-400"
        placeholder="ssh-ed25519 AAAAC3... user@host"
        value={publicKey}
        oninput={(e) => { publicKey = e.currentTarget.value; if (!publicKeyTouched && publicKey.length > 0) publicKeyTouched = true; }}
        onblur={() => (publicKeyTouched = true)}
      />
    </Field>

    <div class="space-y-2">
      <div class="flex items-center gap-3">
        <label class="text-sm font-medium text-slate-200" for="secret-passphrase-enable">
          Passphrase
        </label>
        <button
          id="secret-passphrase-enable"
          type="button"
          role="switch"
          aria-checked={usePassphrase}
          aria-label="Use passphrase"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer items-center rounded-full border border-slate-700 transition-colors aria-checked:border-cyan-400 aria-checked:bg-cyan-400/20"
          onclick={() => {
            usePassphrase = !usePassphrase;
            passphraseTouched = false;
            if (!usePassphrase) passphrase = '';
          }}
        >
          <span
            class={`inline-block size-4 rounded-full transition-transform ${
              usePassphrase ? 'translate-x-6 bg-cyan-400' : 'translate-x-1 bg-slate-500'
            }`}
            aria-hidden="true"
          ></span>
        </button>
        <span class="text-sm text-slate-400">
          {usePassphrase ? 'Encrypted key' : 'No passphrase'}
        </span>
      </div>
      {#if usePassphrase}
        <Field
          id="secret-passphrase"
          label="Private key passphrase"
          required
          error={passphraseError}
          hint="The passphrase is stored encrypted alongside the key and is never displayed again."
        >
          <input
            id="secret-passphrase"
            type="password"
            class="mt-1 w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-sm text-white placeholder-slate-500 focus:border-cyan-400 focus:outline-none focus:ring-1 focus:ring-cyan-400"
            placeholder="Enter passphrase"
            value={passphrase}
            oninput={(e) => {
              passphrase = e.currentTarget.value;
              if (!passphraseTouched && passphrase.length > 0) passphraseTouched = true;
            }}
            onblur={() => (passphraseTouched = true)}
          />
        </Field>
      {:else}
        <p class="text-xs text-slate-500">No passphrase will be stored for this key.</p>
      {/if}
    </div>
  {/if}

  <div class="flex justify-end gap-3 pt-2">
    <Button variant="secondary" type="button" onclick={oncancel} disabled={submitting}>
      Cancel
    </Button>
    <Button
      type="submit"
      disabled={submitting}
    >
      {submitting
        ? (mode === 'create' ? 'Creating…' : 'Creating version…')
        : (mode === 'create' ? 'Create secret' : 'Create version')}
    </Button>
  </div>
</form>
