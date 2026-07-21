<script lang="ts">
  import type { SecretsResourceClient } from '../../api/client';
  import type { SecretInfo, SecretType, SecretVersionInfo } from '../../api/types';

  let {
    client,
    secretType,
    secretName = $bindable(''),
    secretVersion = $bindable(null),
    allowVersionPinning = true,
    disabled = false,
    error = undefined,
    id = 'secret-picker',
  }: {
    client: SecretsResourceClient;
    secretType: SecretType;
    secretName?: string;
    secretVersion?: number | null;
    allowVersionPinning?: boolean;
    disabled?: boolean;
    error?: string;
    id?: string;
  } = $props();

  const inputId = $derived(`${id}-input`);
  const listId = $derived(`${id}-list`);
  const versionId = $derived(`${id}-version`);

  let secrets = $state<SecretInfo[]>([]);
  let versions = $state<SecretVersionInfo[]>([]);
  let loading = $state(false);
  let versionsLoading = $state(false);
  let dropdownOpen = $state(false);
  let filterText = $state('');
  let loadError = $state<string>();
  let versionsError = $state<string>();
  let listReloadKey = $state(0);
  let versionReloadKey = $state(0);

  const typedSecrets = $derived(
    secrets.filter((secret) => secret.secretType === secretType),
  );
  const filteredSecrets = $derived(
    filterText.trim()
      ? typedSecrets.filter((secret) =>
          secret.name.toLowerCase().includes(filterText.trim().toLowerCase()),
        )
      : typedSecrets,
  );
  const selectedSecret = $derived(
    typedSecrets.find((secret) => secret.name === secretName),
  );
  const incompatibleSelectedSecret = $derived(
    secrets.find((secret) => secret.name === secretName && secret.secretType !== secretType),
  );
  const sortedVersions = $derived(
    versions
      .filter((version) => version.secretType === secretType)
      .toSorted((left, right) => right.version - left.version),
  );
  const selectedVersionIsListed = $derived(
    secretVersion !== null &&
      sortedVersions.some((version) => version.version === secretVersion),
  );
  const inputDescribedBy = $derived(
    [
      error ? `${id}-error` : undefined,
      incompatibleSelectedSecret ? `${id}-type-error` : undefined,
    ].filter(Boolean).join(' ') || undefined,
  );

  $effect(() => {
    void listReloadKey;
    const resourceClient = client;
    const controller = new AbortController();
    void loadSecrets(resourceClient, controller.signal);
    return () => controller.abort();
  });

  $effect(() => {
    void versionReloadKey;
    const secret = selectedSecret;
    if (!secret) {
      versions = [];
      versionsLoading = false;
      versionsError = undefined;
      return;
    }

    const resourceClient = client;
    const controller = new AbortController();
    void loadVersions(resourceClient, secret, controller.signal);
    return () => controller.abort();
  });

  async function loadSecrets(
    resourceClient: SecretsResourceClient,
    signal: AbortSignal,
  ): Promise<void> {
    loading = true;
    loadError = undefined;
    try {
      const loadedSecrets = await resourceClient.list(signal);
      if (signal.aborted) return;
      secrets = loadedSecrets;
    } catch (requestError) {
      if (signal.aborted || isAbortError(requestError)) return;
      loadError = 'Could not load secrets. Try again.';
    } finally {
      if (!signal.aborted) loading = false;
    }
  }

  async function loadVersions(
    resourceClient: SecretsResourceClient,
    secret: SecretInfo,
    signal: AbortSignal,
  ): Promise<void> {
    versions = [];
    versionsLoading = true;
    versionsError = undefined;
    try {
      const loadedVersions = await resourceClient.listVersions(secret.name, signal);
      if (signal.aborted) return;
      versions = loadedVersions.filter(
        (version) => version.secretType === secret.secretType,
      );
    } catch (requestError) {
      if (signal.aborted || isAbortError(requestError)) return;
      versionsError = `Could not load versions for “${secret.name}”.`;
    } finally {
      if (!signal.aborted) versionsLoading = false;
    }
  }

  function selectSecret(secret: SecretInfo): void {
    if (secret.name !== secretName) {
      secretVersion = null;
    }
    secretName = secret.name;
    filterText = '';
    dropdownOpen = false;
  }

  function selectVersion(event: Event): void {
    const value = (event.currentTarget as HTMLSelectElement).value;
    secretVersion = value ? Number(value) : null;
  }

  function openDropdown(): void {
    dropdownOpen = true;
    filterText = '';
  }

  function closeDropdown(): void {
    dropdownOpen = false;
    filterText = '';
  }

  function typeLabel(type: SecretType): string {
    return type === 'ssh-key' ? 'SSH key' : 'PAT';
  }

  function typeLabelWithArticle(type: SecretType): string {
    return type === 'ssh-key' ? 'an SSH key' : 'a PAT';
  }

  function isAbortError(requestError: unknown): boolean {
    return requestError instanceof DOMException && requestError.name === 'AbortError';
  }

  $effect(() => {
    if (!dropdownOpen) return;

    function onGlobalClick(event: MouseEvent) {
      const target = event.target as HTMLElement;
      if (!target.closest(`[data-secret-picker="${id}"]`)) {
        closeDropdown();
      }
    }

    document.addEventListener('click', onGlobalClick);
    return () => document.removeEventListener('click', onGlobalClick);
  });

  $effect(() => {
    if (!dropdownOpen) return;

    function onKeydown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        closeDropdown();
      }
    }

    document.addEventListener('keydown', onKeydown);
    return () => document.removeEventListener('keydown', onKeydown);
  });
</script>

