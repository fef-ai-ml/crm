# Segmentation — real-time evaluation (plan I) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An Active, real-time-eligible segment keeps a Redis membership set current within 2 s of the event or identify that changes it, records every real-time transition in `segments.MembershipChanges` (`Source = Realtime`), exposes `realtimeMemberCount` on `GET /v1/segments/{id}`, and is corrected by every batch run.

**Architecture:** `Segmentation.Domain` gains `Transition`/`Transitions`. `Segmentation.Application` gains the pure `RealtimeEvaluator` (rule tree + attribute snapshot + counter lookup → bool; beside `RuleCompiler`, because it needs `PromotedAttributeColumns`) and `RealtimeCounterKey` (key + TTL per event leaf). `Segmentation.Infrastructure` gains the `events.processed` consumer (group `segmentation`), the per-tenant `RealtimeSegmentCache` invalidated over the `segments:changed` channel, the Redis membership store, the batch runner's set rebuild, and `realtimeMemberCount` in the detail read. Plan H's tables are reused unchanged — no migration.

**Tech Stack:** .NET 10, Confluent.Kafka, StackExchange.Redis, EF Core 10 (SQL Server 2022), xUnit + FluentAssertions 7, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-15-crm-slice1-design.md` §5.3 step 5, §5.4, §3.4, §3.5; ADR-0005, ADR-0006. Test specification: UT-046…049, IT-022…028, LT-003. Plan H's rulings (H1–H30) still hold.

## Global Constraints

- Work on the session's feature branch. `git add` explicit paths only; never add `mise.toml`, `.superpowers/`, `.env.local`, or a locally edited `global.json`.
- Warnings are errors (CS1573, CA1873, CA2100, CA2000 bite). No `.ConfigureAwait(false)`. Test files need `using Xunit;`. FluentAssertions stays on 7.x.
- Module boundary (AT-001/002/004): `Segmentation.*` references only `Crm.Kernel`, `Crm.Contracts` and itself. The consumer decodes `Crm.Contracts` messages; it never references `Profiles.*` (so it owns its own small retry loop rather than borrowing `MessageRetryPolicy`).
- Redis is rebuildable state (ADR-0006): no Redis failure ever fails an API request, a batch run, or blocks the consumer forever.
- `docs/api/openapi.json` is regenerated with `make openapi` if the HTTP surface changes (it should not: `realtimeMemberCount` has been in the contract since H2). `python3 tools/docs_check.py` stays at 0 errors.

## Rulings (I1–I14)

| # | Ruling | Why | Cost if wrong |
|---|---|---|---|
| I1 | The evaluator (in `Segmentation.Application`, the compiler's twin) mirrors the compiler's SQL semantics, not a C#-native reading of the rule: an attribute's value is what `JSON_VALUE` would return (a JSON string → its text, a number → its raw JSON text, `true`/`false` → the strings `true`/`false`, `null`/object/array/missing → absent); promoted keys read the same JSON property (the computed column is `CAST(JSON_VALUE(...) AS nvarchar(256))`, so values over 256 chars are truncated there and compare as their first 256 chars here too); text comparisons are ordinal case-insensitive (the database's default `_CI_` collation); numeric comparisons convert the text with invariant-culture `double.TryParse` (`TRY_CONVERT(float, …)`), an unconvertible value compares UNKNOWN; missing-key semantics follow `architecture.md` §6 exactly (`neq`/`nin`/`not_exists` include missing, everything else excludes it). | ADR-0005: "the compiler and the evaluator share the same node types" — they must also agree on the answer, or a batch run flips members back and forth. An integration test (IT-043) executes every eligible attribute-only fixture, plus extra comparator rules, through SQL Server and through the evaluator over a tenant of crafted edge-case profiles and requires identical member sets. | Trailing-space and accent rules of the SQL collation are not reproduced (`'pro ' = 'pro'` is true in SQL, false here); documented, batch corrects. |
| I2 | Event leaves in real time are `gte n` with `n ≥ 1` only (the classifier guarantees it). The evaluator reads a counter per leaf and compares `counter >= n`; a missing counter is 0. | §5.4. | — |
| I3 | Counter keys use the existing `RedisKeys.EventCounter(tenant, segment, eventName, window.Text, profile)`; the TTL is the leaf's window. On `EventTracked`, every leaf of every cached segment whose `Name` equals the event name gets `INCR` then `EXPIRE key ttl NX` — unless the event's `OccurredAt` is already older than `now − window` (a late event from a client clock never enters a window it is outside of). Two leaves with the same name and window in one segment share one key and are incremented once. | §3.4; IT-024/025. | — |
| I4 | No per-event dedupe. `events.processed` is at-least-once (E7), so a redelivered `EventTracked` increments its counters again; a per-`eventId` "seen" key would cost one Redis key per event for the retention period (hundreds of millions at the load-test rate). The over-count is bounded to the rare redelivery and corrected for membership by the next batch run. | ADR-0005 accepts approximate counters. | A burst of redeliveries can admit a profile early; the next batch run removes it. |
| I5 | The set mutation is the transition detector: evaluation true → `SADD`, false → `SREM`; a return value of 1 is the transition (`Entered` / `Exited`), 0 is none. `Transition.From(previous, current)` (UT-048) is the pure statement of the same rule and is what the store's result is mapped through. The `MembershipChanges` rows for one message are written in one `SaveChanges` after all Redis mutations; if that save fails after its retries, the Redis mutations that produced those rows are reverted (`SREM`/`SADD` back) so the next message for that profile re-detects them (the record itself is skipped, I9) — a failed revert is logged and left for the batch run. | Atomic per key without a read-then-write race, idempotent on redelivery (a second `SADD` returns 0, so no duplicate `Entered`). | A failed revert loses one change row; logged at Error. |
| I6 | Only **Active** and **eligible** segments are evaluated, from `RealtimeSegmentCache`: per tenant, the parsed trees of those segments, loaded on first use with one query. An entry is dropped on a `segments:changed` message carrying that tenant id (hex `N`, the data dictionary's GUID encoding) and also expires after `Segmentation:Realtime:CacheTtl` (default 60 s), because Redis pub/sub is fire-and-forget: a message sent while the Worker is reconnecting is lost. A segment whose stored definition no longer parses is skipped and logged, never fails the message. | §5.4 step 1; IT-027. | A lost invalidation delays a rule change by at most one TTL. |
| I7 | `segments:changed` is published by the API (`SegmentChangeNotifier`, called by `EfSegmentService`) after a successful create, update or archive (and by the dev seed endpoint through the same service). A publish failure is logged at Warning; the save still succeeds (I6's TTL covers it). | ADR-0006. | — |
| I8 | The consumer: `ProcessedEventsConsumerService`, group `segmentation`, `Segmentation:Realtime:Instances` (default 4; the section also holds `Enabled`, `RetryDelaysMs`, `RestartDelayMs`, `CacheTtl`) consumers over the 12 partitions, per-message synchronous offset commit, subscribes only after `SegmentSchemaGate` (E11). Message types by the `message-type` header: `EventTracked` → counters + evaluate; `ProfileUpdated` → evaluate (no counter change); `ProfileMerged` → `SREM` the loser from every cached segment's set and record `Exited` for each removal (the winner is re-evaluated by the `ProfileUpdated`/`EventTracked` that follows; the loser's counters are not transferred — batch heals). Any other type is ignored. | §3.5, §5.4. | — |
| I9 | Failure handling. An undecodable message (bad JSON, missing tenant) is logged at Error, counted, and skipped — `events.processed` is produced by our own Worker, so this is a programmer error, not user input, and there is no DLQ for it (the data dictionary lists `events.dlq` as a raw-topic destination). A Redis failure skips the message (logged at Warning, counted): real-time state is approximate and rebuildable (ADR-0006), and blocking the partition on a Redis outage would build unbounded lag. A SQL failure writing change rows is retried twice (200 ms, 2 s), then I5's revert and a skip. Nothing here dead-letters; `crm_realtime_skipped_total{reason}` (`decode`/`redis`/`sql`) is the signal. | E12 precedent; no module boundary crossing to reuse Profiles' DLQ publisher. | A long Redis outage loses real-time transitions for its duration; the next batch run heals membership but not the change feed. |
| I10 | `crm_realtime_eval_seconds{type}` (histogram, seconds): one message's handling from decode to the SQL save, tag `type` = `event_tracked`/`profile_updated`/`profile_merged`. The Grafana panel already exists (plan C). No separate consumer-lag series for this group — `crm_consumer_lag` is owned by the Profiles meter and a second meter must not register the same instrument name; recorded as backlog. | §8.2. | Lag for group `segmentation` is visible only through Kafka tooling for now. |
| I11 | Batch rebuild (§5.3 step 5): after a run commits, if the segment is eligible **and Active**, the runner replaces the set with the run's members in one `MULTI` (`DEL` + `SADD` in chunks of 1,000), so no reader sees it half-built. A Redis failure is logged at Warning and never fails the (already committed) run. A real-time `SADD` landing between the run's snapshot and the rebuild is overwritten — the next event for that profile restores it. | IT-026; ADR-0005 "batch corrects drift". | One missed transition window per run; documented. |
| I12 | `realtimeMemberCount` = `SCARD` of the set when the segment is eligible and Active, else `null`; a Redis failure is also `null` (logged at Warning) — never a failed read. | H2's field; IT-028. | — |
| I13 | Saving a segment as not Active or not eligible, or archiving it, deletes its set (best-effort): nothing maintains it any more, so a stale count must not survive. Saving an Active eligible segment with a changed definition leaves the set alone — members that no longer qualify leave on their next event or the next batch run; deleting it instead would record a false `Entered` for every existing member on their next event. Counters are never deleted explicitly; they expire with their window. | Correct change feed beats instant count accuracy. | A rule edit shows a stale `realtimeMemberCount` until the next event/run. |
| I14 | Test seam: the consumer handler is `RealtimeMessageHandler.HandleAsync(ProcessedEnvelope, ct)`, a scoped service tests can resolve from the Worker's container to drive one message directly (IT-024 sets a counter's TTL by hand between two messages instead of controlling Redis time). | Deterministic TTL test without `DEBUG` or sleeping 7 days. | — |

## File Structure

```
src/Modules/Segmentation/Segmentation.Domain/Rules/
  Transition.cs               enum Transition { None, Entered, Exited } + Transitions.From(previous, current)
