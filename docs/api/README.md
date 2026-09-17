# API Reference

Base URL locally: `http://localhost:8080/v1`. All request and response bodies are JSON. The machine-readable contract `openapi.json` in this directory is generated from the API project at build time and committed; CI fails when the generated file differs from the committed one. Interactive docs are served at `/swagger` in development.

## Authentication

| Caller | Mechanism | Header |
|---|---|---|
| Ingest clients | Tenant API key, format `crm_live_<32 base62>` | `X-Api-Key: crm_live_...` |
| Dashboard users | JWT bearer (15 min access, 7 d refresh) with claims `sub`, `tenant_id`, `role` | `Authorization: Bearer <token>` |

Roles: `Admin` may do everything; `Viewer` may only read profiles and segments. API keys can only call Ingest endpoints. Keys are rate limited with a token bucket of 2,000 requests per minute; exceeding it returns `429` with `Retry-After`.

`POST /auth/login`, `POST /auth/refresh` and `POST /tenants` are anonymous and rate limited per client address (policy `auth`): a token bucket of 20 requests per minute; exceeding it returns `429` with `Retry-After`. Behind the bundled nginx proxy this depends on `ForwardedHeaders:KnownNetworks` trusting the proxy's network (set to the compose network in `deploy/docker-compose.yml`); a caller-supplied `X-Forwarded-For` is only honoured from a network listed there.

An access token issued before logout or a tenant suspension stays valid until it expires (at most 15 minutes) — only the refresh token is revoked immediately.

## Errors

Every error is `application/problem+json` (RFC 9457):

```json
{
  "type": "https://crm.example/problems/validation",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "detail": "event must match [A-Za-z0-9_]{1,64}",
  "instance": "/v1/track",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "errors": { "event": ["must match [A-Za-z0-9_]{1,64}"] }
}
```

| Status | When |
|---|---|
| 400 | Validation failure; `errors` maps field to messages |
| 401 | Missing or invalid credentials |
| 403 | Role not allowed, or API key used on a non-ingest endpoint |
| 404 | Resource not found within the caller's tenant |
| 409 | Conflict, for example duplicate segment name |
| 429 | Rate limit exceeded |
| 503 | Kafka unavailable at ingest; retry after `Retry-After` seconds |
| 500 | Unexpected error; `traceId` identifies the trace |

Every problem body also carries `code`, a stable machine-readable error code, safe to branch on in client code. A specific failure (for example `auth.invalid_credentials`, `tenant.slug_taken`, `user.email_taken`) gets its own code; anything else falls back to one derived from the status: `request.invalid` (400), `auth.unauthorized` (401), `auth.forbidden` (403), `resource.not_found` (404), `request.conflict` (409), `rate_limit.exceeded` (429), `service.unavailable` (503), `server.unexpected` (otherwise).

## Pagination

List endpoints accept `limit` (default 50, max 200) and `cursor` (opaque string from the previous response). Responses have the shape `{ "items": [...], "nextCursor": "..." | null }`. Offsets are not supported.

## Ingest

Auth: `X-Api-Key`. All ingest endpoints return `202 Accepted` after the event is durably written to Kafka; nothing touches the database on this path.

### POST /track

```json
{ "userId": "u-123", "anonymousId": null, "event": "purchase", "properties": { "amount": 49.9, "sku": "A1" }, "timestamp": "2026-09-15T08:00:00Z" }
```

Rules: at least one of `userId`, `anonymousId`; `event` matches `[A-Za-z0-9_]{1,64}`; `properties` ≤ 32 KB; `timestamp` optional, within ±7 days of now.

Response `202`: `{ "eventId": "0192b1c4-..." }`

```bash
curl -X POST http://localhost:8080/v1/track \
  -H "X-Api-Key: $CRM_API_KEY" -H "Content-Type: application/json" \
  -d '{"userId":"u-123","event":"purchase","properties":{"amount":49.9}}'
```

### POST /identify

```json
{ "userId": "u-123", "anonymousId": "anon-9f1", "traits": { "plan": "pro", "country": "ID" } }
```

Links `anonymousId` to `userId` (merging profiles when both already exist) and applies `traits` key by key, last write wins. Response `202`: `{ "eventId": "..." }`.

### POST /batch

```json
{ "events": [ { "type": "track", "userId": "u-1", "event": "app_open" }, { "type": "track", "userId": "u-1", "event": "bad name!" } ] }
```

Up to 500 items, each validated independently. Each item carries a `type` discriminator (`track` or `identify`) selecting which shape and validation rules apply. Response `202`:

```json
{ "accepted": 1, "rejected": 1, "results": [ { "index": 0, "eventId": "..." }, { "index": 1, "errors": { "events[1].event": ["must match [A-Za-z0-9_]{1,64}"] } } ] }
```

## Profiles

Auth: JWT (Admin or Viewer).

| Method | Path | Description |
|---|---|---|
| GET | `/profiles?q=&limit=&cursor=` | Search by email, external id, or phone prefix; sorted by `lastSeenAt` desc |
| GET | `/profiles/{id}` | Core fields plus `attributes` object |
| GET | `/profiles/{id}/events?from=&to=&name=&limit=&cursor=` | Timeline, newest first |

