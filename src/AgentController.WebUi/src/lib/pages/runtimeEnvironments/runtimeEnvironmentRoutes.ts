export type RuntimeEnvironmentRoute =
  | { view: 'list' }
  | { view: 'create' }
  | { view: 'detail'; key: string }
  | { view: 'edit'; key: string };

export function parseRuntimeEnvironmentRoute(
  pathname: string,
): RuntimeEnvironmentRoute | undefined {
  const normalized = pathname === '/' ? pathname : pathname.replace(/\/+$/, '');
  if (normalized === '/runtime-environments') return { view: 'list' };
  if (normalized === '/runtime-environments/new') return { view: 'create' };

  const match = /^\/runtime-environments\/([^/]+?)(\/edit)?$/.exec(normalized);
  if (!match) return undefined;

  try {
    const key = decodeURIComponent(match[1]);
    if (!key) return undefined;
    return match[2] ? { view: 'edit', key } : { view: 'detail', key };
  } catch {
    return undefined;
  }
}

export function runtimeEnvironmentDetailPath(key: string): string {
  return `/runtime-environments/${encodeURIComponent(key)}`;
}

export function runtimeEnvironmentEditPath(key: string): string {
  return `${runtimeEnvironmentDetailPath(key)}/edit`;
}
