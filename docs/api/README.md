# API Reference

Base URL locally: `http://localhost:8080/v1`. All request and response bodies are JSON. The machine-readable contract `openapi.json` in this directory is generated from the API project at build time and committed; CI fails when the generated file differs from the committed one. Interactive docs are served at `/swagger` in development.

## Authentication

| Caller | Mechanism | Header |
|---|---|---|
| Ingest clients | Tenant API key, format `crm_live_<32 base62>` | `X-Api-Key: crm_live_...` |
| Dashboard users | JWT bearer (15 min access, 7 d refresh) with claims `sub`, `tenant_id`, `role` | `Authorization: Bearer <token>` |

Roles: `Admin` may do everything; `Viewer` may only read profiles and segments. API keys can only call Ingest endpoints. Keys are rate limited with a token bucket of 2,000 requests per minute; exceeding it returns `429` with `Retry-After`. The budget is per request, not per event: a `/batch` call of up to 500 events still costs one token, so the effective ceiling is 1,000,000 events per minute per key (a known limitation; per-event quotas are future work).

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
| 413 | Request body too large |
| 429 | Rate limit exceeded |
| 503 | Kafka unavailable at ingest; retry after `Retry-After` seconds |
| 500 | Unexpected error; `traceId` identifies the trace |

Every problem body also carries `code`, a stable machine-readable error code, safe to branch on in client code. A specific failure (for example `auth.invalid_credentials`, `tenant.slug_taken`, `user.email_taken`, `pagination.invalid_limit`, `pagination.invalid_cursor`, `profiles.invalid_query`, `profiles.invalid_filter`, `profile.not_found`, `segment.not_found`, `segment.name_taken`, `segment.archived`, `segment.run_enqueue_conflict`, `segments.invalid_definition`, `segments.invalid_request`, `segments.invalid_filter`) gets its own code; anything else falls back to one derived from the status: `request.invalid` (400), `auth.unauthorized` (401), `auth.forbidden` (403), `resource.not_found` (404), `request.conflict` (409), `rate_limit.exceeded` (429), `service.unavailable` (503), `server.unexpected` (otherwise).

## Pagination

List endpoints accept `limit` (default 50, max 200) and `cursor` (opaque string from the previous response). Responses have the shape `{ "items": [...], "nextCursor": "..." | null }`. Offsets are not supported.

`limit` outside 1…200 is `400 pagination.invalid_limit`; a `cursor` that this endpoint did not issue (malformed, or issued by another endpoint) is `400 pagination.invalid_cursor`. A cursor is a position in the sort order, not a snapshot: rows that change their sort key between two pages can appear twice or not at all.

## Ingest

Auth: `X-Api-Key`. All ingest endpoints return `202 Accepted` after the event is durably written to Kafka; nothing touches the database on this path. The event id in every response is a UUIDv7 whose time component is the receive time, so ids sort in receive order.

Limits, beyond what each endpoint states below: `userId` ≤ 128 characters, `anonymousId` ≤ 256 characters, `properties`/`traits` are JSON objects when present and serialize to ≤ 32 KB. A blank (empty or whitespace-only) `userId` or `anonymousId` counts as absent. Request bodies are capped by Kestrel: `/track` and `/identify` at 256 KB, `/batch` at 20 MB (a maximal valid batch of 500 items is ≈ 16.5 MB); exceeding the cap returns `413` before the body is buffered.

Error codes: `ingest.invalid` (`400`, field errors in `errors`), `ingest.unavailable` (`503`, `Retry-After: 5`). A `503` means the event is *unconfirmed, not absent*: a request the broker received just before it failed to acknowledge may still be appended. Retry, but only when the business event warrants a duplicate — the pipeline tolerates a retried request as a separate event rather than deduplicating client retries (redelivery of the same message is deduplicated downstream; a second client retry is not).

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

Up to 500 items, each validated independently. Each item carries a `type` discriminator (`track` or `identify`) selecting which shape and validation rules apply. Envelope errors — `events` missing, empty, or more than 500 items — are `400`; item errors never fail the request, so the response is `202` even when every item is rejected:

```json
{ "accepted": 1, "rejected": 1, "results": [ { "index": 0, "eventId": "..." }, { "index": 1, "errors": { "events[1].event": ["must match [A-Za-z0-9_]{1,64}"] } } ] }
```

If Kafka does not acknowledge any accepted item, the whole request is `503` (see the ingest error codes above) rather than a partial `202`.

## Profiles

Auth: JWT (Admin or Viewer).

| Method | Path | Description |
|---|---|---|
| GET | `/profiles?q=&limit=&cursor=` | Search by email, external id, or phone prefix; sorted by `lastSeenAt` desc |
| GET | `/profiles/{id}` | Core fields plus `attributes` object |
| GET | `/profiles/{id}/events?from=&to=&name=&limit=&cursor=` | Timeline, newest first |

