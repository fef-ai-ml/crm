// LT-003 — real-time segment latency under load (docs/test/e2e-load.md, plan I).
//
//   source .env.local && k6 run -e CRM_API_KEY=$CRM_API_KEY tests/Crm.LoadTests/realtime-latency.js
//
// Run it alongside LT-001 (the ingest load). Once per second a probe tracks the probe event for a brand-new
// user and polls GET /v1/segments/{id} until `realtimeMemberCount` has grown by one; the time from the track's
// 202 to that observation is the `realtime_latency` trend, and the threshold fails the run when its p95 is 2 s
// or more (NFR-02).
//
// The spec names `seed-purchasers` (`purchase gte 1 in 30d`), but LT-001's own traffic contains purchases by
// new users, which would move that count too and make an increment unattributable. The probe therefore uses
// its own real-time-eligible segment, `lt-003-probe` (`lt003_probe gte 1 in 1h`), created on first use and
// reused afterwards: the same code path, with only the probe able to move the count.
//
// Environment: CRM_BASE_URL (default http://localhost:8080), CRM_API_KEY (required), CRM_ADMIN_EMAIL /
// CRM_ADMIN_PASSWORD (default: the seeded demo admin), PROBES (default 60).
import http from 'k6/http';
import { check, fail, sleep } from 'k6';
import { Trend } from 'k6/metrics';

const BASE_URL = __ENV.CRM_BASE_URL || 'http://localhost:8080';
const API_KEY = __ENV.CRM_API_KEY;
const EMAIL = __ENV.CRM_ADMIN_EMAIL || 'admin@demo.local';
const PASSWORD = __ENV.CRM_ADMIN_PASSWORD || 'Demo-Passw0rd!';
const PROBES = Number(__ENV.PROBES || 60);
const SEGMENT_NAME = 'lt-003-probe';
const PROBE_EVENT = 'lt003_probe';
const POLL_SECONDS = 0.05;
const GIVE_UP_SECONDS = 10;

const latency = new Trend('realtime_latency', true);

export const options = {
  scenarios: {
    // One VU, one probe at a time: overlapping probes could each claim the other's increment.
    probe: { executor: 'per-vu-iterations', vus: 1, iterations: PROBES, maxDuration: `${PROBES * (GIVE_UP_SECONDS + 2)}s` },
  },
  thresholds: {
    realtime_latency: ['p(95)<2000'],
    checks: ['rate==1'],
  },
};

export function setup() {
  if (!API_KEY) {
    fail('CRM_API_KEY is required (source .env.local after make seed)');
  }

  const login = http.post(`${BASE_URL}/v1/auth/login`, JSON.stringify({ email: EMAIL, password: PASSWORD }), json());
  check(login, { 'login 200': (r) => r.status === 200 }) || fail(`login failed: ${login.status}`);
  const token = login.json('accessToken');
  const auth = json({ Authorization: `Bearer ${token}` });

  const list = http.get(`${BASE_URL}/v1/segments?limit=200`, auth);
  const existing = (list.json('items') || []).find((s) => s.name === SEGMENT_NAME);
  if (existing) {
    return { token, segmentId: existing.id };
  }

  const created = http.post(`${BASE_URL}/v1/segments`, JSON.stringify({
    name: SEGMENT_NAME,
    status: 'Active',
    description: 'LT-003 probe segment; safe to archive',
    definition: { type: 'event', name: PROBE_EVENT, cmp: 'gte', count: 1, window: '1h' },
  }), auth);
  check(created, { 'probe segment created': (r) => r.status === 201 }) || fail(`creating ${SEGMENT_NAME} failed: ${created.status}`);
  return { token, segmentId: created.json('id') };
}

export default function (data) {
  const started = Date.now();
  probe(data);
  sleep(Math.max(0, 1 - (Date.now() - started) / 1000)); // once per second, never faster
}

function probe(data) {
  const auth = json({ Authorization: `Bearer ${data.token}` });
  const before = realtimeCount(data.segmentId, auth);

  const userId = `lt3-${__VU}-${__ITER}-${Date.now()}`;
  const tracked = http.post(`${BASE_URL}/v1/track`, JSON.stringify({ userId, event: PROBE_EVENT }), json({ 'X-Api-Key': API_KEY }));
  if (!check(tracked, { 'track 202': (r) => r.status === 202 })) {
    return;
  }

  const sent = Date.now();
  while (Date.now() - sent < GIVE_UP_SECONDS * 1000) {
    if (realtimeCount(data.segmentId, auth) > before) {
      latency.add(Date.now() - sent);
      return;
    }

    sleep(POLL_SECONDS);
  }

  check(null, { [`membership within ${GIVE_UP_SECONDS} s`]: () => false });
}

function realtimeCount(segmentId, params) {
  const response = http.get(`${BASE_URL}/v1/segments/${segmentId}`, Object.assign({ tags: { name: 'segment-detail' } }, params));
  return response.status === 200 ? (response.json('realtimeMemberCount') ?? -1) : -1;
}

function json(headers = {}) {
  return { headers: Object.assign({ 'Content-Type': 'application/json' }, headers) };
}
