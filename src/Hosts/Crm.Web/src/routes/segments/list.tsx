import { useState } from 'react';
import { Link } from '@tanstack/react-router';
import { useInfiniteQuery } from '@tanstack/react-query';
import { api } from '@/lib/client';
import { useSession } from '@/lib/auth';
import { formatDateTime } from '@/lib/format';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Select, type SelectOption } from '@/components/ui/select';
import { Table, TBody, TD, TH, THead, TR } from '@/components/ui/table';

const STATUS_OPTIONS: SelectOption[] = [
  { value: '', label: 'Draft + Active' },
  { value: 'Draft', label: 'Draft' },
  { value: 'Active', label: 'Active' },
  { value: 'Archived', label: 'Archived' },
];

function statusVariant(status: string): 'success' | 'muted' | 'warning' {
  if (status === 'Active') return 'success';
  if (status === 'Archived') return 'muted';
  return 'warning';
}

export function SegmentsListPage() {
  const session = useSession();
  const isAdmin = session?.role === 'Admin';
  const [status, setStatus] = useState('');

  const query = useInfiniteQuery({
    queryKey: ['segments', status],
    queryFn: ({ pageParam }: { pageParam: string | undefined }) =>
      api.segments.list({ status: status || undefined, cursor: pageParam }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });

  const items = query.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">Segments</h1>
        <div className="flex items-center gap-3">
          <Select
            aria-label="Status"
            options={STATUS_OPTIONS}
            value={status}
            onChange={(event) => setStatus(event.target.value)}
            className="w-40"
          />
          {isAdmin && (
            <Link to="/segments/new">
              <Button>New segment</Button>
            </Link>
          )}
        </div>
      </div>

      <Table>
        <THead>
          <TR>
            <TH>Name</TH>
            <TH>Status</TH>
            <TH>Members</TH>
            <TH>Last run</TH>
            <TH>Kind</TH>
          </TR>
        </THead>
        <TBody>
          {items.map((item) => (
            <TR key={item.id} data-testid="segment-row">
              <TD>
                <Link to="/segments/$id" params={{ id: item.id }} className="font-medium underline-offset-2 hover:underline">
                  {item.name}
                </Link>
              </TD>
              <TD>
                <Badge variant={statusVariant(item.status)}>{item.status}</Badge>
              </TD>
              <TD>{item.memberCount}</TD>
              <TD>{formatDateTime(item.lastRunAt)}</TD>
              <TD>
                <Badge data-testid="segment-badge" variant={item.isRealtimeEligible ? 'success' : 'muted'}>
                  {item.isRealtimeEligible ? 'real-time' : 'batch'}
                </Badge>
              </TD>
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
