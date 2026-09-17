# Integration Test Cases

Run against real SQL Server 2022, Kafka (KRaft), and Redis 7 started by Testcontainers; the API runs in-process via `WebApplicationFactory`, the Worker as a hosted service in the same test host. Each test class gets a fresh tenant created through the bootstrap endpoint (FR-25). Timing assertions use the local budgets from NFR-02 with a 3× CI multiplier. Seed dataset and rule fixtures: [fixtures.md](fixtures.md).

## Ingestion pipeline

### IT-001 — track round-trip appears in the timeline
- Traces: FR-01, FR-05, NFR-02
- Given a tenant with API key `K`
- When `POST /v1/track` `{userId: "u1", event: "purchase", properties: {amount: 49.9}}` with `X-Api-Key: K`
- Then the response is `202 {eventId}` within 50 ms (p95 over 20 calls), and `GET /v1/profiles?q=u1` then `/events` shows the event with the same id within 2 s
- Planned: tests/Crm.IntegrationTests/Ingestion/TrackRoundTripTests.cs

### IT-002 — identify creates a profile and applies traits
- Traces: FR-02, FR-10
- Given no profile for `u2`
- When `POST /v1/identify` `{userId: "u2", traits: {plan: "pro"}}` then `{userId: "u2", traits: {plan: "free", city: "SG"}}`
- Then one profile exists with attributes `{plan: "free", city: "SG"}`, `EventCount = 0`, no rows in `profiles.Events`, and `LastSeenAt` advanced
- Planned: tests/Crm.IntegrationTests/Ingestion/IdentifyTests.cs

### IT-003 — batch returns per-item results
- Traces: FR-03
- Given a batch of 3: valid track, track with `event: "bad name!"`, valid identify
- When `POST /v1/batch`
- Then `202 {accepted: 2, rejected: 1}` with `results[1].errors["events[1].event"]`, and exactly two messages land on `events.raw`
- Planned: tests/Crm.IntegrationTests/Ingestion/BatchTests.cs

### IT-004 — duplicate event id is ignored
- Traces: FR-06, NFR-05
- Given a processed event (Id = X, OccurredAt = T)
- When the identical `TrackEventRaw` message is produced again to `events.raw`
- Then after processing the Events row count and the profile's `EventCount` are unchanged, and the consumer offset advanced
- Planned: tests/Crm.IntegrationTests/Ingestion/IdempotencyTests.cs

### IT-005 — poison message goes to the DLQ after three retries
- Traces: FR-07
- Given a `TrackEventRaw` whose `properties` JSON is malformed bytes
- When it is produced to `events.raw` followed by a valid event
- Then the poison message appears on `events.dlq` with headers `error`, `stack`, `attempt = 3`, `source-topic`, `source-partition`, `source-offset`; `crm_dlq_total` increments by 1; the valid event is processed normally
- Planned: tests/Crm.IntegrationTests/Ingestion/DeadLetterTests.cs

### IT-006 — offset is committed only after the SQL transaction
- Traces: NFR-04
- Given a fault injector that throws after the SQL commit but before the offset commit
- When one event is processed and the worker restarts
- Then the event is redelivered, IT-004's idempotency skips it, and exactly one Events row exists
- Planned: tests/Crm.IntegrationTests/Ingestion/CommitOrderingTests.cs

### IT-039 — Kafka unavailable yields 503
- Traces: FR-05, NFR-04
- Given the Kafka container paused
- When `POST /v1/track`
- Then the response is `503 application/problem+json` with `Retry-After`, no event is buffered, and after unpausing a new request returns 202
- Planned: tests/Crm.IntegrationTests/Ingestion/KafkaUnavailableTests.cs

## Identity and merge

### IT-007 — identify merges anonymous into known profile
- Traces: FR-08, FR-09
- Given two tracks for `anonymousId: a1` (profile P1, older) and one track for `userId: u1` (profile P2)
- When `POST /v1/identify {userId: "u1", anonymousId: "a1", traits: {plan: "pro"}}`
- Then P1 is the winner (older `FirstSeenAt`), all three events have `ProfileId = P1`, identities `anonymous_id:a1` and `external_id:u1` point to P1, P2 has `MergedIntoId = P1`, `GET /v1/profiles/{P2}` returns 404, and a `ProfileMerged` message is on `events.processed`
- Planned: tests/Crm.IntegrationTests/Profiles/MergeTests.cs

### IT-008 — events arriving during a merge are not lost
- Traces: FR-09
- Given profiles P1 (`a1`) and P2 (`u1`) and a merge in flight (transaction held open by a test hook after taking both row locks)
- When 50 tracks for `u1` are produced concurrently on a different partition
- Then after the merge commits all 50 events belong to the winner, none reference the loser, and the applier log shows `MergedIntoId` redirects
- Planned: tests/Crm.IntegrationTests/Profiles/MergeConcurrencyTests.cs

