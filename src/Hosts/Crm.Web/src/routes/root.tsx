import { Outlet } from '@tanstack/react-router';

// Nav lives in AuthedLayout (src/routes/authed.tsx): nothing app-chrome-like renders
// before the auth guard resolves, and the login page owns its own full-page layout.
export function RootLayout() {
  return <Outlet />;
}
