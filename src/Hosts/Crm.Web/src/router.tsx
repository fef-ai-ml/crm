import { createRootRoute, createRoute, createRouter, redirect, type RouterHistory } from '@tanstack/react-router';
import { refreshSession, sessionStore } from '@/lib/auth';
import { RootLayout } from './routes/root';
import { LoginPage } from './routes/login';
import { AuthedLayout } from './routes/authed';
import { StatusPage } from './routes/status';
import { SegmentsListPage } from './routes/segments/list';
import { SegmentNewPage } from './routes/segments/new';
import { SegmentDetailPage } from './routes/segments/detail';

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

  const segmentsNewRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/segments/new',
    component: SegmentNewPage,
  });

  const segmentDetailRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/segments/$id',
    component: SegmentDetailPage,
  });

  const statusRoute = createRoute({
    getParentRoute: () => authedRoute,
    path: '/status',
    component: StatusPage,
  });

  const routeTree = rootRoute.addChildren([
    loginRoute,
    authedRoute.addChildren([indexRoute, segmentsRoute, segmentsNewRoute, segmentDetailRoute, statusRoute]),
  ]);

  return createRouter({ routeTree, ...(history ? { history } : {}) });
}

export const router = createAppRouter();

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