<div data-secret-picker={id} class="space-y-3">
  <div class="relative">
    <button
      type="button"
      id={inputId}
      class="min-h-11 w-full rounded-lg border border-slate-700 bg-slate-950 px-3 py-2 text-left text-slate-100 placeholder:text-slate-600 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400 focus:border-cyan-400 focus:outline-none focus:ring-1 focus:ring-cyan-400"
      disabled={disabled}
      aria-haspopup="listbox"
      aria-expanded={dropdownOpen}
      aria-controls={listId}
      aria-describedby={inputDescribedBy}
      onmousedown={(event) => {
        event.preventDefault();
        openDropdown();
      }}
      onclick={openDropdown}
    >
      {#if secretName}
        <span class="block truncate">{secretName}</span>
        {#if selectedSecret}
          <span class="block truncate text-xs text-slate-500">
            {typeLabel(selectedSecret.secretType)} · {secretVersion
              ? `Pinned to v${secretVersion}`
              : `Latest (currently v${selectedSecret.latestVersion})`}
          </span>
        {:else if loading}
          <span class="block text-xs text-slate-500">Loading secret metadata…</span>
        {:else if incompatibleSelectedSecret}
          <span id={`${id}-type-error`} class="block text-xs text-rose-300">
            Selected secret type is {typeLabel(incompatibleSelectedSecret.secretType)}; expected
            {typeLabel(secretType)}.
          </span>
        {:else}
          <span class="block text-xs text-amber-300">
            Not an available {typeLabel(secretType)} secret
          </span>
        {/if}
      {:else}
        <span class="text-slate-600">Select {typeLabelWithArticle(secretType)} secret…</span>
      {/if}
    </button>

    {#if dropdownOpen}
      <div
        id={listId}
        role="listbox"
        tabindex="-1"
        aria-busy={loading}
        class="absolute z-50 mt-1 max-h-60 w-full overflow-auto rounded-lg border border-slate-700 bg-slate-900 shadow-xl"
      >
        <div class="sticky top-0 border-b border-slate-700 bg-slate-900 p-2">
          <input
            type="text"
            class="w-full rounded-md border border-slate-700 bg-slate-950 px-2 py-1 text-sm text-slate-100 placeholder:text-slate-600 focus:border-cyan-400 focus:outline-none focus:ring-1 focus:ring-cyan-400"
            placeholder={`Filter ${typeLabel(secretType)} secrets…`}
            aria-label={`Filter ${typeLabel(secretType)} secrets`}
            bind:value={filterText}
            onkeydown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                const first = filteredSecrets[0];
                if (first) selectSecret(first);
              }
            }}
          />
        </div>

        {#if loading}
          <div class="px-3 py-2 text-sm text-slate-400">Loading secrets…</div>
        {:else if loadError}
          <div class="space-y-2 px-3 py-2 text-sm text-rose-300">
            <p>{loadError}</p>
            <button
              type="button"
              class="font-semibold text-cyan-300 hover:text-cyan-200"
              onclick={() => { listReloadKey += 1; }}
            >
              Retry
            </button>
          </div>
        {:else if filteredSecrets.length === 0}
          <div class="px-3 py-2 text-sm text-slate-400">
            {filterText
              ? `No ${typeLabel(secretType)} secrets match your filter.`
              : `No ${typeLabel(secretType)} secrets available.`}
          </div>
        {:else}
          {#each filteredSecrets as secret (secret.name)}
            <button
              type="button"
              role="option"
              class="w-full px-3 py-2 text-left text-sm text-slate-100 hover:bg-slate-800 aria-selected:bg-slate-800"
              aria-selected={secret.name === secretName}
              onclick={() => selectSecret(secret)}
            >
              <span class="block truncate font-medium">{secret.name}</span>
              <span class="block truncate text-xs text-slate-500">
                {typeLabel(secret.secretType)} · Latest v{secret.latestVersion}
              </span>
            </button>
          {/each}
        {/if}
      </div>
    {/if}
  </div>

  {#if allowVersionPinning && selectedSecret}
    <div class="space-y-2">
      <div class="flex flex-wrap items-center gap-2">
        <label for={versionId} class="text-sm font-medium text-slate-400">
          Secret version
        </label>
        <select
          id={versionId}
          class="min-h-9 min-w-52 rounded-lg border border-slate-700 bg-slate-950 px-2 py-1 text-sm text-slate-100 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400 focus:border-cyan-400 focus:outline-none focus:ring-1 focus:ring-cyan-400"
          value={secretVersion === null ? '' : String(secretVersion)}
          disabled={disabled || versionsLoading}
          aria-describedby={`${versionId}-hint${versionsError ? ` ${versionId}-error` : ''}`}
          onchange={selectVersion}
        >
          <option value="">Latest (currently v{selectedSecret.latestVersion})</option>
          {#if secretVersion !== null && !selectedVersionIsListed}
            <option value={String(secretVersion)}>
              v{secretVersion} ({versionsLoading ? 'loading…' : 'unavailable'})
            </option>
          {/if}
          {#each sortedVersions as version (version.version)}
            <option value={String(version.version)}>
              v{version.version}{version.version === selectedSecret.latestVersion ? ' (current latest)' : ''}
            </option>
          {/each}
        </select>
      </div>
      <p id={`${versionId}-hint`} class="text-xs text-slate-500">
        Latest follows future rotations automatically; choose a version to pin it.
      </p>
      {#if versionsError}
        <div id={`${versionId}-error`} class="flex flex-wrap items-center gap-2 text-xs text-rose-300">
          <span>{versionsError}</span>
          <button
            type="button"
            class="font-semibold text-cyan-300 hover:text-cyan-200"
            onclick={() => { versionReloadKey += 1; }}
          >
            Retry
          </button>
        </div>
      {/if}
    </div>
  {/if}

  {#if error}
    <p class="text-sm font-medium text-rose-300" id={`${id}-error`}>{error}</p>
  {/if}
</div>
