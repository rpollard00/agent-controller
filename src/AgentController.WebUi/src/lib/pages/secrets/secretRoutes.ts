export type SecretRoute =
  | { view: 'list' }
  | { view: 'create' }
  | { view: 'detail'; name: string }
  | { view: 'versions'; name: string }
  | { view: 'newVersion'; name: string };

export function parseSecretRoute(
  pathname: string,
): SecretRoute | undefined {
  const normalized = pathname === '/' ? pathname : pathname.replace(/\/+$/, '');
  if (normalized === '/secrets') return { view: 'list' };
  if (normalized === '/secrets/new') return { view: 'create' };

  const match = /^\/secrets\/([^/]+?)(?:\/(?:versions|new-version))?$/.exec(normalized);
  if (!match) return undefined;

  try {
    const name = decodeURIComponent(match[1]);
    if (!name) return undefined;

    if (normalized.endsWith('/versions')) return { view: 'versions', name };
    if (normalized.endsWith('/new-version')) return { view: 'newVersion', name };
    return { view: 'detail', name };
  } catch {
    return undefined;
  }
}

export function secretDetailPath(name: string): string {
  return `/secrets/${encodeURIComponent(name)}`;
}

export function secretVersionsPath(name: string): string {
  return `/secrets/${encodeURIComponent(name)}/versions`;
}

export function secretNewVersionPath(name: string): string {
  return `/secrets/${encodeURIComponent(name)}/new-version`;
}
