import { useNavigate } from '@tanstack/react-router';
import { useQuery } from '@tanstack/react-query';
import { ApiError } from '@/lib/api';
import { api } from '@/lib/client';
import { formatDateTime } from '@/lib/format';
import { Button } from '@/components/ui/button';
import { Spinner } from '@/components/ui/spinner';
import { Timeline } from './Timeline';

const ROUTE_PATH = '/profiles';

export type ProfileDrawerProps = { id: string };

/** J14: the selected profile is the `id` URL search param, so the drawer's own state (which profile, its data)
 * is entirely a function of that param — closing it just removes `id`. */
export function ProfileDrawer({ id }: ProfileDrawerProps) {
  const navigate = useNavigate({ from: ROUTE_PATH });
  const query = useQuery({ queryKey: ['profile', id], queryFn: () => api.profiles.get(id) });

  function handleClose() {
    void navigate({ search: (prev) => ({ ...prev, id: undefined }) });
  }

  const notFound = query.error instanceof ApiError && query.error.status === 404;

  return (
    <aside
      role="dialog"
      aria-label="Profile"
      aria-modal="false"
      className="fixed inset-y-0 right-0 z-20 flex w-full flex-col gap-4 overflow-y-auto border-l border-zinc-200 bg-white p-6 shadow-lg sm:w-[28rem] dark:border-zinc-800 dark:bg-zinc-900"
    >
      <div className="flex items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">{query.data ? (query.data.externalId ?? query.data.email ?? id) : 'Profile'}</h2>
        <Button variant="outline" size="sm" onClick={handleClose}>
          Close
        </Button>
      </div>

      {query.isPending ? (
        <p className="flex items-center gap-2 text-sm">
          <Spinner /> Loading…
        </p>
      ) : null}

      {query.isError ? (
        <p role="alert" className="text-sm text-red-600">
          {notFound
            ? 'Profile not found'
            : query.error instanceof ApiError
              ? (query.error.problem?.detail ?? 'Failed to load profile.')
              : 'Failed to load profile.'}
        </p>
      ) : null}

      {query.data ? (
        <div className="space-y-4">
          <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
            <dt className="text-zinc-500 dark:text-zinc-400">External id</dt>
            <dd>{query.data.externalId ?? '—'}</dd>
            <dt className="text-zinc-500 dark:text-zinc-400">Email</dt>
            <dd>{query.data.email ?? '—'}</dd>
            <dt className="text-zinc-500 dark:text-zinc-400">Phone</dt>
            <dd>{query.data.phone ?? '—'}</dd>
            <dt className="text-zinc-500 dark:text-zinc-400">First seen</dt>
            <dd>{formatDateTime(query.data.firstSeenAt)}</dd>
            <dt className="text-zinc-500 dark:text-zinc-400">Last seen</dt>
            <dd>{formatDateTime(query.data.lastSeenAt)}</dd>
            <dt className="text-zinc-500 dark:text-zinc-400">Events</dt>
            <dd>{query.data.eventCount}</dd>
          </dl>

          <div>
            <h3 className="text-sm font-medium">Attributes</h3>
            <pre
              data-testid="attributes-json"
              className="mt-1 overflow-auto rounded-md bg-zinc-100 p-3 text-xs dark:bg-zinc-800"
            >
              {JSON.stringify(query.data.attributes ?? {}, null, 2)}
            </pre>
          </div>

          <Timeline profileId={id} />
        </div>
      ) : null}
    </aside>
  );
}
