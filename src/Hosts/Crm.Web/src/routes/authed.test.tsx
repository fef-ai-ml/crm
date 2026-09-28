import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { decodeSession, sessionStore } from '@/lib/auth';
import { createAppRouter } from '@/router';

// SegmentsListPage (rendered at /segments) uses TanStack Query (Task 4); every render of the router tree
// in this file needs a QueryClientProvider ancestor, same as main.tsx provides in the real app.
function renderRouter(router: ReturnType<typeof createAppRouter>) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

function base64UrlEncode(input: string): string {
  return btoa(input).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function makeToken(payload: Record<string, unknown>): string {
  const header = base64UrlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const body = base64UrlEncode(JSON.stringify(payload));
  return `${header}.${body}.signature`;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

describe('authed route guard', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('redirects to /login with a redirect search param when there is no session and refresh fails', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ code: 'auth.invalid_refresh_token' }, 401)));

    const router = createAppRouter(createMemoryHistory({ initialEntries: ['/segments'] }));
    await router.load();
    renderRouter(router);

    await waitFor(() => expect(router.state.location.pathname).toBe('/login'));
    expect(router.state.location.search).toEqual({ redirect: '/segments' });
  });

  it('renders the outlet when a session exists', async () => {
    const accessToken = makeToken({
      sub: 'u1',
      tenant_id: 't1',
      role: 'Admin',
      exp: Math.floor(Date.now() / 1000) + 900,
    });
    sessionStore.set(decodeSession({ accessToken, expiresIn: 900 }));

    const router = createAppRouter(createMemoryHistory({ initialEntries: ['/segments'] }));
    await router.load();
    renderRouter(router);

    expect(await screen.findByRole('heading', { name: 'Segments' })).not.toBeNull();
  });
});
