# Redis Keys and Kafka Topics

Redis holds only rebuildable state ([ADR 0006](../adr/0006-redis-roles.md)). Kafka is the ingestion backbone ([ADR 0002](../adr/0002-kafka-ingestion.md)). Every key is prefixed `t:{tenantId}:` except the API key cache (`apikey:{prefix}`), which is looked up before the tenant is known, and the `segments:changed` control channel, whose payload carries the tenant id instead.

## Redis

| Key pattern | Type | TTL | Written by | Read by | Rebuild from |
|---|---|---|---|---|---|
| `t:{tenantId}:id:{kind}:{value}` | string → ProfileId | 1 h | Raw events consumer on lookup miss; deleted on merge for the loser | Raw events consumer | `profiles.UserIdentities` |
| `t:{tenantId}:seg:{segmentId}:members` | SET of ProfileId | none | Segmentation consumer (SADD/SREM); Segment Runner (DEL + SADD on batch finish) | Segments API (`GET /segments/{id}` realtimeMemberCount), Realtime Evaluator | Batch run or replay of `events.processed` |
| `t:{tenantId}:seg:{segmentId}:ev:{eventName}:{window}:{profileId}` | string counter | = that leaf's window (e.g. 30 d) | Segmentation consumer (INCR + EXPIRE NX) | Realtime Evaluator | Replay of `events.processed` |
| `t:{tenantId}:preview:{definitionHashHex}` | string count | 60 s | Segments API after a preview query | Segments API | Re-run preview |
| `apikey:{prefix}` | string → `{tenantId:N}:{keyId:N}:{keyHashHex}` (a hit still verifies the full key in constant time) | 10 min | Tenant middleware on SQL fallback; deleted on revoke | Tenant middleware | `identity.ApiKeys` |
| `segments:changed` | pub/sub channel | — | Segments API on save/archive (payload: tenantId) | Worker in-memory segment cache invalidation | — |

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

Producer settings: `acks=all`, `enable.idempotence=true`. Consumer settings: `enable.auto.commit=false`, manual commit after the SQL transaction commits, `auto.offset.reset=earliest`.
