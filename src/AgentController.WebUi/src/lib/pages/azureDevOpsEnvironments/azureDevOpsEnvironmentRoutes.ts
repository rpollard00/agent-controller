export type AzureDevOpsEnvironmentRoute =
  | { view: 'list' }
  | { view: 'create' }
  | { view: 'detail'; key: string }
  | { view: 'edit'; key: string };

export function parseAzureDevOpsEnvironmentRoute(
  pathname: string,
): AzureDevOpsEnvironmentRoute | undefined {
  const normalized = pathname === '/' ? pathname : pathname.replace(/\/+$/, '');
  if (normalized === '/ado-environments') return { view: 'list' };
  if (normalized === '/ado-environments/new') return { view: 'create' };

  const match = /^\/ado-environments\/([^/]+?)(\/edit)?$/.exec(normalized);
  if (!match) return undefined;

  try {
    const key = decodeURIComponent(match[1]);
    if (!key) return undefined;
    return match[2] ? { view: 'edit', key } : { view: 'detail', key };
  } catch {
    return undefined;
  }
}

export function azureDevOpsEnvironmentDetailPath(key: string): string {
  return `/ado-environments/${encodeURIComponent(key)}`;
}

export function azureDevOpsEnvironmentEditPath(key: string): string {
  return `${azureDevOpsEnvironmentDetailPath(key)}/edit`;
}
