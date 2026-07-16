<script lang="ts">
  import type { SecretInfo } from '../../api/types';
  import type { SecretsResourceClient } from '../../api/client';

  let {
    client,
    secretName = $bindable(''),
    secretVersion = $bindable(null),
    disabled = false,
    error = undefined,
    id = 'secret-picker',
  }: {
    client: SecretsResourceClient;
    secretName?: string;
    secretVersion?: number | null;
    disabled?: boolean;
    error?: string;
    id?: string;
  } = $props();

  const inputId = `${id}-input`;
  const listId = `${id}-list`;
  const versionId = `${id}-version`;

  let secrets = $state<SecretInfo[]>([]);
  let loading = $state(false);
  let dropdownOpen = $state(false);
  let filterText = $state('');
  let loadError = $state<string>();

  const filteredSecrets = $derived(
    filterText.trim()
      ? secrets.filter((s) =>
          s.name.toLowerCase().includes(filterText.trim().toLowerCase()),
        )
      : secrets,
  );

  $effect(() => {
    loadSecrets();
  });

  async function loadSecrets(): Promise<void> {
    loading = true;
    loadError = undefined;
    try {
      secrets = await client.list();
    } catch {
      loadError = 'Could not load secrets. Try again.';
    } finally {
      loading = false;
    }
  }

  function selectSecret(secret: SecretInfo): void {
    secretName = secret.name;
    filterText = '';
    dropdownOpen = false;
  }

  function openDropdown(): void {
    dropdownOpen = true;
    filterText = secretName;
  }

  function closeDropdown(): void {
    dropdownOpen = false;
    filterText = '';
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
      aria-labelledby={id}
      onmousedown={(event) => {
        event.preventDefault();
        openDropdown();
      }}
      onclick={openDropdown}
    >
      {#if secretName}
        <span class="truncate">{secretName}</span>
        {#if secretVersion}
          <span class="ml-1 text-xs text-slate-500">v{secretVersion}</span>
        {/if}
      {:else}
        <span class="text-slate-600">Select a secret…</span>
      {/if}
    </button>

    {#if dropdownOpen}
      <div
        id={listId}
        role="listbox"
        tabindex="-1"
        class="absolute z-50 mt-1 max-h-60 w-full overflow-auto rounded-lg border border-slate-700 bg-slate-900 shadow-xl"
      >
        <div class="sticky top-0 border-b border-slate-700 bg-slate-900 p-2">
          <input
            type="text"
            class="w-full rounded-md border border-slate-700 bg-slate-950 px-2 py-1 text-sm text-slate-100 placeholder:text-slate-600 focus:border-cyan-400 focus:outline-none focus:ring-1 focus:ring-cyan-400"
            placeholder="Filter secrets…"
            bind:value={filterText}
            onkeydown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                const first = filteredSecrets[0];
                if (first) selectSecret(first);
              }
            }}
            onfocus={(event) => event.currentTarget.select()}
          />
        </div>

        {#if loading}
          <div class="px-3 py-2 text-sm text-slate-400">Loading secrets…</div>
        {:else if loadError}
          <div class="px-3 py-2 text-sm text-rose-300">{loadError}</div>
        {:else if filteredSecrets.length === 0}
          <div class="px-3 py-2 text-sm text-slate-400">
            {filterText ? 'No secrets match your filter.' : 'No secrets available.'}
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
                Latest: v{secret.latestVersion}
              </span>
            </button>
          {/each}
        {/if}
      </div>
    {/if}
  </div>

  <div class="flex items-center gap-2">
    <label
      for={versionId}
      class="text-sm font-medium text-slate-400"
    >
      Pin version
    </label>
    <input
      id={versionId}
      type="number"
      min="1"
      class="min-h-8 w-24 rounded-lg border border-slate-700 bg-slate-950 px-2 py-1 text-sm text-slate-100 placeholder:text-slate-600 disabled:cursor-not-allowed disabled:bg-slate-900 disabled:text-slate-400 focus:border-cyan-400 focus:outline-none focus:ring-1 focus:ring-cyan-400"
      placeholder="Latest"
      bind:value={secretVersion}
      disabled={disabled || !secretName}
      oninput={() => {
        const val = secretVersion;
        if (val !== null && val < 1) secretVersion = null;
      }}
    />
    <span class="text-xs text-slate-500">
      {secretVersion ? `v${secretVersion}` : 'Latest'}
    </span>
  </div>

  {#if error}
    <p class="text-sm font-medium text-rose-300" id={`${id}-error`}>{error}</p>
  {/if}
</div>
