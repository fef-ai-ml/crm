import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Timeline } from './Timeline';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

function makeEvent(overrides: Record<string, unknown> = {}) {
  return {
    id: 'e1',
    name: 'app_open',
    properties: { screen: 'home' },
    occurredAt: '2026-01-01T00:00:00Z',
    receivedAt: '2026-01-01T00:00:01Z',
    ...overrides,
  };
}

function makeWrapper(client: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

function newClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

/** Stubs the global `IntersectionObserver` (absent in jsdom, J10) so the effect under test can be exercised
 * without a real layout engine; each constructed instance is recorded so tests can drive its callback. */
class MockIntersectionObserver {
  static instances: MockIntersectionObserver[] = [];
  callback: IntersectionObserverCallback;
  observe = vi.fn();
  disconnect = vi.fn();
  unobserve = vi.fn();
  takeRecords = vi.fn(() => []);
  root = null;
  rootMargin = '';
  thresholds: ReadonlyArray<number> = [];

  constructor(callback: IntersectionObserverCallback) {
    this.callback = callback;
    MockIntersectionObserver.instances.push(this);
  }
}

// `act(async ...)` (rather than the sync overload) is needed here: react-query's internal notify manager
// flips `isFetchingNextPage` via a microtask, and the effect must re-run (producing a fresh observer whose
// closure sees the updated flag) before the test grabs `instances.at(-1)` again.
async function fireIntersect(observer: MockIntersectionObserver) {
  await act(async () => {
    observer.callback(
      [{ isIntersecting: true } as IntersectionObserverEntry],
      observer as unknown as IntersectionObserver,
    );
  });
}

describe('Timeline', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    MockIntersectionObserver.instances = [];
  });

  it('renders the first page of rows', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse({
        items: [makeEvent({ id: 'e1', name: 'app_open' }), makeEvent({ id: 'e2', name: 'purchase' })],
        nextCursor: null,
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<Timeline profileId="p1" />, { wrapper: makeWrapper(newClient()) });

    const rows = await screen.findAllByTestId('timeline-row');
    expect(rows).toHaveLength(2);
    expect(fetchMock.mock.calls[0][0]).toBe('/v1/profiles/p1/events');
  });

  it('renders "No events" for an empty page', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ items: [], nextCursor: null })));

    render(<Timeline profileId="p1" />, { wrapper: makeWrapper(newClient()) });

    expect(await screen.findByText('No events')).toBeTruthy();
  });

  it('"Load more" requests the next cursor', async () => {
    const page1 = { items: [makeEvent({ id: 'e1' })], nextCursor: 'cursor-1' };
    const page2 = { items: [makeEvent({ id: 'e2' })], nextCursor: null };
    const fetchMock = vi.fn((url: string) => {
      if (String(url).includes('cursor=cursor-1')) return Promise.resolve(jsonResponse(page2));
      return Promise.resolve(jsonResponse(page1));
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<Timeline profileId="p1" />, { wrapper: makeWrapper(newClient()) });

    await screen.findAllByTestId('timeline-row');
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));

    await waitFor(() => expect(screen.getAllByTestId('timeline-row')).toHaveLength(2));
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('cursor=cursor-1'))).toBe(true);
  });

  it('the name filter changes the request to name=purchase after 300 ms', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ items: [], nextCursor: null }));
    vi.stubGlobal('fetch', fetchMock);

    render(<Timeline profileId="p1" />, { wrapper: makeWrapper(newClient()) });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });

    const input = screen.getByLabelText('Event name');
    fireEvent.change(input, { target: { value: 'purchase' } });

    await act(async () => {
      await vi.advanceTimersByTimeAsync(200);
    });
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('name=purchase'))).toBe(false);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(100);
    });

    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('name=purchase'))).toBe(true);
  });

  it('an intersecting sentinel triggers the next page exactly once while a fetch is in flight', async () => {
    vi.stubGlobal('IntersectionObserver', MockIntersectionObserver);
    const page1 = { items: [makeEvent({ id: 'e1' })], nextCursor: 'cursor-1' };
    const page2 = { items: [makeEvent({ id: 'e2' })], nextCursor: null };
    let resolvePage2: (value: Response) => void = () => {};
    const fetchMock = vi.fn((url: string) => {
      if (String(url).includes('cursor=cursor-1')) {
        return new Promise<Response>((resolve) => {
          resolvePage2 = resolve;
        });
      }
      return Promise.resolve(jsonResponse(page1));
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<Timeline profileId="p1" />, { wrapper: makeWrapper(newClient()) });

    await screen.findAllByTestId('timeline-row');
    const callsBefore = fetchMock.mock.calls.length;
    const observersBefore = MockIntersectionObserver.instances.length;
    expect(observersBefore).toBe(1); // M-3: one observer for the sentinel element, not recreated per flag flip

    await fireIntersect(MockIntersectionObserver.instances.at(-1)!);
    expect(fetchMock.mock.calls.length).toBe(callsBefore + 1); // the in-flight request for cursor-1

    // `isFetchingNextPage` flips via react-query's own microtask/flush queue, one tick beyond what
    // `act(async ...)` alone drains. The observer itself is never recreated (M-3), so wait on a rendered
    // side effect of the flag instead: the "Load more" button (which shares the same guard) becoming disabled.
    await waitFor(() =>
      expect((screen.getByRole('button', { name: 'Load more' }) as HTMLButtonElement).disabled).toBe(true),
    );
    expect(MockIntersectionObserver.instances.length).toBe(observersBefore); // still exactly one observer

    // a second intersection on that SAME observer while the fetch is still in flight must not fire a duplicate
    // request — this is the guard the mutation test exercises.
    await fireIntersect(MockIntersectionObserver.instances.at(-1)!);
    expect(fetchMock.mock.calls.length).toBe(callsBefore + 1);

    await act(async () => {
      resolvePage2(jsonResponse(page2));
      await Promise.resolve();
      await Promise.resolve();
    });

    await waitFor(() => expect(screen.getAllByTestId('timeline-row')).toHaveLength(2));
  });
});