src/Modules/Segmentation/Segmentation.Application/Realtime/
  RealtimeEvaluator.cs        Evaluate(tree, attributes JsonElement, Func<EventNode,long> counter) → bool (I1, I2)
  RealtimeCounterKey.cs       (Key, Ttl) per (tenant, segment, EventNode, profile); distinct leaves (I3)
src/Modules/Segmentation/Segmentation.Infrastructure/Realtime/
  RealtimeMessageDecoder.cs               ProcessedEnvelope, RealtimeMessage, decode errors
  RealtimeSegmentCache.cs                 per-tenant cache with TTL (I6)
  RealtimeMembershipStore.cs              counters, SADD/SREM, SCARD, rebuild, delete (I3, I5, I11–I13)
  RealtimeMessageHandler.cs               one message: decode → counters → evaluate → mutate → save (I5, I8, I9)
  ProcessedEventsConsumerService.cs, RealtimeOptions.cs
  SegmentChangeNotifier.cs                API publish + set delete (I7, I13); Worker subscriber (I6)
  SegmentationMetrics.cs (+ crm_realtime_eval_seconds, crm_realtime_skipped_total)
tests/Segmentation.UnitTests/RealtimeEvaluatorTests.cs, RedisKeyBuilderTests.cs
tests/Crm.IntegrationTests/Segments/RealtimeTests.cs, RealtimeParityTests.cs
tests/Crm.IntegrationTests/Fixtures/{SegmentsDb,ProfilesDb,CrmWorkerFactory}.cs (helpers, short consumer delays)
tests/Crm.LoadTests/realtime-latency.js
```

---

### Task 1: Evaluator, transitions, counter keys (UT-046…049)

**Files:** create the three Domain files, `RealtimeCounterKey.cs`, tests `RealtimeEvaluatorTests.cs`, `RedisKeyBuilderTests.cs`.

- [ ] **Step 1:** Failing tests. UT-046 (snapshot `{plan:"pro", country:"ID"}`: `plan eq "pro"` true, `country nin ["ID"]` false, `tier not_exists` true, `ltv gt 10` false), plus I1's rows: case-insensitive `eq`, numeric `eq` on a numeric string, `neq`/`nin` include missing, `contains` with LIKE metacharacters taken literally, booleans compare as `true`/`false`, object/array values are absent, `in`/`nin` with numbers, and/or groups. UT-047 (`purchase gte 3 in 30d` with counter 2 → false, 3 → true; missing counter → 0). UT-048 (four combinations → None, Exited, Entered, None). UT-049 (`t:{T}:seg:{S}:ev:purchase:30d:{P}` and `…:7d:…`, TTLs 30 d and 7 d; duplicate leaves collapse to one key).
- [ ] **Step 2:** RED → implement → GREEN. Evaluator throws on a `not` group or a non-`gte` event leaf (callers only pass eligible trees; the classifier is the gate).
- [ ] **Step 3:** Unit suites green. Commit: `feat(segmentation): real-time evaluator mirrors the compiler's SQL semantics; transitions and counter keys`.