### IT-009 — identity cache is invalidated on merge
- Traces: FR-09
- Given `t:{T}:id:anonymous_id:a1` cached to P2
- When the merge P2 → P1 commits
- Then the key is absent immediately after commit and the next lookup caches P1
- Planned: tests/Crm.IntegrationTests/Profiles/MergeTests.cs

### IT-010 — EventCount is recomputed after merge
- Traces: FR-09
- Given P1 with 2 events and P2 with 3 events
- When they merge
- Then the winner's `EventCount` is 5
- Planned: tests/Crm.IntegrationTests/Profiles/MergeTests.cs

## Profile queries

### IT-011 — profile search matches by prefix
- Traces: FR-11
- Given profiles with email `alice@x.io`, external id `ALI-9`, phone `+628123456789`
- When `GET /v1/profiles?q=ali`, `?q=ALI`, `?q=%2B62812`
- Then each returns the expected profile, results are ordered by `lastSeenAt` desc, and `?q=zzz` returns an empty `items`
- Planned: tests/Crm.IntegrationTests/Profiles/SearchTests.cs

### IT-012 — timeline pagination and name filter
- Traces: FR-11
- Given a profile with 120 events, 40 named `purchase`
- When `GET /v1/profiles/{id}/events?limit=50` is followed with `cursor` twice, and `?name=purchase`
- Then pages have 50, 50, 20 items newest-first with no overlap, `nextCursor` is null on the last page, and the filtered query returns 40
- Planned: tests/Crm.IntegrationTests/Profiles/TimelineTests.cs

## Segments: CRUD, preview, batch runs

### IT-013 — segment CRUD and duplicate name
- Traces: FR-18
- Given an Admin JWT
- When a segment is created, fetched, updated, archived, and a second segment with the same name is created
- Then responses are 201, 200, 200, 204 (status Archived), and 409 with `type` ending `/problems/conflict`
- Planned: tests/Crm.IntegrationTests/Segments/SegmentCrudTests.cs

### IT-014 — preview count is correct and cached
- Traces: FR-17
- Given the seed dataset
- When `POST /v1/segments/preview` with fixture `seed-pro-id.json` twice within 60 s
- Then both return `count = 412` (fixture-defined), the second response has the same `cachedAt`, and only one `SELECT COUNT(*)` appears in the SQL trace
- Planned: tests/Crm.IntegrationTests/Segments/PreviewTests.cs

### IT-015 — preview timeout reports timedOut
- Traces: FR-17
- Given a `WAITFOR DELAY '00:00:06'` injected via the test compiler hook
- When `POST /v1/segments/preview`
- Then the response is 200 `{count: null, timedOut: true}` within 5.5 s
- Planned: tests/Crm.IntegrationTests/Segments/PreviewTests.cs

### IT-016 — queued run is picked up and materialized
- Traces: FR-15
- Given segment `seed-pro-id` saved
- When `POST /v1/segments/{id}/run`
- Then the response is `202 {runId}`, the run reaches `Running` within 5 s and `Finished` with `MemberCount = 412`, `SegmentRunMembers` has 412 rows for that run, `Segments.CurrentRunId` equals the run id, and `Trigger = Manual`
- Planned: tests/Crm.IntegrationTests/Segments/BatchRunTests.cs

### IT-017 — run diff records entered and exited
- Traces: FR-15
- Given a finished run, then 5 profiles gain `plan = pro` and 3 lose it via identify
- When a second run finishes
- Then `MembershipChanges` has 5 `Entered` and 3 `Exited` rows with `Source = Batch` and the new run id's timestamp
- Planned: tests/Crm.IntegrationTests/Segments/BatchRunTests.cs

### IT-018 — readers never see a partial run
- Traces: FR-15
- Given a run in progress (test hook pauses after inserting half the members)
- When `GET /v1/segments/{id}/members` and `GET /v1/segments/{id}` are called
- Then they return the previous run's 412 members and `memberCount = 412`, not a partial count
- Planned: tests/Crm.IntegrationTests/Segments/BatchRunTests.cs

### IT-019 — only the last three runs are kept
- Traces: FR-15
- Given five finished runs
- When the fifth completes
- Then `SegmentRuns` holds the newest three, their `SegmentRunMembers` rows exist, and the two oldest runs and their members are gone
- Planned: tests/Crm.IntegrationTests/Segments/BatchRunTests.cs