Profile shape:

```json
{ "id": "...", "externalId": "u-123", "email": "a@b.com", "phone": "+628123", "attributes": { "plan": "pro" }, "firstSeenAt": "...", "lastSeenAt": "...", "eventCount": 42 }
```

Event shape: `{ "id": "...", "name": "purchase", "properties": { }, "occurredAt": "...", "receivedAt": "..." }`.

## Segments

Auth: JWT. Writes require Admin.

| Method | Path | Description |
|---|---|---|
| GET | `/segments?status=&limit=&cursor=` | List with `memberCount`, `lastRunAt`, `isRealtimeEligible`, `status` |
| POST | `/segments` | Create; body `{ name, description?, definition, status? }`; `409` on duplicate name |
| GET | `/segments/{id}` | Full segment including `definition`, `memberCount`, and `realtimeMemberCount` |
| PUT | `/segments/{id}` | Replace name, description, definition, status |
| DELETE | `/segments/{id}` | Archive (soft delete) |
| POST | `/segments/preview` | Body `{ definition }`; returns `{ "count": 1234, "timedOut": false, "cachedAt": "..." }` or `{ "count": null, "timedOut": true }` after 5 s |
| POST | `/segments/{id}/run` | Queue a batch run; `202 { "runId": "..." }` |
| GET | `/segments/{id}/runs?limit=&cursor=` | Runs newest first: `runId`, `status`, `startedAt`, `finishedAt`, `memberCount`, `trigger`, `error` |
| GET | `/segments/{id}/members?limit=&cursor=` | Profiles in the current run (id, externalId, email, lastSeenAt) |
| GET | `/segments/{id}/changes?since=&limit=&cursor=` | Membership changes: `profileId`, `change`, `source`, `occurredAt` |

`memberCount` is from the current batch run; `realtimeMemberCount` is the live Redis set size for eligible segments and `null` otherwise.

Definition example (see spec §5.1 for the grammar):

```json
{ "op": "and", "children": [
  { "type": "attribute", "key": "plan", "cmp": "eq", "value": "pro" },
  { "type": "event", "name": "purchase", "cmp": "gte", "count": 3, "window": "30d" }
] }
```

Validation: depth ≤ 5, leaves ≤ 20, `key` matches `[A-Za-z0-9_]{1,64}`, `window` matches `^\d+(d|h)$` with at most 365 days; violations return `400` with field paths such as `definition.children[1].window`.

## Tenancy

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/auth/login` | none | Body `{ email, password }`; returns `{ accessToken, expiresIn }` and sets the `crm_refresh` cookie (httpOnly, Secure, `SameSite=Strict`, path `/v1/auth`) |
| POST | `/auth/refresh` | none | No body; reads the `crm_refresh` cookie, rotates it, and returns `{ accessToken, expiresIn }` |
| POST | `/auth/logout` | JWT | Revokes the caller's refresh tokens, clears the cookie, 204 |
| POST | `/tenants` | none, development only | Body `{ name, slug, adminEmail, adminPassword }`; bootstraps a tenant and its first Admin; optional `viewerEmail` + `viewerPassword` also create a Viewer |
| GET | `/api-keys` | JWT Admin | Returns every key of the tenant as `{ items, nextCursor }`; each item: `id`, `name`, `prefix`, `createdAt`, `lastUsedAt`, `revokedAt` |
| POST | `/api-keys` | JWT Admin | Body `{ name }`; returns the full key once: `{ id, name, prefix, key }` |
| DELETE | `/api-keys/{id}` | JWT Admin | Revoke; takes effect immediately (cache eviction) |

`GET /api-keys` does not accept `limit` or `cursor` (unlike the paginated lists above): it always returns the tenant's full key list, and `nextCursor` is always `null`.

`DELETE /api-keys/{id}` is idempotent: the key is revoked in SQL before the cache is evicted, so a `5xx` from a failed eviction still means the revocation is recorded; retrying the same request completes the cache eviction without side effects.

Protected operations document `bearer` security and their `401`/`403` problem responses in `openapi.json`. The `apiKey` security scheme is also declared in `openapi.json` for the ingest endpoints (`POST /track`, `/identify`, `/batch`) that arrive with the Ingestion plan; no currently-documented operation references it yet.

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@demo.local","password":"Demo-Passw0rd!"}' | jq -r .accessToken)
curl -s http://localhost:8080/v1/segments -H "Authorization: Bearer $TOKEN"
```

## Ops

| Method | Path | Description |
|---|---|---|
| GET | `/health/live` | Process is up |
| GET | `/health/ready` | SQL Server, Redis, and Kafka reachable |
| GET | `/metrics` | Prometheus exposition: `crm_ingest_events_total`, `crm_ingest_rejected_total`, `crm_consumer_lag`, `crm_dlq_total`, `crm_segment_run_seconds`, `crm_realtime_eval_seconds` |

Ops endpoints are served on the API host without authentication and are not under `/v1`.
