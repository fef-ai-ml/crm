import type { QueryClient } from '@tanstack/react-query';
import { sessionStore, type Session } from './auth';

type SessionIdentity = string | null;

function identityOf(session: Session | null): SessionIdentity {
  return session ? `${session.tenantId}:${session.userId}` : null;
}

type SessionSource = Pick<typeof sessionStore, 'get' | 'subscribe'>;

/**
 * I-1/J26: clears the whole Query cache whenever the session's tenant/user identity changes — including to
 * null, on logout or a failed background refresh — so the next request on the tab never renders a previous
 * user's (or tenant's) cached rows while its own refetch is in flight. Query keys carry no tenant/user
 * themselves (`['segments', status]` etc.), so this subscription is what actually enforces isolation
 * client-side; the server enforces it independently regardless (FR-24).
 *
 * A small exported function (rather than an inline subscription in main.tsx) so it's unit-testable without
 * mounting the app shell. `store` defaults to the real `sessionStore` and is only overridden in tests.
 */
export function bindQueryCacheToSession(
  queryClient: Pick<QueryClient, 'clear'>,
  store: SessionSource = sessionStore,
): () => void {
  let lastIdentity = identityOf(store.get());
  return store.subscribe(() => {
    const identity = identityOf(store.get());
    if (identity !== lastIdentity) {
      lastIdentity = identity;
      queryClient.clear();
    }
  });
}
