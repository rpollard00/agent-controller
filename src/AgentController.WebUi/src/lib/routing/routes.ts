export type RouteId = 'overview' | 'repositories' | 'ado-environments' | 'runtime-environments';

export interface AppRoute {
  id: RouteId;
  path: string;
  label: string;
  shortLabel: string;
  title: string;
  description: string;
}

export const routes: readonly AppRoute[] = [
  {
    id: 'overview',
    path: '/',
    label: 'Overview',
    shortLabel: 'Overview',
    title: 'Agent Controller',
    description: 'Onboard repositories and manage the environments used to run your agents.',
  },
  {
    id: 'repositories',
    path: '/repositories',
    label: 'Repositories',
    shortLabel: 'Repositories',
    title: 'Repositories',
    description: 'Onboard and configure source repositories for agent work.',
  },
  {
    id: 'ado-environments',
    path: '/ado-environments',
    label: 'Azure DevOps Environments',
    shortLabel: 'Azure DevOps',
    title: 'Azure DevOps Environments',
    description: 'Manage Azure DevOps organizations, projects, and board policies.',
  },
  {
    id: 'runtime-environments',
    path: '/runtime-environments',
    label: 'Runtime Environments',
    shortLabel: 'Runtimes',
    title: 'Runtime Environments',
    description: 'Configure workspace providers and the runtimes that execute agent work.',
  },
] as const;

export function matchRoute(pathname: string): AppRoute | undefined {
  const normalizedPath = normalizePath(pathname);
  return routes.find((route) => route.path === normalizedPath);
}

function normalizePath(pathname: string): string {
  if (!pathname || pathname === '/') return '/';
  return pathname.replace(/\/+$/, '') || '/';
}
