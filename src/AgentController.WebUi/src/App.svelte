<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { webUiApi, type WebUiApiClient } from './lib/api/client';
  import NotFoundPage from './lib/pages/NotFoundPage.svelte';
  import OverviewPage from './lib/pages/OverviewPage.svelte';
  import WorkSourceEnvironmentPage from './lib/pages/azureDevOpsEnvironments/AzureDevOpsEnvironmentPage.svelte';
  import RepositoryPage from './lib/pages/repositories/RepositoryPage.svelte';
  import RuntimeEnvironmentPage from './lib/pages/runtimeEnvironments/RuntimeEnvironmentPage.svelte';
  import { matchRoute, routes } from './lib/routing/routes';

  let { client = webUiApi }: { client?: WebUiApiClient } = $props();

  let pathname = $state(typeof window === 'undefined' ? '/' : window.location.pathname);
  let mobileNavigationOpen = $state(false);
  let mainElement: HTMLElement;
  const currentRoute = $derived(matchRoute(pathname));

  function showPath(nextPath: string, pushState: boolean): void {
    const url = new URL(nextPath, window.location.href);
    if (pushState && `${url.pathname}${url.search}${url.hash}` !== currentLocation()) {
      window.history.pushState({}, '', `${url.pathname}${url.search}${url.hash}`);
    }

    pathname = url.pathname;
    mobileNavigationOpen = false;
    void tick().then(() => mainElement?.focus({ preventScroll: true }));
  }

  function currentLocation(): string {
    return `${window.location.pathname}${window.location.search}${window.location.hash}`;
  }

  function handleDocumentClick(event: MouseEvent): void {
    if (
      event.defaultPrevented ||
      event.button !== 0 ||
      event.metaKey ||
      event.ctrlKey ||
      event.shiftKey ||
      event.altKey
    ) {
      return;
    }

    const target = event.target;
    const anchor = target instanceof Element ? target.closest<HTMLAnchorElement>('a[href]') : null;
    if (!anchor || anchor.target || anchor.hasAttribute('download')) return;

    const url = new URL(anchor.href, window.location.href);
    if (url.origin !== window.location.origin || url.pathname.startsWith('/api/')) return;

    event.preventDefault();
    showPath(`${url.pathname}${url.search}${url.hash}`, true);
  }

  onMount(() => {
    const handlePopState = () => showPath(currentLocation(), false);
    window.addEventListener('popstate', handlePopState);
    document.addEventListener('click', handleDocumentClick);

    return () => {
      window.removeEventListener('popstate', handlePopState);
      document.removeEventListener('click', handleDocumentClick);
    };
  });

  $effect(() => {
    document.title = currentRoute ? `${currentRoute.title} · Agent Controller` : 'Page not found';
  });
</script>

<svelte:head>
  <meta
    name="description"
    content="Configure Agent Controller repositories and managed environments."
  />
</svelte:head>

<a
  href="#main-content"
  class="fixed top-3 left-3 z-50 -translate-y-20 rounded-lg bg-cyan-300 px-4 py-2 font-semibold text-slate-950 transition-transform focus:translate-y-0"
>
  Skip to main content
</a>

<div class="flex min-h-screen flex-col bg-slate-950">
  <header class="sticky top-0 z-40 border-b border-slate-800/90 bg-slate-950/95 backdrop-blur">
    <div class="mx-auto flex min-h-16 max-w-7xl items-center justify-between gap-4 px-4 sm:px-6 lg:px-8">
      <a href="/" class="flex min-h-11 items-center gap-3 rounded-lg font-semibold text-white">
        <span
          class="grid size-9 place-items-center rounded-lg bg-cyan-400 text-sm font-black text-slate-950"
          aria-hidden="true"
        >
          AC
        </span>
        <span>Agent Controller</span>
      </a>

      <nav class="hidden md:block" aria-label="Primary navigation">
        <ul class="flex items-center gap-1">
          {#each routes as item}
            <li>
              <a
                href={item.path}
                aria-current={currentRoute?.id === item.id ? 'page' : undefined}
                class={`inline-flex min-h-10 items-center rounded-lg px-3 py-2 text-sm font-medium transition-colors ${
                  currentRoute?.id === item.id
                    ? 'bg-slate-800 text-white'
                    : 'text-slate-300 hover:bg-slate-900 hover:text-white'
                }`}
              >
                {item.label}
              </a>
            </li>
          {/each}
        </ul>
      </nav>

      <button
        type="button"
        class="inline-flex size-11 items-center justify-center rounded-lg border border-slate-700 text-slate-200 hover:bg-slate-900 md:hidden"
        aria-label={mobileNavigationOpen ? 'Close navigation' : 'Open navigation'}
        aria-controls="mobile-navigation"
        aria-expanded={mobileNavigationOpen}
        onclick={() => (mobileNavigationOpen = !mobileNavigationOpen)}
      >
        {#if mobileNavigationOpen}
          <svg viewBox="0 0 24 24" class="size-5" fill="none" stroke="currentColor" aria-hidden="true">
            <path d="M6 6l12 12M18 6 6 18" stroke-linecap="round" stroke-width="2"></path>
          </svg>
        {:else}
          <svg viewBox="0 0 24 24" class="size-5" fill="none" stroke="currentColor" aria-hidden="true">
            <path d="M4 7h16M4 12h16M4 17h16" stroke-linecap="round" stroke-width="2"></path>
          </svg>
        {/if}
      </button>
    </div>

    {#if mobileNavigationOpen}
      <nav id="mobile-navigation" class="border-t border-slate-800 px-4 py-3 md:hidden" aria-label="Mobile navigation">
        <ul class="mx-auto grid max-w-7xl gap-1">
          {#each routes as item}
            <li>
              <a
                href={item.path}
                aria-current={currentRoute?.id === item.id ? 'page' : undefined}
                class={`flex min-h-11 items-center rounded-lg px-3 py-2 text-sm font-medium ${
                  currentRoute?.id === item.id
                    ? 'bg-slate-800 text-white'
                    : 'text-slate-300 hover:bg-slate-900 hover:text-white'
                }`}
              >
                {item.label}
              </a>
            </li>
          {/each}
        </ul>
      </nav>
    {/if}
  </header>

  <main
    id="main-content"
    class="mx-auto w-full max-w-7xl flex-1 px-4 py-10 outline-none sm:px-6 sm:py-12 lg:px-8"
    tabindex="-1"
    bind:this={mainElement}
  >
    {#if !currentRoute}
      <NotFoundPage />
    {:else if currentRoute.id === 'overview'}
      <OverviewPage />
    {:else if currentRoute.id === 'repositories'}
      <RepositoryPage
        {pathname}
        {client}
        navigate={(path) => showPath(path, true)}
      />
    {:else if currentRoute.id === 'work-source-environments'}
      <WorkSourceEnvironmentPage
        {pathname}
        {client}
        navigate={(path) => showPath(path, true)}
      />
    {:else}
      <RuntimeEnvironmentPage
        {pathname}
        {client}
        navigate={(path) => showPath(path, true)}
      />
    {/if}
  </main>

  <footer class="border-t border-slate-900 px-4 py-6 text-center text-sm text-slate-500">
    Agent Controller configuration console
  </footer>
</div>
