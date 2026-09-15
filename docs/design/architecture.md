# Architecture — CRM Platform, Slice 1

This document visualizes the design in the [slice 1 spec](../superpowers/specs/2026-09-15-crm-slice1-design.md). Diagrams use the C4 model at three levels, followed by a module map and sequence diagrams for the four core flows. Decisions are recorded in the [ADRs](../adr/README.md).

## 1. System Context

```mermaid
C4Context
  title CRM Platform — System Context (slice 1)
  Person(marketer, "Marketer", "Builds segments, explores profiles")
  Person(dev, "Developer", "Integrates the ingest API, manages API keys")
  Person(admin, "Tenant Admin", "Manages users and API keys for one workspace")
  System(crm, "CRM Platform", "Ingests events, unifies profiles, evaluates segments")
  System_Ext(app, "Customer's App / Backend", "Sends track and identify calls")
  Rel(app, crm, "POST /v1/track, /v1/identify, /v1/batch", "HTTPS + X-Api-Key")
  Rel(marketer, crm, "Uses dashboard", "HTTPS + JWT")
  Rel(admin, crm, "Creates users and API keys", "HTTPS + JWT")
  Rel(dev, crm, "Reads OpenAPI reference")
```

## 2. Containers

```mermaid
C4Container
  title CRM Platform — Containers
  Person(marketer, "Marketer")
  System_Ext(app, "Customer's App")
  Container(web, "Crm.Web", "React 19, Vite, TypeScript", "Segment builder, profile explorer")
  Container(api, "Crm.Api", "ASP.NET Core (.NET 10)", "Ingest and admin REST, Swagger, rate limiting")
  Container(worker, "Crm.Worker", "ASP.NET BackgroundService (.NET 10)", "Kafka consumers, segment batch jobs")
  ContainerDb(sql, "SQL Server 2022", "schemas profiles, segments, identity", "Single source of truth")
  ContainerDb(redis, "Redis 7", "", "Identity cache, real-time segment sets, counters")
  ContainerQueue(kafka, "Kafka (KRaft)", "topics events.raw, events.processed, events.dlq", "Durable event log")
  Rel(marketer, web, "HTTPS")
  Rel(app, api, "HTTPS + API key")
  Rel(web, api, "JSON over HTTPS, JWT")
  Rel(api, kafka, "produce events.raw, acks=all")
  Rel(api, sql, "admin reads and writes, preview counts")
  Rel(api, redis, "API key cache, preview cache")
  Rel(worker, kafka, "consume events.raw; produce and consume events.processed; produce events.dlq")
  Rel(worker, sql, "profiles, events, segment runs, membership changes")
  Rel(worker, redis, "identity cache, segment sets, event counters")
```

## 3. Components

### 3.1 Crm.Api

```mermaid
C4Component
  title Crm.Api — Components
  Container_Boundary(api, "Crm.Api") {
    Component(tenantmw, "Tenant Middleware", "ASP.NET middleware", "Resolves tenant from JWT claim or X-Api-Key; populates ITenantContext")
    Component(ratelimit, "Rate Limiter", "ASP.NET rate limiting", "Token bucket per API key, 2,000 req/min")
    Component(ingest, "Ingest Endpoints", "Minimal API + FluentValidation", "/v1/track, /v1/identify, /v1/batch")
    Component(profiles, "Profiles Endpoints", "Minimal API", "search, detail, timeline")
    Component(segments, "Segments Endpoints", "Minimal API", "CRUD, preview, run, members, runs, changes")
    Component(tenancy, "Tenancy Endpoints", "Minimal API + ASP.NET Identity", "login, refresh, api-keys")
    Component(problem, "Problem Details Mapper", "RFC 9457", "Maps Result errors and exceptions to problem+json")
  }
  ContainerQueue(kafka, "Kafka")
  ContainerDb(sql, "SQL Server")
  ContainerDb(redis, "Redis")
  Rel(tenantmw, redis, "apikey:{prefix} lookup")
  Rel(tenantmw, sql, "ApiKeys fallback")
  Rel(ingest, kafka, "produce events.raw")
  Rel(profiles, sql, "read profiles, events")
  Rel(segments, sql, "segments, runs, preview COUNT")
  Rel(segments, redis, "preview cache")
  Rel(tenancy, sql, "identity schema")
```

