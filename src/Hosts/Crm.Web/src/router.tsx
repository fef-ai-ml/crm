import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router';
import { RootLayout } from './routes/root';
import { HomePage } from './routes/home';
import { LoginPage } from './routes/login';

const rootRoute = createRootRoute({ component: RootLayout });
const indexRoute = createRoute({ getParentRoute: () => rootRoute, path: '/', component: HomePage });
const loginRoute = createRoute({ getParentRoute: () => rootRoute, path: '/login', component: LoginPage });

export const router = createRouter({ routeTree: rootRoute.addChildren([indexRoute, loginRoute]) });

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router;
  }
}
