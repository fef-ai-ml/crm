import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { sessionStore } from '@/lib/auth';
import { LoginForm } from './login';

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

describe('LoginForm', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('logs in and calls onSuccess with the session, never touching storage', async () => {
    const accessToken = makeToken({
      sub: 'u1',
      tenant_id: 't1',
      role: 'Admin',
      exp: Math.floor(Date.now() / 1000) + 900,
    });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ accessToken, expiresIn: 900 })));
    const onSuccess = vi.fn();

    render(<LoginForm onSuccess={onSuccess} />);
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'admin@demo.local' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: /Sign in/ }));

    await waitFor(() => expect(onSuccess).toHaveBeenCalledTimes(1));
    expect((onSuccess.mock.calls[0][0] as { role: string }).role).toBe('Admin');
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('shows the problem detail under the form on a 401', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        jsonResponse({ code: 'auth.invalid_credentials', detail: 'Email or password is incorrect.' }, 401),
      ),
    );

    render(<LoginForm onSuccess={vi.fn()} />);
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'admin@demo.local' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'wrong' } });
    fireEvent.click(screen.getByRole('button', { name: /Sign in/ }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Email or password is incorrect.');
    expect(window.localStorage.length).toBe(0);
  });

  it('shows field errors under the form on a 400', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(jsonResponse({ code: 'request.invalid', errors: { email: ['is required'] } }, 400)),
    );

    render(<LoginForm onSuccess={vi.fn()} />);
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'wrong' } });
    fireEvent.click(screen.getByRole('button', { name: /Sign in/ }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('is required');
  });
});
