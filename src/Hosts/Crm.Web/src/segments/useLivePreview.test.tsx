import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useLivePreview } from './useLivePreview';
import type { LivePreviewResult } from './useLivePreview';
import type { RuleTree } from '@/rules/types';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

function makeWrapper(client: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

function newClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

const INVALID_TREE: RuleTree = { type: 'attribute', key: '', cmp: 'eq', value: '' };
const TREE_A: RuleTree = { type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' };
const TREE_B: RuleTree = { type: 'attribute', key: 'plan', cmp: 'eq', value: 'enterprise' };

describe('useLivePreview (UT-063)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('never requests for an invalid tree', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const client = newClient();

    const { result } = renderHook(() => useLivePreview(INVALID_TREE), { wrapper: makeWrapper(client) });

    expect(result.current.status).toBe('invalid');
    expect(result.current.count).toBeNull();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2000);
    });

    expect(fetchMock).not.toHaveBeenCalled();
    expect(result.current.status).toBe('invalid');
  });

  it('requests once 500 ms after the tree settles; an edit at 300 ms resets the timer', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse({ count: 7, timedOut: false, cachedAt: new Date().toISOString() }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const client = newClient();

    const { result, rerender } = renderHook<LivePreviewResult, { tree: RuleTree }>(
      ({ tree }) => useLivePreview(tree),
      {
        wrapper: makeWrapper(client),
        initialProps: { tree: INVALID_TREE },
      },
    );

    rerender({ tree: TREE_A });
    expect(result.current.status).toBe('pending');

    await act(async () => {
      await vi.advanceTimersByTimeAsync(300);
    });
    expect(fetchMock).not.toHaveBeenCalled();

    // an edit at 300 ms resets the debounce timer
    rerender({ tree: TREE_B });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(300);
    });
    expect(fetchMock).not.toHaveBeenCalled();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(200);
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/v1/segments/preview');
    expect(JSON.parse(init.body as string)).toEqual({ definition: TREE_B });

    // One more flush: the response's own `.json()` parsing and TanStack Query's internal state update land in
    // a microtask queued by the fetch resolution itself, one tick after the timer that triggered the request.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(result.current.status).toBe('ready');
    expect(result.current.count).toBe(7);
  });

  it('renders timedOut when the server reports { count: null, timedOut: true }', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ count: null, timedOut: true, cachedAt: null }));
    vi.stubGlobal('fetch', fetchMock);
    const client = newClient();

    const { result } = renderHook(() => useLivePreview(TREE_A), { wrapper: makeWrapper(client) });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    expect(result.current.status).toBe('timedOut');
    expect(result.current.count).toBeNull();
  });

  it('shares one request between two structurally identical trees (query key)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse({ count: 5, timedOut: false, cachedAt: new Date().toISOString() }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const client = newClient();
    const treeACopy: RuleTree = JSON.parse(JSON.stringify(TREE_A)) as RuleTree;

    const { result, rerender } = renderHook<LivePreviewResult, { tree: RuleTree }>(
      ({ tree }) => useLivePreview(tree),
      {
        wrapper: makeWrapper(client),
        initialProps: { tree: TREE_A },
      },
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(result.current.status).toBe('ready');
    expect(fetchMock).toHaveBeenCalledTimes(1);

    rerender({ tree: treeACopy });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(600);
    });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(result.current.count).toBe(5);
  });
});
