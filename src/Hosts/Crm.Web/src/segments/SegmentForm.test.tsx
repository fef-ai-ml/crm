import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SegmentForm } from './SegmentForm';
import { decodeSession, sessionStore } from '@/lib/auth';
import { isRealtimeEligible } from '@/rules/eligibility';
import type { SegmentResponse } from '@/lib/client';
import type { RuleTree } from '@/rules/types';

// Never `import` a fixture (CT-002): resolve `import.meta.url` to a path first, then join.
const RULES_ROOT = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  '..',
  '..',
  '..',
  '..',
  '..',
  'tests',
  'fixtures',
  'rules',
);

function loadDefinition(kind: 'valid' | 'invalid', name: string): RuleTree {
  const raw = readFileSync(path.join(RULES_ROOT, kind, `${name}.json`), 'utf-8');
  return (JSON.parse(raw) as { definition: RuleTree }).definition;
}

function base64UrlEncode(input: string): string {
  return btoa(input).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function makeToken(payload: Record<string, unknown>): string {
  const header = base64UrlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const body = base64UrlEncode(JSON.stringify(payload));
  return `${header}.${body}.signature`;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

function setSession(role: 'Admin' | 'Viewer') {
  const accessToken = makeToken({ sub: 'u1', tenant_id: 't1', role, exp: Math.floor(Date.now() / 1000) + 900 });
  sessionStore.set(decodeSession({ accessToken, expiresIn: 900 }));
}

function makeSegment(overrides: Partial<SegmentResponse> = {}): SegmentResponse {
  return {
    id: 'seg-1',
    name: 'existing',
    description: null,
    definition: { type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' },
    status: 'Draft',
    isRealtimeEligible: true,
    memberCount: 0,
    realtimeMemberCount: null,
    lastRunAt: null,
    currentRunId: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function renderForm(props: { initial?: SegmentResponse; onSaved: (segment: SegmentResponse) => void }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SegmentForm {...props} />
    </QueryClientProvider>,
  );
}

describe('SegmentForm', () => {
  beforeEach(() => {
    sessionStore.set(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the client validation message on the window input immediately, and the request is still sent (E2E-003 ruling: client errors never block Save)', () => {
    setSession('Admin');
    // Never resolves: this test only cares what happens synchronously, before any network response lands.
    const fetchMock = vi.fn(() => new Promise<Response>(() => {}));
    vi.stubGlobal('fetch', fetchMock);

    const badWindowTree: RuleTree = { type: 'event', name: 'purchase', cmp: 'gte', count: 1, window: '400d' };
    const initial = makeSegment({ definition: badWindowTree });

    renderForm({ initial, onSaved: vi.fn() });

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(screen.getByText('window must match ^\\d+(d|h)$ and be at most 365 days')).toBeTruthy();
    expect(fetchMock).toHaveBeenCalledWith('/v1/segments/seg-1', expect.objectContaining({ method: 'PUT' }));
  });

  it('renders a server 400 field error on the row it belongs to (J7)', async () => {
    setSession('Admin');
    const initial = makeSegment({
      definition: { type: 'event', name: 'purchase', cmp: 'gte', count: 1, window: '30d' },
    });
    const fetchMock = vi.fn((url: string) => {
      if (url === '/v1/segments/seg-1') {
        return Promise.resolve(
          jsonResponse(
            {
              code: 'segments.invalid_definition',
              errors: { 'definition.window': ['window must match ^\\d+(d|h)$ and be at most 365 days'] },
            },
            400,
          ),
        );
      }
      return Promise.resolve(jsonResponse({ count: 10, timedOut: false, cachedAt: null }));
    });
    vi.stubGlobal('fetch', fetchMock);

    renderForm({ initial, onSaved: vi.fn() });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    const message = await screen.findByText('window must match ^\\d+(d|h)$ and be at most 365 days');
    expect(message).toBeTruthy();
  });

  it('renders a 409 name conflict under Name', async () => {
    setSession('Admin');
    const fetchMock = vi.fn((url: string) => {
      if (url === '/v1/segments') {
        return Promise.resolve(
          jsonResponse({ code: 'segment.name_taken', detail: 'A segment named "vip" already exists.' }, 409),
        );
      }
      return Promise.resolve(jsonResponse({ count: null, timedOut: false, cachedAt: null }));
    });
    vi.stubGlobal('fetch', fetchMock);

    renderForm({ onSaved: vi.fn() });
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'vip' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    const message = await screen.findByText('A segment named "vip" already exists.');
    expect(message).toBeTruthy();
  });

  it('the badge reads batch for seed-pro-id and real-time for seed-purchasers', () => {
    setSession('Admin');
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})));

    const proId = loadDefinition('valid', 'seed-pro-id');
    const { unmount } = renderForm({
      initial: makeSegment({ definition: proId, isRealtimeEligible: isRealtimeEligible(proId) }),
      onSaved: vi.fn(),
    });
    expect(screen.getByTestId('segment-badge').textContent).toBe('batch');
    unmount();

    const purchasers = loadDefinition('valid', 'seed-purchasers');
    renderForm({
      initial: makeSegment({ definition: purchasers, isRealtimeEligible: isRealtimeEligible(purchasers) }),
      onSaved: vi.fn(),
    });
    expect(screen.getByTestId('segment-badge').textContent).toBe('real-time');
  });

  it('a Viewer session renders no Save button', () => {
    setSession('Viewer');
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})));

    renderForm({ onSaved: vi.fn() });

    expect(screen.queryByRole('button', { name: 'Save' })).toBeNull();
  });
});