`q` matches a case-insensitive **prefix** of `externalId`, `email`, or — when `q` minus separators (spaces, `-`, `.`, `(`, `)`) is an optional `+` followed by 1–15 digits (E.164) — `phone`; `q` absent lists every live profile of the tenant. `q` is at most 256 characters (`400 profiles.invalid_query`). LIKE metacharacters (`\ % _ [`) in `q` are escaped and match literally. Anonymous ids are not searchable. Search items are thin: `{ "id", "externalId", "email", "phone", "lastSeenAt", "eventCount" }` — no `attributes`; only the detail endpoint returns those.

Profile shape (detail):

```json
{ "id": "...", "externalId": "u-123", "email": "a@b.com", "phone": "+628123", "attributes": { "plan": "pro" }, "firstSeenAt": "...", "lastSeenAt": "...", "eventCount": 42 }
```

The timeline is ordered by `occurredAt` (then `id` as a tiebreak) descending, so an event sent with an old `timestamp` appears at that position rather than at the top. `name` must match `[A-Za-z0-9_]{1,64}`; `from` is inclusive and `to` is exclusive, both ISO 8601 with an offset (`400 profiles.invalid_filter` for a bad name, or `from >= to`).

Event shape: `{ "id": "...", "name": "purchase", "properties": { }, "occurredAt": "...", "receivedAt": "..." }`.

`404 profile.not_found` on both `/{id}` and `/{id}/events` for an unknown id, another tenant's id, and a profile that was merged into another one — the three are indistinguishable, so nothing about a profile's existence leaks. No rate limit applies to these endpoints. All timestamps in every response are UTC (`Z` suffix).

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
| POST | `/segments/{id}/run` | Queue a batch run; `202 { "runId": "..." }`. An existing Queued/Running run for the segment is returned as-is, same status and shape (H5/H25) — not a new run. Archived is `409 segment.archived`; a Draft segment is runnable. `409 segment.run_enqueue_conflict` means two enqueues raced and the winner already finished before the loser could re-read it — retry the request. A segment archived after its run was already queued does not materialise: the run fails with `error: "segment archived"` once the worker picks it up (H30). The definition is compiled when the worker claims the run, not at enqueue time — a `PUT` that lands while a run is already in flight has no effect on it; the run's `CompiledSql` always matches the definition it started with. |
| GET | `/segments/{id}/runs?limit=&cursor=` | Runs newest first: `runId`, `status`, `trigger`, `startedAt`, `finishedAt`, `memberCount`, `error`, `createdAt` |
| GET | `/segments/{id}/members?limit=&cursor=` | Profiles in the current run (id, externalId, email, lastSeenAt) |
| GET | `/segments/{id}/changes?since=&limit=&cursor=` | Membership changes, newest first: `id`, `profileId`, `change`, `source`, `occurredAt` |

`memberCount` is from the current batch run; `realtimeMemberCount` is the live Redis set size for eligible segments and `null` otherwise. As of this plan's slice, `realtimeMemberCount` is always `null` — the real-time evaluator arrives in a later plan.

`status` defaults to `Draft` on create when omitted; `PUT` requires it explicitly (a full replace of name, description, definition and status). `DELETE` archives (soft delete): `204`, idempotent — archiving an already-archived segment still returns `204`, and an unknown id is `404 segment.not_found` (the same code for a missing id and another tenant's id — nothing about a segment's existence leaks). `name` is trimmed and must be 1–128 characters, unique per tenant **including archived segments** — a duplicate is `409 segment.name_taken`. `description` is optional, at most 1024 characters. Field errors (`name`, `description`, `status`) are checked before the rule definition is parsed: a request invalid in both ways reports `400 segments.invalid_request`, never `segments.invalid_definition`. A missing or `null` `definition` is `400 segments.invalid_definition` with a `definition` field error, not a framework binding error and not a `500`.

`GET /segments` without `status` returns `Draft` and `Active` segments only (`Archived` is hidden); `status=Draft|Active|Archived` (case-insensitive) filters to exactly that one status; any other value is `400 segments.invalid_filter`.

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

Protected operations document `bearer` security and their `401`/`403` problem responses in `openapi.json`. The `apiKey` security scheme is also declared in `openapi.json`, referenced by the ingest endpoints (`POST /track`, `/identify`, `/batch`).

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
| GET | `/metrics` | Prometheus exposition: `crm_ingest_events_total`, `crm_ingest_rejected_total`, `crm_consumer_lag`, `crm_dlq_total`, `crm_publish_retries_total`, `crm_segment_run_seconds`, `crm_realtime_eval_seconds` |

Ops endpoints are served on the API host without authentication and are not under `/v1`.
