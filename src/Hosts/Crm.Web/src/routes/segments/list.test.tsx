import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen } from '@testing-library/react';
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

function setAdminSession() {
  const accessToken = makeToken({ sub: 'u1', tenant_id: 't1', role: 'Admin', exp: Math.floor(Date.now() / 1000) + 900 });
  sessionStore.set(decodeSession({ accessToken, expiresIn: 900 }));
}

async function renderAtSegments() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter(createMemoryHistory({ initialEntries: ['/segments'] }));
  await router.load();
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('SegmentsListPage (E2E-005 unit coverage)', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders columns and badges from a stubbed list response', async () => {
    setAdminSession();
    const listResponse = {
      items: [
        {
          id: 'a',
          name: 'seed-pro-id',
          status: 'Active',
          memberCount: 412,
          lastRunAt: '2026-01-01T00:00:00Z',
          isRealtimeEligible: false,
          createdAt: '2026-01-01T00:00:00Z',
        },
        {
          id: 'b',
          name: 'seed-purchasers',
          status: 'Active',
          memberCount: 2731,
          lastRunAt: '2026-01-02T00:00:00Z',
          isRealtimeEligible: true,
          createdAt: '2026-01-01T00:00:00Z',
        },
      ],
      nextCursor: null,
    };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(listResponse)));

    await renderAtSegments();

    const rows = await screen.findAllByTestId('segment-row');
    expect(rows).toHaveLength(2);
    expect(screen.getByText('seed-pro-id')).toBeTruthy();
    expect(screen.getByText('seed-purchasers')).toBeTruthy();

    const badges = screen.getAllByTestId('segment-badge');
    expect(badges.map((badge) => badge.textContent)).toEqual(['batch', 'real-time']);
    expect(screen.getByText('412')).toBeTruthy();
  });

  it('"Load more" requests with the cursor', async () => {
    setAdminSession();
    const page1 = {
      items: [
        {
          id: 'a',
          name: 'seg-a',
          status: 'Active',
          memberCount: 5,
          lastRunAt: null,
          isRealtimeEligible: true,
          createdAt: '2026-01-01T00:00:00Z',
        },
      ],
      nextCursor: 'cursor-1',
    };
    const page2 = {
      items: [
        {
          id: 'b',
          name: 'seg-b',
          status: 'Active',
          memberCount: 9,
          lastRunAt: null,
          isRealtimeEligible: false,
          createdAt: '2026-01-01T00:00:00Z',
        },
      ],
      nextCursor: null,
    };
    const fetchMock = vi.fn((url: string) => {
      if (url.includes('cursor=cursor-1')) return Promise.resolve(jsonResponse(page2));
      return Promise.resolve(jsonResponse(page1));
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderAtSegments();

    await screen.findByText('seg-a');
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));

    await screen.findByText('seg-b');
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('cursor=cursor-1'))).toBe(true);
  });
});