### Task 2: Consumer, cache, change notification (IT-022…025, IT-027, IT-043)

**Files:** the Infrastructure `Realtime/` files, `SegmentationWorkerRegistration.cs`, `SegmentationModule.cs` (store, notifier), `EfSegmentService.cs` (notify after save), `SegmentationMetrics.cs`, `Segmentation.Infrastructure.csproj` (+ `Confluent.Kafka`), tests `Segments/RealtimeTests.cs`, `Segments/RealtimeParityTests.cs`, `Fixtures/CrmWorkerFactory.cs` (short consumer delays).

- [ ] **Step 1:** Failing tests through the real pipeline (API `track`/`identify` → `events.raw` → profiles → `events.processed` → segmentation). IT-022 (second `purchase` → `SISMEMBER` = 1 within 2 s ×3 on a remote engine, one `Entered`/`Realtime` row). IT-023 (`identify plan=free` → removed, one `Exited`/`Realtime`). IT-024 (drive `RealtimeMessageHandler` directly: first event → TTL ≈ 7 d; set TTL to 3,600 s by hand; second event → TTL ≤ 3,600, value 2). IT-025 (two purchases → not a member, separate counters = 2 and absent; one `app_open` → member, counter = 1). IT-027 (subscribe to `segments:changed`, PUT the rule to `plan eq "gold"`, assert the tenant id arrives, then an identify for a `gold` profile makes it a member within 2 s). IT-043 (parity, I1): a tenant of crafted edge-case profiles; for every eligible attribute-only valid fixture and each extra comparator rule, the SQL member set equals the set of profiles the evaluator accepts from their stored `Attributes`; mutation-check it.
- [ ] **Step 2:** RED → implement → GREEN.
- [ ] **Step 3:** Full integration suite, unit suites, docs-check. Commit together with task 3 (the two share `RealtimeTests.cs` and `EfSegmentService.cs`): `feat(segmentation): real-time evaluation on events.processed; batch runs rebuild the set; realtimeMemberCount`.

