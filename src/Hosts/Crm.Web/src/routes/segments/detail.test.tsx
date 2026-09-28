import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { decodeSession, sessionStore } from '@/lib/auth';
import { createAppRouter } from '@/router';
import type { SegmentResponse } from '@/lib/client';

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

function setSession(role: 'Admin' | 'Viewer') {
  const accessToken = makeToken({ sub: 'u1', tenant_id: 't1', role, exp: Math.floor(Date.now() / 1000) + 900 });
  sessionStore.set(decodeSession({ accessToken, expiresIn: 900 }));
}

function makeSegment(overrides: Partial<SegmentResponse> = {}): SegmentResponse {
  return {
    id: 'seg-1',
    name: 'seed-pro-id',
    description: null,
    definition: { type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' },
    status: 'Active',
    isRealtimeEligible: false,
    memberCount: 412,
    realtimeMemberCount: null,
    lastRunAt: '2026-01-01T00:00:00Z',
    currentRunId: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function makeFetchMock(segment: SegmentResponse) {
  return vi.fn((url: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    const path = String(url);

    if (path === `/v1/segments/${segment.id}` && method === 'GET') {
      return Promise.resolve(jsonResponse(segment));
    }
    if (path === `/v1/segments/${segment.id}/run` && method === 'POST') {
      return Promise.resolve(jsonResponse({ runId: 'run-9' }, 202));
    }
    if (path.startsWith(`/v1/segments/${segment.id}/runs`)) {
      return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
    }
    if (path.startsWith(`/v1/segments/${segment.id}/members`)) {
      return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
    }
    if (path === `/v1/segments/${segment.id}` && method === 'DELETE') {
      return Promise.resolve(jsonResponse(null, 204));
    }
    if (path.startsWith('/v1/segments?') || path === '/v1/segments') {
      return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
    }
    return Promise.resolve(jsonResponse({ title: 'not found' }, 404));
  });
}

async function renderDetail(segmentId = 'seg-1') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter(createMemoryHistory({ initialEntries: [`/segments/${segmentId}`] }));
  await router.load();
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

describe('SegmentDetailPage (E2E-004 unit coverage)', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('Admin sees Run now and Archive; Viewer does not', async () => {
    setSession('Admin');
    const segment = makeSegment();
    vi.stubGlobal('fetch', makeFetchMock(segment));

    await renderDetail();

    expect(await screen.findByRole('button', { name: 'Run now' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Archive' })).toBeTruthy();
  });

  it('Viewer sees neither Run now nor Archive', async () => {
    setSession('Viewer');
    const segment = makeSegment();
    vi.stubGlobal('fetch', makeFetchMock(segment));

    await renderDetail();

    await screen.findByText('seed-pro-id');
    expect(screen.queryByRole('button', { name: 'Run now' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Archive' })).toBeNull();
  });

  it('Run now posts, shows "Run queued", and switches to the Runs tab', async () => {
    setSession('Admin');
    const segment = makeSegment();
    const fetchMock = makeFetchMock(segment);
    vi.stubGlobal('fetch', fetchMock);

    await renderDetail();

    const runButton = await screen.findByRole('button', { name: 'Run now' });
    await act(async () => {
      runButton.click();
    });

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        `/v1/segments/${segment.id}/run`,
        expect.objectContaining({ method: 'POST' }),
      );
    });

    expect(await screen.findByRole('status')).toHaveProperty('textContent', 'Run queued');
    expect(screen.getByRole('tab', { name: 'Runs' }).getAttribute('aria-selected')).toBe('true');

    await waitFor(() => {
      expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith(`/v1/segments/${segment.id}/runs`))).toBe(
        true,
      );
    });
  });

  it('Archive requires two clicks and then calls DELETE', async () => {
    setSession('Admin');
    const segment = makeSegment();
    const fetchMock = makeFetchMock(segment);
    vi.stubGlobal('fetch', fetchMock);

    await renderDetail();

    const archiveButton = await screen.findByRole('button', { name: 'Archive' });
    await act(async () => {
      archiveButton.click();
    });

    expect(fetchMock).not.toHaveBeenCalledWith(
      `/v1/segments/${segment.id}`,
      expect.objectContaining({ method: 'DELETE' }),
    );
    const confirmButton = await screen.findByRole('button', { name: 'Confirm archive' });

    await act(async () => {
      confirmButton.click();
    });

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        `/v1/segments/${segment.id}`,
        expect.objectContaining({ method: 'DELETE' }),
      );
    });

    await screen.findByRole('heading', { name: 'Segments' });
  });
});
