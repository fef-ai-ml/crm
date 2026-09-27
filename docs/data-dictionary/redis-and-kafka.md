# Redis Keys and Kafka Topics

Redis holds only rebuildable state ([ADR 0006](../adr/0006-redis-roles.md)). Kafka is the ingestion backbone ([ADR 0002](../adr/0002-kafka-ingestion.md)). Every key is prefixed `t:{tenantId}:` except the API key cache (`apikey:{prefix}`), which is looked up before the tenant is known, and the `segments:changed` control channel, whose payload carries the tenant id instead.

## Redis

| Key pattern | Type | TTL | Written by | Read by | Rebuild from |
|---|---|---|---|---|---|
| `t:{tenantId}:id:{kind}:{value}` | string → ProfileId | 1 h | Raw events consumer after the SQL commit, on a lookup miss or a new identity; deleted inside the merge transaction for the loser's identities | Raw events consumer | `profiles.UserIdentities` |
| `t:{tenantId}:seg:{segmentId}:members` | SET of ProfileId | none | Segmentation consumer (SADD/SREM; the return value is the transition, plan I ruling I5); Segment Runner (DEL + SADD in one MULTI after a run commits, Active eligible segments only, I11); Segments API deletes it when the segment is archived or saved as not Active / not eligible (I13) | Segments API (`GET /segments/{id}` realtimeMemberCount = SCARD, null when Redis is down, I12) | Batch run or replay of `events.processed` |
| `t:{tenantId}:seg:{segmentId}:ev:{eventName}:{window}:{profileId}` | string counter | = that leaf's window (e.g. 30 d), armed by the first event and never re-armed | Segmentation consumer (INCR + EXPIRE NX), only for an event whose `occurredAt` lies inside the window; leaves sharing a name and window share the key (I3) | Segmentation consumer, read for every event leaf when a profile is evaluated | Replay of `events.processed` |
| `t:{tenantId}:preview:{definitionHashHex}` | string count | 60 s | Segments API after a preview query | Segments API | Re-run preview |
| `apikey:{prefix}` | string → `{tenantId:N}:{keyId:N}:{keyHashHex}` (a hit still verifies the full key in constant time) | 10 min | Tenant middleware on SQL fallback; deleted on revoke | Tenant middleware | `identity.ApiKeys` |
| `segments:changed` | pub/sub channel | — | Segments API after a successful create, update or archive (payload: tenantId as 32 hex characters; best-effort, I7) | Worker in-memory segment cache invalidation; the cache also expires after `Segmentation:Realtime:CacheTtl` (60 s) because pub/sub loses what is sent while a Worker reconnects (I6) | — |

Value encoding: GUIDs as 32 lower-case hex characters without dashes; counters as decimal strings.

## Kafka

| Topic | Key | Partitions | Retention | Producer | Consumer groups | Message types |
|---|---|---|---|---|---|---|
| `events.raw` | `{tenantId}:{userKey}` where userKey = `userId` if present else `anonymousId` | 12 | 7 d | Crm.Api ingest endpoints | `profiles` (Crm.Worker) | `TrackEventRaw`, `IdentifyRaw` |
| `events.processed` | `{tenantId}:{profileId}` | 12 | 7 d | Crm.Worker raw events consumer | `segmentation` (Crm.Worker) | `EventTracked`, `ProfileUpdated`, `ProfileMerged` |
| `events.dlq` | same as source message | 12 | 30 d | Crm.Worker DLQ publisher | none (manual inspection) | original bytes |

Headers on every message: `tenant-id` (GUID string), `message-type`, `content-type: application/json`, `traceparent` (W3C trace context). DLQ adds `error`, `stack`, `attempt`, `source-topic`, `source-partition`, `source-offset`.

Message contracts (in `Crm.Contracts`, JSON, camelCase):

| Type | Fields |
|---|---|
| `TrackEventRaw` | eventId, tenantId, userId?, anonymousId?, event, properties (object), occurredAt, receivedAt |
| `IdentifyRaw` | eventId, tenantId, userId, anonymousId?, traits (object), receivedAt |
| `EventTracked` | eventId, tenantId, profileId, event, properties, occurredAt, profile: { externalId?, email?, phone?, attributes } |
| `ProfileUpdated` | tenantId, profileId, changedKeys (string[]), profile: { externalId?, email?, phone?, attributes }, occurredAt |
| `ProfileMerged` | tenantId, winnerProfileId, loserProfileId, occurredAt |

Producer settings: `acks=all`, `enable.idempotence=true`. `events.processed` is at-least-once: a redelivered duplicate re-announces its `EventTracked` (ruling E7). Consumer settings: `enable.auto.commit=false`, `auto.offset.reset=earliest`, one synchronous commit per record, after the SQL transaction commits.

The `segmentation` group (plan I) runs `Segmentation:Realtime:Instances` consumers (default 4) and never dead-letters: `events.processed` is produced by the Worker itself, so an undecodable record is a programmer error, and real-time state is rebuildable — an undecodable record, a Redis failure or a SQL failure after two retries is logged, counted in `crm_realtime_skipped_total{reason}` and skipped (ruling I9). It does not deduplicate a redelivered `EventTracked` by `eventId`; the rare over-count is corrected for membership by the next batch run (I4).
