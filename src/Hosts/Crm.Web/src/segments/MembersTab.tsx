import { useInfiniteQuery } from '@tanstack/react-query';
import { api } from '@/lib/client';
import { formatDateTime } from '@/lib/format';
import { Button } from '@/components/ui/button';
import { Table, TBody, TD, TH, THead, TR } from '@/components/ui/table';

export type MembersTabProps = {
  segmentId: string;
  currentRunId: string | null;
};

/** J10/brief: no current run yet means nothing to page through — the query stays disabled (no request goes
 * out) and the empty state renders instead of an empty table. */
export function MembersTab({ segmentId, currentRunId }: MembersTabProps) {
  const query = useInfiniteQuery({
    queryKey: ['segment', segmentId, 'members'],
    queryFn: ({ pageParam }: { pageParam: string | undefined }) =>
      api.segments.members(segmentId, { cursor: pageParam }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: currentRunId !== null,
  });

  if (currentRunId === null) {
    return <p className="text-sm text-zinc-500">No current run yet</p>;
  }

  const items = query.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <div className="space-y-4">
      <Table>
        <THead>
          <TR>
            <TH>External id</TH>
            <TH>Email</TH>
            <TH>Last seen</TH>
          </TR>
        </THead>
        <TBody>
          {items.map((member) => (
            <TR key={member.id} data-testid="member-row">
              <TD>{member.externalId ?? '—'}</TD>
              <TD>{member.email ?? '—'}</TD>
              <TD>{formatDateTime(member.lastSeenAt)}</TD>
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
