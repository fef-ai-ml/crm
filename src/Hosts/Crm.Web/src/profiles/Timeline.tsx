import { useEffect, useRef, useState } from 'react';
import { useInfiniteQuery } from '@tanstack/react-query';
import { ApiError } from '@/lib/api';
import { api } from '@/lib/client';
import { useDebouncedValue } from '@/lib/useDebouncedValue';
import { formatDateTime } from '@/lib/format';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TBody, TD, TH, THead, TR } from '@/components/ui/table';

const PROPERTIES_SUMMARY_LIMIT = 80;

function summarizeProperties(properties: Record<string, unknown>): string {
  const json = JSON.stringify(properties);
  return json.length > PROPERTIES_SUMMARY_LIMIT ? `${json.slice(0, PROPERTIES_SUMMARY_LIMIT)}…` : json;
}

export type TimelineProps = { profileId: string };

/** J10/J14: the event name filter is local state (not a URL param); paging is an `IntersectionObserver`
 * sentinel plus a "Load more" button sharing the same `hasNextPage && !isFetchingNextPage` guard — jsdom has no
 * `IntersectionObserver`, so the effect below is a no-op there and unit tests drive the button (or a stubbed
 * observer) instead. */
export function Timeline({ profileId }: TimelineProps) {
  const [nameText, setNameText] = useState('');
  const name = useDebouncedValue(nameText, 300);
  const sentinelRef = useRef<HTMLDivElement | null>(null);

  const query = useInfiniteQuery({
    queryKey: ['profile', profileId, 'events', name],
    queryFn: ({ pageParam }: { pageParam: string | undefined }) =>
      api.profiles.events(profileId, { name: name || undefined, cursor: pageParam }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });

  const { hasNextPage, isFetchingNextPage, fetchNextPage } = query;

  useEffect(() => {
    if (typeof IntersectionObserver === 'undefined') return;
    const node = sentinelRef.current;
    if (!node) return;

    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting) && hasNextPage && !isFetchingNextPage) {
        void fetchNextPage();
      }
    });
    observer.observe(node);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  const items = query.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <div className="space-y-4">
      <div className="max-w-xs space-y-1 text-sm">
        <label htmlFor="timeline-event-name">Event name</label>
        <Input
          id="timeline-event-name"
          aria-label="Event name"
          value={nameText}
          onChange={(event) => setNameText(event.target.value)}
        />
      </div>

      {query.isError ? (
        <p role="alert" className="text-sm text-red-600">
          {query.error instanceof ApiError ? (query.error.problem?.detail ?? 'Failed to load events.') : 'Failed to load events.'}
        </p>
      ) : null}

      {items.length === 0 && !query.isPending && !query.isError ? (
        <p className="text-sm text-zinc-500">No events</p>
      ) : (
        <Table>
          <THead>
            <TR>
              <TH>Name</TH>
              <TH>Occurred at</TH>
              <TH>Properties</TH>
            </TR>
          </THead>
          <TBody>
            {items.map((event) => (
              <TR key={event.id} data-testid="timeline-row">
                <TD>{event.name}</TD>
                <TD>{formatDateTime(event.occurredAt)}</TD>
                <TD>{summarizeProperties(event.properties)}</TD>
              </TR>
            ))}
          </TBody>
        </Table>
      )}

      <div data-testid="timeline-sentinel" ref={sentinelRef} />

      {hasNextPage && (
        <Button variant="outline" onClick={() => void fetchNextPage()} disabled={isFetchingNextPage}>
          Load more
        </Button>
      )}
    </div>
  );
}
