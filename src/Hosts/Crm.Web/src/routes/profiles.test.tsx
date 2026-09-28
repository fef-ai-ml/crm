import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen } from '@testing-library/react';
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
  return new Response(body === null ? null : JSON.stringify(body), {
    status,
    headers: body === null ? {} : { 'content-type': 'application/json' },
  });
}

function setAdminSession() {
  const accessToken = makeToken({ sub: 'u1', tenant_id: 't1', role: 'Admin', exp: Math.floor(Date.now() / 1000) + 900 });
  sessionStore.set(decodeSession({ accessToken, expiresIn: 900 }));
}

function makeListItem(overrides: Record<string, unknown> = {}) {
  return {
    id: 'p1',
    externalId: 'u-001',
    email: 'alice.1@example.test',
    phone: null,
    lastSeenAt: '2026-01-02T00:00:00Z',
    eventCount: 3,
    ...overrides,
  };
}

function makeProfile(overrides: Record<string, unknown> = {}) {
  return {
    id: 'p1',
    externalId: 'u-001',
    email: 'alice.1@example.test',
    phone: null,
    attributes: { plan: 'pro' },
    firstSeenAt: '2026-01-01T00:00:00Z',
    lastSeenAt: '2026-01-02T00:00:00Z',
    eventCount: 3,
    ...overrides,
  };
}

async function renderAtProfiles(initialEntries: string[] = ['/profiles']) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter(createMemoryHistory({ initialEntries }));
  await router.load();
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

describe('ProfilesPage (E2E-006 unit coverage)', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('renders the results table from a stubbed search response', async () => {
    setAdminSession();
    const listResponse = { items: [makeListItem()], nextCursor: null };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(listResponse)));

    await renderAtProfiles();

    const rows = await screen.findAllByTestId('profile-row');
    expect(rows).toHaveLength(1);
    expect(screen.getByText('u-001')).toBeTruthy();
    expect(screen.getByText('alice.1@example.test')).toBeTruthy();
    expect(screen.getByText('3')).toBeTruthy();
  });

  it('debounces the search input into a single request after 300 ms; an edit at 200 ms resets it', async () => {
    vi.useFakeTimers();
    setAdminSession();
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ items: [], nextCursor: null }));
    vi.stubGlobal('fetch', fetchMock);

    await renderAtProfiles();

    // flush the initial (empty `q`) fetch triggered on mount
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    const initialCalls = fetchMock.mock.calls.length;

    const input = screen.getByLabelText('Search profiles');

    fireEvent.change(input, { target: { value: 'ali' } });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(200);
    });
    expect(fetchMock.mock.calls.length).toBe(initialCalls);

    // an edit at 200 ms resets the debounce timer
    fireEvent.change(input, { target: { value: 'alice' } });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(200);
    });
    expect(fetchMock.mock.calls.length).toBe(initialCalls);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(100);
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    const aliceCalls = fetchMock.mock.calls.filter(([url]) => String(url) === '/v1/profiles?q=alice');
    expect(aliceCalls).toHaveLength(1);
    expect(fetchMock.mock.calls.length).toBe(initialCalls + 1);
  });

  it('clicking a row sets id in the URL and opens the drawer with the attributes JSON', async () => {
    setAdminSession();
    const profile = makeProfile();
    const fetchMock = vi.fn((url: string) => {
      const path = String(url);
      if (path === '/v1/profiles/p1') return Promise.resolve(jsonResponse(profile));
      if (path.startsWith('/v1/profiles/p1/events')) return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
      if (path.startsWith('/v1/profiles')) return Promise.resolve(jsonResponse({ items: [makeListItem()], nextCursor: null }));
      return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
    });
    vi.stubGlobal('fetch', fetchMock);

    const router = await renderAtProfiles();

    const row = await screen.findByTestId('profile-row');
    fireEvent.click(row);

    await screen.findByRole('dialog', { name: 'Profile' });
    expect((router.state.location.search as { id?: string }).id).toBe('p1');

    const attributesJson = await screen.findByTestId('attributes-json');
    expect(attributesJson.textContent).toBe(JSON.stringify(profile.attributes, null, 2));
  });

  it('shows "Profile not found" for a 404 in the drawer', async () => {
    setAdminSession();
    const fetchMock = vi.fn((url: string) => {
      const path = String(url);
      if (path === '/v1/profiles/missing') {
        return Promise.resolve(jsonResponse({ title: 'Not Found', status: 404, code: 'profile.not_found' }, 404));
      }
      return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderAtProfiles(['/profiles?id=missing']);

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toBe('Profile not found');
  });
});
