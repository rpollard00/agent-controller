<svelte:head>
  <title>Agent Controller</title>
</svelte:head>

<main class="min-h-screen px-6 py-16 sm:px-10 lg:px-16">
  <div class="mx-auto max-w-5xl">
    <p class="text-sm font-semibold tracking-widest text-cyan-300 uppercase">Control plane</p>
    <h1 class="mt-3 text-4xl font-semibold tracking-tight text-white sm:text-5xl">
      Agent Controller
    </h1>
    <p class="mt-5 max-w-2xl text-lg leading-8 text-slate-300">
      Onboard repositories and manage the environments used to run your agents.
    </p>

    <section
      aria-labelledby="scaffold-status"
      class="mt-12 rounded-2xl border border-slate-800 bg-slate-900/70 p-6 shadow-xl shadow-black/20"
    >
      <div class="flex items-start gap-4">
        <span class="mt-2 size-2.5 shrink-0 rounded-full bg-emerald-400" aria-hidden="true"></span>
        <div>
          <h2 id="scaffold-status" class="text-lg font-medium text-white">Web UI scaffold ready</h2>
          <p class="mt-2 leading-7 text-slate-400">
            Svelte, TypeScript, Tailwind CSS, and component testing are configured.
          </p>
        </div>
      </div>
    </section>
  </div>
</main>