### IT-020 — failed run leaves the previous run current
- Traces: FR-15
- Given a finished run R1 and a compiler hook that makes the next run's SQL raise an error
- When a second run executes
- Then R2 has `Status = Failed` with `Error` text, `CurrentRunId` is still R1, and `memberCount` is unchanged
- Planned: tests/Crm.IntegrationTests/Segments/BatchRunTests.cs

### IT-021 — schedule runs Active segments only
- Traces: FR-15
- Given segments in Draft, Active, Archived and the Quartz clock advanced 6 h
- When the scheduled job fires
- Then exactly one run with `Trigger = Schedule` exists, for the Active segment
- Planned: tests/Crm.IntegrationTests/Segments/ScheduleTests.cs

## Segments: real-time

### IT-022 — qualifying event adds the profile to the set
- Traces: FR-16, NFR-02
- Given eligible segment `purchase gte 2 in 30d` saved and Active, profile P with one purchase
- When a second `purchase` for P is tracked
- Then `SISMEMBER t:{T}:seg:{S}:members P` is 1 within 2 s and `MembershipChanges` has one `Entered` row with `Source = Realtime`
- Planned: tests/Crm.IntegrationTests/Segments/RealtimeTests.cs

### IT-023 — attribute change removes the profile from the set
- Traces: FR-16
- Given eligible segment `plan eq "pro"` and member P
- When `identify` sets `plan = free` for P
- Then P is removed from the set and an `Exited` change with `Source = Realtime` is recorded
- Planned: tests/Crm.IntegrationTests/Segments/RealtimeTests.cs

### IT-024 — counter TTL is set once, not re-armed
- Traces: FR-16
- Given segment `purchase gte 3 in 7d` and Redis time controllable via `DEBUG` or a fake clock wrapper
- When P tracks purchases at t = 0 and t = 6 d 23 h
- Then `TTL` of the counter after the second event is about 1 h, not 7 d, and the counter value is 2
- Planned: tests/Crm.IntegrationTests/Segments/RealtimeTests.cs

### IT-025 — multi-event segments keep separate counters
- Traces: FR-16
- Given eligible segment `purchase gte 2 in 30d AND app_open gte 1 in 7d`
- When P tracks two `purchase` and zero `app_open`
- Then P is not a member; after one `app_open` P becomes a member; keys `…:ev:purchase:30d:P = 2` and `…:ev:app_open:7d:P = 1` exist separately
- Planned: tests/Crm.IntegrationTests/Segments/RealtimeTests.cs

### IT-026 — batch run rebuilds the real-time set
- Traces: FR-16
- Given the members set manually polluted with a non-matching profile id
- When a batch run for the eligible segment finishes
- Then the set equals the run's members exactly (`SCARD` = `MemberCount`, polluted id absent)
- Planned: tests/Crm.IntegrationTests/Segments/RealtimeTests.cs

### IT-027 — saving a segment invalidates the worker cache
- Traces: FR-16
- Given the worker has cached the tenant's eligible segments
- When `PUT /v1/segments/{id}` changes the rule to `plan eq "gold"` and an event for a `gold` profile arrives
- Then a `segments:changed` message with the tenant id was published and the profile is evaluated against the new rule within 2 s
- Planned: tests/Crm.IntegrationTests/Segments/RealtimeTests.cs

### IT-028 — segment detail exposes both member counts
- Traces: FR-18, FR-22
- Given an eligible segment with a finished run and one extra real-time member since
- When `GET /v1/segments/{id}`
- Then `memberCount` equals the run count, `realtimeMemberCount` equals `SCARD` (run count + 1), and for an ineligible segment `realtimeMemberCount` is `null`
- Planned: tests/Crm.IntegrationTests/Segments/SegmentReadTests.cs

### IT-029 — membership changes are queryable since a timestamp
- Traces: FR-18
- Given 10 batch changes at T1 and 3 real-time changes at T2 > T1
- When `GET /v1/segments/{id}/changes?since=T2-1s&limit=2` and then the cursor
- Then the pages contain the 3 T2 changes with `source = Realtime`, newest first, and nothing from T1
- Planned: tests/Crm.IntegrationTests/Segments/SegmentReadTests.cs

### IT-042 — golden rules execute against the real schema
- Traces: FR-14
- Given every `tests/fixtures/rules/valid/*.json`
- When each is compiled and executed against the seed dataset
- Then every statement executes without error and returns the count listed in [fixtures.md](fixtures.md)
- Planned: tests/Crm.IntegrationTests/Segments/GoldenExecutionTests.cs

## Tenancy, auth, isolation

### IT-030 — tenant isolation through the API
- Traces: FR-24, NFR-03
- Given tenants A and B each with a profile and a segment
- When A's JWT requests B's `GET /v1/profiles/{id}`, `/segments/{id}`, `/segments/{id}/members`, and A's API key tracks an event for B's user id
- Then the reads return 404, the event lands in A's tenant only, and B's data is unchanged
- Planned: tests/Crm.IntegrationTests/Tenancy/IsolationTests.cs

