import { useCallback, useState } from 'react';
import { useNavigate, useParams, useSearch } from '@tanstack/react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '@/lib/api';
import { api } from '@/lib/client';
import { useSession } from '@/lib/auth';
import { formatDateTime } from '@/lib/format';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Spinner } from '@/components/ui/spinner';
import { Tabs, TabPanel } from '@/components/ui/tabs';
import { SegmentForm } from '@/segments/SegmentForm';
import { MembersTab } from '@/segments/MembersTab';
import { RunsTab } from '@/segments/RunsTab';

export type DetailTab = 'definition' | 'members' | 'runs';
export type SegmentDetailSearch = { tab?: DetailTab };

const TAB_ITEMS: { value: DetailTab; label: string }[] = [
  { value: 'definition', label: 'Definition' },
  { value: 'members', label: 'Members' },
  { value: 'runs', label: 'Runs' },
];

function statusVariant(status: string): 'success' | 'muted' | 'warning' {
  if (status === 'Active') return 'success';
  if (status === 'Archived') return 'muted';
  return 'warning';
}

// The registered route id carries the pathless `authed` layout's prefix (J1/J16: `id: 'authed'`) even though
// the URL itself is just `/segments/$id` — `useParams`/`useSearch` key off that route id, but `useNavigate`'s
// `from` resolves against the URL's full path (no `authed` segment), so the two constants differ.
const ROUTE_ID = '/authed/segments/$id';
const ROUTE_PATH = '/segments/$id';

export function SegmentDetailPage() {
  const { id } = useParams({ from: ROUTE_ID });
  const search = useSearch({ from: ROUTE_ID });
  const navigate = useNavigate({ from: ROUTE_PATH });
  const queryClient = useQueryClient();
  const session = useSession();
  const isAdmin = session?.role === 'Admin';

  const activeTab: DetailTab = search.tab ?? 'definition';

  const query = useQuery({ queryKey: ['segment', id], queryFn: () => api.segments.get(id) });

  const [running, setRunning] = useState(false);
  const [runMessage, setRunMessage] = useState<string | null>(null);
  const [runError, setRunError] = useState<string | null>(null);
  const [archiveConfirming, setArchiveConfirming] = useState(false);

  const setTab = useCallback(
    (tab: DetailTab) => {
      void navigate({ search: (prev) => ({ ...prev, tab }), replace: true });
    },
    [navigate],
  );

  const handleRunSettled = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['segment', id] });
    void queryClient.invalidateQueries({ queryKey: ['segment', id, 'members'] });
  }, [queryClient, id]);

  async function handleRunNow() {
    setRunning(true);
    setRunError(null);
    setRunMessage(null);
    try {
      await api.segments.run(id);
      setRunMessage('Run queued');
      setTab('runs');
    } catch (err) {
      setRunError(err instanceof ApiError ? (err.problem?.detail ?? 'Run failed.') : 'Run failed.');
    } finally {
      setRunning(false);
    }
  }

  async function handleArchiveClick() {
    if (!archiveConfirming) {
      setArchiveConfirming(true);
      return;
    }
    await api.segments.archive(id);
    void navigate({ to: '/segments' });
  }

  if (query.isPending) {
    return (
      <p className="flex items-center gap-2 text-sm">
        <Spinner /> Loading…
      </p>
    );
  }

  if (query.isError) {
    return (
      <p role="alert" className="text-sm text-red-600">
        {String(query.error)}
      </p>
    );
  }

  const segment = query.data;

  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-center gap-3">
          <CardTitle as="h1">{segment.name}</CardTitle>
          <Badge variant={statusVariant(segment.status)}>{segment.status}</Badge>
          <Badge data-testid="segment-badge" variant={segment.isRealtimeEligible ? 'success' : 'muted'}>
            {segment.isRealtimeEligible ? 'real-time' : 'batch'}
          </Badge>
        </div>
        <p className="text-sm text-zinc-500 dark:text-zinc-400">
          Members {segment.memberCount}
          {segment.realtimeMemberCount !== null ? ` · live ${segment.realtimeMemberCount}` : ''}
          {' · '}Last run {formatDateTime(segment.lastRunAt)}
        </p>

        {isAdmin && (
          <div className="flex flex-wrap items-center gap-3">
            <Button onClick={() => void handleRunNow()} disabled={running}>
              Run now
            </Button>
            <Button
              variant="outline"
              onClick={() => void handleArchiveClick()}
              onBlur={() => setArchiveConfirming(false)}
            >
              {archiveConfirming ? 'Confirm archive' : 'Archive'}
            </Button>
          </div>
        )}

        {runMessage ? (
          <p role="status" className="text-sm">
            {runMessage}
          </p>
        ) : null}
        {runError ? (
          <p role="alert" className="text-sm text-red-600">
            {runError}
          </p>
        ) : null}
      </CardHeader>
      <CardContent className="space-y-4">
        <Tabs value={activeTab} onChange={setTab} tabs={TAB_ITEMS} />

        <TabPanel value="definition" active={activeTab}>
          <SegmentForm
            initial={segment}
            onSaved={() => {
              void queryClient.invalidateQueries({ queryKey: ['segment', id] });
            }}
          />
        </TabPanel>

        <TabPanel value="members" active={activeTab}>
          <MembersTab segmentId={id} currentRunId={segment.currentRunId} />
        </TabPanel>

        <TabPanel value="runs" active={activeTab}>
          <RunsTab segmentId={id} onSettled={handleRunSettled} />
        </TabPanel>
      </CardContent>
    </Card>
  );
}