### 3.2 Crm.Worker

```mermaid
C4Component
  title Crm.Worker — Components
  Container_Boundary(worker, "Crm.Worker") {
    Component(rawconsumer, "Raw Events Consumer", "Confluent.Kafka, group profiles", "One tenant scope and SQL transaction per message")
    Component(identity, "Identity Resolver", "Profiles.Application", "Lookup, create, merge")
    Component(applier, "Event Applier", "Profiles.Application", "Upsert profile, append event, idempotent on EventId")
    Component(processedprod, "Processed Producer", "Confluent.Kafka", "EventTracked, ProfileUpdated, ProfileMerged")
    Component(segconsumer, "Segmentation Consumer", "Confluent.Kafka, group segmentation", "Real-time evaluation of eligible segments")
    Component(evaluator, "Realtime Evaluator", "Segmentation.Application", "Evaluates rule tree against snapshot + counters")
    Component(runner, "Segment Runner", "Quartz.NET job + on-demand", "Compile, materialize, diff, swap, prune")
    Component(dlq, "DLQ Publisher", "Polly + Confluent.Kafka", "After 3 retries, publish with error headers")
  }
  ContainerQueue(kafka, "Kafka")
  ContainerDb(sql, "SQL Server")
  ContainerDb(redis, "Redis")
  Rel(kafka, rawconsumer, "events.raw")
  Rel(rawconsumer, identity, "resolve")
  Rel(identity, redis, "identity cache")
  Rel(identity, sql, "UserIdentities, Profiles")
  Rel(rawconsumer, applier, "apply")
  Rel(applier, sql, "Profiles, Events")
  Rel(rawconsumer, processedprod, "after commit")
  Rel(processedprod, kafka, "events.processed")
  Rel(kafka, segconsumer, "events.processed")
  Rel(segconsumer, evaluator, "evaluate")
  Rel(evaluator, redis, "SADD/SREM sets, INCR counters")
  Rel(evaluator, sql, "MembershipChanges")
  Rel(runner, sql, "SegmentRuns, SegmentRunMembers, MembershipChanges")
  Rel(runner, redis, "rebuild eligible sets")
  Rel(rawconsumer, dlq, "poison")
  Rel(dlq, kafka, "events.dlq")
```

## 4. Module Map

| Module | Projects | Owns | Depends on | Talks to others via |
|---|---|---|---|---|
| Ingestion | Application, Infrastructure | validation, event envelope, Kafka producer | Kernel, Contracts | `events.raw` topic |
| Profiles | Domain, Application, Infrastructure | schema `profiles`, identity resolution, merge, timeline | Kernel, Contracts | consumes `events.raw`; produces `events.processed` |
| Segmentation | Domain, Application, Infrastructure | schema `segments`, rule compiler, runner, real-time evaluator | Kernel, Contracts | consumes `events.processed`; reads `profiles.*` tables read-only ([ADR 0009](../adr/0009-segmentation-reads-profiles-tables.md)) |
| Tenancy | Domain, Application, Infrastructure | schema `identity`, tenants, users, API keys, JWT | Kernel | `ITenantContext` (Kernel) |
| Crm.Contracts | class library | integration events `TrackEventRaw`, `IdentifyRaw`, `EventTracked`, `ProfileUpdated`, `ProfileMerged`; shared DTOs | — | — |
| Crm.Kernel | class library | `Entity`, `Result<T>`, `ITenantContext`, `IClock`, error types | — | — |

Dependency rules enforced by `Crm.ArchTests`:

```mermaid
flowchart LR
  Api[Crm.Api] --> Ingestion
  Api --> Profiles
  Api --> Segmentation
  Api --> Tenancy
  Worker[Crm.Worker] --> Profiles
  Worker --> Segmentation
  Ingestion --> Contracts[Crm.Contracts]
  Ingestion --> Kernel[Crm.Kernel]
  Profiles --> Contracts
  Profiles --> Kernel
  Segmentation --> Contracts
  Segmentation --> Kernel
  Tenancy --> Kernel
  Segmentation -. read-only SQL .-> ProfilesTables[(profiles.* tables)]
```

## 5. Sequence Diagrams

