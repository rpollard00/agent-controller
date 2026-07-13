export type RepositoryRoute =
  | { view: 'list' }
  | { view: 'create' }
  | { view: 'detail'; key: string }
  | { view: 'edit'; key: string };

export function parseRepositoryRoute(pathname: string): RepositoryRoute | undefined {
  const normalized = pathname === '/' ? pathname : pathname.replace(/\/+$/, '');
  if (normalized === '/repositories') return { view: 'list' };
  if (normalized === '/repositories/new') return { view: 'create' };

  const match = /^\/repositories\/([^/]+?)(\/edit)?$/.exec(normalized);
  if (!match) return undefined;

  try {
    const key = decodeURIComponent(match[1]);
    if (!key) return undefined;
    return match[2] ? { view: 'edit', key } : { view: 'detail', key };
  } catch {
    return undefined;
  }
}

export function repositoryDetailPath(key: string): string {
  return `/repositories/${encodeURIComponent(key)}`;
}

export function repositoryEditPath(key: string): string {
  return `${repositoryDetailPath(key)}/edit`;
}
