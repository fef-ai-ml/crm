import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { decodeSession, sessionStore } from '@/lib/auth';
import { createAppRouter } from '@/router';

function base64UrlEncode(input: string): string {
  return btoa(input).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function makeToken(payload: Record<string, unknown>): string {
  const header = base64UrlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const body = base64UrlEncode(JSON.stringify(payload));
  return `${header}.${body}.signature`;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(body === null ? null : JSON.stringify(body), {
    status,
    headers: body === null ? {} : { 'content-type': 'application/json' },
  });
}

function setSession(role: 'Admin' | 'Viewer') {
  const accessToken = makeToken({ sub: 'u1', tenant_id: 't1', role, exp: Math.floor(Date.now() / 1000) + 900 });
  sessionStore.set(decodeSession({ accessToken, expiresIn: 900 }));
}

type ApiKeyItem = {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
};

async function renderAtApiKeys() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createAppRouter(createMemoryHistory({ initialEntries: ['/api-keys'] }));
  await router.load();
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

describe('/api-keys route guard', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('redirects a Viewer session to /segments', async () => {
    setSession('Viewer');
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ items: [], nextCursor: null })));

    const router = await renderAtApiKeys();

    await waitFor(() => expect(router.state.location.pathname).toBe('/segments'));
  });

  it('renders for an Admin session', async () => {
    setSession('Admin');
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ items: [], nextCursor: null })));

    await renderAtApiKeys();

    expect(await screen.findByRole('heading', { name: 'API keys' })).toBeTruthy();
  });
});

