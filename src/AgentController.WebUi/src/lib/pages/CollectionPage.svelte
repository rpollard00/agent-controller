<script lang="ts">
  import { onMount } from 'svelte';
  import { getErrorMessage, getFieldErrors } from '../api/client';
  import Alert from '../components/ui/Alert.svelte';
  import Button from '../components/ui/Button.svelte';
  import Card from '../components/ui/Card.svelte';

  let {
    title,
    description,
    singularName,
    pluralName,
    load,
  }: {
    title: string;
    description: string;
    singularName: string;
    pluralName: string;
    load: (signal?: AbortSignal) => Promise<unknown[]>;
  } = $props();

  let status = $state<'loading' | 'empty' | 'success' | 'error'>('loading');
  let count = $state(0);
  let requestError = $state<unknown>();
  let controller: AbortController | undefined;

  const validationMessages = $derived(
    Object.entries(getFieldErrors(requestError)).flatMap(([field, messages]) =>
      messages.map((message) => `${field}: ${message}`),
    ),
  );

  async function refresh(): Promise<void> {
    controller?.abort();
    controller = new AbortController();
    status = 'loading';
    requestError = undefined;

    try {
      const profiles = await load(controller.signal);
      count = profiles.length;
      status = profiles.length === 0 ? 'empty' : 'success';
    } catch (error) {
      if (controller.signal.aborted) return;
      requestError = error;
      status = 'error';
    }
  }

  onMount(() => {
    void refresh();
    return () => controller?.abort();
  });
</script>

<div class="space-y-8">
  <div class="max-w-3xl">
    <p class="text-sm font-semibold tracking-widest text-cyan-300 uppercase">Configuration</p>
    <h1 class="mt-2 text-3xl font-semibold tracking-tight text-white sm:text-4xl">{title}</h1>
    <p class="mt-3 text-base leading-7 text-slate-300">{description}</p>
  </div>

  <Card title={`Managed ${pluralName}`} description="Profiles configured through Agent Controller.">
    {#snippet actions()}
      <Button variant="secondary" onclick={() => void refresh()} disabled={status === 'loading'}>
        Refresh
      </Button>
    {/snippet}

    {#if status === 'loading'}
      <div class="flex min-h-28 items-center justify-center gap-3 text-sm text-slate-300" role="status">
        <span
          class="size-4 animate-spin rounded-full border-2 border-slate-700 border-t-cyan-300"
          aria-hidden="true"
        ></span>
        Loading {pluralName}…
      </div>
    {:else if status === 'error'}
      <div class="space-y-4">
        <Alert
          variant="error"
          title={`Could not load ${pluralName}`}
          message={getErrorMessage(requestError)}
          errors={validationMessages}
        />
        <Button variant="secondary" onclick={() => void refresh()}>Try again</Button>
      </div>
    {:else if status === 'empty'}
      <div class="rounded-xl border border-dashed border-slate-700 px-5 py-10 text-center">
        <h2 class="font-semibold text-white">No {pluralName} yet</h2>
        <p class="mx-auto mt-2 max-w-lg text-sm leading-6 text-slate-400">
          Create a {singularName} profile to make it available to Agent Controller.
        </p>
      </div>
    {:else}
      <Alert
        variant="success"
        title={`${count} ${count === 1 ? singularName : pluralName} configured`}
        message="The API connection is healthy and managed profiles are available."
      />
    {/if}
  </Card>
</div>