### IT-031 — tenant isolation through the query filter
- Traces: FR-24, NFR-03
- Given tenant A in `ITenantContext` and rows for A and B in every module table
- When each repository's list query runs
- Then only A's rows are returned for Profiles, Events, UserIdentities, Segments, SegmentRuns, MembershipChanges, ApiKeys
- Planned: tests/Crm.IntegrationTests/Tenancy/QueryFilterTests.cs

### IT-032 — request without a resolvable tenant fails closed
- Traces: FR-24
- Given a request with no JWT and no API key to `GET /v1/segments`
- When executed
- Then the response is 401; a request with a JWT lacking `tenant_id` returns 500 with a `traceId` and a logged `TenantContextMissingException`, and no rows are returned
- Planned: tests/Crm.IntegrationTests/Tenancy/IsolationTests.cs

### IT-033 — API key authentication
- Traces: FR-23, NFR-07
- Given a valid key, a key with one character changed, a revoked key
- When each calls `POST /v1/track` and the valid key calls `GET /v1/segments`
- Then 202, 401, 401, and 403 respectively; `LastUsedAt` on the valid key is set once even after 10 calls within a minute
- Planned: tests/Crm.IntegrationTests/Tenancy/ApiKeyTests.cs

### IT-034 — full key is shown once
- Traces: FR-23
- Given an Admin JWT
- When `POST /v1/api-keys {name: "sdk"}` then `GET /v1/api-keys`
- Then the create response contains `key` matching `^crm_live_`, the list response has `prefix` (8 chars) and no `key` field, and the database row has `KeyHash` only
- Planned: tests/Crm.IntegrationTests/Tenancy/ApiKeyTests.cs

### IT-035 — ingest rate limit
- Traces: NFR-07
- Given one API key and a token bucket of 2,000 per minute
- When 2,500 requests are sent within 10 s
- Then at least 100 responses are `429` with `Retry-After`, accepted requests never exceed the bucket plus the tokens refilled during the burst (continuous refill at 2,000 per minute), no 5xx occurs, and a second key is unaffected
- Planned: tests/Crm.IntegrationTests/Tenancy/RateLimitTests.cs

### IT-036 — login issues a JWT and a refresh cookie that rotates
- Traces: FR-19, NFR-07
- Given a seeded Admin user
- When `POST /v1/auth/login`, then `POST /v1/auth/refresh` twice reusing the first cookie the second time
- Then login returns `{accessToken, expiresIn: 900}` and a `Set-Cookie: crm_refresh=…; HttpOnly; Secure; SameSite=Strict; Path=/v1/auth`; the first refresh returns a new pair and revokes the old token (`RevokedAt`, `ReplacedByTokenId` set); the reuse attempt returns 401
- Planned: tests/Crm.IntegrationTests/Tenancy/AuthTests.cs

### IT-037 — Viewer role is read-only
- Traces: FR-19
- Given a Viewer JWT
- When it calls `GET /v1/segments`, `POST /v1/segments`, `POST /v1/segments/{id}/run`, `POST /v1/api-keys`
- Then 200, 403, 403, 403
- Planned: tests/Crm.IntegrationTests/Tenancy/AuthTests.cs

### IT-038 — tenant bootstrap is development-only
- Traces: FR-25
- Given the API in `Development` and in `Production` environment
- When `POST /v1/tenants {name, slug, adminEmail, adminPassword}`
- Then Development returns 201 with a tenant, an Admin user, and login succeeds; Production returns 404 and no rows are written
- Planned: tests/Crm.IntegrationTests/Tenancy/BootstrapTests.cs

## Observability and operations

### IT-040 — traces and metrics span the pipeline
- Traces: NFR-06
- Given an in-memory OTLP exporter and the Prometheus endpoint
- When one event is tracked and processed
- Then spans from `Crm.Api` (HTTP), the Kafka produce, the Worker consume, and the SQL insert share one `traceId` carried in the Kafka `traceparent` header; logs for the request carry `TenantId` and `TraceId`; `/metrics` exposes `crm_ingest_events_total ≥ 1` and `crm_consumer_lag`
- Planned: tests/Crm.IntegrationTests/Ops/ObservabilityTests.cs

### IT-041 — readiness reflects dependencies
- Traces: NFR-10
- Given all containers running, then Redis paused
- When `GET /health/live` and `GET /health/ready`
- Then live is 200 both times; ready is 200 then 503 with `redis: Unhealthy` in the body
- Planned: tests/Crm.IntegrationTests/Ops/HealthTests.cs
