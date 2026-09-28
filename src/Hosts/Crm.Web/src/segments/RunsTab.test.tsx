import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { RunsTab } from './RunsTab';
import type { SegmentRunListItem } from '@/lib/client';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

function makeRun(overrides: Partial<SegmentRunListItem> = {}): SegmentRunListItem {
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

function makeWrapper(client: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

function newClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

function wait(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// J11 says a 2 s interval; the test drives a much shorter one (`pollIntervalMs`, a test-only prop on
// `RunsTab`) with REAL timers instead of `vi.useFakeTimers()`. This app's TanStack Query version defers a
// refetch's re-render notification through its own `setTimeout(0)` flush queue in a way that `vi.advanceTimersByTimeAsync`
// does not reliably drain here (verified: the query cache updates correctly — `queryClient.getQueryData`
// shows the new status — but the DOM never re-renders inside the fake clock, even after several extra 0 ms
// advances and a bare microtask flush). Real timers with a tiny interval sidestep that interaction entirely
// while still exercising the exact same `refetchInterval` code path (decision not dictated by the brief,
// called out in the report).
const POLL_MS = 20;

describe('RunsTab', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('polls while the newest run is Queued/Running, stops once Finished, and fires onSettled once', async () => {
    const queuedRun = makeRun({ status: 'Queued' });
    const runningRun = makeRun({ status: 'Running', startedAt: '2026-01-01T00:00:01Z' });
    const finishedRun = makeRun({
      status: 'Finished',
      startedAt: '2026-01-01T00:00:01Z',
      finishedAt: '2026-01-01T00:00:05Z',
      memberCount: 412,
    });

    const fetchMock = vi.fn();
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ items: [queuedRun], nextCursor: null }))
      .mockResolvedValueOnce(jsonResponse({ items: [runningRun], nextCursor: null }))
      .mockResolvedValue(jsonResponse({ items: [finishedRun], nextCursor: null }));
    vi.stubGlobal('fetch', fetchMock);

    const client = newClient();
    const onSettled = vi.fn();

    render(<RunsTab segmentId="seg-1" onSettled={onSettled} pollIntervalMs={POLL_MS} />, {
      wrapper: makeWrapper(client),
    });

    await screen.findByText('Queued');
    expect(onSettled).not.toHaveBeenCalled();

    await screen.findByText('Running');
    expect(onSettled).not.toHaveBeenCalled();

    await screen.findByText('Finished');
    // The settle callback runs from a `useEffect` (after the DOM commit `findByText` resolved on), so it may
    // land a tick later than the text itself.
    await waitFor(() => expect(onSettled).toHaveBeenCalledTimes(1));
    expect(onSettled).toHaveBeenCalledWith(expect.objectContaining({ runId: 'run-1', status: 'Finished' }));

    const callsAtFinish = fetchMock.mock.calls.length;
    // Polling has stopped: waiting several more intervals brings no further fetch.
    await act(async () => {
      await wait(POLL_MS * 5);
    });
    expect(fetchMock.mock.calls.length).toBe(callsAtFinish);
    expect(onSettled).toHaveBeenCalledTimes(1);
  });

  it('renders Trigger, Started, Finished, Members and Error columns and pages with Load more', async () => {
    const page1 = { items: [makeRun({ runId: 'r1', status: 'Finished', memberCount: 5 })], nextCursor: 'cursor-1' };
    const page2 = { items: [makeRun({ runId: 'r2', status: 'Failed', error: 'boom' })], nextCursor: null };
    const fetchMock = vi.fn((url: string) => {
      if (String(url).includes('cursor=cursor-1')) return Promise.resolve(jsonResponse(page2));
      return Promise.resolve(jsonResponse(page1));
    });
    vi.stubGlobal('fetch', fetchMock);

    const client = newClient();
    render(<RunsTab segmentId="seg-1" onSettled={vi.fn()} />, { wrapper: makeWrapper(client) });

    const rows = await screen.findAllByTestId('run-row');
    expect(rows).toHaveLength(1);
    expect(rows[0].textContent).toContain('Finished');
    expect(rows[0].textContent).toContain('5');

    await act(async () => {
      screen.getByRole('button', { name: 'Load more' }).click();
    });

    await screen.findByText('boom');
    const allRows = screen.getAllByTestId('run-row');
    expect(allRows).toHaveLength(2);
    expect(allRows[1].textContent).toContain('Failed');
    expect(allRows[1].textContent).toContain('boom');
    expect(screen.queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('does not fire onSettled on mount when the first fetch already returns a terminal run', async () => {
    const finishedRun = makeRun({ status: 'Finished', startedAt: '2026-01-01T00:00:01Z', finishedAt: '2026-01-01T00:00:05Z' });
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ items: [finishedRun], nextCursor: null })));
    vi.stubGlobal('fetch', fetchMock);

    const client = newClient();
    const onSettled = vi.fn();

    // currentRunId matches the already-terminal run's id: the caller already knows about it (it's not a
    // newly-appeared run), so M-5's first-observation check must not fire either.
    render(<RunsTab segmentId="seg-1" currentRunId="run-1" onSettled={onSettled} pollIntervalMs={POLL_MS} />, {
      wrapper: makeWrapper(client),
    });

    await screen.findByText('Finished');
    await act(async () => {
      await wait(POLL_MS * 3);
    });

    expect(onSettled).not.toHaveBeenCalled();
  });

  // M-5: a run that's already Finished/Failed by the time RunsTab's first fetch lands (e.g. a fast worker, or
  // a small tenant) never transitions through `activeRunIdsRef`, so the transition-based check above can never
  // fire for it. If its id is one the caller doesn't already know about (differs from the segment's own
  // `currentRunId`), the very first observation must still fire `onSettled` so the header/Members refresh.
  it('fires onSettled on the very first observation when the newest run is already terminal and unknown to the caller', async () => {
    const finishedRun = makeRun({
      runId: 'run-new',
      status: 'Finished',
      startedAt: '2026-01-01T00:00:01Z',
      finishedAt: '2026-01-01T00:00:05Z',
      memberCount: 412,
    });
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ items: [finishedRun], nextCursor: null })));
    vi.stubGlobal('fetch', fetchMock);

    const client = newClient();
    const onSettled = vi.fn();

    render(<RunsTab segmentId="seg-1" currentRunId="run-old" onSettled={onSettled} pollIntervalMs={POLL_MS} />, {
      wrapper: makeWrapper(client),
    });

    await screen.findByText('Finished');
    await waitFor(() => expect(onSettled).toHaveBeenCalledTimes(1));
    expect(onSettled).toHaveBeenCalledWith(expect.objectContaining({ runId: 'run-new', status: 'Finished' }));
  });

  it('does not fire onSettled for a previous segment after remounting via key on segmentId change', async () => {
    const activeRun = makeRun({ runId: 'run-shared', status: 'Running', startedAt: '2026-01-01T00:00:01Z' });
    const finishedRun = makeRun({
      runId: 'run-shared',
      status: 'Finished',
      startedAt: '2026-01-01T00:00:01Z',
      finishedAt: '2026-01-01T00:00:05Z',
    });

    const fetchMock = vi.fn((url: string) => {
      if (String(url).includes('/segments/seg-a/')) {
        return Promise.resolve(jsonResponse({ items: [activeRun], nextCursor: null }));
      }
      return Promise.resolve(jsonResponse({ items: [finishedRun], nextCursor: null }));
    });
    vi.stubGlobal('fetch', fetchMock);

    const client = newClient();
    const onSettled = vi.fn();

    // currentRunId matches the shared runId here too: this test is about the active-tracking ref not leaking
    // across a key-based remount, not about M-5's new-run detection.
    function Harness({ segmentId }: { segmentId: string }) {
      return (
        <RunsTab
          key={segmentId}
          segmentId={segmentId}
          currentRunId="run-shared"
          onSettled={onSettled}
          pollIntervalMs={POLL_MS}
        />
      );
    }

    const { rerender } = render(<Harness segmentId="seg-a" />, { wrapper: makeWrapper(client) });

    await screen.findByText('Running');
    expect(onSettled).not.toHaveBeenCalled();

    rerender(<Harness segmentId="seg-b" />);

    await screen.findByText('Finished');
    await act(async () => {
      await wait(POLL_MS * 3);
    });

    expect(onSettled).not.toHaveBeenCalled();
  });
});
