import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch, ApiError } from './api';
import { decodeSession, sessionStore, type Session } from './auth';

function base64UrlEncode(input: string): string {
  return btoa(input).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function makeToken(payload: Record<string, unknown>): string {
  const header = base64UrlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const body = base64UrlEncode(JSON.stringify(payload));
  return `${header}.${body}.signature`;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/problem+json' },
  });
}

function sampleSession(sub = 'user-1'): Session {
  const exp = Math.floor(Date.now() / 1000) + 900;
  const accessToken = makeToken({ sub, tenant_id: 'tenant-1', role: 'Admin', exp, jti: 'j-1', kind: 'user' });
  return decodeSession({ accessToken, expiresIn: 900 });
}

describe('apiFetch', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('sends Authorization: Bearer <token> when a session exists', async () => {
    const session = sampleSession();
    sessionStore.set(session);
    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ ok: true }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await apiFetch('/v1/segments');

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = init.headers as Record<string, string>;
    expect(headers.Authorization).toBe(`Bearer ${session.accessToken}`);
  });

  it('on a 401 it refreshes once and replays the original request with the new token', async () => {
    sessionStore.set(sampleSession());
    const newAccessToken = makeToken({
      sub: 'user-1',
      tenant_id: 'tenant-1',
      role: 'Admin',
      exp: Math.floor(Date.now() / 1000) + 900,
    });

    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse({ code: 'auth.unauthorized' }, 401))
      .mockResolvedValueOnce(new Response(JSON.stringify({ accessToken: newAccessToken, expiresIn: 900 }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ items: [] }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiFetch('/v1/segments');

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(fetchMock.mock.calls[1][0]).toBe('/v1/auth/refresh');
    const retryInit = fetchMock.mock.calls[2][1] as RequestInit;
    expect((retryInit.headers as Record<string, string>).Authorization).toBe(`Bearer ${newAccessToken}`);
    expect(result).toEqual({ items: [] });
  });

  it('throws ApiError(401) and clears the store when the refresh also fails', async () => {
    sessionStore.set(sampleSession());
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse({ code: 'auth.unauthorized' }, 401))
      .mockResolvedValueOnce(jsonResponse({ code: 'auth.invalid_refresh_token' }, 401));
    vi.stubGlobal('fetch', fetchMock);

    const error = await apiFetch('/v1/segments').catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(401);
    expect(sessionStore.get()).toBeNull();
  });

  it('exposes a 400 problem body as error.problem.errors', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ code: 'request.invalid', errors: { email: ['is required'] } }, 400));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiFetch('/v1/auth/login', { method: 'POST', json: {} })).rejects.toMatchObject({
      status: 400,
      problem: { errors: { email: ['is required'] } },
    });
  });

  it('does not retry /v1/auth/login on a 401', async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      jsonResponse({ code: 'auth.invalid_credentials', detail: 'Email or password is incorrect.' }, 401),
    );
    vi.stubGlobal('fetch', fetchMock);

    await expect(
      apiFetch('/v1/auth/login', { method: 'POST', json: { email: 'a', password: 'b' } }),
    ).rejects.toMatchObject({ status: 401 });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('resolves a 204 response to undefined', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(apiFetch('/v1/auth/logout', { method: 'POST' })).resolves.toBeUndefined();
  });
});
