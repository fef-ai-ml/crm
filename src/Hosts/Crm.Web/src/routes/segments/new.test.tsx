import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { decodeSession, sessionStore } from '@/lib/auth';
import { createAppRouter } from '@/router';

function base64UrlEncode(input: string): string {
  return btoa(input).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function makeToken(payload: Record<string, unknown>): string {
  const header = base64UrlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const body = base64UrlEncode(JSON.stringify(payload));
  return `${header}.${body}.signature`;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

function setSession(role: 'Admin' | 'Viewer') {
  const accessToken = makeToken({ sub: 'u1', tenant_id: 't1', role, exp: Math.floor(Date.now() / 1000) + 900 });
  sessionStore.set(decodeSession({ accessToken, expiresIn: 900 }));
}

async function renderAtSegmentsNew() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter(createMemoryHistory({ initialEntries: ['/segments/new'] }));
  await router.load();
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

// M-7: a Viewer could open /segments/new by URL — harmless (the API 403s) but inconsistent with /api-keys,
// which already redirects. Mirrors the "/api-keys route guard" tests in api-keys.test.tsx.
describe('/segments/new route guard (M-7)', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('redirects a Viewer session to /segments', async () => {
    setSession('Viewer');
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ items: [], nextCursor: null })));

    const router = await renderAtSegmentsNew();

    await waitFor(() => expect(router.state.location.pathname).toBe('/segments'));
  });

  it('renders for an Admin session', async () => {
    setSession('Admin');
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ count: null, timedOut: false, cachedAt: null })));

    await renderAtSegmentsNew();

    expect(await screen.findByRole('heading', { name: 'New segment' })).toBeTruthy();
  });
});
