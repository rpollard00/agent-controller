export type WorkSourceEnvironmentRoute =
  | { view: 'list' }
  | { view: 'create' }
  | { view: 'detail'; key: string }
  | { view: 'edit'; key: string };

export function parseWorkSourceEnvironmentRoute(
  pathname: string,
): WorkSourceEnvironmentRoute | undefined {
  const normalized = pathname === '/' ? pathname : pathname.replace(/\/+$/, '');
  if (normalized === '/work-source-environments') return { view: 'list' };
  if (normalized === '/work-source-environments/new') return { view: 'create' };

  const match = /^\/work-source-environments\/([^/]+?)(\/edit)?$/.exec(normalized);
  if (!match) return undefined;

  try {
    const key = decodeURIComponent(match[1]);
    if (!key) return undefined;
    return match[2] ? { view: 'edit', key } : { view: 'detail', key };
  } catch {
    return undefined;
  }
}

export function workSourceEnvironmentDetailPath(key: string): string {
  return `/work-source-environments/${encodeURIComponent(key)}`;
}

export function workSourceEnvironmentEditPath(key: string): string {
  return `${workSourceEnvironmentDetailPath(key)}/edit`;
}
