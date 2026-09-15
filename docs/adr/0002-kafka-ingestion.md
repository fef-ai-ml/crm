# 0002. Use Kafka as the event ingestion backbone

- **Status:** Accepted
- **Date:** 2026-09-15
- **Deciders:** project owner

## Context and Problem Statement

The ingest API must accept bursts of events, acknowledge quickly, and never lose an accepted event, while profile updates, identity merges, and segment evaluation happen asynchronously. Which transport sits between the API and the worker?

## Decision Drivers

- NFR-01 and NFR-02: 1,000 events/s with ingest p95 under 50 ms.
- NFR-04: durability; acknowledge only after the transport has persisted the event.
- FR-09: per-identifier ordering keeps one identifier's events serial; cross-identifier merges are protected by row locks, see spec §4.2.
- Portfolio value: MoEngage and comparable platforms run on Kafka; reviewers expect it.
- Replay: segmentation must be able to rebuild state from processed events.

## Considered Options

1. Kafka (KRaft mode, single node locally) with topics `events.raw`, `events.processed`, `events.dlq`.
2. Redis Streams with consumer groups; Redis is already in the stack.
3. RabbitMQ with MassTransit.
4. Synchronous writes to SQL Server from the API; no queue.

## Decision Outcome

Chosen option: "Kafka", because partition keys give per-user ordering for free, log retention gives replay, consumer groups give independent Profiles and Segmentation consumers on the same stream, and it is the industry-standard answer for this workload. Redis Streams would work at portfolio scale but weakens the throughput story and mixes queue duties into the cache. RabbitMQ lacks cheap replay. Synchronous writes fail NFR-02 under burst.

### Positive consequences

- Ordering per `{tenantId}:{userKey}` key keeps one identifier's events serial; cross-identifier merges are protected by row locks, see spec §4.2.
- Two consumer groups (`profiles`, `segmentation`) read the same processed stream; adding Campaign triggers later is another consumer group.
- DLQ is just another topic; poison handling is uniform.

### Negative consequences

- One more heavyweight container locally; documented 8 GB RAM minimum.
- Confluent.Kafka client and offset management add learning cost compared with Redis Streams.

## Consequences

Topics, keys, partition counts (12), and retention (raw 7 d, processed 7 d, dlq 30 d) are specified in design spec §3.5. Trace context propagates through Kafka headers (NFR-06). The Worker commits offsets only after the SQL transaction commits.
