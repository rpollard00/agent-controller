export type ConnectionRoute =
  | { view: 'list' }
  | { view: 'create' }
  | { view: 'detail'; key: string }
  | { view: 'edit'; key: string }
  | { view: 'repoPicker'; connectionKey: string; project: string | null };

export function parseConnectionRoute(
  pathname: string,
): ConnectionRoute | undefined {
  return parseConnectionRouteWithSearch(pathname, '');
}

export function parseConnectionRouteWithSearch(
  pathname: string,
  search: string,
): ConnectionRoute | undefined {
  const normalized = pathname === '/' ? pathname : pathname.replace(/\/+$/, '');
  if (normalized === '/connections') return { view: 'list' };
  if (normalized === '/connections/new') return { view: 'create' };

  // /connections/{key}/repos?project= — repo picker
  const pickerMatch = /^\/connections\/([^/]+)\/repos$/.exec(normalized);
  if (pickerMatch) {
    try {
      const key = decodeURIComponent(pickerMatch[1]);
      const project = searchParam(search, 'project');
      if (key) return { view: 'repoPicker', connectionKey: key, project };
    } catch {
      // fall through
    }
  }

  const match = /^\/connections\/([^/]+?)(\/edit)?$/.exec(normalized);
  if (!match) return undefined;

  try {
    const key = decodeURIComponent(match[1]);
    if (!key) return undefined;
    return match[2] ? { view: 'edit', key } : { view: 'detail', key };
  } catch {
    return undefined;
  }
}

function searchParam(search: string, name: string): string | null {
  if (!search) return null;
  const params = new URLSearchParams(search);
  return params.get(name);
}

export function connectionDetailPath(key: string): string {
  return `/connections/${encodeURIComponent(key)}`;
}

export function connectionEditPath(key: string): string {
  return `${connectionDetailPath(key)}/edit`;
}

export function connectionRepoPickerPath(key: string, project?: string): string {
  const base = `/connections/${encodeURIComponent(key)}/repos`;
  return project ? `${base}?project=${encodeURIComponent(project)}` : base;
}
