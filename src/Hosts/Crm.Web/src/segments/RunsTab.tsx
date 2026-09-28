import { useEffect, useRef } from 'react';
import { useInfiniteQuery } from '@tanstack/react-query';
import { api } from '@/lib/client';
import type { SegmentRunListItem } from '@/lib/client';
import { formatDateTime } from '@/lib/format';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Table, TBody, TD, TH, THead, TR } from '@/components/ui/table';

const DEFAULT_POLL_INTERVAL_MS = 2000;
const ACTIVE_STATUSES = new Set(['Queued', 'Running']);

function isActive(run: SegmentRunListItem): boolean {
  return ACTIVE_STATUSES.has(run.status);
}

function statusVariant(status: string): 'success' | 'muted' | 'warning' {
  if (status === 'Finished') return 'success';
  if (status === 'Failed') return 'warning';
  return 'muted';
}

export type RunsTabProps = {
  segmentId: string;
  /** The segment's own `currentRunId` (M-5): used only to tell, on the very first observation of the runs
   * list, whether its newest run is one the caller already knows about. A run that's already terminal by the
   * time this tab first fetches never transitions through the active-run tracking below, so without this it
   * would never fire `onSettled`. */
  currentRunId?: string | null;
  /** Fires once per run, the instant it's first observed to have left Queued/Running for Finished/Failed
   * (J11), or — per M-5 above — the instant a run that's already terminal on first load turns out to be one
   * the caller didn't already know about: the detail page uses this to invalidate the segment and members
   * queries. */
  onSettled?: (run: SegmentRunListItem) => void;
  /** J11 says 2 s; overridable only so the unit test can exercise the poll/stop transition with real timers
   * and a short wait instead of fighting this app's TanStack Query version's interaction with fake timers
   * (decision not dictated by the brief, called out in the report). Production code never passes this. */
  pollIntervalMs?: number;
};

export function RunsTab({
  segmentId,
  currentRunId = null,
  onSettled,
  pollIntervalMs = DEFAULT_POLL_INTERVAL_MS,
}: RunsTabProps) {
  const query = useInfiniteQuery({
    queryKey: ['segment', segmentId, 'runs'],
    queryFn: ({ pageParam }: { pageParam: string | undefined }) => api.segments.runs(segmentId, { cursor: pageParam }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    // J11: poll while the newest page contains an active run; stop once none are active.
    refetchInterval: (latest) => {
      const items = latest.state.data?.pages[0]?.items ?? [];
      return items.some(isActive) ? pollIntervalMs : false;
    },
  });

  const activeRunIdsRef = useRef<Set<string>>(new Set());
  const hasObservedFirstPageRef = useRef(false);

  useEffect(() => {
    const firstPage = query.data?.pages[0];
    if (!firstPage) return;

    const currentActive = new Set(firstPage.items.filter(isActive).map((run) => run.runId));

    if (!hasObservedFirstPageRef.current) {
      hasObservedFirstPageRef.current = true;
      // M-5: the newest run (first item) may already be terminal on this very first fetch — for example
      // right after Run now, on a small tenant or a fast worker. That never transitions through
      // `activeRunIdsRef` below, so it needs its own check: if it's not one the caller already knew about,
      // treat it as newly settled.
      const newest = firstPage.items[0];
      if (newest && !isActive(newest) && newest.runId !== currentRunId) {
        onSettled?.(newest);
      }
    } else {
      for (const run of firstPage.items) {
        if (activeRunIdsRef.current.has(run.runId) && !currentActive.has(run.runId)) {
          onSettled?.(run);
        }
      }
    }
    activeRunIdsRef.current = currentActive;
  }, [query.data, onSettled, currentRunId]);

  const items = query.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <div className="space-y-4">
      <Table>
        <THead>
          <TR>
            <TH>Status</TH>
            <TH>Trigger</TH>
            <TH>Started</TH>
            <TH>Finished</TH>
            <TH>Members</TH>
            <TH>Error</TH>
          </TR>
        </THead>
        <TBody>
          {items.map((run) => (
            <TR key={run.runId} data-testid="run-row">
              <TD>
                <Badge variant={statusVariant(run.status)}>{run.status}</Badge>
              </TD>
              <TD>{run.trigger}</TD>
              <TD>{formatDateTime(run.startedAt)}</TD>
              <TD>{formatDateTime(run.finishedAt)}</TD>
              <TD>{run.memberCount ?? '—'}</TD>
              <TD>{run.error ?? '—'}</TD>
            </TR>
          ))}
        </TBody>
      </Table>

      {query.hasNextPage && (
        <Button variant="outline" onClick={() => void query.fetchNextPage()} disabled={query.isFetchingNextPage}>
          Load more
        </Button>
      )}
    </div>
  );
}
