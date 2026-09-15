# Business Requirements Document — CRM Platform, Slice 1

- **Version:** 1.0
- **Date:** 2026-09-15
- **Status:** Approved
- **Design spec:** [2026-09-15-crm-slice1-design.md](../superpowers/specs/2026-09-15-crm-slice1-design.md)

## 1. Vision

Build a MoEngage-style customer engagement platform as a portfolio project that demonstrates production-grade architecture: high-throughput event ingestion, identity resolution into unified customer profiles, and a segmentation engine that works both in batch and in real time. Slice 1 delivers the data foundation that every later capability (campaigns, journeys, analytics, channels, AI) builds on.

Project purpose is portfolio and learning. Documentation quality, testability, and clear architectural boundaries weigh as much as feature count. The system must run end-to-end on one developer machine with `docker compose`.

### Success metrics

| Metric | Target |
|---|---|
| Local startup | `docker compose up` brings up every service; README quickstart is three commands |
| Ingest-to-timeline latency | An event posted to `/v1/track` appears in the profile timeline within 2 s (p95, local) |
| Real-time segment latency | Eligible segment membership updates within 2 s of the triggering event |
| Tenant isolation | Integration test proves tenant A cannot read tenant B data |
| Throughput | 1,000 events/s sustained for 60 s locally; consumer lag returns to zero within 30 s |
| Documentation | BRD, ADRs, architecture, data dictionary, API reference exist and are linked from the README |

## 2. Personas

### Marketer (primary dashboard user)

Builds audiences without writing SQL. Needs a visual rule builder with a live count, a list of segments with member counts, and the ability to look up any customer and see their attributes and event history.

### Developer (integrator)

Sends events from an app or backend. Needs a small, well-documented REST API, an API key per environment, clear validation errors, and confidence that duplicates and retries are safe.

### Tenant admin

Owns one workspace. Creates dashboard users, issues and revokes API keys, and expects that no other tenant can see their data.

## 3. Scope

Slice 1 delivers three capabilities end-to-end:

1. **Ingestion** — `track`, `identify`, and `batch` endpoints authenticated by API key, published to Kafka, processed asynchronously.
2. **Profiles** — identity resolution across external id, anonymous id, email, and phone; merge of anonymous into known profiles; attributes as JSON; per-profile event timeline.
3. **Segmentation** — rule tree of attribute and event conditions with AND/OR/NOT groups; compiled to parameterized SQL for batch materialization; real-time evaluation for eligible rules via Redis sets; live preview count.

Supporting scope: multi-tenancy, dashboard authentication, API key management, observability, docker-compose runtime, automated tests at unit, integration, architecture, contract, end-to-end, and load levels.

## 4. Functional Requirements

### Ingestion

| ID | Requirement |
|---|---|
| FR-01 | `POST /v1/track` accepts `{ userId?, anonymousId?, event, properties?, timestamp? }` and requires at least one of `userId` or `anonymousId`. |
| FR-02 | `POST /v1/identify` accepts `{ userId, anonymousId?, traits? }`, links `anonymousId` to `userId`, and merges `traits` into profile attributes. |
| FR-03 | `POST /v1/batch` accepts up to 500 items, validates each independently, and returns per-item accepted/rejected results. |
| FR-04 | Validation rejects event names longer than 64 characters or containing characters outside `[A-Za-z0-9_]`, properties larger than 32 KB, and timestamps more than 7 days from receipt. |
| FR-05 | Accepted events receive a UUIDv7 event id, are published to Kafka topic `events.raw` keyed by `{tenantId}:{userKey}`, and the API responds `202 { eventId }` without touching the database. |
| FR-06 | Duplicate event ids are ignored by the worker; re-sending the same event never creates a second timeline entry. |
| FR-07 | Messages that fail after three retries are moved to `events.dlq` with error headers; the worker continues with the next message. |

### Profiles

