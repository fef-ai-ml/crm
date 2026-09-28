import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { sessionStore } from './auth';
import { api } from './client';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

// M-2: ids come from route params and the `id` search param (TanStack decodes them), so an id containing `/`
// or `..` was interpolated raw into the path — `fetch` then normalizes it into a different same-origin
// endpoint, which could carry the user's bearer and, on the DELETE endpoints, a destructive method.
describe('client.ts path ids are encoded (M-2)', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('segments.get encodes an id containing a slash', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ definition: { type: 'attribute', key: 'k', cmp: 'eq', value: 'v' } }));
    vi.stubGlobal('fetch', fetchMock);

    await api.segments.get('a/b').catch(() => {});

    expect(fetchMock.mock.calls[0][0]).toBe('/v1/segments/a%2Fb');
  });

  it('segments.update encodes the id', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ definition: { type: 'attribute', key: 'k', cmp: 'eq', value: 'v' } }));
    vi.stubGlobal('fetch', fetchMock);

    await api.segments
      .update('a/b', { name: 'n', description: null, definition: { type: 'attribute', key: 'k', cmp: 'eq', value: 'v' }, status: 'Draft' })
      .catch(() => {});

    expect(fetchMock.mock.calls[0][0]).toBe('/v1/segments/a%2Fb');
  });

  it('segments.archive encodes the id', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await api.segments.archive('a/b');

    expect(fetchMock.mock.calls[0][0]).toBe('/v1/segments/a%2Fb');
  });

  it('segments.run encodes the id', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ runId: 'r1' }, 202));
    vi.stubGlobal('fetch', fetchMock);

    await api.segments.run('a/b');

    expect(fetchMock.mock.calls[0][0]).toBe('/v1/segments/a%2Fb/run');
  });

  it('segments.runs encodes the id', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ items: [], nextCursor: null }));
    vi.stubGlobal('fetch', fetchMock);

    await api.segments.runs('a/b');

    expect(fetchMock.mock.calls[0][0]).toBe('/v1/segments/a%2Fb/runs');
  });

  it('segments.members encodes the id', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ items: [], nextCursor: null }));
    vi.stubGlobal('fetch', fetchMock);

    await api.segments.members('a/b');

    expect(fetchMock.mock.calls[0][0]).toBe('/v1/segments/a%2Fb/members');
  });

  it('profiles.get encodes the id', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ attributes: {} }));
    vi.stubGlobal('fetch', fetchMock);

    await api.profiles.get('a/b').catch(() => {});

    expect(fetchMock.mock.calls[0][0]).toBe('/v1/profiles/a%2Fb');
  });

  it('profiles.events encodes the id', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ items: [], nextCursor: null }));
    vi.stubGlobal('fetch', fetchMock);

    await api.profiles.events('a/b');

    expect(fetchMock.mock.calls[0][0]).toBe('/v1/profiles/a%2Fb/events');
  });

  it('apiKeys.revoke encodes the id', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await api.apiKeys.revoke('a/b');

    expect(fetchMock.mock.calls[0][0]).toBe('/v1/api-keys/a%2Fb');
  });
});
