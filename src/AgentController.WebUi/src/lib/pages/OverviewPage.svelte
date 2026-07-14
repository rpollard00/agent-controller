<script lang="ts">
  import Card from '../components/ui/Card.svelte';

  const resources = [
    {
      href: '/repositories',
      title: 'Repositories',
      description: 'Onboard source repositories and connect them to managed environments.',
    },
    {
      href: '/work-source-environments',
      title: 'Work source environments',
      description: 'Configure work source organizations, projects, board policies, and credential references.',
    },
    {
      href: '/runtime-environments',
      title: 'Runtime Environments',
      description: 'Choose workspace and runtime providers for agent execution.',
    },
  ] as const;
</script>

<div class="space-y-10">
  <div class="max-w-3xl">
    <p class="text-sm font-semibold tracking-widest text-cyan-300 uppercase">Control plane</p>
    <h1 class="mt-3 text-4xl font-semibold tracking-tight text-white sm:text-5xl">
      Agent Controller
    </h1>
    <p class="mt-5 text-lg leading-8 text-slate-300">
      Onboard repositories and manage the environments used to run your agents.
    </p>
  </div>

  <section aria-labelledby="configuration-heading">
    <div class="mb-5">
      <h2 id="configuration-heading" class="text-xl font-semibold text-white">Configuration</h2>
      <p class="mt-1 text-sm leading-6 text-slate-400">
        Select a resource to view and manage its profiles.
      </p>
    </div>
    <div class="grid gap-4 md:grid-cols-3">
      {#each resources as resource}
        <Card>
          <h3 class="text-base font-semibold text-white">{resource.title}</h3>
          <p class="mt-2 min-h-18 text-sm leading-6 text-slate-400">{resource.description}</p>
          <a
            class="mt-5 inline-flex min-h-10 items-center gap-2 rounded-lg text-sm font-semibold text-cyan-300 hover:text-cyan-200"
            href={resource.href}
          >
            Manage {resource.title}
            <span aria-hidden="true">→</span>
          </a>
        </Card>
      {/each}
    </div>
  </section>
</div>
