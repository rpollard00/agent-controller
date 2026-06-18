// Builds the monitoring UI into ./dist for static hosting.
//
// Bundles src/monitor/main.ts (browser ESM, minified) and copies the host page
// and styles next to it so dist/ is self-contained. Run with: `bun run build`.
import { copyFile, mkdir, rm } from 'node:fs/promises';
import path from 'node:path';

const root = path.resolve(new URL('.', import.meta.url).pathname, '..');
const dist = path.join(root, 'dist');

await rm(dist, { recursive: true, force: true });
await mkdir(dist, { recursive: true });

const result = await Bun.build({
  entryPoints: [path.join(root, 'src/monitor/main.ts')],
  outdir: dist,
  target: 'browser',
  format: 'esm',
  minify: true,
});

if (!result.success) {
  for (const log of result.logs) console.error(log);
  process.exit(1);
}

await copyFile(path.join(root, 'index.html'), path.join(dist, 'index.html'));
await copyFile(path.join(root, 'src/monitor/styles.css'), path.join(dist, 'styles.css'));

console.log(`Built monitoring UI -> ${dist}`);
