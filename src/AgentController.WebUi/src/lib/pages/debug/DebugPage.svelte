<script lang="ts">
  import type { WebUiApiClient } from '../../api/client';
  import BoardItemsDebugTab from './BoardItemsDebugTab.svelte';
  import PullRequestsDebugTab from './PullRequestsDebugTab.svelte';

  let { client }: { client: WebUiApiClient } = $props();

  type DebugTab = 'board-items' | 'pull-requests';
  const tabs: readonly { id: DebugTab; label: string }[] = [
    { id: 'board-items', label: 'Board items' },
    { id: 'pull-requests', label: 'Pull requests' },
  ];
  let activeTab = $state<DebugTab>('board-items');

  function selectTab(tab: DebugTab, focus = false): void {
    activeTab = tab;
    if (focus) {
      requestAnimationFrame(() => document.getElementById(`debug-tab-${tab}`)?.focus());
    }
  }

  function handleTabKeydown(event: KeyboardEvent, index: number): void {
    let nextIndex: number | undefined;
    if (event.key === 'ArrowRight') nextIndex = (index + 1) % tabs.length;
    if (event.key === 'ArrowLeft') nextIndex = (index - 1 + tabs.length) % tabs.length;
    if (event.key === 'Home') nextIndex = 0;
    if (event.key === 'End') nextIndex = tabs.length - 1;
    if (nextIndex === undefined) return;

    event.preventDefault();
    selectTab(tabs[nextIndex].id, true);
  }
</script>

<div class="space-y-8">
  <div class="max-w-3xl">
    <h1 class="text-3xl font-semibold tracking-tight text-white sm:text-4xl">Debug</h1>
    <p class="mt-3 text-base leading-7 text-slate-300">
      Inspect work items and pull requests visible to the controller.
    </p>
  </div>

  <div>
    <div class="flex gap-1 border-b border-slate-800" role="tablist" aria-label="Debug views">
      {#each tabs as tab, index (tab.id)}
        <button
          id={`debug-tab-${tab.id}`}
          type="button"
          role="tab"
          aria-selected={activeTab === tab.id}
          aria-controls={`debug-panel-${tab.id}`}
          tabindex={activeTab === tab.id ? 0 : -1}
          class={`min-h-11 rounded-t-lg border-b-2 px-4 py-2 text-sm font-semibold transition-colors ${
            activeTab === tab.id
              ? 'border-cyan-300 text-white'
              : 'border-transparent text-slate-400 hover:text-white'
          }`}
          onclick={() => selectTab(tab.id)}
          onkeydown={(event) => handleTabKeydown(event, index)}
        >
          {tab.label}
        </button>
      {/each}
    </div>

    <div
      id="debug-panel-board-items"
      role="tabpanel"
      aria-labelledby="debug-tab-board-items"
      tabindex="0"
      hidden={activeTab !== 'board-items'}
      class="pt-6 outline-none"
    >
      <BoardItemsDebugTab {client} active={activeTab === 'board-items'} />
    </div>

    <div
      id="debug-panel-pull-requests"
      role="tabpanel"
      aria-labelledby="debug-tab-pull-requests"
      tabindex="0"
      hidden={activeTab !== 'pull-requests'}
      class="pt-6 outline-none"
    >
      <PullRequestsDebugTab {client} active={activeTab === 'pull-requests'} />
    </div>
  </div>
</div>
