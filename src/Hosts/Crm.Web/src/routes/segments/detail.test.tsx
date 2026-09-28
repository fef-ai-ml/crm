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

async function renderDetail(segmentId = 'seg-1', search = '', client = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
  const router = createAppRouter(createMemoryHistory({ initialEntries: [`/segments/${segmentId}${search}`] }));
  await router.load();
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

function makeRunItem(overrides: Record<string, unknown> = {}) {
  return {
    runId: 'run-1',
    status: 'Queued',
    trigger: 'Manual',
    startedAt: null,
    finishedAt: null,
    memberCount: null,
    error: null,
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
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

  it('Archive is single-flight: rapid re-clicks while the DELETE is in flight fire exactly one request', async () => {
    setSession('Admin');
    const segment = makeSegment();
    let resolveDelete: (response: Response) => void = () => {};
    const deletePromise = new Promise<Response>((resolve) => {
      resolveDelete = resolve;
    });
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      const path = String(url);
      if (path === `/v1/segments/${segment.id}` && method === 'DELETE') {
        return deletePromise;
      }
      return makeFetchMock(segment)(url, init);
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderDetail();

    const archiveButton = await screen.findByRole('button', { name: 'Archive' });
    await act(async () => {
      archiveButton.click();
    });
    const confirmButton = await screen.findByRole('button', { name: 'Confirm archive' });
    await act(async () => {
      confirmButton.click();
    });

    const archivingButton = await screen.findByRole('button', { name: 'Archiving…' });
    expect(archivingButton).toHaveProperty('disabled', true);

    // Two more rapid clicks while the DELETE is still in flight must not issue another request.
    await act(async () => {
      archivingButton.click();
      archivingButton.click();
    });

    const deleteCalls = fetchMock.mock.calls.filter(
      ([url, i]) => String(url) === `/v1/segments/${segment.id}` && (i as RequestInit | undefined)?.method === 'DELETE',
    );
    expect(deleteCalls).toHaveLength(1);

    await act(async () => {
      resolveDelete(jsonResponse(null, 204));
    });

    await screen.findByRole('heading', { name: 'Segments' });
  });

  it('Archive shows the problem detail and resets to the plain Archive state on failure', async () => {
    setSession('Admin');
    const segment = makeSegment();
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      const path = String(url);
      if (path === `/v1/segments/${segment.id}` && method === 'DELETE') {
        return Promise.resolve(
          new Response(
            JSON.stringify({
              type: 'https://crm.example/problems/conflict',
              title: 'Conflict',
              status: 409,
              code: 'segment.archived',
              detail: 'The segment is already archived.',
            }),
            { status: 409, headers: { 'content-type': 'application/problem+json' } },
          ),
        );
      }
      return makeFetchMock(segment)(url, init);
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderDetail();

    const archiveButton = await screen.findByRole('button', { name: 'Archive' });
    await act(async () => {
      archiveButton.click();
    });
    const confirmButton = await screen.findByRole('button', { name: 'Confirm archive' });
    await act(async () => {
      confirmButton.click();
    });

    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'The segment is already archived.');
    const resetButton = await screen.findByRole('button', { name: 'Archive' });
    expect(resetButton).toHaveProperty('disabled', false);
  });

  it('Run now shows the problem detail on a 409 segment.archived response, without "Run queued" or a tab switch', async () => {
    setSession('Admin');
    const segment = makeSegment();
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      const path = String(url);
      if (path === `/v1/segments/${segment.id}/run` && method === 'POST') {
        return Promise.resolve(
          new Response(
            JSON.stringify({
              type: 'https://crm.example/problems/conflict',
              title: 'Conflict',
              status: 409,
              code: 'segment.archived',
              detail: 'The segment is archived.',
            }),
            { status: 409, headers: { 'content-type': 'application/problem+json' } },
          ),
        );
      }
      return makeFetchMock(segment)(url, init);
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderDetail();

    const runButton = await screen.findByRole('button', { name: 'Run now' });
    await act(async () => {
      runButton.click();
    });

    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'The segment is archived.');
    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.getByRole('tab', { name: 'Definition' }).getAttribute('aria-selected')).toBe('true');
  });

  // I-2: Run now while the Runs tab is already open previously only called `setTab('runs')`, a no-op when
  // that tab is already active — the runs query was never invalidated, so the new Queued run never appeared.
  it('Run now while the Runs tab is open triggers a fresh runs fetch and renders the new Queued run (I-2)', async () => {
    setSession('Admin');
    const segment = makeSegment({ currentRunId: 'run-old' });
    let runsCallCount = 0;
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      const path = String(url);
      if (path === `/v1/segments/${segment.id}` && method === 'GET') {
        return Promise.resolve(jsonResponse(segment));
      }
      if (path === `/v1/segments/${segment.id}/run` && method === 'POST') {
        return Promise.resolve(jsonResponse({ runId: 'run-new' }, 202));
      }
      if (path.startsWith(`/v1/segments/${segment.id}/runs`)) {
        runsCallCount += 1;
        if (runsCallCount === 1) {
          return Promise.resolve(
            jsonResponse({ items: [makeRunItem({ runId: 'run-old', status: 'Finished', finishedAt: '2026-01-01T00:00:05Z', memberCount: 10 })], nextCursor: null }),
          );
        }
        return Promise.resolve(
          jsonResponse({ items: [makeRunItem({ runId: 'run-new', status: 'Queued', createdAt: '2026-01-02T00:00:00Z' })], nextCursor: null }),
        );
      }
      if (path.startsWith(`/v1/segments/${segment.id}/members`)) {
        return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
      }
      return Promise.resolve(jsonResponse({ title: 'not found' }, 404));
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderDetail(segment.id, '?tab=runs');

    await screen.findByText('Finished');

    const runButton = await screen.findByRole('button', { name: 'Run now' });
    await act(async () => {
      runButton.click();
    });

    await waitFor(() => expect(runsCallCount).toBeGreaterThanOrEqual(2));
    await screen.findByText('Queued');
  });

  // M-5 (page level): a run that's already terminal on the Runs tab's very first fetch, with a runId the
  // segment resource doesn't yet know about, must still refresh the segment header via RunsTab's onSettled.
  it('a terminal run present on the very first runs fetch with a new run id refreshes the segment header (M-5)', async () => {
    setSession('Admin');
    const segment = makeSegment({ currentRunId: 'run-old' });
    let segmentGetCount = 0;
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      const path = String(url);
      if (path === `/v1/segments/${segment.id}` && method === 'GET') {
        segmentGetCount += 1;
        return Promise.resolve(jsonResponse(segment));
      }
      if (path.startsWith(`/v1/segments/${segment.id}/runs`)) {
        return Promise.resolve(
          jsonResponse({
            items: [makeRunItem({ runId: 'run-new', status: 'Finished', finishedAt: '2026-01-01T00:00:05Z', memberCount: 20, createdAt: '2026-01-02T00:00:00Z' })],
            nextCursor: null,
          }),
        );
      }
      if (path.startsWith(`/v1/segments/${segment.id}/members`)) {
        return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
      }
      return Promise.resolve(jsonResponse({ title: 'not found' }, 404));
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderDetail(segment.id, '?tab=runs');

    await screen.findByText('Finished');
    await waitFor(() => expect(segmentGetCount).toBeGreaterThanOrEqual(2));
  });

  // M-3: SegmentForm seeded its fields from `initial` only once (`useState`); navigating from one detail page
  // to another without a full remount kept the previous segment's name/tree in the form.
  it('the Definition form resets when navigating from one segment detail to another (M-3)', async () => {
    setSession('Admin');
    const segment1 = makeSegment({ id: 'seg-1', name: 'first' });
    const segment2 = makeSegment({ id: 'seg-2', name: 'second' });
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      const path = String(url);
      if (path === '/v1/segments/seg-1' && method === 'GET') return Promise.resolve(jsonResponse(segment1));
      if (path === '/v1/segments/seg-2' && method === 'GET') return Promise.resolve(jsonResponse(segment2));
      if (path.startsWith('/v1/segments/seg-1/') || path.startsWith('/v1/segments/seg-2/')) {
        return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
      }
      return Promise.resolve(jsonResponse({ title: 'not found' }, 404));
    });
    vi.stubGlobal('fetch', fetchMock);

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = createAppRouter(createMemoryHistory({ initialEntries: ['/segments/seg-1'] }));
    await router.load();
    render(
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    );

    expect(await screen.findByLabelText('Name')).toHaveProperty('value', 'first');

    // M-3 only reproduces when the target is already CACHED: `query.isPending` is then false immediately, so
    // the page never renders its "Loading…" branch (which would otherwise unmount/remount SegmentForm on its
    // own) — the bug is specifically that the still-mounted, unkeyed form then never re-seeds from `initial`.
    client.setQueryData(['segment', 'seg-2'], segment2);

    await act(async () => {
      await router.navigate({ to: '/segments/$id', params: { id: 'seg-2' } });
    });

    await waitFor(() => expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe('second'));
  });

  // M-4: nothing invalidated the segments list after Archive, so a subsequent visit to /segments could show
  // the archived segment as Active until its own mount refetch happened to land.
  it('Archive invalidates the segments list cache after success (M-4)', async () => {
    setSession('Admin');
    const segment = makeSegment();
    const fetchMock = makeFetchMock(segment);
    vi.stubGlobal('fetch', fetchMock);

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const invalidateSpy = vi.spyOn(client, 'invalidateQueries');

    await renderDetail(segment.id, '', client);

    const archiveButton = await screen.findByRole('button', { name: 'Archive' });
    await act(async () => {
      archiveButton.click();
    });
    const confirmButton = await screen.findByRole('button', { name: 'Confirm archive' });
    await act(async () => {
      confirmButton.click();
    });

    await waitFor(() => expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['segments'] }));
  });
});
