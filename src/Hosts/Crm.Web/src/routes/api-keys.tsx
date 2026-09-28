import { useState, type FormEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '@/lib/api';
import { api } from '@/lib/client';
import type { ApiKeyCreated, ApiKeyListItem } from '@/lib/client';
import { formatDateTime } from '@/lib/format';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Dialog } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Spinner } from '@/components/ui/spinner';
import { Table, TBody, TD, TH, THead, TR } from '@/components/ui/table';

type RevokeState = 'idle' | 'confirming' | 'revoking';
type CopyState = 'idle' | 'copied' | 'failed';

type ApiKeyRowProps = {
  item: ApiKeyListItem;
  onRevoked: () => void;
};

/** Revoke is a two-click confirm (J13), the same idiom as segment Archive: the first click arms a
 * "Confirm revoke" state, a second click within the same focus sends the request, and blurring the button
 * resets it. A revoked row never renders this button again — `item.revokedAt` alone decides. */
function ApiKeyRow({ item, onRevoked }: ApiKeyRowProps) {
  const [state, setState] = useState<RevokeState>('idle');
  const [error, setError] = useState<string | null>(null);

  async function handleClick() {
    if (state === 'revoking') return;

    if (state === 'idle') {
      setError(null);
      setState('confirming');
      return;
    }

    setState('revoking');
    try {
      await api.apiKeys.revoke(item.id);
      onRevoked();
    } catch (err) {
      setError(err instanceof ApiError ? (err.problem?.detail ?? 'Revoke failed.') : 'Revoke failed.');
      setState('idle');
    }
  }

  return (
    <TR data-testid="api-key-row">
      <TD>{item.name}</TD>
      <TD>{item.prefix}</TD>
      <TD>{formatDateTime(item.createdAt)}</TD>
      <TD>{formatDateTime(item.lastUsedAt)}</TD>
      <TD>{formatDateTime(item.revokedAt)}</TD>
      <TD>
        {item.revokedAt === null ? (
          <>
            <Button
              variant="outline"
              size="sm"
              onClick={() => void handleClick()}
              onBlur={() => setState((current) => (current === 'confirming' ? 'idle' : current))}
              disabled={state === 'revoking'}
            >
              {state === 'revoking' ? 'Revoking…' : state === 'confirming' ? 'Confirm revoke' : 'Revoke'}
            </Button>
            {error ? (
              <p role="alert" className="mt-1 text-xs text-red-600">
                {error}
              </p>
            ) : null}
          </>
        ) : null}
      </TD>
    </TR>
  );
}

/** Admin-only (J9; the route's `beforeLoad` in `router.tsx` redirects a Viewer to `/segments` before this
 * ever renders). The created key's full secret lives only in this component's own state (`secret`), never in
 * the `['api-keys']` query cache and never re-fetched — closing the dialog drops it from the DOM for good. */
export function ApiKeysPage() {
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: ['api-keys'], queryFn: () => api.apiKeys.list() });

  const [name, setName] = useState('');
  const [nameErrors, setNameErrors] = useState<string[]>([]);
  const [creating, setCreating] = useState(false);
  const [secret, setSecret] = useState<ApiKeyCreated | null>(null);
  const [copyState, setCopyState] = useState<CopyState>('idle');

  function handleRevoked() {
    void queryClient.invalidateQueries({ queryKey: ['api-keys'] });
  }

  function handleCloseSecret() {
    setSecret(null);
    setCopyState('idle');
  }

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setNameErrors([]);
    setCreating(true);
    try {
      const created = await api.apiKeys.create(name);
      setName('');
      setCopyState('idle');
      setSecret(created);
      void queryClient.invalidateQueries({ queryKey: ['api-keys'] });
    } catch (err) {
      if (err instanceof ApiError && err.status === 400 && err.problem?.errors?.name) {
        setNameErrors(err.problem.errors.name);
      } else if (err instanceof ApiError) {
        setNameErrors([err.problem?.detail ?? 'Create failed.']);
      } else {
        setNameErrors(['Create failed.']);
      }
    } finally {
      setCreating(false);
    }
  }

  async function handleCopy() {
    if (!secret) return;
    try {
      await navigator.clipboard.writeText(secret.key);
      setCopyState('copied');
    } catch {
      setCopyState('failed');
    }
  }

  const items = query.data?.items ?? [];

  return (
    <Card>
      <CardHeader>
        <CardTitle as="h1">API keys</CardTitle>
      </CardHeader>
      <CardContent className="space-y-6">
        <form className="flex flex-wrap items-end gap-3" onSubmit={(event) => void handleCreate(event)}>
          <div className="space-y-1 text-sm">
            <label htmlFor="api-key-name">Key name</label>
            <Input
              id="api-key-name"
              aria-label="Key name"
              value={name}
              onChange={(event) => setName(event.target.value)}
              disabled={creating}
            />
            {nameErrors.map((message) => (
              <p role="alert" key={message} className="text-xs text-red-600">
                {message}
              </p>
            ))}
          </div>
          <Button type="submit" disabled={creating}>
            {creating ? 'Creating…' : 'Create key'}
          </Button>
        </form>

        {query.isPending ? (
          <p className="flex items-center gap-2 text-sm">
            <Spinner /> Loading…
          </p>
        ) : query.isError ? (
          <p role="alert" className="text-sm text-red-600">
            {String(query.error)}
          </p>
        ) : (
          <Table>
            <THead>
              <TR>
                <TH>Name</TH>
                <TH>Prefix</TH>
                <TH>Created</TH>
                <TH>Last used</TH>
                <TH>Revoked</TH>
                <TH>Actions</TH>
              </TR>
            </THead>
            <TBody>
              {items.map((item) => (
                <ApiKeyRow key={item.id} item={item} onRevoked={handleRevoked} />
              ))}
            </TBody>
          </Table>
        )}
      </CardContent>

      <Dialog open={secret !== null} onClose={handleCloseSecret}>
        {secret ? (
          <div className="space-y-4">
            <h2 className="text-lg font-semibold">Key created</h2>
            <p className="text-sm text-zinc-600 dark:text-zinc-400">
              Copy this key now — it is shown only once and cannot be retrieved again.
            </p>
            <code
              data-testid="api-key-secret"
              className="block break-all rounded-md bg-zinc-100 p-3 text-sm dark:bg-zinc-800"
            >
              {secret.key}
            </code>
            <div className="flex justify-end gap-3">
              <Button variant="outline" onClick={() => void handleCopy()}>
                {copyState === 'copied' ? 'Copied' : copyState === 'failed' ? 'Copy failed' : 'Copy'}
              </Button>
              <Button onClick={handleCloseSecret}>Close</Button>
            </div>
          </div>
        ) : null}
      </Dialog>
    </Card>
  );
}
