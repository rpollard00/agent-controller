<script lang="ts">
  import Dialog from './Dialog.svelte';

  let {
    variant = 'default',
    loading = false,
    error,
    withExternalLink = false,
  }: {
    variant?: 'default' | 'wide';
    loading?: boolean;
    error?: string;
    withExternalLink?: boolean;
  } = $props();

  let open = $state(false);
  let retries = $state(0);
</script>

<button type="button" onclick={() => (open = true)}>Open diagnostics</button>
<span data-testid="retry-count">{retries}</span>

<Dialog
  {open}
  title={variant === 'wide' ? 'Pickup diagnostics' : 'Delete item?'}
  description={variant === 'default' ? 'This action cannot be undone.' : undefined}
  contextLine={variant === 'wide' ? 'Work item #42 · Project' : undefined}
  {variant}
  {loading}
  loadingLabel="Loading diagnostics…"
  {error}
  onretry={error ? () => (retries += 1) : undefined}
  externalLink={withExternalLink ? { href: 'https://example.test/item/42', label: 'View item' } : undefined}
  onclose={() => (open = false)}
>
  <button type="button">First detail action</button>
  <p>Diagnostic content</p>
  <button type="button">Last detail action</button>
  {#snippet actions()}
    <button type="button" onclick={() => (open = false)}>Cancel</button>
    <button type="button">Delete</button>
  {/snippet}
</Dialog>
