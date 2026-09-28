import { createRootRoute, createRoute, createRouter, redirect, type RouterHistory } from '@tanstack/react-router';
import { refreshSession, sessionStore } from '@/lib/auth';
import { RootLayout } from './routes/root';
import { LoginPage } from './routes/login';
import { AuthedLayout } from './routes/authed';
import { StatusPage } from './routes/status';
import { SegmentsListPage } from './routes/segments/list';
import { SegmentNewPage } from './routes/segments/new';
import { SegmentDetailPage } from './routes/segments/detail';
import type { SegmentDetailSearch } from './routes/segments/detail';
import { ProfilesPage } from './routes/profiles';
import type { ProfilesSearch } from './routes/profiles';
import { ApiKeysPage } from './routes/api-keys';

/**
 * Builds a fresh route tree + router. Exported (rather than only a module-level
 * singleton) so tests can create an isolated router per test with its own memory
 * history, instead of sharing mutable route state across test cases.
 */
export function createAppRouter(history?: RouterHistory) {
  const rootRoute = createRootRoute({ component: RootLayout });

  const loginRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/login',
    component: LoginPage,
    validateSearch: (search: Record<string, unknown>): { redirect?: string } => {
      const redirect = search.redirect;
      return typeof redirect === 'string' ? { redirect } : {};
    },
  });

  // J1/J16: on entry, an existing session passes straight through; otherwise one
  // refresh attempt (cookie-based) is made before falling back to the login page.
  const authedRoute = createRoute({
    getParentRoute: () => rootRoute,
    id: 'authed',
    beforeLoad: async ({ location }) => {
      if (!sessionStore.get() && !(await refreshSession())) {
        throw redirect({ to: '/login', search: { redirect: location.href } });
      }
    },
    component: AuthedLayout,
  });

  const indexRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/',
    beforeLoad: () => {
      throw redirect({ to: '/segments' });
    },
  });

  const segmentsRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/segments',
    component: SegmentsListPage,
  });

  // M-7: same Admin-only guard as /api-keys — a Viewer could otherwise open this by URL and see a disabled
  // form with no Save button; harmless (the API 403s regardless) but inconsistent with /api-keys.
  const segmentsNewRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/segments/new',
    beforeLoad: () => {
      if (sessionStore.get()?.role !== 'Admin') {
        throw redirect({ to: '/segments' });
      }
    },
    component: SegmentNewPage,
  });

  const segmentDetailRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/segments/$id',
    component: SegmentDetailPage,
    // Task 5: the active tab is a search param (not local state) so a reload keeps it (controller resolution).
    validateSearch: (search: Record<string, unknown>): SegmentDetailSearch => {
      const tab = search.tab;
      return tab === 'definition' || tab === 'members' || tab === 'runs' ? { tab } : {};
    },
  });

  const profilesRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/profiles',
    component: ProfilesPage,
    // Task 6/J14: `q` and the selected profile `id` are URL search params (strings only, dropped otherwise).
    validateSearch: (search: Record<string, unknown>): ProfilesSearch => {
      const result: ProfilesSearch = {};
      if (typeof search.q === 'string') result.q = search.q;
      if (typeof search.id === 'string') result.id = search.id;
      return result;
    },
  });

  const statusRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/status',
    component: StatusPage,
  });

  // J9: the API-keys screen is Admin-only; a Viewer session (or one that lost Admin since it was decoded)
  // is redirected before the page ever renders — the API's own 403 is the guard that actually matters.
  const apiKeysRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/api-keys',
    beforeLoad: () => {
      if (sessionStore.get()?.role !== 'Admin') {
        throw redirect({ to: '/segments' });
      }
    },
    component: ApiKeysPage,
  });

  const routeTree = rootRoute.addChildren([
    loginRoute,
    authedRoute.addChildren([
      indexRoute,
      segmentsRoute,
      segmentsNewRoute,
      segmentDetailRoute,
      profilesRoute,
      statusRoute,
      apiKeysRoute,
    ]),
  ]);

  return createRouter({ routeTree, ...(history ? { history } : {}) });
}

export const router = createAppRouter();

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
