// Dev server for the monitoring UI.
//
// Serves the built ./dist directory and proxies /api/* to the controller API so
// the page can call the local-sync channel same-origin (no CORS, SSE included).
// Run `bun run dev` (which builds first) or `bun run serve` after a build.
//
// Env:
//   AC_API_BASE  upstream controller API base (default: http://localhost:5000)
//   PORT         port to serve on (default: 5173)
import { readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';

const root = path.resolve(new URL('.', import.meta.url).pathname, '..');
const dist = path.join(root, 'dist');
const apiBase = process.env.AC_API_BASE ?? 'http://localhost:5000';
const port = Number(process.env.PORT ?? 5173);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
};

if (!existsSync(dist)) {
  console.error(`dist/ not found at ${dist}. Run "bun run build" first.`);
  process.exit(1);
}

const server = Bun.serve({
  port,
  async fetch(req) {
    const url = new URL(req.url);

    // Proxy the local-sync channel (and any /api route) to the controller API,
    // streaming the response body so server-sent events flow through unchanged.
    if (url.pathname.startsWith('/api/')) {
      const upstream = new URL(url.pathname + url.search, apiBase);
      const init = {
        method: req.method,
        headers: req.headers,
        body: ['GET', 'HEAD'].includes(req.method) ? undefined : req.body,
        // Let fetch follow redirects to the upstream rather than the dev server.
        redirect: 'manual',
      };
      try {
        const upstreamRes = await fetch(upstream, init);
        return new Response(upstreamRes.body, {
          status: upstreamRes.status,
          headers: upstreamRes.headers,
        });
      } catch (err) {
        return new Response(
          `Upstream API unreachable: ${apiBase} (${describeError(err)})`,
          { status: 502, headers: { 'content-type': 'text/plain; charset=utf-8' } },
        );
      }
    }

    let rel = decodeURIComponent(url.pathname);
    if (rel === '/' || rel === '') rel = '/index.html';
    const filePath = path.join(dist, rel);
    try {
      const data = await readFile(filePath);
      const ext = path.extname(filePath);
      return new Response(data, {
        headers: { 'content-type': MIME[ext] ?? 'application/octet-stream' },
      });
    } catch {
      return new Response('Not found', { status: 404 });
    }
  },
});

console.log(`Monitoring UI: http://localhost:${server.port} (API proxy -> ${apiBase})`);

function describeError(err) {
  if (err instanceof Error) return err.message;
  return String(err);
}
