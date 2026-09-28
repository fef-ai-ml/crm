import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MembersTab } from './MembersTab';

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

describe('MembersTab', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders the empty state and requests nothing when currentRunId is null', () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    render(<MembersTab segmentId="seg-1" currentRunId={null} />, { wrapper: makeWrapper(newClient()) });

    expect(screen.getByText('No current run yet')).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('renders rows and pages with Load more using the cursor', async () => {
    const page1 = {
      items: [{ id: 'p1', externalId: 'u-1', email: 'alice@example.com', lastSeenAt: '2026-01-01T00:00:00Z' }],
      nextCursor: 'cursor-1',
    };
    const page2 = {
      items: [{ id: 'p2', externalId: 'u-2', email: 'bob@example.com', lastSeenAt: '2026-01-02T00:00:00Z' }],
      nextCursor: null,
    };
    const fetchMock = vi.fn((url: string) => {
      if (String(url).includes('cursor=cursor-1')) return Promise.resolve(jsonResponse(page2));
      return Promise.resolve(jsonResponse(page1));
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<MembersTab segmentId="seg-1" currentRunId="run-1" />, { wrapper: makeWrapper(newClient()) });

    const rows = await screen.findAllByTestId('member-row');
    expect(rows).toHaveLength(1);
    expect(rows[0].textContent).toContain('u-1');
    expect(rows[0].textContent).toContain('alice@example.com');

    await act(async () => {
      screen.getByRole('button', { name: 'Load more' }).click();
    });

    await screen.findByText('bob@example.com');
    const allRows = screen.getAllByTestId('member-row');
    expect(allRows).toHaveLength(2);
    expect(allRows[1].textContent).toContain('u-2');
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('cursor=cursor-1'))).toBe(true);
  });
});