### Task 3: Batch rebuild and `realtimeMemberCount` (IT-026, IT-028)

**Files:** `SegmentRunner.cs`, `EfSegmentQueries.cs`, `EfSegmentService.cs` (I13 delete), `SegmentContracts.cs` doc comment, tests in `RealtimeTests.cs`.

- [ ] **Step 1:** Failing tests. IT-026 (pollute the set with a random id, run, set = run members exactly, `SCARD` = `MemberCount`). IT-028 (eligible Active segment after a run plus one extra `SADD` → `realtimeMemberCount` = run count + 1, `memberCount` = run count; ineligible → `null`). I13 (archive → set deleted; PUT to Draft → set deleted). Redis paused → detail still 200 with `realtimeMemberCount: null`.
- [ ] **Step 2:** RED → implement → GREEN.
- [ ] **Step 3:** Full suite; committed with task 2.

### Task 4: Documentation and load-test script

**Files:** spec (new paragraph "**Rulings (plan I).**" after plan H's, summarising I1–I14; §5.4 amended where I5/I6/I9 refine it), `docs/data-dictionary/redis-and-kafka.md`, `docs/test/{unit,integration,e2e-load,traceability}.md` (Planned → Implemented, IT-043 added), `docs/design/architecture.md` if a sequence diagram names the consumer, README status, `tests/Crm.LoadTests/realtime-latency.js` (LT-003).

- [ ] **Step 1:** Write the docs; docs-check 0 errors. Commit: `docs(segmentation): real-time evaluation rulings I1–I14, test statuses, LT-003 script`.

### Task 5 (controller): live verification on home-lab

- [ ] `make down && make up && make seed`; `seed-purchasers` shows `realtimeMemberCount` = `memberCount` after its first run; a quickstart `purchase` for a new user increments it within 2 s; `crm_realtime_eval_seconds` appears in Prometheus and on the Grafana panel.
- [ ] LT-003 with `tests/Crm.LoadTests/realtime-latency.js` alongside LT-001; record p95 in the README.

## Self-review notes

- Spec coverage: §5.4 steps 1–4 (T2), §5.3 step 5 (T3), `realtimeMemberCount` (T3), `crm_realtime_eval_seconds` (T2), UT-046…049 (T1), IT-022…028 (T2/T3), LT-003 (T4 script, T5 measurement).
- Known interactions: T2's cache must see T3's I13 deletes as ordinary saves (both publish `segments:changed`); T3's rebuild reads run members after commit, outside the run transaction, so a Redis stall never lengthens the SQL transaction.