describe('ApiKeysPage (E2E-007 unit coverage)', () => {
  beforeEach(() => {
    sessionStore.set(null);
    HTMLDialogElement.prototype.showModal = vi.fn();
    HTMLDialogElement.prototype.close = vi.fn();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists keys with their prefix, created/last-used/revoked timestamps ("—" for null)', async () => {
    setSession('Admin');
    const item: ApiKeyItem = {
      id: 'k1',
      name: 'ci',
      prefix: 'abcd1234',
      createdAt: '2026-01-01T00:00:00Z',
      lastUsedAt: null,
      revokedAt: null,
    };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ items: [item], nextCursor: null })));

    await renderAtApiKeys();

    const rows = await screen.findAllByTestId('api-key-row');
    expect(rows).toHaveLength(1);
    expect(within(rows[0]).getByText('ci')).toBeTruthy();
    expect(within(rows[0]).getByText('abcd1234')).toBeTruthy();
    expect(within(rows[0]).getAllByText('—')).toHaveLength(2); // last used, revoked
  });

  it('create shows the dialog with the secret exactly once; Close removes it from the DOM', async () => {
    setSession('Admin');
    let created = false;
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (url === '/v1/api-keys' && method === 'GET') {
        const items: ApiKeyItem[] = created
          ? [{ id: 'k1', name: 'sdk-e2e', prefix: 'abcd1234', createdAt: '2026-01-01T00:00:00Z', lastUsedAt: null, revokedAt: null }]
          : [];
        return Promise.resolve(jsonResponse({ items, nextCursor: null }));
      }
      if (url === '/v1/api-keys' && method === 'POST') {
        created = true;
        return Promise.resolve(
          jsonResponse({ id: 'k1', name: 'sdk-e2e', prefix: 'abcd1234', key: 'sdk_live_abcd1234thesecret' }, 201),
        );
      }
      return Promise.resolve(jsonResponse({ title: 'not found' }, 404));
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderAtApiKeys();

    fireEvent.change(await screen.findByLabelText('Key name'), { target: { value: 'sdk-e2e' } });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    });

    const secretEls = await screen.findAllByTestId('api-key-secret');
    expect(secretEls).toHaveLength(1);
    expect(secretEls[0].textContent).toBe('sdk_live_abcd1234thesecret');

    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByTestId('api-key-secret')).toBeNull());

    // The secret never enters the ['api-keys'] query cache — the refetched list carries only the prefix.
    expect(await screen.findByText('abcd1234')).toBeTruthy();
    expect(screen.queryByText('sdk_live_abcd1234thesecret')).toBeNull();
  });

  it('Copy writes the secret to a stubbed clipboard and reads "Copied"', async () => {
    setSession('Admin');
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (url === '/v1/api-keys' && method === 'GET') {
        return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
      }
      if (url === '/v1/api-keys' && method === 'POST') {
        return Promise.resolve(jsonResponse({ id: 'k1', name: 'sdk', prefix: 'abcd1234', key: 'the-secret-key' }, 201));
      }
      return Promise.resolve(jsonResponse({ title: 'not found' }, 404));
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderAtApiKeys();

    fireEvent.change(await screen.findByLabelText('Key name'), { target: { value: 'sdk' } });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    });
    await screen.findByTestId('api-key-secret');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Copy' }));
    });

    expect(writeText).toHaveBeenCalledWith('the-secret-key');
    expect(await screen.findByRole('button', { name: 'Copied' })).toBeTruthy();
  });

  it('a clipboard failure shows "Copy failed"', async () => {
    setSession('Admin');
    const writeText = vi.fn().mockRejectedValue(new Error('denied'));
    Object.assign(navigator, { clipboard: { writeText } });
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (url === '/v1/api-keys' && method === 'GET') {
        return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
      }
      if (url === '/v1/api-keys' && method === 'POST') {
        return Promise.resolve(jsonResponse({ id: 'k1', name: 'sdk', prefix: 'abcd1234', key: 'the-secret-key' }, 201));
      }
      return Promise.resolve(jsonResponse({ title: 'not found' }, 404));
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderAtApiKeys();

    fireEvent.change(await screen.findByLabelText('Key name'), { target: { value: 'sdk' } });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    });
    await screen.findByTestId('api-key-secret');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Copy' }));
    });

    expect(await screen.findByRole('button', { name: 'Copy failed' })).toBeTruthy();
  });

  it('Revoke needs two clicks, then calls DELETE and the row shows a revoked timestamp with no button', async () => {
    setSession('Admin');
    let revoked = false;
    const baseItem: ApiKeyItem = {
      id: 'k1',
      name: 'ci',
      prefix: 'abcd1234',
      createdAt: '2026-01-01T00:00:00Z',
      lastUsedAt: null,
      revokedAt: null,
    };
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (url === '/v1/api-keys' && method === 'GET') {
        const item = revoked ? { ...baseItem, revokedAt: '2026-02-01T00:00:00Z' } : baseItem;
        return Promise.resolve(jsonResponse({ items: [item], nextCursor: null }));
      }
      if (url === '/v1/api-keys/k1' && method === 'DELETE') {
        revoked = true;
        return Promise.resolve(jsonResponse(null, 204));
      }
      return Promise.resolve(jsonResponse({ title: 'not found' }, 404));
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderAtApiKeys();

    const row = await screen.findByTestId('api-key-row');
    const revokeButton = within(row).getByRole('button', { name: 'Revoke' });
    await act(async () => {
      fireEvent.click(revokeButton);
    });

    expect(fetchMock).not.toHaveBeenCalledWith('/v1/api-keys/k1', expect.objectContaining({ method: 'DELETE' }));
    const confirmButton = within(row).getByRole('button', { name: 'Confirm revoke' });

    await act(async () => {
      fireEvent.click(confirmButton);
    });

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith('/v1/api-keys/k1', expect.objectContaining({ method: 'DELETE' })),
    );

    await waitFor(() => {
      const updatedRow = screen.getByTestId('api-key-row');
      expect(within(updatedRow).queryByRole('button', { name: /Revoke/ })).toBeNull();
      const cells = updatedRow.querySelectorAll('td');
      expect(cells[4].textContent).toContain('2026'); // "Revoked" column, no longer "—"
    });
  });

  it('a 400 `errors.name` renders under the input as an alert', async () => {
    setSession('Admin');
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      if (url === '/v1/api-keys' && method === 'GET') {
        return Promise.resolve(jsonResponse({ items: [], nextCursor: null }));
      }
      if (url === '/v1/api-keys' && method === 'POST') {
        return Promise.resolve(
          jsonResponse({ code: 'validation_error', errors: { name: ['must be 1 to 128 characters'] } }, 400),
        );
      }
      return Promise.resolve(jsonResponse({ title: 'not found' }, 404));
    });
    vi.stubGlobal('fetch', fetchMock);

    await renderAtApiKeys();

    fireEvent.change(await screen.findByLabelText('Key name'), { target: { value: '' } });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Create key' }));
    });

    const message = await screen.findByText('must be 1 to 128 characters');
    expect(message.closest('[role="alert"]')).toBeTruthy();
    expect(screen.queryByTestId('api-key-secret')).toBeNull();
  });
});