### 5.1 Track event

```mermaid
sequenceDiagram
  participant SDK as Customer App
  participant Api as Crm.Api
  participant Kafka
  participant Worker as Crm.Worker (profiles)
  participant Redis
  participant SQL as SQL Server
  SDK->>Api: POST /v1/track (X-Api-Key)
  Api->>Api: resolve tenant, validate, EventId = UUIDv7
  Api->>Kafka: produce events.raw key tenant:user (acks=all)
  Api-->>SDK: 202 { eventId }
  Kafka->>Worker: consume
  Worker->>Redis: GET t:{tenant}:id:{kind}:{value}
  alt cache miss
    Worker->>SQL: SELECT UserIdentities / INSERT Profile + Identity
    Worker->>Redis: SET identity cache (TTL 1h)
  end
  Worker->>SQL: BEGIN; UPDATE Profile LastSeenAt, EventCount; INSERT Event; COMMIT
  Worker->>Kafka: produce events.processed EventTracked
  Worker->>Kafka: commit offset
```

### 5.2 Identify with merge

```mermaid
sequenceDiagram
  participant SDK as Customer App
  participant Api as Crm.Api
  participant Kafka
  participant Worker as Crm.Worker (profiles)
  participant SQL as SQL Server
  participant Redis
  SDK->>Api: POST /v1/identify { userId: U, anonymousId: A, traits }
  Api->>Kafka: events.raw IdentifyRaw
  Kafka->>Worker: consume
  Worker->>SQL: resolve anonymous_id:A -> Profile P1, external_id:U -> Profile P2
  alt P1 != P2
    Worker->>SQL: winner = older FirstSeenAt
    Worker->>SQL: UPDATE Events SET ProfileId=winner WHERE ProfileId=loser
    Worker->>SQL: UPDATE UserIdentities SET ProfileId=winner WHERE ProfileId=loser
    Worker->>SQL: merge Attributes (winner values win); SET loser.MergedIntoId=winner
    Worker->>Redis: DEL identity cache entries for loser
  end
  Worker->>SQL: JSON_MODIFY traits per key on winner; COMMIT
  Worker->>Kafka: events.processed ProfileMerged (if merged), ProfileUpdated
```

### 5.3 Batch segment run

```mermaid
sequenceDiagram
  participant UI as Crm.Web
  participant Api as Crm.Api
  participant Runner as Segment Runner (Worker)
  participant SQL as SQL Server
  participant Redis
  UI->>Api: POST /v1/segments/{id}/run
  Api->>SQL: INSERT SegmentRuns (Status=Queued)
  Api-->>UI: 202 { runId }
  Runner->>SQL: compile rule tree; UPDATE run Status=Running, CompiledSql
  Runner->>SQL: INSERT SegmentRunMembers SELECT ... (5 min timeout)
  Runner->>SQL: INSERT MembershipChanges (new EXCEPT current = Entered; current EXCEPT new = Exited)
  Runner->>SQL: UPDATE Segments SET CurrentRunId, MemberCount, LastRunAt; run Status=Finished
  opt real-time eligible
    Runner->>Redis: DEL + SADD t:{tenant}:seg:{id}:members from run
  end
  Runner->>SQL: DELETE runs older than last 3
```

### 5.4 Real-time segment evaluation

```mermaid
sequenceDiagram
  participant Kafka
  participant Seg as Segmentation Consumer (Worker)
  participant Cache as In-memory segment cache
  participant Redis
  participant SQL as SQL Server
  Kafka->>Seg: events.processed EventTracked { tenant, profile snapshot, event }
  Seg->>Cache: eligible Active segments for tenant
  loop each segment referencing event.name
    Seg->>Redis: INCR t:{tenant}:seg:{seg}:ev:{profile}; EXPIRE window
  end
  loop each eligible segment
    Seg->>Seg: evaluate rule tree (snapshot + counters)
    alt now member, was not
      Seg->>Redis: SADD members
      Seg->>SQL: INSERT MembershipChanges Entered (Source=Realtime)
    else was member, now not
      Seg->>Redis: SREM members
      Seg->>SQL: INSERT MembershipChanges Exited (Source=Realtime)
    end
  end
  Seg->>Kafka: commit offset
```
