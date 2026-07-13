<script lang="ts">
  import Card from '../components/ui/Card.svelte';
</script>

<div class="mx-auto max-w-2xl py-10">
  <Card>
    <p class="text-sm font-semibold tracking-widest text-cyan-300 uppercase">404</p>
    <h1 class="mt-3 text-3xl font-semibold text-white">Page not found</h1>
    <p class="mt-3 leading-7 text-slate-400">
      The requested page does not exist in Agent Controller.
    </p>
    <a
      href="/"
      class="mt-6 inline-flex min-h-10 items-center rounded-lg bg-cyan-400 px-4 py-2 text-sm font-semibold text-slate-950 hover:bg-cyan-300"
    >
      Return to overview
    </a>
  </Card>
</div>
