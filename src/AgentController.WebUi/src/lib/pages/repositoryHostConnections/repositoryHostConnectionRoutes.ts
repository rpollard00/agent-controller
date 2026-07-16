export type RepositoryHostConnectionRoute =
  | { view: 'list' }
  | { view: 'create' }
  | { view: 'detail'; key: string }
  | { view: 'edit'; key: string }
  | { view: 'repoPicker'; connectionKey: string };

export function parseRepositoryHostConnectionRoute(
  pathname: string,
): RepositoryHostConnectionRoute | undefined {
  const normalized = pathname === '/' ? pathname : pathname.replace(/\/+$/, '');
  if (normalized === '/repository-host-connections') return { view: 'list' };
  if (normalized === '/repository-host-connections/new') return { view: 'create' };

  // /repository-host-connections/{key}/repos — repo picker
  const pickerMatch = /^\/repository-host-connections\/([^/]+)\/repos$/.exec(normalized);
  if (pickerMatch) {
    try {
      const key = decodeURIComponent(pickerMatch[1]);
      if (key) return { view: 'repoPicker', connectionKey: key };
    } catch {
      // fall through
    }
  }

  const match = /^\/repository-host-connections\/([^/]+?)(\/edit)?$/.exec(normalized);
  if (!match) return undefined;

  try {
    const key = decodeURIComponent(match[1]);
    if (!key) return undefined;
    return match[2] ? { view: 'edit', key } : { view: 'detail', key };
  } catch {
    return undefined;
  }
}

export function repositoryHostConnectionDetailPath(key: string): string {
  return `/repository-host-connections/${encodeURIComponent(key)}`;
}

export function repositoryHostConnectionEditPath(key: string): string {
  return `${repositoryHostConnectionDetailPath(key)}/edit`;
}

export function repositoryHostConnectionRepoPickerPath(key: string): string {
  return `/repository-host-connections/${encodeURIComponent(key)}/repos`;
}
