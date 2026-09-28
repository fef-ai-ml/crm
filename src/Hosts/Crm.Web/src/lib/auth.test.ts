import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { decodeSession, login, logout, refreshSession, sessionStore, type Session } from './auth';

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
    headers: { 'content-type': 'application/json' },
  });
}

function sampleAccessToken(overrides: Record<string, unknown> = {}): string {
  const exp = Math.floor(Date.now() / 1000) + 900;
  return makeToken({ sub: 'user-1', tenant_id: 'tenant-1', role: 'Admin', exp, jti: 'j-1', kind: 'user', ...overrides });
}

function sampleSession(): Session {
  return decodeSession({ accessToken: sampleAccessToken(), expiresIn: 900 });
}

describe('decodeSession', () => {
  it('reads sub, tenant_id, role and exp from the JWT payload', () => {
    const exp = Math.floor(Date.now() / 1000) + 900;
    const accessToken = makeToken({ sub: 'user-1', tenant_id: 'tenant-1', role: 'Admin', exp, jti: 'j-1', kind: 'user' });

    const session = decodeSession({ accessToken, expiresIn: 900 });

    expect(session).toEqual({
      accessToken,
      expiresAt: exp * 1000,
      role: 'Admin',
      tenantId: 'tenant-1',
      userId: 'user-1',
    });
  });

  it('takes the first role when the claim is an array', () => {
    const accessToken = makeToken({ sub: 'u', tenant_id: 't', role: ['Admin', 'Viewer'], exp: Math.floor(Date.now() / 1000) + 900 });

    const session = decodeSession({ accessToken, expiresIn: 900 });

    expect(session.role).toBe('Admin');
  });

  it('falls back to now + expiresIn seconds when exp is absent', () => {
    const accessToken = makeToken({ sub: 'u', tenant_id: 't', role: 'Viewer' });
    const before = Date.now();

    const session = decodeSession({ accessToken, expiresIn: 60 });

    expect(session.expiresAt).toBeGreaterThanOrEqual(before + 60_000);
  });

  it('throws on a payload without role', () => {
    const accessToken = makeToken({ sub: 'user-1', tenant_id: 'tenant-1', exp: Math.floor(Date.now() / 1000) + 900 });

    expect(() => decodeSession({ accessToken, expiresIn: 900 })).toThrow();
  });
});

describe('login', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('posts { email, password } to /v1/auth/login and stores the session', async () => {
    const accessToken = sampleAccessToken();
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ accessToken, expiresIn: 900 }));
    vi.stubGlobal('fetch', fetchMock);

    const session = await login('admin@demo.local', 'secret');

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/v1/auth/login');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({ email: 'admin@demo.local', password: 'secret' });
    expect(session.role).toBe('Admin');
    expect(sessionStore.get()).toEqual(session);
  });
});

describe('refreshSession', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('returns null and clears the store on 401', async () => {
    sessionStore.set(sampleSession());
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(jsonResponse({ code: 'auth.invalid_refresh_token', detail: 'x' }, 401)),
    );

    const result = await refreshSession();

    expect(result).toBeNull();
    expect(sessionStore.get()).toBeNull();
  });

  it('shares one in-flight promise across concurrent callers', async () => {
    const accessToken = sampleAccessToken();
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ accessToken, expiresIn: 900 }));
    vi.stubGlobal('fetch', fetchMock);

    const [a, b] = await Promise.all([refreshSession(), refreshSession()]);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(a).toEqual(b);
    expect(sessionStore.get()).toEqual(a);
  });
});

describe('logout', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('clears the store even when the request errors', async () => {
    sessionStore.set(sampleSession());
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('network down')));

    await logout();

    expect(sessionStore.get()).toBeNull();
  });
});
