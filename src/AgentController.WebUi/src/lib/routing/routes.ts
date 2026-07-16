export type RouteId = 'overview' | 'repositories' | 'work-source-environments' | 'repository-host-connections' | 'runtime-environments';

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
    id: 'work-source-environments',
    path: '/work-source-environments',
    label: 'Work source environments',
    shortLabel: 'Work sources',
    title: 'Work source environments',
    description: 'Manage work source organizations, projects, and board policies.',
  },
  {
    id: 'repository-host-connections',
    path: '/repository-host-connections',
    label: 'Repository hosts',
    shortLabel: 'Repo hosts',
    title: 'Repository host connections',
    description: 'Manage connected repository hosts for discovering and onboarding repositories.',
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
  const exactRoute = routes.find((route) => route.path === normalizedPath);
  if (exactRoute) return exactRoute;

  if (/^\/repositories\/(?:new|[^/]+(?:\/edit)?)$/.test(normalizedPath)) {
    return routes.find((route) => route.id === 'repositories');
  }

  if (/^\/work-source-environments\/(?:new|[^/]+(?:\/edit)?)$/.test(normalizedPath)) {
    return routes.find((route) => route.id === 'work-source-environments');
  }

  if (/^\/repository-host-connections\/(?:new|[^/]+(?:\/edit|\/repos)?)$/.test(normalizedPath)) {
    return routes.find((route) => route.id === 'repository-host-connections');
  }

  if (/^\/runtime-environments\/(?:new|[^/]+(?:\/edit)?)$/.test(normalizedPath)) {
    return routes.find((route) => route.id === 'runtime-environments');
  }

  return undefined;
}

function normalizePath(pathname: string): string {
  if (!pathname || pathname === '/') return '/';
  return pathname.replace(/\/+$/, '') || '/';
}
