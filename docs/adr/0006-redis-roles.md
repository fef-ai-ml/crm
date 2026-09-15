# 0006. Treat Redis as a rebuildable cache, never as a source of truth

- **Status:** Accepted
- **Date:** 2026-09-15
- **Deciders:** project owner

## Context and Problem Statement

Redis is in the stack for low-latency lookups and set operations. Without a clear rule, state tends to accumulate in Redis until it becomes an undeclared second database. What may live in Redis, and what guarantees does the system make about it?

## Decision Drivers

- Single source of truth in SQL Server ([0003](0003-sql-server-json-profiles.md)).
- NFR-02: identity lookup and real-time segment updates need sub-millisecond reads.
- Operability: losing Redis must degrade performance, not correctness.

## Considered Options

1. Redis holds only derived, rebuildable state with an explicit key inventory; SQL Server is authoritative.
2. Redis as primary store for profiles and membership; SQL Server for events only.
3. No Redis; all lookups hit SQL Server.

## Decision Outcome

Chosen option: "Rebuildable cache with an explicit key inventory", because every Redis key in slice 1 can be reconstructed from SQL Server or by replaying `events.processed`, so a Redis flush costs latency, not data. A Redis-primary design breaks the single-truth rule; no Redis makes the identity hot path a SQL round trip per event.

### Positive consequences

- Flushing Redis is a safe operational action; a batch run or replay restores it.
- The key inventory is a reviewable table, and every key carries a tenant prefix except the two resolved before a tenant is known or scoped: `apikey:{prefix}` and the `segments:changed` control channel, which carries the tenant id in its payload instead.
- TTLs are explicit; nothing grows unbounded.

### Negative consequences

- Real-time segment membership can lag truth until the next batch run.
- Cache invalidation logic (identity cache after merges, segment cache after saves) must be tested.

## Consequences

Slice 1 key inventory: identity cache (TTL 1 h), real-time segment member sets, per-(segment, event leaf) counters keyed by event name and window (TTL = that leaf's window, one key per leaf so two leaves on the same event with different windows stay separate), preview count cache (60 s), API key resolution cache (10 min), and the `segments:changed` pub/sub control channel. Documented in [redis-and-kafka.md](../data-dictionary/redis-and-kafka.md). Any new key requires a row in that table.