| ID | Requirement |
|---|---|
| FR-08 | Identities of kind `external_id`, `anonymous_id`, `email`, `phone` map to exactly one profile per tenant. |
| FR-09 | When `identify` links an anonymous profile to a known profile, the older profile wins, the loser's events and identities are re-pointed to the winner, the loser's attributes are merged (winner's values win on conflict), and the loser is marked merged and excluded from all reads. Merges hold row locks on both profiles; events arriving for a merged-away profile are re-routed to the winner. The winner's `EventCount` is recomputed. |
| FR-10 | Each event updates the profile's `LastSeenAt` and `EventCount`; each identify applies traits key-by-key with last-write-wins. identify calls do not create timeline entries or change `EventCount`. |
| FR-11 | `GET /v1/profiles?q=` searches by email, external id, or phone prefix (email and external id are matched by prefix; phone by prefix after E.164 normalization); `GET /v1/profiles/{id}` returns core fields and attributes; `GET /v1/profiles/{id}/events` returns the timeline with cursor pagination and optional name filter. |

### Segmentation

| ID | Requirement |
|---|---|
| FR-12 | A segment definition is a JSON rule tree with `group` (and/or/not), `attribute`, and `event` nodes, depth ≤ 5 and ≤ 20 leaves. |
| FR-13 | Attribute comparators: eq, neq, in, nin, gt, gte, lt, lte, contains, exists, not_exists. Event comparators: eq, gte, lte on a count within a window of at most 365 days. |
| FR-14 | The rule compiler produces parameterized T-SQL; no user value is ever concatenated into SQL. |
| FR-15 | `POST /v1/segments/{id}/run` (and a 6-hour schedule for Active segments) materializes membership into an immutable run, records entered/exited changes against the previous run, swaps the current run pointer, and keeps the last 3 runs. |
| FR-16 | Segments whose rules contain no `not` group and whose event leaves use `gte` with count ≥ 1 are real-time eligible; the worker updates their Redis membership sets on every relevant event and records membership changes. |
| FR-17 | `POST /v1/segments/preview` returns a member count within 5 s or reports a timeout; results are cached 60 s by definition hash. |
| FR-18 | Segment CRUD, runs list, members list, and membership changes are available over the API with cursor pagination. |

### Dashboard

| ID | Requirement |
|---|---|
| FR-19 | Users log in with email and password and receive a JWT scoped to one tenant with role Admin or Viewer. |
| FR-20 | The profile explorer provides search, a results table, and a detail drawer with attributes and an infinite-scrolling event timeline. |
| FR-21 | The segment builder supports nested groups, typed condition inputs, a debounced live count, save with field-level validation errors, run-now, and members and runs tabs. |
| FR-22 | Segment lists show member count, last run time, status, and a real-time or batch badge. |

### Tenancy and administration

| ID | Requirement |
|---|---|
| FR-23 | Admins create and revoke API keys; the full key is shown once, only its prefix afterwards. |
| FR-24 | Every read and write is scoped to the caller's tenant; a request without a resolvable tenant fails rather than returning unscoped data. |
| FR-25 | A development-only `POST /v1/tenants` bootstraps a tenant with its first Admin user. |

## 5. Non-Functional Requirements

| ID | Requirement |
|---|---|
| NFR-01 | **Throughput:** 1,000 events/s sustained for 60 s on a developer machine (8 GB RAM allocated to Docker). |
| NFR-02 | **Latency:** ingest p95 < 50 ms; ingest-to-timeline p95 < 2 s; real-time segment update p95 < 2 s. |
| NFR-03 | **Isolation:** tenant data separation enforced in the data access layer (global query filters) and covered by integration tests. |
| NFR-04 | **Durability:** events are acknowledged only after Kafka `acks=all`; the worker commits offsets only after the database transaction commits. |
| NFR-05 | **Idempotency:** at-least-once delivery combined with a unique event id yields effectively-once processing (enforced on the pair (Id, OccurredAt), which redelivery preserves). |
| NFR-06 | **Observability:** distributed traces across API, Kafka, and worker; structured logs with tenant and trace ids; Prometheus metrics for ingest rate, consumer lag, DLQ count, and segment run duration. |
| NFR-07 | **Security:** API keys stored as SHA-256 hashes; JWT access tokens expire in 15 minutes; rate limit 2,000 requests/min per API key. |
| NFR-08 | **Modularity:** module dependency rules are enforced by automated architecture tests; no module references another module's assemblies. |
| NFR-09 | **Contract stability:** OpenAPI document is generated from code, committed, and CI fails on drift. |
| NFR-10 | **Portability:** the whole system runs with `docker compose`; no cloud account required. |

## 6. Out of Scope

Campaign sending, journeys and flows, analytics dashboards, channel adapters (push, SMS, WhatsApp, email), mobile and web SDKs, AI features, behavioral sequence rules ("did A then B"), users belonging to multiple tenants, database-per-tenant isolation, cloud deployment, per-tenant attribute promotion UI.

## 7. Roadmap

| Order | Sub-project | Depends on |
|---|---|---|
| 1 | Slice 1: events, profiles, segments (this document) | — |
| 2 | Campaigns: email channel via MailHog, templates, send-to-segment, delivery events back through ingestion | 1 |
| 3 | Journeys / Flows: DAG builder, triggers on membership changes, waits, branches | 1, 2 |
| 4 | Analytics: funnels and retention over the events table, campaign reports | 1, 2 |
| 5 | Channels and SDKs: push, SMS, WhatsApp adapters, JavaScript web SDK | 2 |
| 6 | AI: send-time optimization, churn scoring | 2, 4 |

Each sub-project gets its own brainstorm, design spec, and implementation plan.

## 8. Glossary

| Term | Meaning |
|---|---|
| Tenant | One customer workspace; every row and Redis key belongs to exactly one tenant |
| Profile | Unified record of one end user of a tenant's product |
| Identity | A (kind, value) pair such as `email:a@b.com` that resolves to one profile |
| Merge | Combining two profiles when an identify call proves they are the same person |
| Event | A timestamped action with a name and JSON properties, attached to a profile |
| Attribute / trait | A key-value fact about a profile, stored in the profile's JSON attributes |
| Segment | A saved rule tree plus its materialized membership |
| Run | One immutable batch evaluation of a segment |
| Real-time eligible | A segment whose rules can be evaluated from a single event plus a profile snapshot |
| Membership change | An entered/exited record for a profile and segment, produced by batch or real-time evaluation |
| DLQ | Dead-letter queue topic for messages that failed processing |
| MADR | Markdown Architecture Decision Record format |
