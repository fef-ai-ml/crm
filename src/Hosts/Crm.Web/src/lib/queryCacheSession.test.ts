import { QueryClient } from '@tanstack/react-query';
import { beforeEach, describe, expect, it } from 'vitest';
import { decodeSession, sessionStore, type Session } from './auth';
import { bindQueryCacheToSession } from './queryCacheSession';

function base64UrlEncode(input: string): string {
  return btoa(input).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function makeToken(payload: Record<string, unknown>): string {
  const header = base64UrlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const body = base64UrlEncode(JSON.stringify(payload));
  return `${header}.${body}.signature`;
}

function makeSession(userId: string, tenantId: string): Session {
  const exp = Math.floor(Date.now() / 1000) + 900;
  const accessToken = makeToken({ sub: userId, tenant_id: tenantId, role: 'Admin', exp });
  return decodeSession({ accessToken, expiresIn: 900 });
}

describe('bindQueryCacheToSession (I-1)', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  it('clears a cached entry after logout (session becomes null)', () => {
    const client = new QueryClient();
    sessionStore.set(makeSession('user-1', 'tenant-1'));
    const unsubscribe = bindQueryCacheToSession(client, sessionStore);
    client.setQueryData(['segments', ''], { items: ['tenant-1 data'] });

    sessionStore.set(null); // what logout() / a failed refresh does

    expect(client.getQueryData(['segments', ''])).toBeUndefined();
    unsubscribe();
  });

  it('clears a cached entry after a login as a different userId', () => {
    const client = new QueryClient();
    sessionStore.set(makeSession('user-1', 'tenant-1'));
    const unsubscribe = bindQueryCacheToSession(client, sessionStore);
    client.setQueryData(['segments', ''], { items: ['user-1 data'] });

    sessionStore.set(makeSession('user-2', 'tenant-1'));

    expect(client.getQueryData(['segments', ''])).toBeUndefined();
    unsubscribe();
  });

  it('clears a cached entry after a login into a different tenant', () => {
    const client = new QueryClient();
    sessionStore.set(makeSession('user-1', 'tenant-1'));
    const unsubscribe = bindQueryCacheToSession(client, sessionStore);
    client.setQueryData(['segments', ''], { items: ['tenant-1 data'] });

    sessionStore.set(makeSession('user-1', 'tenant-2'));

    expect(client.getQueryData(['segments', ''])).toBeUndefined();
    unsubscribe();
  });

  it('does not clear the cache when the session identity is unchanged (e.g. a token refresh)', () => {
    const client = new QueryClient();
    const session = makeSession('user-1', 'tenant-1');
    sessionStore.set(session);
    const unsubscribe = bindQueryCacheToSession(client, sessionStore);
    client.setQueryData(['segments', ''], { items: ['tenant-1 data'] });

    sessionStore.set(session); // same identity, e.g. a rotated access token from refresh

    expect(client.getQueryData(['segments', ''])).toEqual({ items: ['tenant-1 data'] });
    unsubscribe();
  });

  it('unsubscribe stops further clears', () => {
    const client = new QueryClient();
    sessionStore.set(makeSession('user-1', 'tenant-1'));
    const unsubscribe = bindQueryCacheToSession(client, sessionStore);
    unsubscribe();
    client.setQueryData(['segments', ''], { items: ['tenant-1 data'] });

    sessionStore.set(null);

    expect(client.getQueryData(['segments', ''])).toEqual({ items: ['tenant-1 data'] });
  });
});
