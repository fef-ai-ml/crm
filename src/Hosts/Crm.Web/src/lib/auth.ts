import { useSyncExternalStore } from 'react';
import { apiFetch } from './api';

export type Role = 'Admin' | 'Viewer';

export type Session = {
  accessToken: string;
  expiresAt: number;
  role: Role;
  tenantId: string;
  userId: string;
};

type TokenResponse = { accessToken: string; expiresIn: number };

function base64UrlDecode(segment: string): string {
  const base64 = segment.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
  return atob(padded);
}

function isRole(value: unknown): value is Role {
  return value === 'Admin' || value === 'Viewer';
}

/** Decodes the access token's JWT payload; no signature check (the server enforces auth anyway). */
export function decodeSession(token: TokenResponse): Session {
  const parts = token.accessToken.split('.');
  if (parts.length !== 3) {
    throw new Error('Malformed access token: expected a 3-part JWT');
  }

  let claims: Record<string, unknown>;
  try {
    claims = JSON.parse(base64UrlDecode(parts[1])) as Record<string, unknown>;
  } catch {
    throw new Error('Malformed access token: payload is not valid JSON');
  }

  const sub = claims.sub;
  const tenantId = claims.tenant_id;
  const roleClaim = Array.isArray(claims.role) ? claims.role[0] : claims.role;
  const exp = claims.exp;

  if (typeof sub !== 'string' || typeof tenantId !== 'string' || !isRole(roleClaim)) {
    throw new Error('Malformed access token: missing sub, tenant_id, or role claim');
  }

  const expiresAt = typeof exp === 'number' ? exp * 1000 : Date.now() + token.expiresIn * 1000;

  return { accessToken: token.accessToken, expiresAt, role: roleClaim, tenantId, userId: sub };
}

type Listener = () => void;

let currentSession: Session | null = null;
const listeners = new Set<Listener>();

export const sessionStore = {
  get(): Session | null {
    return currentSession;
  },
  set(next: Session | null): void {
    currentSession = next;
    for (const listener of listeners) listener();
  },
  subscribe(listener: Listener): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
};

export function useSession(): Session | null {
  return useSyncExternalStore(sessionStore.subscribe, sessionStore.get);
}

export async function login(email: string, password: string): Promise<Session> {
  const response = await apiFetch<TokenResponse>('/v1/auth/login', {
    method: 'POST',
    json: { email, password },
  });
  const session = decodeSession(response);
  sessionStore.set(session);
  return session;
}

let inFlightRefresh: Promise<Session | null> | null = null;

async function performRefresh(): Promise<Session | null> {
  try {
    const response = await apiFetch<TokenResponse>('/v1/auth/refresh', { method: 'POST' });
    const session = decodeSession(response);
    sessionStore.set(session);
    return session;
  } catch {
    sessionStore.set(null);
    return null;
  }
}

/** Shares one in-flight promise across concurrent callers (React StrictMode double-invokes effects). */
export function refreshSession(): Promise<Session | null> {
  if (!inFlightRefresh) {
    inFlightRefresh = performRefresh().finally(() => {
      inFlightRefresh = null;
    });
  }
  return inFlightRefresh;
}

export async function logout(): Promise<void> {
  try {
    // J25: apiFetch now gives /v1/auth/logout the standard refresh-and-replay on a 401 (C-1), so an idle
    // tab's "Log out" click still revokes the refresh token server-side instead of silently no-op'ing.
    await apiFetch('/v1/auth/logout', { method: 'POST' });
  } catch {
    // best effort — the server-side revocation matters more than the client noticing
  } finally {
    sessionStore.set(null);
  }
}
