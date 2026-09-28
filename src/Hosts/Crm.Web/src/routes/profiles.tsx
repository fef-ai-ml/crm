import { useEffect, useRef, useState } from 'react';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { useInfiniteQuery } from '@tanstack/react-query';
import { ApiError } from '@/lib/api';
import { api } from '@/lib/client';
import { useDebouncedValue } from '@/lib/useDebouncedValue';
import { formatDateTime } from '@/lib/format';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TBody, TD, TH, THead, TR } from '@/components/ui/table';
import { ProfileDrawer } from '@/profiles/ProfileDrawer';

export type ProfilesSearch = { q?: string; id?: string };

// J1/J16 (see src/routes/segments/detail.tsx for the same note): the registered route id carries the pathless
// `authed` layout's prefix even though the URL itself is just `/profiles`.
const ROUTE_ID = '/authed/profiles';
const ROUTE_PATH = '/profiles';

export function ProfilesPage() {
  const search = useSearch({ from: ROUTE_ID });
  const navigate = useNavigate({ from: ROUTE_PATH });
  const q = search.q ?? '';

  const [text, setText] = useState(q);
  const debouncedText = useDebouncedValue(text, 300);
  // I-2: the last value THIS page navigated `q` to, so the reverse-sync effect below can tell "the URL changed
  // because we navigated it" apart from "the URL changed for some other reason" (e.g. the "Profiles" nav link
  // resetting search to `{}`) — only the latter should resync the input.
  const lastNavigatedRef = useRef(q);

  // External changes to `q` (not produced by this page's own debounce-driven navigate below) resync the input.
  // Runs before the forward effect notices anything: it records the externally-set value in the ref too, so
  // once `debouncedText` catches up to it (after 300 ms) the forward effect below sees them equal and skips.
  useEffect(() => {
    if (q === lastNavigatedRef.current) return;
    lastNavigatedRef.current = q;
    setText(q);
  }, [q]);

  // J14: `q` is debounced 300 ms and mirrored into the URL search param. Deliberately depends on `debouncedText`
  // alone (not `q`) — otherwise an external `q` change (handled above) would immediately re-run this effect
  // before the reset propagates through `text`/`debouncedText`, and navigate `q` right back (I-2).
  useEffect(() => {
    if (debouncedText === q) return;
    lastNavigatedRef.current = debouncedText;
    void navigate({ search: (prev) => ({ ...prev, q: debouncedText || undefined }), replace: true });
  }, [debouncedText]);

  const query = useInfiniteQuery({
    queryKey: ['profiles', q],
    queryFn: ({ pageParam }: { pageParam: string | undefined }) =>
      api.profiles.search({ q: q || undefined, cursor: pageParam }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });

  const items = query.data?.pages.flatMap((page) => page.items) ?? [];

  function openProfile(id: string) {
    void navigate({ search: (prev) => ({ ...prev, id }) });
  }

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-semibold">Profiles</h1>

      <div className="max-w-sm space-y-1 text-sm">
        <label htmlFor="profile-search">Search profiles</label>
        <Input
          id="profile-search"
          aria-label="Search profiles"
          placeholder="email, external id or phone"
          value={text}
          onChange={(event) => setText(event.target.value)}
        />
      </div>

      {query.isError ? (
        <p role="alert" className="text-sm text-red-600">
          {query.error instanceof ApiError ? (query.error.problem?.detail ?? 'Search failed.') : 'Search failed.'}
        </p>
      ) : null}

      <Table>
        <THead>
          <TR>
            <TH>External id</TH>
            <TH>Email</TH>
            <TH>Last seen</TH>
            <TH>Events</TH>
          </TR>
        </THead>
        <TBody>
          {items.map((item) => (
            <TR key={item.id} data-testid="profile-row">
              <TD>
                <button
                  type="button"
                  className="font-medium text-left underline-offset-2 hover:underline"
                  onClick={() => openProfile(item.id)}
                >
                  {item.externalId ?? item.id}
                </button>
              </TD>
              <TD>{item.email ?? '—'}</TD>
              <TD>{formatDateTime(item.lastSeenAt)}</TD>
              <TD>{item.eventCount}</TD>
            </TR>
          ))}
        </TBody>
      </Table>

      {query.hasNextPage && (
        <Button variant="outline" onClick={() => void query.fetchNextPage()} disabled={query.isFetchingNextPage}>
          Load more
        </Button>
      )}

      {search.id ? <ProfileDrawer id={search.id} /> : null}
    </div>
  );
}
